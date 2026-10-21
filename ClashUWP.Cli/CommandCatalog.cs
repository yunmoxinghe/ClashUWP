// 命令表 —— 本文件是 clashuwp-cli 里「有哪些命令、每条能不能随手发」的<b>唯一</b>答案。
//
// 【为什么要把它单独抽出来，而且一份表喂三个用途】
//   旧版只有一段写死在 Usage() 里的一行字符串，和主应用 DebugCommandLoop 的注释各说各话。
//   结果是：不知道命令的人（包括 AI）必须先去读 app 源码才敢下发，
//   而下发错了要到 app 的日志里才发现错了 —— 一次来回。
//   现在 --help / --commands json / 本地参数校验 / 危险命令闸门 <b>全部读这同一张表</b>：
//   加一条命令只需要在 Commands[] 里加一项，四处自动跟上，不会漂。
//
// 【危险分级是这张表的核心，不是注释】
//   read  = 纯读，任何时候都能发，不留副作用；
//   state = 改内核运行态或改订阅文件，不动隧道、不重装包，但会改变"下一次连接走哪个节点"；
//   tunnel= 会拨号/拆隧道/改系统 VPN profile —— 一旦失败就是整机断网。
//   ⇒ tunnel 级里除 disconnect 以外都要求 --force（见 NeedsForce）。
//     disconnect 刻意不拦：它是把网络还回去的<b>逃生口</b>，
//     给逃生口加闸门 = 断网时远程恢复也做不了，那正是 Docs/08 §10.10 付过的学费。
//
// 【口径必须与主应用一致】服务端 DebugCommandLoop.ExecuteAsync 才是命令的真身，
//   这张表只做"透传前的说明书 + 明显笔误的本地拦截"，<b>不</b>在服务端之外再造一套语义。

using System.Text;

namespace ClashUWP.Cli;

internal enum Danger
{
    /// <summary>纯读：不碰隧道、不改 profile、不改内核运行态。</summary>
    Read,
    /// <summary>改运行态/落文件：不碰隧道，但会留下影响下一次连接的状态。</summary>
    State,
    /// <summary>碰隧道或系统 VPN profile：失败形态是整机断网。</summary>
    Tunnel,
}

internal sealed record CommandInfo(
    string Name,
    string Args,
    Danger Danger,
    string Summary,
    string[]? Aliases = null);

