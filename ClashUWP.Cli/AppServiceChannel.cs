// AppService 通道 —— 本工具与主应用之间唯一的那根线。
//
// 【通道选型没变过，但值得留在这】主应用清单声明的是 in-app AppService「ClashUWP.Debug」
//   （无 EntryPoint）⇒ 连接激活的是<b>主进程</b>的 App.OnBackgroundActivated，
//   VpnSession / 健康哨兵 / DebugCommandLoop 的状态全部可及。
//   选 background-task AppService 会另起一个空壳宿主进程，那边什么状态都没有。
//
// 【协议】请求 ValueSet{["cmd"]=一行命令}，响应 ValueSet{["out"]=原文} 或 ValueSet{["error"]=原因}。
//   与 LocalState\debug-cmd.txt 文件回路逐字同义 —— 两条通道一份命令表，
//   复盘"哪条命令在什么时候被谁下发过"时不能因为走了新门就查不到。
//
// 【为什么要从 Program.cs 抽出来】以前"连接 + 发送 + 超时 + 错误码"这套只写在 Main 里，
//   guard 要做兜底断开就得再抄一遍；抄一遍就意味着两处对 15s 连接超时
//   这个实测结论的处理可能漂。现在命令透传 / guard 兜底 / doctor 体检共用这一份。
//
// ★ 15s 连接超时是实测结论不是拍的：服务端还没声明该 AppService 时，
//   OpenAsync 不快速失败而是<b>无限挂起</b>（平台在尝试激活主应用进程，2026-10-18 实测），
//   不给连接套超时，CLI 就会无声卡死 —— 对自动化来说卡死比报错严重得多。

using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;
using Windows.Management.Deployment;

namespace ClashUWP.Cli;

internal static class AppServiceChannel
{
    /// <summary>主应用清单里声明的 AppService 名。</summary>
    public const string ServiceName = "ClashUWP.Debug";

    /// <summary>
    /// 主应用 Package.appxmanifest 的 Identity <b>Name</b>（现读为证：
    /// <c>ClashUWP/Package.appxmanifest:45</c> 与装机 <c>Get-AppxPackage</c> 都是
    /// <c>8B8CC4F4.ClashUWP</c>，家族 <c>8B8CC4F4.ClashUWP_4tjvwvefxrz66</c>）。
    /// 存 Name、运行时反查 PFN 而不是把 PFN 写死，是因为<b>只有</b> FamilyName 的发布者 hash
    /// 后缀会随签名证书变；换证书时不必来改这里。
    /// ★【1.0.2 → 1.0.3 · Docs/38 §3.4 F5 · 现读 git 历史为证】这里原先写死的是
    ///   <c>421769c5-17f4-4287-b6c1-193222d21e14</c>，<b>在它写下来那天是对的</b>：主清单的
    ///   Identity Name 一直到 <c>3b11f69</c>（1.0.74→1.0.75 那笔）才从那个 GUID 改成
    ///   <c>8B8CC4F4.ClashUWP</c>（顺带换了包家族哈希 ⇒ 老包与新包是两个家族，LocalState 不互通）。
    ///   ⇒ 真根因不是"当初抄错"，是<b>改名那次没有把 CLI 的发现逻辑一起改</b>，
    ///     于是 <c>clashuwp-cli profiles</c> / <c>status</c> 这类不带 <c>--pfn</c> 的命令
    ///     从 1.0.75 起<b>一律</b>走到"找不到目标包"（退出码 4），只有手工传 <c>--pfn</c> 才能用 ——
    ///     本工具宣称的"拿过来就会用"这条设计目标从那时起就没成立过，而 <c>Docs/13:93</c>
    ///     那句"主包 Identity Name 是那个 GUID"也从此是<b>历史形态</b>。
    /// </summary>
    public const string TargetPackageName = "8B8CC4F4.ClashUWP";

    /// <summary>连接阶段固定超时（秒）：覆盖主应用冷启动，又不放任平台无限挂起。</summary>
    private const int OpenTimeoutSec = 15;

    /// <summary>
    /// 一次下发的结果。<see cref="Code"/> 直接用进程退出码口径，
    /// 调用方（Main / guard / doctor）据此决定打印什么、返回什么。
    /// </summary>
    internal readonly record struct Reply(int Code, string Text);

    /// <summary>目标包信息：PFN + 已装版本号。</summary>
    internal readonly record struct Package(string FamilyName, string Version);

