// guard —— 连通性兜底：指定服务器/网站连不上，就自动拆隧道并退出。
//
// 【要解决的病】这台机器上"隧道装上但没通"的历史形态都是同一个样子：
//   L3 接口在、NRPT 在、路由在，但包进去就没了 —— 整机看似连着，实际全黑
//   （Docs/08 §10.10：孤儿 NRPT + 幽灵路由；Docs/09：send 池 7188 借空）。
//   主应用侧本来就有 HealthMonitor 兜着（每轮探 223.5.5.5 / 119.29.29.29 / 8.8.8.8，
//   连续 2 轮失败判黑洞，另有异常重拨与物理网络 5 分钟兜底），
//   但它探的是<b>它自己写死的那几个靶子</b>。
//   guard 补的是另一半：<b>由当下这个人/AI 指定一个真正关心的目标</b> ——
//   "我就是要 google 的 204 能通"，那个目标不通就把隧道拆掉。
//   两者不冲突：HealthMonitor 是产品内置保险丝，guard 是外部一次性验证脚本。
//
// 【为什么动作只有 disconnect，绝不 connect】
//   这个命令的存在理由是"把网络还回去"。如果它还能反向拨号，就等于给了自动化
//   一条"失败→重拨→再失败→重拨"的死循环燃料 —— 而 RasMan 无条件重拨把用户网络
//   占一整天，是 run 25 量过的真实事故（单日 dial=413）。
//   ⇒ 兜底动作单向：只拆不建。要重连由人明确下达 connect，不是脚本自己决定。
//
// 【--duration 默认 15 分钟】自动化的常态是"跑着跑着就忘了"，而一个每 15 秒
//   探一次的循环一旦留下就是长期占机器。到点自己退出、返回健康，比无限期守着安全。
//   真要长期守着自己写 --duration 0。
//
// 【探测本体在 Probe.cs】和 doctor 同一把尺子，避免"guard 说通、doctor 说不通"。

using System.Text;

namespace ClashUWP.Cli;

internal static class Guard
{
    // ── 可调项（全部有默认值，最小用法只有一个目标地址）──────────────────
    private static int _probeTimeoutSec = 5;
    private static int _intervalSec = 15;
    private static int _failMax = 3;
    private static int _durationSec = 900;
    private static int _cmdTimeout = 60;
    private static int _expectStatus;
    private static bool _noProxy;
    private static string _method = "auto";
    private static string _then = "disconnect";
    private static int _portOverride;
    private static string? _pfn;

    public static async Task<int> RunAsync(string targetRaw, List<string> flags, string? pfn)
    {
        _pfn = pfn;

        string? err = ParseFlags(flags);
        if (err is not null)
        {
            Console.Error.WriteLine(err);
            Console.Error.WriteLine(HelpLine());
            return ExitCodes.Usage;
        }

        if (!Probe.TryParse(targetRaw, out Target t, out string? parseErr))
        {
            Console.Error.WriteLine($"guard 目标写法有问题：{parseErr}");
            Console.Error.WriteLine(HelpLine());
            return ExitCodes.Usage;
        }

        // 方式判定：目标带 scheme 就能走 http；只给 host[:port] 就只可能走 tcp。
        // --method 显式指定时以它为准，--port 覆盖目标里没写死的端口。
        if (_portOverride > 0 && !t.PortGiven) t = t with { Port = _portOverride };
        if (_method == "auto") _method = t.Scheme.Length > 0 ? "http" : "tcp";
        if (_method == "http" && t.Scheme.Length == 0)
            t = t with { Scheme = t.Port == 443 ? "https" : "http" };

        string probeDesc = _method == "http" ? $"http {t.Describe}" : $"tcp {t.Host}:{t.Port}";

        // 开局把这一轮口径全打出来：自动化只看这一行就知道在验什么、什么时候会触发。
        Console.WriteLine(
            $"[guard] 目标={probeDesc} 方式={_method} 单次超时={_probeTimeoutSec}s " +
            $"间隔={_intervalSec}s 连续失败阈值={_failMax} 总时长={(_durationSec == 0 ? "无限" : _durationSec + "s")} " +
            $"兜底动作={_then}");
        Console.WriteLine("[guard] 口径：本进程走系统正常路由探测（隧道在跑就经隧道），" +
                          "fake-ip 下 DNS 恒成功所以不拿解析当判据；只拆不建，Ctrl+C 随时退出。");

        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        int fails = 0, probes = 0;
        long slowestMs = 0;

        while (true)
        {
            probes++;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            (bool ok, string why) = _method == "http"
                ? await Probe.HttpAsync(t, _probeTimeoutSec, _expectStatus, _noProxy)
                : await Probe.TcpAsync(t, _probeTimeoutSec);
            sw.Stop();
            if (sw.ElapsedMilliseconds > slowestMs) slowestMs = sw.ElapsedMilliseconds;

            fails = ok ? 0 : fails + 1;
            // 单行、键值对形态：日志采集与 AI 都能直接 split 解析，不需要正则猜。
            Console.WriteLine($"[guard] #{probes} {DateTime.Now:HH:mm:ss} ok={(ok ? 1 : 0)} " +
                              $"{sw.ElapsedMilliseconds}ms 连续失败={fails}/{_failMax} {why}");

            if (fails >= _failMax)
            {
                Console.WriteLine($"[guard] 判定：连续 {fails} 次探不通 {probeDesc}" +
                                  $"（观察 {swTotal.Elapsed.TotalSeconds:F0}s）");
                return await FallbackAsync(probeDesc, fails);
            }

            if (_durationSec > 0 && swTotal.Elapsed.TotalSeconds >= _durationSec)
            {
                Console.WriteLine($"[guard] VERDICT=healthy 观察窗口 {_durationSec}s 跑完，" +
                                  $"共 {probes} 次探测全部可达（最慢 {slowestMs}ms）—— 隧道未被改动");
                return ExitCodes.Ok;
            }

            await Task.Delay(TimeSpan.FromSeconds(_intervalSec));
        }
    }