internal static class CommandCatalog
{
    /// <summary>
    /// 与主应用 DebugCommandLoop 的命令表逐条对齐。
    /// 顺序即 --help 的分组顺序：读 → 改运行态 → 碰隧道。
    /// </summary>
    public static readonly CommandInfo[] Commands =
    [
        // ── 纯读 ────────────────────────────────────────────────────────
        new("status", "", Danger.Read,
            "内核运行态三要素：mode / 日志级别 / 出站绑卡"),
        new("groups", "", Danger.Read,
            "列出可切换的组与成员（含最近一次测速）—— 选节点前必看，组名是中文、节点名带 emoji，靠猜必错"),
        new("conf", "", Danger.Read,
            "内核当前生效的配置 dump"),
        new("controller", "", Danger.Read,
            "REST 控制器(127.0.0.1:9090)可达性探针"),
        new("logs", "", Danger.Read,
            "内核日志尾部 12 行"),
        new("logtail", "[app|plugin|sessions] [行数]", Danger.Read,
            "读滚动日志尾部：app=前台，plugin=vpnClient 后台任务(数据面现场)，sessions=会话事件流"),
        new("sub", "", Danger.Read,
            "当前生效订阅的元信息(节点/规则/组/协议)",
            ["subscription"]),
        new("sublist", "", Danger.Read,
            "列出全部订阅 profile，▶ = 当前生效"),
        new("profiles", "", Danger.Read,
            "列出本包名下的系统 VPN profile(名称/subId/是否进系统UI/状态)"),

        // ── 改运行态 ────────────────────────────────────────────────────
        new("delay", "[节点名]", Danger.State,
            "让内核立刻拨一次该节点并原样带回结果 —— 看「拨号死在哪一步」最短的路"),
        new("connections", "[kill <id>|kill-all]", Danger.State,
            "不带参数=列出活动连接(目标/链路/流量)；kill 强断指定连接或全部",
            ["conn"]),
        new("select", "<组名> <节点名>", Danger.State,
            "切换节点(只有一个组时可只写节点名)。大小写敏感；带空格的节点名照抄，第一个空格后全算节点名"),
        new("mode", "<rule|global|direct>", Danger.State,
            "运行时改分流模式，即时生效，不动 config.yaml、不重装包"),
        new("bind", "<网卡名|->", Danger.State,
            "运行时改出站绑定网卡(“bind -”=清空)。大小写敏感：wlan 会被判成找不到网卡"),
        new("reload", "[配置绝对路径]", Danger.State,
            "热重载内核配置(PUT /configs?force=true)。内核常驻 ⇒ 改了磁盘文件不 reload 就不生效且不报错"),
        new("subfetch", "<URL>", Danger.State,
            "按 URL 拉订阅、落成 profile 并设为生效。「不碰隧道」，但要 reload 才影响正在跑的内核",
            ["fetch"]),
        new("subupdate", "", Danger.State,
            "按当前生效 profile 的 URL 重拉一次",
            ["update"]),
        new("subdel", "<id>", Danger.State,
            "删一份 profile(不允许删当前生效的那条)"),

        // ── 碰隧道 ──────────────────────────────────────────────────────
        new("connect", "[订阅id]", Danger.Tunnel,
            "拨号：不带参=连基础 profile，带参=连该订阅专属系统 profile(等价于在快速设置里点那条订阅)。慢，可能几十秒"),
        // ★【1.0.86 · Docs/36 A 案】级别从 state 提到 tunnel，并挪进本分组：
        //   已连接时 subuse 不再只是"复制到 config.yaml"，它会先断旧订阅的 profile 会话、
        //   等收尾收干净，再拨新订阅那条 —— 失败形态和其它 tunnel 命令一样是整机断网。
        //   留在 state 里就是让自动化可以不带 --force 悄悄换隧道，正是 memory
        //   「Never hog the machine's network」要防的那类事。
        new("subuse", "<id>", Danger.Tunnel,
            "切到某份 profile：复制 config.yaml + 热重载 + 把隧道会话串行切到该订阅的 profile(已连接时会先断再拨)。先用 sublist 拿 id",
            ["subswitch"]),
        new("disconnect", "", Danger.Tunnel,
            "拆隧道、把网络还给物理网卡 —— 这是兜底逃生口，本 CLI 不要求 --force"),
        new("recreate", "", Danger.Tunnel,
            "删旧+新建主 VPN profile 并回读验证。会先走 GracefulTeardown 那道闸，收不干净就拒绝删除"),
        new("probe", "", Danger.Tunnel,
            "建临时 profile 逐臂试连(数据面取证)。会真的拨号，跑完自删 —— 别在网络敏感时刻发"),
    ];

    /// <summary>
    /// tunnel 级里<b>需要 --force</b> 的命令。
    /// disconnect 不在这里：它是恢复网络的出口，拦它等于把断网时的远程自救也锁死。
    /// ★【1.0.86】加 <c>subuse</c>：它现在会断旧会话再拨新会话（Docs/36 A 案），
    ///   和 connect 同属"发起拨号"，不能因为名字里带 sub 就当成改文件。
    /// </summary>
    private static readonly string[] ForcedCommands = ["connect", "recreate", "probe", "subuse"];

    public static bool NeedsForce(string name) => ForcedCommands.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>按主名或别名查命令；查不到返回 null。</summary>
    public static CommandInfo? Find(string name) =>
        Commands.FirstOrDefault(c =>
            c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            || (c.Aliases?.Contains(name, StringComparer.OrdinalIgnoreCase) ?? false));

    /// <summary>
    /// 取命令级别。<b>未登记的名字一律按 Tunnel 处理</b> —— 这张表漏一条时
    /// 宁可多要一次 --force，也不能把"没见过的命令"当成安全的默认放行。
    /// </summary>
    public static Danger LevelOf(string name) => Find(name)?.Danger ?? Danger.Tunnel;

    /// <summary>命令名的规范写法（别名归一到主名）。</summary>
    public static string Canonical(string name) => Find(name)?.Name ?? name.ToLowerInvariant();

    public static string DangerTag(Danger d) => d switch
    {
        Danger.Read => "read",
        Danger.State => "state",
        _ => "tunnel",
    };

