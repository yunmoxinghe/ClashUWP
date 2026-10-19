// YAML 行式判据离线夹具（Docs/30）。
// 覆盖五组"不需要设备、不需要内核、不碰网络"的判据：
//   ① KernelConfig 的 url-test tolerance 锁死（块写法 / 行内写法 / 键位先后 / 无键补写）
//   ② SubscriptionView 的条目计数（节点数、规则数、组名）不再把嵌套列表数成条目
//   ③ MihomoController.ReadProxiesTable 的组/节点/内置策略分类（安卓 convertProxies 等价物）
//   ④ MihomoController.SplitSelectArg 的组名最长前缀匹配（组名带空格不再被拆错）
//   ⑤ KernelConfig.ReasonIfNoOutbound 的"空壳不拨号"判据（该拦的拦、能用的绝不拦）
// 用法：dotnet run --project Tools/YamlShapeTest
using System;
using System.Collections.Generic;
using System.Linq;

using ClashUWP.Backend.Mihomo;
using ClashUWP.Vpn;

internal static class Program
{
    private static int _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name +
                          (detail.Length > 0 ? "  " + detail : ""));
        if (!ok) _fail++;
    }

    private static int Main()
    {
        ToleranceBlockStyle();
        ToleranceOtherShapes();
        SubscriptionCounts();
        ProxyClassification();
        SelectArgSplit();
        OutboundGuard();

        Console.WriteLine(_fail == 0 ? "ALL PASS" : _fail + " FAIL");
        return _fail == 0 ? 0 : 1;
    }

    // ── ① tolerance 锁死 ────────────────────────────────────────────────

    /// <summary>把 proxy-groups 段单独拎出来跑 LockUrlTestTolerance，返回改后的行。</summary>
    private static List<string> LockGroups(string groupsYaml)
    {
        var lines = new List<string>(groupsYaml.Replace("\r\n", "\n").Split('\n'));
        KernelConfig.LockUrlTestTolerance(lines);
        return lines;
    }

    private static int CountToleranceLines(List<string> lines) =>
        lines.Count(l => l.Trim().StartsWith("tolerance:"));

    private static void ToleranceBlockStyle()
    {
        // 块写法：type 在前、成员列表在中、tolerance 在后 —— 1.0.74 及以前<b>整组被跳过</b>
        //（内层扫描在第一条成员行就 break），实测内核里仍是 tolerance: 30。
        string y =
            "proxy-groups:\n" +
            "  - name: 自动优选\n" +
            "    type: url-test\n" +
            "    proxies:\n" +
            "      - 🇺🇸 01\n" +
            "      - 🇺🇸 02\n" +
            "    tolerance: 30\n" +
            "    lazy: true\n" +
            "  - name: 备用\n" +
            "    type: fallback\n" +
            "    proxies:\n" +
            "      - 🇭🇰 01\n" +
            "    timeout: 5000\n";

        var lines = LockGroups(y);
        Check("块写法: 成员行之后的 tolerance 被锁死",
              lines.Any(l => l.Trim() == "tolerance: 999999"), Join(lines));
        Check("块写法: 没有重复键（tolerance 只有一行）",
              CountToleranceLines(lines) == 1, "count=" + CountToleranceLines(lines));
        Check("块写法: fallback 组不动（它语义就是挂了才换）",
              !lines.Any(l => l.Contains("timeout: 999999")));
        Check("块写法: 缩进原样保留（四空格）",
              lines.Any(l => l == "    tolerance: 999999"), Join(lines));

        // 两个块写法组：各自锁各自的，不能把第一个的 tolerance 改两遍 / 漏改第二个。
        string two =
            "proxy-groups:\n" +
            "  - name: A\n" +
            "    type: url-test\n" +
            "    proxies:\n" +
            "      - a1\n" +
            "    tolerance: 30\n" +
            "  - name: B\n" +
            "    type: url-test\n" +
            "    proxies:\n" +
            "      - b1\n" +
            "    tolerance: 50\n";
        var tl = LockGroups(two);
        Check("块写法: 两个 url-test 组都锁",
              CountToleranceLines(tl) == 2 &&
              tl.All(l => !l.Trim().StartsWith("tolerance: 3") && !l.Trim().StartsWith("tolerance: 5")),
              Join(tl));
    }

    private static void ToleranceOtherShapes()
    {
        // tolerance 写在 type <b>之前</b>：只能改写那一行，不能补第二行（yaml.v3 对
        // 同一映射里的重复键是直接报错的 —— 那就是"改坏 YAML"）。
        string before =
            "proxy-groups:\n" +
            "  - name: A\n" +
            "    tolerance: 30\n" +
            "    type: url-test\n" +
            "    proxies:\n" +
            "      - a1\n";
        var bl = LockGroups(before);
        Check("键序倒置: 改写已有行而不是补新行",
              CountToleranceLines(bl) == 1 &&
              bl.Any(l => l.Trim() == "tolerance: 999999"), Join(bl));

        // 完全没有 tolerance 键：mihomo 的 url-test 默认是 0（更快就切），必须补写。
        string none =
            "proxy-groups:\n" +
            "  - name: A\n" +
            "    type: url-test\n" +
            "    proxies:\n" +
            "      - a1\n" +
            "      - a2\n";
        var nl = LockGroups(none);
        Check("无 tolerance 键: 按本组缩进补写一行",
              CountToleranceLines(nl) == 1 && nl.Any(l => l == "    tolerance: 999999"), Join(nl));

        // 行内流式（订阅最常见写法）：原有行为不能退化。
        string flow =
            "proxy-groups:\n" +
            "  - { name: A, type: url-test, proxies: [a1, a2], tolerance: 30 }\n" +
            "  - { name: B, type: url-test, proxies: [b1] }\n";
        var fl = LockGroups(flow);
        Check("行内写法: 数字值被替换",
              fl.Any(l => l.Contains("tolerance: 999999 }") || l.Contains("tolerance: 999999,")), Join(fl));
        Check("行内写法: 无键时补在 } 之前",
              fl.Count(l => l.Contains("tolerance: 999999")) == 2, Join(fl));

        // 端到端：走 Transform（含 dns/tun 注入），tolerance 仍是锁死的，且 JSON 兜底不碰。
        string sub =
            "mode: rule\n" +
            "proxy-groups:\n" +
            "  - name: A\n" +
            "    type: url-test\n" +
            "    proxies:\n" +
            "      - a1\n" +
            "    tolerance: 30\n";
        string outText = KernelConfig.Transform(sub, null, null, 0, "",
            out string splitNote, out string dnsNote);
        Check("Transform 端到端: 块写法 tolerance 已锁",
              outText.Contains("tolerance: 999999") && !outText.Contains("tolerance: 30"),
              splitNote + " / " + dnsNote);
        Check("Transform: 兜底 JSON 不被行式解析器碰",
              KernelConfig.Transform("{\"mode\":\"rule\"}", null, null, 0, "",
                  out _, out _) == "{\"mode\":\"rule\"}");
    }

    // ── ② 订阅条目计数 ──────────────────────────────────────────────────

    private static void SubscriptionCounts()
    {
        // 节点内部带嵌套列表（client-ips）——旧判据把 2 个数成 4。
        string y =
            "proxies:\n" +
            "  - name: A\n" +
            "    type: vless\n" +
            "    server: 1.2.3.4\n" +
            "    client-ips:\n" +
            "      - 10.0.0.1\n" +
            "      - 10.0.0.2\n" +
            "  - name: B\n" +
            "    type: ss\n" +
            "    cipher: aes-256-gcm\n" +
            "rules:\n" +
            "  - DOMAIN-SUFFIX,a.com,DIRECT\n" +
            "  - MATCH,Proxy\n" +
            "proxy-groups:\n" +
            "  - name: 美国 01 直连\n" +
            "    type: select\n" +
            "    proxies:\n" +
            "      - A\n" +
            "      - B\n" +
            "  - { name: 自动, type: url-test, proxies: [A, B], tolerance: 30 }\n";

        var info = SubscriptionView.Parse(y, y.Length, DateTime.Now);
        Check("计数: 嵌套列表不算节点（2 个就是 2 个）", info.NodeCount == 2, "node=" + info.NodeCount);
        Check("计数: 协议分布不虚高",
              info.Protocols.Sum(p => p.Value) == 2 &&
              info.Protocols.Any(p => p.Key == "vless") &&
              info.Protocols.Any(p => p.Key == "ss"),
              string.Join(",", info.Protocols.Select(p => p.Key + "×" + p.Value)));
        Check("计数: 规则 2 条", info.RuleCount == 2, "rule=" + info.RuleCount);
        Check("计数: 组 2 个，组名里的空格原样保留",
              info.GroupCount == 2 && info.GroupNames.Contains("美国 01 直连"),
              string.Join(" | ", info.GroupNames));

        // 行内流式订阅（多数机场的形态）不能因为加了缩进判据而数不到。
        string flow =
            "proxies:\n" +
            "  - { name: a, type: ss, server: 1.1.1.1 }\n" +
            "  - { name: b, type: trojan, server: 2.2.2.2 }\n" +
            "rules:\n" +
            "  - MATCH,DIRECT\n";
        var f = SubscriptionView.Parse(flow, flow.Length, DateTime.Now);
        Check("计数: 行内写法仍是 2 节点 1 规则",
              f.NodeCount == 2 && f.RuleCount == 1, $"node={f.NodeCount} rule={f.RuleCount}");

        // 零缩进列表（`proxies:` 后直接 `- name:`，YAML 合法写法）
        string flat =
            "proxies:\n" +
            "- name: a\n" +
            "  type: ss\n" +
            "- name: b\n" +
            "  type: vless\n" +
            "proxy-groups:\n" +
            "- { name: G, type: select, proxies: [a, b] }\n" +
            "rules:\n" +
            "- MATCH,G\n";
        var p = SubscriptionView.Parse(flat, flat.Length, DateTime.Now);
        Check("计数: 零缩进列表也能数对", p.NodeCount == 2, "node=" + p.NodeCount);
        Check("计数: 零缩进时下一段顶层键仍能收尾（组 1、规则 1）",
              p.GroupCount == 1 && p.RuleCount == 1, $"group={p.GroupCount} rule={p.RuleCount}");
    }

    // ── ③ /proxies 分类 ────────────────────────────────────────────────

    private const string ProxiesJson = """
    {"proxies":{
      "GLOBAL":{"name":"GLOBAL","type":"Selector","now":"A","all":["A","B","DIRECT"]},
      "A":{"name":"A","type":"SS","history":[{"delay":123}]},
      "B":{"name":"B","type":"Trojan","history":[]},
      "嵌套组":{"name":"嵌套组","type":"Selector","now":"A","all":["A","B"]},
      "美国 01 直连":{"name":"美国 01 直连","type":"URLTest","now":"B",
                     "all":["A","B","嵌套组","DIRECT","自动优选"]},
      "自动优选":{"name":"自动优选","type":"URLTest","now":"A","all":["A","B"],"history":[]},
      "DIRECT":{"name":"DIRECT","type":"Direct","all":["DIRECT"],"history":[]},
      "REJECT":{"name":"REJECT","type":"Reject","all":["REJECT"]}
    }}
    """;

    private static void ProxyClassification()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(ProxiesJson);
        var (groups, delay, byName) = MihomoController.ReadProxiesTable(doc.RootElement);

        Check("分类: GLOBAL 与内置策略不进可切换组表",
              groups.All(g => g.Name is not ("GLOBAL" or "DIRECT" or "REJECT")),
              string.Join(" | ", groups.Select(g => g.Name)));

        Check("分类: 嵌套组被标成组（不是节点）",
              byName["嵌套组"].IsGroup && byName["嵌套组"].Type == "Selector",
              byName["嵌套组"].ToString());
        Check("分类: 真节点不是组、不是策略，类型是协议名",
              !byName["A"].IsGroup && !byName["A"].IsBuiltin && byName["A"].Type == "SS");
        Check("分类: DIRECT 是内置策略、不算组",
              byName["DIRECT"].IsBuiltin && !byName["DIRECT"].IsGroup &&
              byName["DIRECT"].Type == "Direct");
        Check("分类: 延迟只挂在自己条目上（B 没测过）",
              delay.ContainsKey("A") && delay["A"] == 123 && !delay.ContainsKey("B"),
              string.Join(",", delay.Select(kv => kv.Key + "=" + kv.Value)));

        var g = groups.First(x => x.Name == "美国 01 直连");
        Check("分类: 组表按原样带上空格组名", g.Type == "URLTest" && g.Now == "B");

        // KernelView 层（UI 直接吃的结构）
        var members = g.Members.Select((name, idx) => (name, idx)).ToList();
        Check("分类: 成员数与 all 一致", members.Count == 5, "n=" + members.Count);

        // 用 KernelView 的 Group 记录复核"真节点数"与"能不能手选"
        var vm = new List<KernelView.Member>();
        foreach (string name in g.Members)
        {
            byName.TryGetValue(name, out var i);
            vm.Add(new KernelView.Member(name, delay.TryGetValue(name, out long d) ? d : -1L,
                i?.Type ?? "", i?.IsGroup ?? false, i?.IsBuiltin ?? false, i?.Now ?? ""));
        }
        var kg = new KernelView.Group(g.Name, g.Type, g.Now, vm);
        Check("视图: 真节点数=2（A/B），组与策略不算", kg.RealNodeCount == 2, "real=" + kg.RealNodeCount);
        Check("视图: URLTest 组不可手选（安卓只在 Selector 才接点行动作）", !kg.IsUserSelectable);
        Check("视图: Selector 组可手选",
              new KernelView.Group("s", "Selector", "A", vm).IsUserSelectable);
    }

    // ── ④ select 参数拆分 ──────────────────────────────────────────────

    private static void SelectArgSplit()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(ProxiesJson);
        var groups = MihomoController.ReadProxiesTable(doc.RootElement).Groups;

        var (g1, n1) = MihomoController.SplitSelectArg("美国 01 直连 A", groups);
        Check("拆分: 组名带空格也能整名命中", g1 == "美国 01 直连" && n1 == "A", $"[{g1}] [{n1}]");

        var (g2, n2) = MihomoController.SplitSelectArg("美国 01 直连 嵌套组", groups);
        Check("拆分: 成员是嵌套组时也不误判组名", g2 == "美国 01 直连" && n2 == "嵌套组", $"[{g2}] [{n2}]");

        var (g3, n3) = MihomoController.SplitSelectArg("自动优选 A", groups);
        Check("拆分: 组名互为前缀时取最长（不误命中短名）", g3 == "自动优选" && n3 == "A", $"[{g3}] [{n3}]");

        var (g4, n4) = MihomoController.SplitSelectArg("A", groups);
        Check("拆分: 只给一段时当节点名、组名留空由调用方补", g4 == "" && n4 == "A", $"[{g4}] [{n4}]");

        var (g5, n5) = MihomoController.SplitSelectArg("不存在的组 A", groups);
        Check("拆分: 组名匹配不到才退回空格口径（报错才有的可抄）",
              g5 == "不存在的组" && n5 == "A", $"[{g5}] [{n5}]");

        // 旧口径在这里必然失败：第一个空格前是"美国"，而"美国"不是任何组名。
        var (g6, n6) = MihomoController.SplitSelectArg("美国 01 直连 B", groups);
        Check("拆分: 回归 —— 旧'第一个空格'口径会拆出半截组名",
              g6 == "美国 01 直连" && !groups.Any(x => x.Name == "美国"), $"[{g6}] [{n6}]");
    }

    // ── ⑤ 空壳不拨号：ReasonIfNoOutbound ───────────────────────────────

    /// <summary>拦（返回理由非空）时把理由原样带进检查输出，放行时给空串。</summary>
    private static void Blocked(string name, string config, bool shouldBlock)
    {
        string reason = KernelConfig.ReasonIfNoOutbound(config);
        Check(name + $"（应{(shouldBlock ? "拦" : "放行")}）",
              (reason.Length > 0) == shouldBlock,
              reason.Length > 0 ? "理由=" + reason : "放行");
    }

    private static void OutboundGuard()
    {
        // 插件内置兜底那份（ClashVpnPlugin.LoadMihomoConfig 的最后一支）—— 一个节点都没有。
        Blocked("兜底: 内置 JSON 兜底不算可用出站", """
            {
              "mode": "rule",
              "log-level": "info",
              "ipv6": false,
              "tun": { "enable": false, "stack": "gvisor" }
            }
            """, true);

        Blocked("兜底: JSON 里有 proxies 数组就放行",
            """{ "mode": "rule", "proxies": [ { "name": "A", "type": "ss" } ] }""", false);

        Blocked("兜底: 空配置", "", true);

        string twoNodes = """
            proxies:
              - name: A
                type: ss
                server: 1.2.3.4
              - name: B
                type: trojan
            proxy-groups:
              - name: 自动优选
                type: url-test
                proxies: [A, B]
            rules:
              - MATCH,自动优选
            """;
        Blocked("块写法: 两条命名节点就是可用出站", twoNodes, false);

        // 行内/锚点/!include：数不清条目 ≠ 没有条目。判据误杀 = 用户连不上网。
        Blocked("行内写法: flow 列表不能当空",
            """proxies: [{name: A, type: ss, server: 1.2.3.4}, {name: B, type: trojan}]""", false);
        Blocked("外部引入: !include 的节点由内核读文件",
            "proxies: !include proxies.yaml\nproxy-groups:\n  - name: g\n    type: select\n", false);
        Blocked("锚点写法: *ref 指向别处定义的节点",
            "proxies: *base\nx-base: &base [{name: A}]\n", false);

        // 只有 "-" 把 name 换行写：合法 YAML，条目在，就该放行。
        Blocked("块写法: 裸破折号（name 写在下一行）也算有条目",
            "proxies:\n  -\n    name: A\n    type: ss\n", false);

        Blocked("空壳: proxies 段是 []",
            "proxies: []\nproxy-groups:\n  - name: g\n    type: select\n    proxies: [DIRECT]\n", true);
        Blocked("空壳: proxies 段下面直接是下一个顶层键",
            "proxies:\nproxy-groups:\n  - name: g\n    type: select\n", true);
        Blocked("空壳: 根本没有 proxies 段",
            "mode: rule\ndns:\n  enhanced-mode: fake-ip\n", true);
        Blocked("空壳: 只有注释行的 proxies 段",
            "proxies:\n  # 这一份订阅把节点挪走了\nproxy-providers: {}\n", true);

        // proxy-providers 的节点由内核运行时自己拉，行数上看不见 ⇒ 放行，不能误杀。
        Blocked("providers: 块写法非空即放行",
            "proxy-providers:\n  p1:\n    type: http\n    url: https://x/y\n", false);
        Blocked("providers: 行内写法非空即放行",
            "proxy-providers: {p1: {type: http, url: https://x/y}}", false);

        // 判据本身出错时一律放行（catch 兜住）—— 这里用 null 走空串分支确认不抛。
        Check("健壮性: 判据不抛异常（null 也只当空配置拦下）",
              KernelConfig.ReasonIfNoOutbound(null!)!.Length > 0);

        // BOM 会让第一行键匹配不上；匹配不到 proxies 在这里等于拒绝连接 ⇒ 必须摘掉。
        Blocked("BOM: 首行带 BOM 的订阅不能被误杀", "\uFEFF" + twoNodes, false);

        // 端到端：Transform 改写后的订阅仍然放行（改写器不该把出站改掉）。
        string transformed = KernelConfig.Transform(twoNodes, null, null, 0, "",
            out _, out _);
        Blocked("端到端: Transform 之后照样放行", transformed, false);
    }

    private static string Join(List<string> lines) =>
        "[" + string.Join("⏎", lines.Where(l => l.Trim().Length > 0)) + "]";
}
