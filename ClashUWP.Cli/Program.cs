// clashuwp-cli —— ClashUWP 的命令行遥控端。
//
// 【这个文件只做三件事】解析全局开关 → 决定走哪条路（元命令 / 透传）→ 汇总结果。
//   命令表在 CommandCatalog.cs，通道在 AppServiceChannel.cs，探测在 Probe.cs，
//   guard / doctor / selftest 各自成文件 —— Main 里不再留任何业务细节，
//   因为「一条命令该怎么用」这件事一旦分散在两个地方，说明书就会和行为漂。
//
// 【设计目标：拿过来就会用，不需要先读主应用源码】
//   1. 无参数即 --help，正文每行是 <c>级别|命令|参数|说明</c>，人和机器同一份；
//   2. <c>--commands --json</c> 给脚本/AI 一份机器可读表（含 needsForce），
//      不用猜哪条危险、也不用猜退出码；
//   3. 参数明显写错时<b>本地</b>就拒绝，并把「先跑哪条」说清楚，
//      省掉「发出去 → 等服务端报错 → 再回来读日志」这一整轮；
//   4. 碰隧道的三条（connect/recreate/probe）必须 --force；
//      disconnect 刻意不拦 —— 它是把网络还回去的逃生口，拦它等于自锁；
//   5. 每条失败都带「下一步」。找不到包 → 去跑 doctor；通道不通 → 走文件回路。
//
// 【通道】主应用清单声明的 in-app AppService「ClashUWP.Debug」。
//   in-app（无 EntryPoint）⇒ 连接激活的是主进程的 App.OnBackgroundActivated，
//   VpnSession / 健康哨兵 / DebugCommandLoop 的状态全部可及。
//
// 用法速查:
//   clashuwp-cli                              帮助（= --help）
//   clashuwp-cli doctor [--target <host|url>] 逐级体检：包 → 通路 → 通道 → 内核 → profile → 数据面
//   clashuwp-cli guard <目标>                 连通性兜底：连续连不上就自动 disconnect 并退出
//   clashuwp-cli --commands --json            机器可读命令表
//   clashuwp-cli help connect                 单条命令的口径（危险级 / 参数 / 例子）
//   clashuwp-cli status                       透传给主应用
//   clashuwp-cli --force connect              透传 + 放行碰隧道的命令
//   clashuwp-cli selftest                     只验本工具的 WinRT/AOT 通路，不碰主应用
//   clashuwp-cli list                         只打印目标包 PFN
//
// 开关（放在命令前后都认）:
//   --pfn <PFN>       指定主应用 PackageFamilyName，默认按 Identity Name 反查
//   --force           放行 tunnel 级命令
//   --timeout <秒>    透传应答超时（默认 120）与 doctor 读超时；guard 用 --cmd-timeout
//   --json            配合 --commands 输出机器可读表
//
// 退出码: 0 成功 · 2 连不上主应用 · 3 服务端报错 · 4 参数/包发现 · 5 超时
//         6 selftest 失败 · 7 tunnel 未加 --force · 8 guard 已拆 · 9 guard 拆失败 · 10 guard 干跑

namespace ClashUWP.Cli;

internal static class Program
{
    /// <summary>
    /// 手工常量而不是取程序集版本：本工程以 Native AOT 发布，
    /// <c>Assembly.GetName().Version</c> 在裁剪后的产物里不保证回得到 csproj 里那个数，
    /// 而 <c>version</c> 这条命令存在的意义正是「报出装的到底是哪版」，不能自己先不确定。
    /// </summary>
    private const string CliVersion = "1.0.2";

    /// <summary>透传的默认应答超时（秒）。connect 拨号可能几十秒，别把它掐死。</summary>
    private const int DefaultTimeoutSec = 120;

    /// <summary>
    /// guard / doctor 里「后面还跟一个值」的开关。
    /// Main 要先知道这些，才能把<b>位置参数</b>（guard 的目标）从开关值里分出来 ——
    /// 否则 <c>guard --method tcp 223.5.5.5</c> 会把 "tcp" 当成目标，
    /// 探一个根本不存在的域名，还一本正经地报告不可达。
    /// </summary>
    private static readonly string[] ValueFlags =
    [
        "--method", "--port", "--probe-timeout", "--interval", "--fail-max",
        "--duration", "--then", "--expect", "--cmd-timeout", "--target",
    ];