    /// <summary>
    /// 本地参数校验 —— 只挡「服务端一定会拒绝、或语义一定歧义」的形态。
    ///
    /// 【为什么不把校验做厚】真身是主应用的 ExecuteAsync，它在缺参/组名歧义时
    ///   会回一句带下一步的原文（例如 select 缺参就让你先跑 groups）。
    ///   在这边再写一套等价判定，等于把服务端的语义抄第二遍 —— 两份迟早漂，
    ///   漂了之后 CLI 会<b>拦掉一条本来合法</b>的命令，那比不校验更糟。
    /// 【这里拦的三类】① 枚举值写错(mode/bind 的取值是固定集合)；
    ///   ② 必填入参整体缺失；③ 目标 URL 明显不是 URL。都是零歧义的笔误。
    /// </summary>
    public static string? Validate(string name, string arg)
    {
        string trimmed = arg.Trim();
        switch (Canonical(name))
        {
            case "mode":
                if (trimmed.Length == 0)
                    return "mode 需要参数：mode rule | mode global | mode direct";
                if (!new[] { "rule", "global", "direct" }.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                    return $"mode 只认 rule/global/direct，收到“{trimmed}”";
                return null;

            case "bind":
                if (trimmed.Length == 0)
                    return "bind 需要参数：bind <网卡名>（清空用 bind -）。网卡友好名区分大小写，先看 groups/系统设置里的确切名字";
                return null;

            case "select":
                if (trimmed.Length == 0)
                    return "select 需要参数：select <组名> <节点名>（只有一个组时可只写节点名）。先跑 groups 看确切名字";
                return null;

            case "logtail":
            {
                string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1 &&
                    !new[] { "app", "plugin", "sessions" }.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
                    return $"logtail 只认 app/plugin/sessions，收到“{parts[0]}”";
                if (parts.Length >= 2 &&
                    (!int.TryParse(parts[1], out int n) || n <= 0 || n > 400))
                    return $"logtail 行数需要 1..400 的整数，收到“{parts[1]}”";
                return null;
            }

            case "subfetch":
            case "fetch":
                if (trimmed.Length == 0)
                    return "subfetch 需要订阅 URL：subfetch https://...";
                if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return $"subfetch 需要带 scheme 的绝对 URL，收到“{trimmed}”";
                return null;

            case "subuse":
            case "subswitch":
            case "subdel":
                if (trimmed.Length == 0)
                    return $"需要先拿到 id：先跑 sublist，再 {Canonical(name)} <id>";
                return null;

            case "connections":
            case "conn":
                if (trimmed.StartsWith("kill", StringComparison.OrdinalIgnoreCase))
                {
                    string rest = trimmed.Substring(4).Trim();
                    if (rest.Length == 0)
                        return "connections kill 需要目标：kill <id> 或 kill-all（先不带参数跑一次 connections 看 id）";
                }
                return null;
        }
        return null;
    }

    /// <summary>
    /// --help 的正文。刻意排成"人能扫、机器也能按行拆"的形状：
    ///   每行固定 <c>级别|命令|参数|说明</c> 用竖线分隔 —— AI 直接 split 就能建命令表，
    ///   而竖线在人眼里只是列分隔，不伤阅读。
    /// </summary>
    public static string HelpText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("clashuwp-cli —— ClashUWP 的命令行遥控端（发给主应用的 AppService，命令语义与 debug-cmd 文件回路一致）");
        sb.AppendLine();
        sb.AppendLine("用法:");
        sb.AppendLine("  clashuwp-cli <命令> [参数...]              直接下发（命令见下表）");
        sb.AppendLine("  clashuwp-cli --commands --json           机器可读命令表（含危险分级），给脚本/AI 用");
        sb.AppendLine("  clashuwp-cli doctor                      体检：包在不在、通道通不通、内核答不答");
        sb.AppendLine("  clashuwp-cli guard <目标>                连通性兜底：连不上就自动拆隧道并退出");
        sb.AppendLine("  clashuwp-cli selftest                    只验本工具的 WinRT/AOT 通路，不碰主应用");
        sb.AppendLine("  clashuwp-cli list                        只打印目标包 PFN");
        sb.AppendLine();
        sb.AppendLine("开关:");
        // ★【1.0.89 · Docs/38 §十四 · F5 同族的第二处漂移】这一行原先把命令名<b>手抄</b>成
        //   "（connect/recreate/probe）"，而真正拦人的那张表是上面的 <see cref="ForcedCommands"/>
        //   （<c>:119</c>，1.0.86 起就多了一条 <c>subuse</c>）。
        //   ⇒ 装机读到的 help 教人"三条"，本地闸按四条拦：照 help 敲 <c>clashuwp-cli subuse <id></c>
        //     会拿到退出码 7（"tunnel 命令未加 --force"），而在 help 里查不到 subuse 属于哪一类 ——
        //     这正是本仓已经认过一次账的形状（1.0.84 对 <c>KernelView.Find</c> 的订正：两处各写一遍早晚分叉）。
        //     所以不补字面量，改成<b>由那张表生成这一行</b>，以后加命令不必再记得改说明。
        sb.AppendLine($"  --force            放行 tunnel 级命令（{string.Join("/", ForcedCommands)}）");
        sb.AppendLine("  --timeout <秒>     命令应答超时，默认 120（connect 拨号可能慢）");
        sb.AppendLine("  --pfn <PFN>        指定主应用包家族名，默认按包名反查");
        sb.AppendLine();
        sb.AppendLine("命令表（格式：级别 | 命令 | 参数 | 说明）:");
        sb.AppendLine("  级别: read=纯读随时可发 / state=改运行态或落文件 / tunnel=碰隧道，失败即整机断网");
        foreach (CommandInfo c in Commands)
            sb.AppendLine($"  {DangerTag(c.Danger),-6} | {c.Name,-12} | {(c.Args.Length == 0 ? "-" : c.Args),-24} | {c.Summary}" +
                          (c.Aliases is { Length: > 0 } a ? $"  (别名: {string.Join(",", a)})" : ""));
        sb.AppendLine();
        sb.AppendLine("退出码:");
        sb.AppendLine("  0=成功  2=连不上主应用  3=服务端返回错误  4=参数/包发现问题  5=命令超时");
        sb.AppendLine("  6=selftest 失败  7=tunnel 命令未加 --force  8=guard 触发且已拆隧道");
        sb.AppendLine("  9=guard 触发但拆隧道失败(隧道可能还在，需人工 disconnect)  10=guard 触发但 --then none 干跑");
        sb.AppendLine();
        sb.AppendLine("例子:");
        sb.AppendLine("  clashuwp-cli groups");
        sb.AppendLine("  clashuwp-cli select 美国组 \"  直连 1\"      # 空格/中文照抄，第一个空格后全算节点名");
        sb.AppendLine("  clashuwp-cli --force connect RyjBM4AAk7gx");
        sb.AppendLine("  clashuwp-cli logtail plugin 80");
        sb.AppendLine("  clashuwp-cli guard https://www.google.com/generate_204 --interval 10 --fail-max 3");
        return sb.ToString();
    }

