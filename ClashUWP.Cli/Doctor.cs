// doctor —— 一条命令回答"这台机器上 ClashUWP 到底能不能用、卡在哪一环"。
//
// 【为什么要专门做它，而不是让人自己拼命令】
//   遥控端这条链上有四个各自会断的点：包没装 → PFN 查不到 → AppService 连不上 →
//   内核没答 → 隧道没通。以前要判断得连发三四条命令（list / selftest / status / profiles），
//   而<b>不知道要发哪几条</b>恰恰就是"要学"的部分。
//   doctor 把这四步按顺序跑一遍，每一步都自带"这步失败意味着什么、下一步该干什么"，
//   并且用同一套退出码 —— 谁读都能定位，不需要先懂这个项目。
//
// 【刻意不发任何碰隧道的命令】只做 read 级：status / profiles 是纯读，
//   数据面那一步（可选 --target）也只是一次 TCP/HTTP 探测，不改路由、不拨号。
//   所以 doctor 可以在任何时候跑，包括正连着的时候。

using System.Text;

namespace ClashUWP.Cli;

internal static class Doctor
{
    private static bool _offline;
    private static string? _targetRaw;
    private static string? _pfn;
    private static int _timeout = 20;
    private static int _probeTimeoutSec = 5;
    private static int _method = 0;   // 0 auto / 1 tcp / 2 http
    private static int _expectStatus;  // 0 = 任何响应都算通；给了就按它卡（配合 generate_204）

