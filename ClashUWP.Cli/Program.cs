// clashuwp-cli —— ClashUWP 的 AppService 遥控端。
//
// 通道:主应用清单声明的 in-app AppService「ClashUWP.Debug」。
//   in-app(无 EntryPoint)= 连接激活的是<b>主进程</b>的 App.OnBackgroundActivated,
//   VpnSession / 健康哨兵 / DebugCommandLoop 的状态全部可及 ——
//   这正是不能选 background-task AppService 的原因(那会起一个空壳宿主进程)。
//
// 协议(与 debug-cmd 文件回路同一张命令表):
//   请求  ValueSet{ ["cmd"] = "status" / "groups" / "select 组名 节点名" / ... }
//   响应  ValueSet{ ["out"] = 结果原文 } 或 ValueSet{ ["error"] = 原因 }
//
// 用法:
//   clashuwp-cli status
//   clashuwp-cli groups
//   clashuwp-cli logtail plugin 100
//   clashuwp-cli --list                     只打印目标包的 PFN(验证包发现)
//   clashuwp-cli --selftest                 自检 WinRT 通道在 AOT 下是否可用(不碰主应用/隧道)
//   ★ --list / --selftest 去掉前面的 -- 也认:走"应用执行别名"时敲进去的第一个词
//     就是别名本身(clashuwp-cli selftest),前面没有 -- 可给。
//   clashuwp-cli --pfn <PFN> status         指定 PackageFamilyName(默认按包名反查)
//   clashuwp-cli --timeout 30 connect       命令超时(秒,默认 120;拨号可能慢)
//
// 退出码:0=成功 2=连接失败 3=服务端返回错误 4=参数/包发现问题 5=超时 6=自检失败
//
// ★ 纪律(与 DebugCommandLoop 注释一致):connect / disconnect / recreate 会碰隧道,
//   本工具只是把命令原样传过去 —— 自动化脚本里不要无条件下发这三条。

using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;
using Windows.Management.Deployment;

namespace ClashUWP.Cli;

internal static class Program
{
    /// <summary>主应用清单里声明的 AppService 名。</summary>
    private const string ServiceName = "ClashUWP.Debug";

    /// <summary>
    /// ClashUWP/Package.appxmanifest 的 Identity Name。
    /// 它是包的固定标识(GUID 形态),不随重装变化 —— 变的只是 FamilyName 的
    /// 发布者 hash 后缀,所以这里存 Name、运行时反查 PFN,而不是把 PFN 写死。
    /// </summary>
    private const string TargetPackageName = "421769c5-17f4-4287-b6c1-193222d21e14";