    /// <summary>
    /// --commands --json：机器可读命令表。
    /// ★ 手写 JSON 而不用 System.Text.Json 序列化对象 —— 本工程是 Native AOT，
    ///   反射式序列化在 AOT 下要么裁剪报错要么运行时炸；这张表就一维数组，手写最稳。
    /// </summary>
    public static string JsonText()
    {
        var sb = new StringBuilder();
        sb.Append("{\"tool\":\"clashuwp-cli\",\"exitCodes\":")
          .Append("{\"ok\":0,\"appUnreachable\":2,\"serverError\":3,\"usage\":4,\"timeout\":5,")
          .Append("\"selftestFailed\":6,\"forceRequired\":7,\"guardDisconnected\":8,")
          .Append("\"guardActionFailed\":9,\"guardDryRun\":10},")
          .Append("\"commands\":[");
        bool first = true;
        foreach (CommandInfo c in Commands)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"name\":\"").Append(JsonStr(c.Name)).Append('"')
              .Append(",\"args\":\"").Append(JsonStr(c.Args)).Append('"')
              .Append(",\"danger\":\"").Append(DangerTag(c.Danger)).Append('"')
              .Append(",\"needsForce\":").Append(NeedsForce(c.Name) ? "true" : "false")
              .Append(",\"summary\":\"").Append(JsonStr(c.Summary)).Append('"')
              .Append(",\"aliases\":[");
            if (c.Aliases is { Length: > 0 })
                sb.Append(string.Join(",", c.Aliases.Select(a => "\"" + JsonStr(a) + "\"")));
            sb.Append("]}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    /// <summary>JSON 字符串转义：只处理 AOT 下这张表里真会出现的字符（含中文原样透传）。</summary>
    private static string JsonStr(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }
}