    /// <summary>
    /// 阶段编号是固定的：1 包发现 · 2 WinRT/AOT 通路 · 3 AppService 通道 · 4 内核应答 ·
    /// 5 系统 profile · 6 数据面连通（只有给了 --target 才跑）。
    /// 每阶段一行 <c>阶段N · [PASS|FAIL|SKIP] · 结论 · 下一步</c>，扫一眼就知道断在哪。
    /// </summary>
    /// <param name="timeoutOverride">
    /// 来自全局 <c>--timeout</c>（Main 已在路由前把它从参数里摘走，所以这里不再自己解析 ——
    /// 两处解析同一开关必然漂）。给定时用它，否则用体检自己的 20s：
    /// doctor 读的都是纯读命令，20s 不通就是不通，等 120s 只会让人以为卡死。
    /// </param>
    public static async Task<int> RunAsync(List<string> flags, string? pfn, int? timeoutOverride)
    {
        _pfn = pfn;
        if (timeoutOverride is > 0) _timeout = timeoutOverride.Value;
        string? err = ParseFlags(flags);
        if (err is not null)
        {
            Console.Error.WriteLine(err);
            Console.Error.WriteLine("用法: clashuwp-cli doctor [--offline] [--target <host|url>] " +
                "[--method tcp|http] [--expect 204] [--probe-timeout 5] [--timeout 20]");
            return ExitCodes.Usage;
        }

        var notes = new List<string>();
        Console.WriteLine("clashuwp-cli doctor —— 逐级体检（全程只读，不碰隧道）");
        Console.WriteLine();

        // ── 阶段 1 · 包发现 ────────────────────────────────────────────
        AppServiceChannel.Package? pkg = AppServiceChannel.FindTargetPackage();
        if (pkg is null)
        {
            Line(1, "FAIL", $"目标包未安装（Identity Name={AppServiceChannel.TargetPackageName}）",
                "装 ClashUWP 主包后重跑；本工具不需要主应用正在运行，但包必须在");
            notes.Add("主包缺失");
            Console.WriteLine("\n[doctor] VERDICT=blocked —— 第 1 步就断了，后面无从谈起");
            return ExitCodes.PackageNotFound;
        }
        string pfnUsed = _pfn ?? pkg.Value.FamilyName;
        Line(1, "PASS", $"已安装 v{pkg.Value.Version} · PFN={pkg.Value.FamilyName}",
            _pfn is null ? "" : $"（--pfn 覆盖了反查结果：{_pfn}）");

        if (_offline)
        {
            Line(2, "SKIP", "--offline：只做包发现", "去掉 --offline 才会连主应用");
            Console.WriteLine("\n[doctor] VERDICT=package-only（离线体检完成）");
            return ExitCodes.Ok;
        }

        // ── 阶段 2 · 本工具的 WinRT/AOT 通路 ───────────────────────────
        // 不重跑 selftest 的四项（那会再多等 15s），只验最关键的一条：包枚举能力可用
        // —— 第 1 步已经事实上验过了，所以这里直接判 PASS，并把"完整验法"指给用户。
        Line(2, "PASS", "PackageManager 包枚举可用（本进程 WinRT 通路通）",
            "要逐项验 AOT 投影请跑 selftest");

        // ── 阶段 3+4 · AppService 通道 + 内核应答 ──────────────────────
        // kernelAwake / anyConnected 要留到阶段 5 之后一起判：
        //   没连着的时候内核本来就不跑，"9090 拒连"是<b>空闲态</b>不是故障；
        //   只有"会话显示已连接、内核却不应答"才是真问题。
        //   早先的版本一看到内核不应答就记 issue，结果每次体检都报忧 —— 判据缺了参照物。
        bool kernelAwake = false, anyConnected = false;
        AppServiceChannel.Reply status = await AppServiceChannel.SendAsync("status", pfnUsed, _timeout);
        string statusText = OneLine(status.Text);
        if (status.Code == ExitCodes.Ok)
        {
            Line(3, "PASS", "AppService 通道连通，主应用已应答", "");
            // 内核没起来时服务端也<b>正常回话</b>（回的是失败原因），所以第 4 步看的是<b>回话形状</b>，
            // 不是"有没有报错字样"。实测踩过：内核没跑时回 "status 查询失败: …积极拒绝…"，
            // 当时用"不含 refused/异常/不可达 就算通"判成了 PASS —— 判据写反了。
            // 现在按服务端 RunStatusAsync 的成功格式判：成功必然是 "mode=… | log-level=… | interface-name=…"。
            kernelAwake = statusText.StartsWith("mode=", StringComparison.Ordinal);
            Line(4, kernelAwake ? "PASS" : "WARN", kernelAwake
                ? $"内核运行态：{statusText}"
                : $"内核未应答（REST 9090 拒连）：{statusText}",
                kernelAwake ? "" : "结合阶段5 判：没连着时这是空闲态；连着却这样 = 内核没起来，数据面一定不通");
        }
        else
        {
            Line(3, "FAIL", $"通道不通（exit={status.Code}）：{statusText}",
                status.Code == ExitCodes.AppUnreachable
                    ? "主应用没在跑或装的版本没声明 ClashUWP.Debug —— 确认主包版本 ≥ 带 AppService 的那版；" +
                      "隧道出问题时改用文件回路 LocalState\\debug-cmd.txt（不依赖任何 IPC 通路）"
                    : "见 --help 的退出码表");
            Line(4, "SKIP", "通道不通，跳过内核应答", "");
            notes.Add("通道不通");
        }

        // ── 阶段 5 · 系统 VPN profile ──────────────────────────────────
        // 判据同样按服务端 ListOurProfilesAsync 的返回形状：
        //   有 profile ⇒ "共 N 条：…"；一条都没有 ⇒ "本包名下没有任何 profile"。
        AppServiceChannel.Reply profiles = await AppServiceChannel.SendAsync("profiles", pfnUsed, _timeout);
        string profilesText = OneLine(profiles.Text);
        if (profiles.Code == ExitCodes.Ok)
        {
            bool hasOne = profilesText.StartsWith("共", StringComparison.Ordinal);
            bool connected = profilesText.Contains("状态=Connected", StringComparison.Ordinal);
            anyConnected = connected;
            // 服务端会把试连用的临时 profile 标成 "[探测残留]" —— 它们本该在 probe 跑完自删，
            // 留下了就是"上一轮 probe 没收尾"，会出现在快速设置面板里，得让人知道。
            bool residue = profilesText.Contains("探测残留", StringComparison.Ordinal);
            string tag = (hasOne, connected, residue) switch
            {
                (false, _, _) => "WARN",
                (true, _, true) => "WARN",
                _ => "PASS",
            };
            Line(5, tag, hasOne ? $"{profilesText}{(connected ? "（当前有已连接会话）" : "")}"
                                : $"系统 profile：{profilesText}",
                !hasOne ? "点「建/重建 profile」，或拉取并启用一条订阅（会自动注册订阅专属 profile）"
                        : residue ? "有 [探测残留] 的临时 profile：系统设置→VPN 里删掉它们，或跑 recreate 重建"
                                  : "");
            if (!hasOne) notes.Add("系统 profile 缺失");
            else if (residue) notes.Add("存在探测残留 profile");
        }
        else
        {
            Line(5, "SKIP", $"读不到系统 profile（exit={profiles.Code}）：{profilesText}", "");
            notes.Add("profile 读不到");
        }

        // 阶段 4 的 WARN 到这里才有参照物可判（通道通才有 kernelAwake 与 anyConnected 两个事实）：
        //   · 没连着 + 内核不应答 ⇒ 空闲态，不记 issue（不然每次体检都无病呻吟）；
        //   · 连着 + 内核不应答 ⇒ 真故障：前台说在跑，REST 却拒连，数据面必然不通。
        bool channelOk = status.Code == ExitCodes.Ok;
        if (channelOk && !kernelAwake)
        {
            if (anyConnected) notes.Add("会话已连接但内核不应答");
            else Console.WriteLine(
                "[doctor] 说明：阶段4 的 WARN 是空闲态 —— 没有已连接会话时内核本来就不跑，这不算故障。");
        }

        // ── 阶段 6 · 数据面连通（可选）─────────────────────────────────
        if (_targetRaw is null)
        {
            Line(6, "SKIP", "未给 --target：没做数据面连通判定",
                "要判「隧道现在到底通不通」请给目标：doctor --target 223.5.5.5:443 " +
                "或 --target https://www.google.com/generate_204 --expect 204");
        }
        else if (!Probe.TryParse(_targetRaw, out Target t, out string? pe))
        {
            Line(6, "FAIL", $"--target 写法有问题：{pe}", "改成 host / host:port / https://host/path");
            notes.Add("目标写法错误");
        }
        else
        {
            string method = _method switch { 1 => "tcp", 2 => "http", _ => t.Scheme.Length > 0 ? "http" : "tcp" };
            if (method == "http" && t.Scheme.Length == 0) t = t with { Scheme = "https" };
            (bool ok, string why) = method == "http"
                ? await Probe.HttpAsync(t, _probeTimeoutSec, _expectStatus, false)
                : await Probe.TcpAsync(t, _probeTimeoutSec);
            Line(6, ok ? "PASS" : "FAIL", $"{method} {t.Describe} → {why}",
                ok ? "" : "注意 fake-ip 下域名恒解析成功，所以「建不起连」就是隧道没真通；" +
                          "连着的时候这条失败 ⇒ 跑 guard 自动拆，或手工 disconnect");
            if (!ok) notes.Add("数据面不通");
        }

        Console.WriteLine();
        if (notes.Count == 0)
            Console.WriteLine("[doctor] VERDICT=ok —— 包、通道、profile 就位" +
                (channelOk && !kernelAwake ? "（当前空闲：没有已连接会话，内核未起）"
                                           : "，内核应答正常") +
                (_targetRaw is null ? "；未做数据面判定，加 --target 才做" : "；数据面连通已验"));
        else
            Console.WriteLine($"[doctor] VERDICT=issues —— {string.Join("；", notes)}");
        return notes.Count == 0 ? ExitCodes.Ok : ExitCodes.ServerError;
    }