    /// <summary>按 Identity Name 反查当前用户已装包（full-trust 桌面进程可直接用 PackageManager）。</summary>
    public static Package? FindTargetPackage()
    {
        try
        {
            var pm = new PackageManager();
            var pkg = pm.FindPackagesForUser("")
                         .FirstOrDefault(p => string.Equals(p.Id.Name, TargetPackageName,
                                                            StringComparison.OrdinalIgnoreCase));
            if (pkg is null) return null;
            // 版本号手工拼而不是 ToString()：CsWinRT 生成的 PackageVersion 结构体
            // 在 AOT 下的 ToString 形态不是"1.0.72.0"这种四段，别让它出现在 doctor 的行里。
            var v = pkg.Id.Version;
            return new Package(pkg.Id.FamilyName, $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把一行命令发给主应用并等回话。
    /// 不抛异常 —— 所有失败形态都折成 Reply.Code，调用方只需 switch 退出码。
    /// </summary>
    public static async Task<Reply> SendAsync(string cmd, string? pfnOverride, int timeoutSec)
    {
        string? pfn = pfnOverride ?? FindTargetPackage()?.FamilyName;
        if (pfn is null)
            return new Reply(ExitCodes.PackageNotFound,
                $"找不到目标包（Name={TargetPackageName}）—— ClashUWP 未安装？先跑 doctor 看包在不在");

        using var conn = new AppServiceConnection
        {
            AppServiceName = ServiceName,
            PackageFamilyName = pfn,
        };

        AppServiceConnectionStatus status;
        try
        {
            status = await conn.OpenAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(OpenTimeoutSec));
        }
        catch (TimeoutException)
        {
            return new Reply(ExitCodes.AppUnreachable,
                $"连接超时（{OpenTimeoutSec}s）：服务端未声明 AppService「{ServiceName}」或主应用激活被挂起");
        }
        catch (Exception ex)
        {
            return new Reply(ExitCodes.AppUnreachable, $"OpenAsync 异常: {ex.Message}");
        }

        if (status != AppServiceConnectionStatus.Success)
        {
            // AppUnavailable 最常见：主应用没在跑且 in-app 激活被挂起/拒绝；
            // AppServiceUnavailable：服务名没对上（服务端还没声明 ClashUWP.Debug）。
            string hint = status switch
            {
                AppServiceConnectionStatus.AppNotInstalled => "目标包未安装",
                AppServiceConnectionStatus.AppUnavailable => "主应用不可用（没在跑且激活被拒/挂起）—— 可先跑 doctor",
                AppServiceConnectionStatus.AppServiceUnavailable => $"主应用没声明服务「{ServiceName}」（装的版本太旧？）",
                _ => "",
            };
            return new Reply(ExitCodes.AppUnreachable,
                $"连接失败: {status}{(hint.Length == 0 ? "" : $"（{hint}）")} service={ServiceName}, pfn={pfn}");
        }

        AppServiceResponse resp;
        try
        {
            resp = await conn.SendMessageAsync(new ValueSet { ["cmd"] = cmd })
                             .AsTask().WaitAsync(TimeSpan.FromSeconds(timeoutSec));
        }
        catch (TimeoutException)
        {
            return new Reply(ExitCodes.Timeout, $"命令超时（{timeoutSec}s）: {cmd}");
        }
        catch (Exception ex)
        {
            return new Reply(ExitCodes.AppUnreachable, $"SendMessageAsync 异常: {ex.Message}");
        }

        if (resp.Status != AppServiceResponseStatus.Success)
            return new Reply(ExitCodes.ServerError, $"响应状态异常: {resp.Status}");

        if (resp.Message.TryGetValue("error", out object? err))
            return new Reply(ExitCodes.ServerError, err?.ToString() ?? "(服务端给了空 error)");

        return new Reply(ExitCodes.Ok,
            resp.Message.TryGetValue("out", out object? outText) ? outText?.ToString() ?? "" : "");
    }
}

/// <summary>
/// 退出码 —— 与 --help 里那行说明、以及 CommandCatalog.JsonText() 的 exitCodes 同源。
/// 【为什么单独成一个类型】自动化脚本判的是退出码，不是文案；
/// 把数字集中在一处，改文案时不会把码表改漂。
/// </summary>
internal static class ExitCodes
{
    public const int Ok = 0;
    public const int AppUnreachable = 2;
    public const int ServerError = 3;
    public const int PackageNotFound = 4;
    public const int Usage = 4;
    public const int Timeout = 5;
    public const int SelfTestFailed = 6;
    public const int ForceRequired = 7;
    public const int GuardDisconnected = 8;
    public const int GuardActionFailed = 9;
    public const int GuardDryRun = 10;
}