    /// <summary>触发兜底后的动作：默认下发 disconnect；--then none 只报不拆（干跑/取证用）。</summary>
    private static async Task<int> FallbackAsync(string probeDesc, int fails)
    {
        if (_then == "none")
        {
            Console.WriteLine("[guard] VERDICT=unreachable --then none：未动隧道（干跑）。" +
                              "要自动拆就去掉 --then none");
            return ExitCodes.GuardDryRun;
        }

        Console.WriteLine("[guard] 兜底动作：下发 disconnect（把网络还给物理网卡）…");
        AppServiceChannel.Reply r = await AppServiceChannel.SendAsync("disconnect", _pfn, _cmdTimeout);
        string text = OneLine(r.Text);

        if (r.Code != ExitCodes.Ok)
        {
            // ★ 整个 guard 里最需要人立刻看到的分支：判定不可达、但拆隧道本身也没做成
            //   （主应用没在跑 / 通道不通 / 装的版本没声明 AppService）。
            //   隧道可能还挂着 = 整机仍是黑的，而外部只知道"guard 退出了"。
            //   ⇒ 单独一个退出码 9，并把手工出口原话给出来，绝不能报成功。
            Console.Error.WriteLine($"[guard] VERDICT=fallback-failed disconnect 未成功（exit={r.Code}）：{text}");
            Console.Error.WriteLine("[guard] 隧道可能仍在。手工出口二选一：rasdial /disconnect <profile名>，" +
                                    "或 系统设置→VPN→断开；仍不通就按 Docs/16 清残留 NRPT 与死接口路由。");
            return ExitCodes.GuardActionFailed;
        }

        // 服务端回"找不到我们的 profile"也是好结果：没连着 = 没有需要拆的东西。
        bool nothingToDo = text.Contains("找不到我们的 profile", StringComparison.Ordinal);
        Console.WriteLine($"[guard] disconnect 回话：{text}");
        Console.WriteLine($"[guard] VERDICT=disconnected 连续 {fails} 次不可达后已拆隧道" +
                          (nothingToDo ? "（本来就没连着，无残留）" : "") + $" —— {probeDesc}");
        return ExitCodes.GuardDisconnected;
    }

    private static string OneLine(string? s) =>
        (s ?? "").Replace("\r", " ").Replace("\n", " | ").Trim();

    private static string HelpLine() =>
        "用法: clashuwp-cli guard <目标> [--method tcp|http|auto] [--port N] [--probe-timeout 5] " +
        "[--interval 15] [--fail-max 3] [--duration 900] [--then disconnect|none] " +
        "[--expect <状态码>] [--no-proxy] [--cmd-timeout 60]";

    private static string? ParseFlags(List<string> flags)
    {
        for (int i = 0; i < flags.Count; i++)
        {
            string f = flags[i];

            bool NeedInt(out int value)
            {
                value = 0;
                if (i + 1 >= flags.Count || !int.TryParse(flags[i + 1], out value)) return false;
                i++;
                return true;
            }

            switch (f)
            {
                case "--method":
                    if (i + 1 >= flags.Count) return "--method 需要 tcp / http / auto";
                    _method = flags[++i].ToLowerInvariant();
                    if (_method is not ("tcp" or "http" or "auto"))
                        return $"--method 只认 tcp/http/auto，收到 {_method}";
                    break;
                case "--port":
                    if (!NeedInt(out _portOverride) || _portOverride is < 1 or > 65535)
                        return "--port 需要 1..65535";
                    break;
                case "--probe-timeout":
                    if (!NeedInt(out _probeTimeoutSec) || _probeTimeoutSec <= 0)
                        return "--probe-timeout 需要正整数秒";
                    break;
                case "--interval":
                    if (!NeedInt(out _intervalSec) || _intervalSec <= 0)
                        return "--interval 需要正整数秒";
                    break;
                case "--fail-max":
                    if (!NeedInt(out _failMax) || _failMax <= 0)
                        return "--fail-max 需要正整数";
                    break;
                case "--duration":
                    // 允许 0 = 不限时长；负数仍然拒绝。
                    if (!NeedInt(out _durationSec) || _durationSec < 0)
                        return "--duration 需要 ≥0 的秒数（0 = 跑到被杀）";
                    break;
                case "--then":
                    if (i + 1 >= flags.Count) return "--then 需要 disconnect 或 none";
                    _then = flags[++i].ToLowerInvariant();
                    if (_then is not ("disconnect" or "none"))
                        return "--then 只认 disconnect / none（本工具不会 connect，兜底方向只有拆）";
                    break;
                case "--expect":
                    if (!NeedInt(out _expectStatus) || _expectStatus is < 0 or > 599)
                        return "--expect 需要 0（任何响应都算通）或 100..599 的状态码";
                    break;
                case "--no-proxy":
                    _noProxy = true;
                    break;
                case "--cmd-timeout":
                    if (!NeedInt(out _cmdTimeout) || _cmdTimeout <= 0)
                        return "--cmd-timeout 需要正整数秒";
                    break;
                default:
                    return $"guard 不认这个开关：{f}";
            }
        }
        return null;
    }
}