    /// <summary>统一行格式，方便人和机器一样好切：阶段号 + 状态 + 结论 + 下一步。</summary>
    private static void Line(int stage, string state, string verdict, string next)
    {
        var sb = new StringBuilder();
        sb.Append($"[doctor] 阶段{stage} · [{state}] · {verdict}");
        if (!string.IsNullOrWhiteSpace(next)) sb.Append($"  → 下一步：{next}");
        Console.WriteLine(sb.ToString());
    }

    private static string OneLine(string? s) =>
        (s ?? "").Replace("\r", " ").Replace("\n", " | ").Trim();

    private static string? ParseFlags(List<string> flags)
    {
        for (int i = 0; i < flags.Count; i++)
        {
            bool NeedInt(out int value)
            {
                value = 0;
                if (i + 1 >= flags.Count || !int.TryParse(flags[i + 1], out value)) return false;
                i++;
                return true;
            }

            switch (flags[i])
            {
                case "--offline":
                    _offline = true;
                    break;
                case "--target":
                    if (i + 1 >= flags.Count) return "--target 需要一个目标";
                    _targetRaw = flags[++i];
                    break;
                case "--method":
                    if (i + 1 >= flags.Count) return "--method 需要 tcp 或 http";
                    string m = flags[++i].ToLowerInvariant();
                    if (m == "tcp") _method = 1;
                    else if (m == "http") _method = 2;
                    else return $"--method 只认 tcp/http，收到 {m}";
                    break;
                case "--probe-timeout":
                    if (!NeedInt(out _probeTimeoutSec) || _probeTimeoutSec <= 0)
                        return "--probe-timeout 需要正整数秒";
                    break;
                case "--expect":
                    // 与 guard 同口径：0 = 拿到任何响应就算通，给了码就只认那个码。
                    if (!NeedInt(out _expectStatus) || _expectStatus is < 0 or > 599)
                        return "--expect 需要 0（任何响应都算通）或 100..599 的状态码";
                    break;
                case "--timeout":
                    return "--timeout 是全局开关（放在命令前后都认），这里不该出现 —— 见 --help";
                default:
                    return $"doctor 不认这个开关：{flags[i]}";
            }
        }
        return null;
    }
}