    private static async Task<int> Main(string[] args)
    {
        // 输出里有中文与节点名里的 emoji：不解码就设了 UTF-8 也白设，但这两行都很便宜。
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        try { Console.InputEncoding = System.Text.Encoding.UTF8; } catch { }

        string? pfn = null;
        bool force = false, json = false;
        int? timeoutOverride = null;
        var tail = new List<string>();     // 路由词 + 它自己的参数，保持原顺序

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pfn":
                    if (i + 1 >= args.Length) return UsageError("缺少 --pfn 的值");
                    pfn = args[++i];
                    break;
                case "--timeout":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out int t) || t <= 0)
                        return UsageError("--timeout 需要正整数秒");
                    timeoutOverride = t;
                    break;
                case "--force":
                case "-f":
                    force = true;
                    break;
                case "--json":
                    json = true;
                    break;
                default:
                    tail.Add(args[i]);
                    break;
            }
        }

        // 没有任何参数 = 新人/AI 的第一条命令，直接给他整份说明书，而不是 "缺少命令"。
        if (tail.Count == 0)
        {
            Console.WriteLine(CommandCatalog.HelpText());
            return ExitCodes.Ok;
        }

        string route = tail[0].ToLowerInvariant();
        List<string> rest = tail.GetRange(1, tail.Count - 1);

        switch (route)
        {
            // ── 元命令（不发到主应用，本进程自己就能答）──────────────────
            case "-h":
            case "--help":
            case "help":
                // help <命令> 只讲一条：AI 常常不是要全表，而是要确认这一条怎么发。
                Console.WriteLine(rest.Count > 0 ? Detail(rest[0]) : CommandCatalog.HelpText());
                return ExitCodes.Ok;

            case "--commands":
            case "commands":
            case "command-list":
                Console.WriteLine(json ? CommandCatalog.JsonText() : TableOnly());
                return ExitCodes.Ok;

            case "--version":
            case "version":
                Console.WriteLine($"clashuwp-cli {CliVersion}");
                return ExitCodes.Ok;

            case "--list":
            case "list":
            {
                AppServiceChannel.Package? pkg = AppServiceChannel.FindTargetPackage();
                if (pkg is null)
                    return Fail(ExitCodes.PackageNotFound,
                        $"找不到目标包（Name={AppServiceChannel.TargetPackageName}）—— ClashUWP 未安装",
                        "下一步：安装主包；装没装、版本对不对都让 doctor 一次说清（clashuwp-cli doctor --offline）");
                Console.WriteLine(pkg.Value.FamilyName);
                if (pfn is not null) Console.Error.WriteLine($"（注意：--pfn={pfn} 覆盖了反查结果）");
                Console.Error.WriteLine($"已装版本 v{pkg.Value.Version} · AppService={AppServiceChannel.ServiceName}");
                return ExitCodes.Ok;
            }

            case "--selftest":
            case "selftest":
                // 两种写法都收：走"应用执行别名"时敲进去的第一个词就是别名本身
                // （clashuwp-cli selftest），前面没有 -- 可给。
                return SelfTest.Run();

            case "doctor":
                return await Doctor.RunAsync(rest, pfn, timeoutOverride);

            case "guard":
            {
                // 目标 = 第一个不属于「开关值」的位置参数；其余原样交给 guard 自己判。
                string? target = null;
                var gflags = new List<string>();
                for (int i = 0; i < rest.Count; i++)
                {
                    string tk = rest[i];
                    if (ValueFlags.Contains(tk, StringComparer.OrdinalIgnoreCase))
                    {
                        gflags.Add(tk);
                        if (i + 1 < rest.Count) gflags.Add(rest[++i]);
                    }
                    else if (target is null && !tk.StartsWith("--")) target = tk;
                    else gflags.Add(tk);
                }
                if (target is null)
                {
                    Console.Error.WriteLine("guard 需要一个探测目标：host / host:port / https://host/path");
                    Console.Error.WriteLine("  例: clashuwp-cli guard https://www.google.com/generate_204 --expect 204");
                    Console.Error.WriteLine("      clashuwp-cli guard 223.5.5.5:443 --interval 10 --fail-max 3");
                    Console.Error.WriteLine("  判定不可达时默认下发 disconnect（只拆不建，不会自己重拨）；");
                    Console.Error.WriteLine("  想先看结论不动隧道就加 --then none。");
                    return ExitCodes.Usage;
                }
                return await Guard.RunAsync(target, gflags, pfn);
            }

            // ── 透传：发给主应用的命令表（DebugCommandLoop）──────────────
            default:
                return await PassThroughAsync(route, rest, pfn, force, timeoutOverride);
        }
    }

    /// <summary>
    /// 把一行命令发给主应用。这里集中了「发出去之前先拦一道」的全部理由：
    ///   · 危险闸门：tunnel 级（connect/recreate/probe）与<b>本表没登记的名字</b>都要 --force；
    ///   · 参数校验：只挡服务端一定会拒、或语义一定歧义的形态（详见 CommandCatalog.Validate 注释）。
    /// 未登记的名字按 Tunnel 处理是刻意的 fail-closed：主应用以后加了命令而这张表没跟上时，
    /// 宁可多要一次 --force，也不能把"没见过"当成"安全"。
    /// </summary>
    private static async Task<int> PassThroughAsync(
        string cmdName, List<string> parts, string? pfn, bool force, int? timeoutOverride)
    {
        string arg = string.Join(' ', parts);
        CommandInfo? info = CommandCatalog.Find(cmdName);

        if (info is null)
        {
            Console.Error.WriteLine(
                $"注意：「{cmdName}」不在本工具的命令表里 —— 可能是主应用版本比这张表新，也可能是拼错了。");
            Console.Error.WriteLine("  看已登记的命令：clashuwp-cli --commands --json");
        }
        else if (parts.Count > 0 && parts[0].StartsWith("--"))
        {
            // 只读命令的多余开关不拦，但透传会把 "--xxx" 原样拼进命令行送给内核，
            // 于是"参数写错"变成"内核收到一句怪命令"。这里先提示，省一轮取证。
            Console.Error.WriteLine($"注意：命令后面还有看起来像开关的东西（{parts[0]}），它会原样拼进命令行。");
        }

        bool requiresForce = info is null ||
            (info.Danger == Danger.Tunnel && CommandCatalog.NeedsForce(info.Name));
        if (requiresForce && !force)
        {
            string level = CommandCatalog.DangerTag(CommandCatalog.LevelOf(cmdName));
            Console.Error.WriteLine($"已被拦下：「{cmdName}」级别 = {level}，会拨号/拆隧道/改系统 VPN profile。");
            Console.Error.WriteLine("  这类命令的失败形态是整机断网，所以自动化必须显式确认：" +
                                    "在命令前加 --force");
            Console.Error.WriteLine("  发之前先看现场：clashuwp-cli doctor" +
                (cmdName.Contains("connect", StringComparison.OrdinalIgnoreCase)
                    ? "；看有哪些订阅可选：clashuwp-cli sublist" : ""));
            Console.Error.WriteLine("  例外：disconnect 不要求 --force —— 它是把网络还回去的逃生口。");
            return ExitCodes.ForceRequired;
        }

        string? invalid = CommandCatalog.Validate(cmdName, arg);
        if (invalid is not null)
        {
            Console.Error.WriteLine(invalid);
            Console.Error.WriteLine($"  完整口径：clashuwp-cli help {CommandCatalog.Canonical(cmdName)}");
            return ExitCodes.Usage;
        }

        AppServiceChannel.Reply r = await AppServiceChannel.SendAsync(
            arg.Length == 0 ? cmdName : $"{cmdName} {arg}", pfn, timeoutOverride ?? DefaultTimeoutSec);

        if (r.Code == ExitCodes.Ok)
        {
            Console.WriteLine(r.Text);
            return ExitCodes.Ok;
        }

        Console.Error.WriteLine(r.Text.Length == 0 ? "(服务端没回原因，也没回内容)" : r.Text);
        // 每种失败都给一条能直接抄的下一步 —— 这是"不用学也会用"的最后一环。
        string next = r.Code switch
        {
            ExitCodes.AppUnreachable =>
                "下一步：clashuwp-cli doctor（包在不在 / 版本对不对 / 清单有没有声明该服务）；" +
                "隧道正出问题时主应用可能没响应，改用文件回路写 LocalState\\debug-cmd.txt",
            ExitCodes.Timeout =>
                $"下一步：拨号/重连本来就慢，用 --timeout {Math.Max(300, (timeoutOverride ?? DefaultTimeoutSec) * 3)} 重试；" +
                "反复超时先 clashuwp-cli logtail plugin 60 看数据面现场",
            ExitCodes.PackageNotFound =>
                "下一步：clashuwp-cli doctor --offline 确认包的安装状态",
            _ => "下一步：上面的原文就是服务端的答复，按它写的来；拿不准就 clashuwp-cli help",
        };
        Console.Error.WriteLine(next);
        return r.Code;
    }

    /// <summary>
    /// 单条命令的说明（<c>help connect</c>）。
    /// 目的很具体：AI 拿到命令表之后，真正卡住它的永远是"这条要不要 --force、参数长什么样"，
    /// 与其让它去读主应用源码注释，不如把答案放在它已经会问的地方。
    /// </summary>
    private static string Detail(string name)
    {
        CommandInfo? c = CommandCatalog.Find(name);
        if (c is null)
            return $"「{name}」不在命令表里。\n主应用的命令真身在 DebugCommandLoop，" +
                   $"本工具这条表可能滞后 —— 先跑：clashuwp-cli --commands --json 看已登记了哪些。";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"命令：{c.Name}" + (c.Aliases is { Length: > 0 } a ? $"（别名 {string.Join(", ", a)}）" : ""));
        sb.AppendLine($"参数：{(c.Args.Length == 0 ? "无" : c.Args)}");
        sb.AppendLine($"级别：{CommandCatalog.DangerTag(c.Danger)}" +
                      $"（{c.Danger switch { Danger.Read => "纯读，任何时候可发"
                          , Danger.State => "改内核运行态或落订阅文件，不碰隧道，但会影响下一次连接"
                          , _ => "碰隧道或系统 VPN profile，失败形态是整机断网" }}）");
        sb.AppendLine($"需要 --force：{(CommandCatalog.NeedsForce(c.Name) ? "是" : "否")}");
        sb.Append("说明：").AppendLine(c.Summary);
        sb.AppendLine($"下发：clashuwp-cli {(CommandCatalog.NeedsForce(c.Name) ? "--force " : "")}{c.Name}" +
                      (c.Args.Length == 0 ? "" : $" {c.Args}"));
        sb.AppendLine("注：参数原样拼成一行命令送给主应用，所以带空格的值要加引号；" +
                      "节点名中间的空格保得住，首尾空格两端都会 Trim 掉。");
        return sb.ToString();
    }

    /// <summary>只要命令表那一段（<c>--commands</c> 不带 --json 时的形态，比整份 help 好切）。</summary>
    private static string TableOnly()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("级别 | 命令 | 参数 | 说明");
        sb.AppendLine("read=纯读 / state=改运行态或落文件 / tunnel=碰隧道，失败即整机断网");
        foreach (CommandInfo c in CommandCatalog.Commands)
            sb.AppendLine($"  {CommandCatalog.DangerTag(c.Danger),-6} | {c.Name,-12} | " +
                          $"{(c.Args.Length == 0 ? "-" : c.Args),-24} | {c.Summary}" +
                          (c.Aliases is { Length: > 0 } a ? $"  (别名: {string.Join(",", a)})" : ""));
        return sb.ToString();
    }

    private static int UsageError(string error)
    {
        Console.Error.WriteLine(error);
        Console.Error.WriteLine("用法: clashuwp-cli [开关] <命令> [参数...] —— 整份命令表与开关说明见 clashuwp-cli --help");
        return ExitCodes.Usage;
    }

    private static int Fail(int code, string message, string next)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(next);
        return code;
    }
}