    private static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        string? pfn = null;
        int timeoutSec = 120;
        bool listOnly = false;
        var cmdParts = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pfn":
                    if (i + 1 >= args.Length) return Usage("缺少 --pfn 的值");
                    pfn = args[++i];
                    break;
                case "--timeout":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out timeoutSec) || timeoutSec <= 0)
                        return Usage("--timeout 需要正整数秒数");
                    break;
                case "--list":
                case "list":   // 同 selftest：别名形态下第一个词没有前置 --
                    listOnly = true;
                    break;
                case "--selftest":
                case "selftest":
                    // 两种写法都收：走"应用执行别名"时，敲进去的第一个词就是别名本身
                    // （clashuwp-cli selftest），前面没有 -- 可给；只认 --selftest 的话
                    // 这个词会被当成命令原文发给服务端，回一句"未知命令"，很难看。
                    // 服务端命令表里没有 selftest 这一条，所以不会撞车。
                    return SelfTest();
                case "-h": case "--help":
                    return Usage(null);
                default:
                    // 命令原文(含带空格的节点名)由调用方加引号,这里整段拼回单行,
                    // 与 debug-cmd.txt 的单行命令协议逐字一致。
                    cmdParts.Add(args[i]);
                    break;
            }
        }

        pfn ??= FindPackageFamilyName(TargetPackageName);
        if (pfn is null)
        {
            Console.Error.WriteLine($"找不到目标包(Name={TargetPackageName})—— ClashUWP 未安装?");
            return 4;
        }
        if (listOnly)
        {
            Console.WriteLine(pfn);
            return 0;
        }
        if (cmdParts.Count == 0) return Usage("缺少命令");
        string cmd = string.Join(' ', cmdParts);

        using var conn = new AppServiceConnection
        {
            AppServiceName = ServiceName,
            PackageFamilyName = pfn,
        };

        AppServiceConnectionStatus status;
        try
        {
            // ★ 实测(2026-10-18):服务端还没声明 ClashUWP.Debug 时,OpenAsync
            //   不快速失败而是无限挂起(平台在尝试激活主应用进程)。必须给连接
            //   也套超时,否则 CLI 会无声卡死。15s 足够覆盖主应用冷启动。
            status = await conn.OpenAsync().AsTask()
                               .WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine(
                $"连接超时(15s): 服务端未声明 AppService「{ServiceName}」或主应用激活被挂起");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"OpenAsync 异常: {ex.Message}");
            return 2;
        }
        if (status != AppServiceConnectionStatus.Success)
        {
            // AppUnavailable 最常见:主应用没在跑且 in-app 激活被挂起/拒绝;
            // AppServiceUnavailable:服务名没对上(服务端还没声明 ClashUWP.Debug)。
            Console.Error.WriteLine($"连接失败: {status} (service={ServiceName}, pfn={pfn})");
            return 2;
        }

        AppServiceResponse resp;
        try
        {
            resp = await conn.SendMessageAsync(new ValueSet { ["cmd"] = cmd })
                             .AsTask()
                             .WaitAsync(TimeSpan.FromSeconds(timeoutSec));
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine($"命令超时({timeoutSec}s): {cmd}");
            return 5;
        }

        if (resp.Status != AppServiceResponseStatus.Success)
        {
            Console.Error.WriteLine($"响应状态异常: {resp.Status}");
            return 3;
        }
        if (resp.Message.TryGetValue("error", out object? err))
        {
            Console.Error.WriteLine(err?.ToString());
            return 3;
        }
        if (resp.Message.TryGetValue("out", out object? outText))
            Console.WriteLine(outText?.ToString());
        return 0;
    }

    /// <summary>
    /// <c>--selftest</c> —— 只验一件事：<b>本工具用到的 WinRT 投影在 Native AOT 下是否可用</b>。
    ///
    /// 【为什么要有它】CLI 改成 PublishAot 发布（官方对可执行文件的做法：启动快、
    ///   免运行时依赖、包体从 27 MB 降到 4 MB）能编译通过<b>不代表</b>跑得起来 ——
    ///   AOT 下跨 ABI 的泛型接口（<see cref="ValueSet"/> 本质是
    ///   IDictionary&lt;string, object&gt;）与 IAsyncOperation&lt;T&gt;.AsTask()
    ///   这些投影代码要靠 CsWinRT 生成，缺了是<b>运行时</b>才炸。
    ///   而端到端那一头要等主应用装上带 AppService 的版本才能验，太晚。
    ///
    /// 【刻意不碰真实目标】第 4 步连的是一个<b>不存在</b>的 PFN：平台没有可激活的包，
    ///   所以既不启动任何应用、也不碰隧道，只是把 OpenAsync 的异步跨 ABI 路径走一遍。
    ///   返回 Success 反而说明有问题；这里只要求"拿到状态、没炸"。
    /// </summary>
    private static int SelfTest()
    {
        int fail = 0;

        // 1) 值集：请求/响应两个方向的载体，也是最容易在 AOT 下炸的泛型字典投影。
        try
        {
            var vs = new ValueSet { ["cmd"] = "ping" };
            bool hit = vs.TryGetValue("cmd", out object? v) && (string?)v == "ping";
            Console.WriteLine($"[{(hit && vs.Count == 1 ? "PASS" : "FAIL")}] ValueSet 读写 " +
                              $"(count={vs.Count}, type={v?.GetType().Name ?? "null"})");
            if (!hit) fail++;
        }
        catch (Exception ex) { Console.WriteLine($"[FAIL] ValueSet 读写: {ex.GetType().Name}: {ex.Message}"); fail++; }

        // 2) 类型激活：RoActivateInstance 能不能拿到 AppServiceConnection 的工厂。
        try
        {
            using var c = new AppServiceConnection
            {
                AppServiceName = ServiceName,
                PackageFamilyName = "ClashUWP.SelfTest.NoSuch_0000000000000",
            };
            bool ok = c.AppServiceName == ServiceName &&
                      c.PackageFamilyName == "ClashUWP.SelfTest.NoSuch_0000000000000";
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] AppServiceConnection 构造/属性回读");
            if (!ok) fail++;
        }
        catch (Exception ex) { Console.WriteLine($"[FAIL] AppServiceConnection 构造: {ex.GetType().Name}: {ex.Message}"); fail++; }

        // 3) 包发现：PackageManager.FindPackagesForUser（full-trust 桌面进程才允许）。
        try
        {
            int n = new PackageManager().FindPackagesForUser("").Count();
            Console.WriteLine($"[{(n > 0 ? "PASS" : "FAIL")}] PackageManager 枚举到 {n} 个包");
            if (n <= 0) fail++;
        }
        catch (Exception ex) { Console.WriteLine($"[FAIL] PackageManager: {ex.GetType().Name}: {ex.Message}"); fail++; }

        // 4) 异步跨 ABI：OpenAsync().AsTask().WaitAsync(...) —— 真实连接用的同一条路径。
        try
        {
            using var c = new AppServiceConnection
            {
                AppServiceName = ServiceName,
                PackageFamilyName = "ClashUWP.SelfTest.NoSuch_0000000000000",
            };
            AppServiceConnectionStatus st = c.OpenAsync().AsTask()
                                             .WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            Console.WriteLine($"[PASS] OpenAsync 异步通路 (不存在包→状态={st}，预期非 Success)");
        }
        catch (TimeoutException) { Console.WriteLine("[PASS] OpenAsync 异步通路 (超时也说明任务在跑，未崩)"); }
        catch (Exception ex) { Console.WriteLine($"[FAIL] OpenAsync: {ex.GetType().Name}: {ex.Message}"); fail++; }

        Console.WriteLine(fail == 0
            ? "自检结论: AOT 下 WinRT 通道可用"
            : $"自检结论: {fail} 项失败 —— 这个发布形态不能用");
        return fail == 0 ? 0 : 6;
    }

    /// <summary>
    /// 按 Identity Name 反查当前用户已装包的 PackageFamilyName。
    /// full-trust 桌面进程可直接使用 PackageManager(Windows SDK 投影自带)。
    /// </summary>
    private static string? FindPackageFamilyName(string packageName)
    {
        try
        {
            var pm = new PackageManager();
            return pm.FindPackagesForUser("")
                     .FirstOrDefault(p => string.Equals(p.Id.Name, packageName,
                                                        StringComparison.OrdinalIgnoreCase))
                     ?.Id.FamilyName;
        }
        catch
        {
            return null;
        }
    }

    private static int Usage(string? error)
    {
        if (error is not null) Console.Error.WriteLine(error);
        Console.Error.WriteLine(
            "用法: clashuwp-cli [--pfn <PFN>] [--timeout 秒] <命令...>\n" +
            "      clashuwp-cli --list        只打印目标包 PFN\n" +
            "      clashuwp-cli --selftest    自检 WinRT/AOT 通路(不激活任何应用)\n" +
            "命令表见主应用 DebugCommandLoop 注释: status / connect / disconnect / recreate /\n" +
            "  probe / controller / conf / groups / logs / select <组名> <节点名> /\n" +
            "  bind <网卡名|-> / mode <rule|global|direct> / delay [节点名] /\n" +
            "  connections [kill <id>|kill-all] / logtail [app|plugin|sessions] [行数]");
        return 4;
    }
}
