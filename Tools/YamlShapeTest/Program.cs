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
using System.Text.Json;

using ClashUWP.Backend;
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

        // ★【1.0.81】本轮新增的回归（M1–M4 / Q1–Q8）
        SubscriptionShapeProbe();
        ProviderView();
        RelaySelectable();
        ProviderNameFlow();

        // ★【1.0.81 · Q4】选择持久化存储的编解码往返（纯函数，离线可测）
        SelectionStoreRoundTrip();

        // ★【1.0.81 · M7/Q10】GET /providers/proxies 解析（纯函数，离线可测）
        ProviderTable();
        // ★【1.0.81 · M8/Q12】重载结果解析：核心拒收/异常必须判成失败，不能"落盘即成功"
        ReloadAcceptance();

        // ★【1.0.81 · Q11/Q13 收尾】同名/重名节点的检测与碰撞感知
        DuplicateNames();
        NameCollision();

        // ★【1.0.84】"选择节点这个组显示成一个节点" —— 一列混排的组行判定数据面
        MemberRows();

        // ★【1.0.84 → 1.0.93 重写】组测速（安卓那枚 ⚡）：路由从 PUT /proxies/…/healthcheck
        //   改成官方唯一的 GET /group/{组}/delay；编码判据留着，另加响应体解析
        GroupHealthCheckUrl();

        Console.WriteLine(_fail == 0 ? "ALL OFFLINE TESTS PASSED" : _fail + " FAIL");
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
        var (groups, delay, byName, _) = MihomoController.ReadProxiesTable(doc.RootElement);

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
                i?.Type ?? "", i?.IsGroup ?? false, i?.IsBuiltin ?? false, i?.Now ?? "",
                i?.ProviderName ?? ""));
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

    // ── ⑥ 订阅形态校验（M1 / Q1 / Q2）───────────────────────────────────

    private static void SubscriptionShapeProbe()
    {
        // 纯 proxy-providers 订阅（无顶层 proxies:）—— Q1/Q2 第二点：旧判据会整体拒收。
        string providersOnly =
            "proxy-providers:\n" +
            "  p1:\n" +
            "    type: http\n" +
            "    url: https://x/y\n" +
            "proxy-groups:\n" +
            "  - name: g\n" +
            "    type: select\n" +
            "    use: [p1]\n";
        var a = SubscriptionShape.ProbeShape(providersOnly);
        Check("形态: 纯 proxy-providers 订阅被接受（不误拒）", a.Ok, a.Error ?? "");

        // 标准带 proxies: 的订阅。
        string normal = "proxies:\n  - { name: a, type: ss }\n";
        Check("形态: 标准 proxies 订阅被接受", SubscriptionShape.ProbeShape(normal).Ok);

        // HTML 错误页（首字符 <）—— Q2 第三点：含 "proxies" 字眼的网关页旧判据会误收并覆盖缓存。
        string htmlPage =
            "<html><body><h1>403 Forbidden</h1><p>please login to view proxies</p></body></html>";
        var h = SubscriptionShape.ProbeShape(htmlPage);
        Check("形态: HTML 错误页被拒（首字符 <）", !h.Ok, h.Error ?? "");

        // <!DOCTYPE 形态。
        string docType = "<!DOCTYPE html><html><body>access denied, no proxies here</body></html>";
        Check("形态: <!DOCTYPE 页面被拒", !SubscriptionShape.ProbeShape(docType).Ok);

        // 正文夹带 <html 但不在开头 —— 仍应拒（错误页常见形态）。
        string midHtml = "HTTP/1.1 200 OK\n<html><body>proxies list</body></html>";
        Check("形态: 正文含 <html 被拒", !SubscriptionShape.ProbeShape(midHtml).Ok);

        // 没有顶层键的纯文本（既不是 YAML 订阅也不是 HTML）。
        string garbage = "this is just some random text without any clash keys";
        Check("形态: 无顶层键的纯文本被拒", !SubscriptionShape.ProbeShape(garbage).Ok);

        // 空内容。
        Check("形态: 空/空白内容被拒", !SubscriptionShape.ProbeShape("   ").Ok);

        // 注释里写了 proxies: 但不在行首 —— 不应误判为顶层键。
        string commentOnly = "# 说明：proxies: 写在注释里\nrules:\n  - MATCH,DIRECT\n";
        Check("形态: 仅注释里出现键名不算（无真实顶层键）",
              !SubscriptionShape.ProbeShape(commentOnly).Ok);
    }

    // ── ⑦ 提供方识别（M2 / Q3）─────────────────────────────────────────

    private static void ProviderView()
    {
        // inline 提供方 + payload 节点；顶层无 proxies: —— Q3：旧版数成 0 节点。
        string inline =
            "proxy-providers:\n" +
            "  p1:\n" +
            "    type: inline\n" +
            "    payload:\n" +
            "      - name: n1\n" +
            "        type: ss\n" +
            "        server: 1.1.1.1\n" +
            "      - name: n2\n" +
            "        type: trojan\n" +
            "        server: 2.2.2.2\n" +
            "proxy-groups:\n" +
            "  - name: g\n" +
            "    type: select\n" +
            "    use: [p1]\n";
        var i1 = SubscriptionView.Parse(inline, inline.Length, DateTime.Now);
        Check("提供方: inline 提供方被识别（ProviderCount=1）",
              i1.ProviderCount == 1, "prov=" + i1.ProviderCount);
        Check("提供方: inline 的 payload 节点被数到（InlineProviderNodes=2）",
              i1.InlineProviderNodes == 2, "inline=" + i1.InlineProviderNodes);
        Check("提供方: HasProviders 为真", i1.HasProviders);
        Check("提供方: 纯提供方订阅不再显示 0 节点（shown = 0+2 = 2）",
              (i1.NodeCount + i1.InlineProviderNodes) == 2,
              "shown=" + (i1.NodeCount + i1.InlineProviderNodes));

        // http 提供方：节点是内核运行时拉的，行式数不到 ⇒ ProviderCount=1 但 InlineProviderNodes=0。
        string http =
            "proxy-providers:\n" +
            "  p2:\n" +
            "    type: http\n" +
            "    url: https://x/y\n" +
            "    interval: 3600\n";
        var i2 = SubscriptionView.Parse(http, http.Length, DateTime.Now);
        Check("提供方: http 提供方被识别但数不到节点",
              i2.ProviderCount == 1 && i2.InlineProviderNodes == 0,
              $"prov={i2.ProviderCount} inline={i2.InlineProviderNodes}");

        // 普通订阅不应被误标为含提供方。
        string normal = "proxies:\n  - { name: a, type: ss }\n";
        var i3 = SubscriptionView.Parse(normal, normal.Length, DateTime.Now);
        Check("提供方: 普通订阅 ProviderCount=0 且 HasProviders 为假",
              i3.ProviderCount == 0 && !i3.HasProviders);

        // 列表写法（每项 - name:）也应被正确数到（映射写法是上面那条，两条互补）。
        string listForm =
            "proxy-providers:\n" +
            "  - name: lp1\n" +
            "    type: inline\n" +
            "    payload:\n" +
            "      - name: ln1\n" +
            "        type: ss\n" +
            "      - name: ln2\n" +
            "        type: ss\n" +
            "      - name: ln3\n" +
            "        type: ss\n";
        var i4 = SubscriptionView.Parse(listForm, listForm.Length, DateTime.Now);
        Check("提供方: 列表写法也被识别（ProviderCount=1, InlineProviderNodes=3）",
              i4.ProviderCount == 1 && i4.InlineProviderNodes == 3,
              $"prov={i4.ProviderCount} inline={i4.InlineProviderNodes}");
    }

    // ── ⑧ relay 组可手选（M3 / Q6 / Q7）────────────────────────────────

    private const string RelayJson = """
    {"proxies":{
      "A":{"name":"A","type":"SS","history":[]},
      "B":{"name":"B","type":"Trojan","history":[]},
      "中继":{"name":"中继","type":"Relay","now":"A","all":["A","B"]},
      "负载":{"name":"负载","type":"LoadBalance","now":"A","all":["A","B"],"history":[]}
    }}
    """;

    private static void RelaySelectable()
    {
        // Q6/Q7：relay 与 Selector 同属手动组，应可手选；LoadBalance 仍不可手选。
        var empty = new System.Collections.Generic.List<KernelView.Member>();
        Check("组: Relay 组可手选（驼峰）",
              new KernelView.Group("r", "Relay", "A", empty).IsUserSelectable);
        Check("组: relay 组可手选（配置写法）",
              new KernelView.Group("r", "relay", "A", empty).IsUserSelectable);
        Check("组: Selector 组可手选",
              new KernelView.Group("s", "Selector", "A", empty).IsUserSelectable);
        Check("组: URLTest 组不可手选（自动组）",
              !new KernelView.Group("u", "URLTest", "A", empty).IsUserSelectable);
        Check("组: LoadBalance 组不可手选（保持只读）",
              !new KernelView.Group("l", "LoadBalance", "A", empty).IsUserSelectable);

        // 底层 ReadProxiesTable 把 Relay 收进组表（IsSelectableGroupType 认 relay）；
        // LoadBalance 也仍收进组表，只是 UI 层 IsUserSelectable 判定其不可手选。
        using var doc = System.Text.Json.JsonDocument.Parse(RelayJson);
        var groups = MihomoController.ReadProxiesTable(doc.RootElement).Groups;
        Check("组: ReadProxiesTable 把 Relay 组收进组表",
              groups.Any(g => g.Name == "中继" && g.Type == "Relay"),
              string.Join(" | ", groups.Select(g => g.Name + ":" + g.Type)));
        Check("组: ReadProxiesTable 仍把 LoadBalance 收进组表（仅 UI 层不可手选）",
              groups.Any(g => g.Name == "负载" && g.Type == "LoadBalance"));
    }

    // ── ⑨ 提供方节点来源（M4 / Q8）─────────────────────────────────────

    private const string ProviderNameJson = """
    {"proxies":{
      "GLOBAL":{"name":"GLOBAL","type":"Selector","now":"直给节点","all":["直给节点","节点P1","DIRECT"]},
      "直给节点":{"name":"直给节点","type":"SS","history":[]},
      "节点P1":{"name":"节点P1","type":"Trojan","provider-name":"我的提供方","history":[]},
      "DIRECT":{"name":"DIRECT","type":"Direct","all":["DIRECT"]}
    }}
    """;

    private static void ProviderNameFlow()
    {
        // Q8：官方 GET /proxies 把提供方节点提升为顶层条目并附 provider-name。
        using var doc = System.Text.Json.JsonDocument.Parse(ProviderNameJson);
        var (_, _, byName, _) = MihomoController.ReadProxiesTable(doc.RootElement);

        Check("来源: 提供方节点带 provider-name",
              byName.TryGetValue("节点P1", out var p) && p.ProviderName == "我的提供方",
              byName.TryGetValue("节点P1", out var pp) ? pp.ProviderName ?? "(缺失)" : "(键缺失)");

        Check("来源: 订阅直给节点不带 provider-name",
              byName.TryGetValue("直给节点", out var d) && d.ProviderName.Length == 0);

        // 流经 KernelView.Member（UI 副标题依赖它）。
        Check("来源: provider-name 流经 KernelView.Member",
              MemberProviderName(byName, "节点P1") == "我的提供方");
    }

    private static string MemberProviderName(
        System.Collections.Generic.Dictionary<string, MihomoController.ProxyInfo> byName, string name)
    {
        byName.TryGetValue(name, out var i);
        var m = new KernelView.Member(name, -1L, i?.Type ?? "", i?.IsGroup ?? false,
            i?.IsBuiltin ?? false, i?.Now ?? "", i?.ProviderName ?? "");
        return m.ProviderName;
    }

    // ── ★【1.0.81 · Q4】选择持久化编解码往返 ─────────────────────────────

    private static void SelectionStoreRoundTrip()
    {
        // Q4：组名/节点名常带空格、中文、emoji，甚至字面量 "="，必须用 UrlEncode
        //   才能安全"按第一个 = 切"。这里钉死往返与非法行处理。
        (string g, string n)[] cases =
        {
            ("美国 01 直连", "香港节点 A"),
            ("组=名", "节点=名"),                       // 字面量 "=" 不能破坏切分
            ("中文组", "🚀 节点"),
            (" 首尾空格组 ", " 节点 "),
            ("", "空组被拒绝"),
        };
        foreach (var (g, n) in cases)
        {
            if (g.Length == 0)
            {
                var (dg, dn) = SelectionStore.DecodeLine(SelectionStore.EncodeLine(g, n));
                Check("选择编解码: 空组名编码后不可还原",
                      dg == null && dn == null);
                continue;
            }
            string line = SelectionStore.EncodeLine(g, n);
            var (rg, rn) = SelectionStore.DecodeLine(line);
            Check($"选择编解码: 往返一致 [{g}]",
                  rg == g && rn == n,
                  $"got=({rg ?? "null"},{rn ?? "null"})");
        }

        // 非法行：注释 / 空 / 无 "="。
        Check("选择编解码: 注释行被忽略", SelectionStore.DecodeLine("# 注释").Item1 == null);
        Check("选择编解码: 空行被忽略",   SelectionStore.DecodeLine("   ").Item1 == null);
        Check("选择编解码: 无 = 被忽略",  SelectionStore.DecodeLine("只有组名").Item1 == null);
    }

    // ── ★【1.0.81 · M7/Q10】GET /providers/proxies 解析 ───────────────────────

    private static void ProviderTable()
    {
        // ★ 官方 GET /providers/proxies 的真实形态：providers 是个对象，
        //   每个提供方有 vehicleType（Http/File/Inline）、proxies 数组（已加载节点）、
        //   updatedAt、以及仅 http 订阅才有的 subscriptionInfo（流量/到期）。
        string json = @"{
          ""providers"": {
            ""机场A"": {
              ""name"": ""机场A"",
              ""vehicleType"": ""Http"",
              ""proxies"": [ {""name"":""n1""}, {""name"":""n2""}, {""name"":""n3""} ],
              ""updatedAt"": ""2024-01-01T00:00:00Z"",
              ""subscriptionInfo"": { ""upload"": 100, ""download"": 200, ""total"": 1000, ""expire"": 1700000000000 }
            },
            ""本地B"": {
              ""name"": ""本地B"",
              ""vehicleType"": ""File"",
              ""proxies"": [ {""name"":""x""} ]
            }
          }
        }";
        using var doc = JsonDocument.Parse(json);
        var list = MihomoController.ReadProvidersTable(doc.RootElement);

        Check("提供方: 解析出 2 个提供方", list.Count == 2, $"got={list.Count}");
        var a = list.FirstOrDefault(p => p.Name == "机场A");
        var b = list.FirstOrDefault(p => p.Name == "本地B");
        Check("提供方: 机场A vehicleType=Http", a?.VehicleType == "Http");
        Check("提供方: 机场A 节点数=3", a?.NodeCount == 3, $"got={a?.NodeCount}");
        Check("提供方: 机场A updatedAt 已读", (a?.UpdatedAt?.Length ?? 0) > 0);
        Check("提供方: 机场A 订阅总量=1000", a?.SubTotal == 1000, $"got={a?.SubTotal}");
        Check("提供方: 机场A 订阅到期已读", a?.SubExpire == 1700000000000, $"got={a?.SubExpire}");
        Check("提供方: 本地B vehicleType=File", b?.VehicleType == "File");
        Check("提供方: 本地B 节点数=1", b?.NodeCount == 1, $"got={b?.NodeCount}");
        Check("提供方: 本地B 无订阅信息（total=0）", b?.SubTotal == 0);

        // 缺 providers 键 / 空对象 → 空列表，不抛。
        using var empty = JsonDocument.Parse("{ \"proxies\": {} }");
        var e = MihomoController.ReadProvidersTable(empty.RootElement);
        Check("提供方: 无 providers 段返回空列表", e.Count == 0);

        // 提供方缺 name 字段 → 兜底用对象 key。
        using var noname = JsonDocument.Parse("{ \"providers\": { \"兜底名\": { \"vehicleType\": \"Inline\", \"proxies\": [] } } }");
        var n = MihomoController.ReadProvidersTable(noname.RootElement);
        Check("提供方: 缺 name 时用对象 key 兜底", n.Count == 1 && n[0].Name == "兜底名");
    }

    // ── ★【1.0.81 · M8/Q12】重载结果解析：核心拒收/异常必须判成失败 ────────────

    private static void ReloadAcceptance()
    {
        // ★ Q12 的核心：RunReloadAsync 把内核对非法配置的拒收（非 2xx + 原因）拼进文本，
        //   这里据"重载成功"前缀判真值。只有真正的 2xx 才算成功，其余一律失败 ——
        //   否则"落盘即成功"会把核心拒绝伪装成"已拉取/已切"假成功。
        Check("重载接受度: 2xx 才算成功",
              MihomoController.IsReloadSuccess("重载成功（HTTP 200）：C:\\x\\config.kernel.yaml"));
        Check("重载接受度: 404 拒收算失败",
              !MihomoController.IsReloadSuccess("重载失败（HTTP 404）：path is not a absolute path"));
        Check("重载接受度: 400 解析错误算失败",
              !MihomoController.IsReloadSuccess("重载失败（HTTP 400）：parse error at line 3"));
        Check("重载接受度: 请求没发出去算失败",
              !MihomoController.IsReloadSuccess("reload 失败: System.Net.Http.HttpRequestException: 连接被拒绝"));
        Check("重载接受度: 缺参数算失败",
              !MihomoController.IsReloadSuccess("reload 需要参数：reload <config.kernel.yaml 的绝对路径>"));
        Check("重载接受度: null 算失败", !MihomoController.IsReloadSuccess(null!));
    }

    // ── ★【1.0.81 · Q13 收尾】订阅重名检测 ─────────────────────────────
    // 节点名 / 组名 / 内置策略保留名共享 mihomo 的扁平命名空间，重名会被内核折叠或
    // 报错且行为未定义。这里在"唯一能观察重名"的订阅解析层把重名抓出来。

    private static void DuplicateNames()
    {
        // 两个节点同名（proxies: 列表里出现两次）—— mihomo 未定义行为，必须报。
        string sameNode =
            "proxies:\n" +
            "  - name: A\n" +
            "    type: ss\n" +
            "    server: 1.1.1.1\n" +
            "  - name: A\n" +
            "    type: trojan\n" +
            "    server: 2.2.2.2\n" +
            "proxy-groups:\n" +
            "  - name: G\n" +
            "    type: select\n" +
            "    proxies: [A]\n";
        var d1 = SubscriptionView.Parse(sameNode, sameNode.Length, DateTime.Now);
        Check("重名: 两个同名节点被检出（DuplicateNames 含 A）",
              d1.HasDuplicateNames && d1.DuplicateNames.Contains("A"),
              string.Join(",", d1.DuplicateNames));

        // 节点名 == 组名：撞进同一个扁平命名空间，内核会折叠/报错。
        string nodeEqGroup =
            "proxies:\n" +
            "  - name: X\n" +
            "    type: ss\n" +
            "proxy-groups:\n" +
            "  - name: X\n" +
            "    type: select\n" +
            "    proxies: [X]\n";
        var d2 = SubscriptionView.Parse(nodeEqGroup, nodeEqGroup.Length, DateTime.Now);
        Check("重名: 节点名与组名相同被检出（含 X）",
              d2.HasDuplicateNames && d2.DuplicateNames.Contains("X"),
              string.Join(",", d2.DuplicateNames));

        // 节点名撞内置策略保留名（DIRECT）：同样是内核会折叠的冲突。
        string hitsBuiltin =
            "proxies:\n" +
            "  - name: DIRECT\n" +
            "    type: ss\n" +
            "proxy-groups:\n" +
            "  - name: g\n" +
            "    type: select\n" +
            "    proxies: [DIRECT]\n";
        var d3 = SubscriptionView.Parse(hitsBuiltin, hitsBuiltin.Length, DateTime.Now);
        Check("重名: 节点名撞内置策略保留名被检出（含 DIRECT）",
              d3.HasDuplicateNames && d3.DuplicateNames.Contains("DIRECT"),
              string.Join(",", d3.DuplicateNames));

        // 干净的订阅：节点名与组名都不重、也不撞保留名 → 无重名。
        string clean =
            "proxies:\n" +
            "  - name: a\n" +
            "    type: ss\n" +
            "  - name: b\n" +
            "    type: trojan\n" +
            "proxy-groups:\n" +
            "  - name: g\n" +
            "    type: select\n" +
            "    proxies: [a, b]\n";
        var c = SubscriptionView.Parse(clean, clean.Length, DateTime.Now);
        Check("重名: 干净订阅 DuplicateNames 为空",
              !c.HasDuplicateNames && c.DuplicateNames.Count == 0,
              string.Join(",", c.DuplicateNames));
    }

    // ── ★【1.0.81 · Q11 收尾】/proxies 同名碰撞感知 ──────────────────────
    // 扁平 ByName 字典一旦遇到同名条目会静默覆盖、元数据错位。这里确认：正常响应
    // Duplicates=0（碰撞感知已接线、且良构输入不触发），且"嵌套组 vs 真节点"的
    // 成员分类在组作用域内是稳的（这正是 Q11 残留的那条展示层风险）。

    private const string CollisionJson = """
    {"proxies":{
      "G":{"name":"G","type":"Selector","now":"A","all":["A","嵌套组","DIRECT"]},
      "A":{"name":"A","type":"SS","history":[{"delay":88}]},
      "嵌套组":{"name":"嵌套组","type":"Selector","now":"A","all":["A"]},
      "DIRECT":{"name":"DIRECT","type":"Direct","all":["DIRECT"]}
    }}
    """;

    private static void NameCollision()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(CollisionJson);
        var (groups, delay, byName, duplicates) = MihomoController.ReadProxiesTable(doc.RootElement);

        // 良构响应不应触发碰撞计数（证明碰撞感知已接线且正常输入为 0）。
        Check("碰撞: 良构 /proxies 响应 Duplicates=0",
              duplicates == 0, "duplicates=" + duplicates);

        // 关键分类稳健性：组 G 的成员同时含真节点(A)、嵌套组(嵌套组)、内置策略(DIRECT)，
        // 三者必须各自标对——不能因为扁平 ByName 而把嵌套组误标成节点、或把 DIRECT 当成节点。
        Check("碰撞: 嵌套组成员被正确标成组（不是节点）",
              byName["嵌套组"].IsGroup && !byName["嵌套组"].IsBuiltin,
              byName["嵌套组"].ToString());
        Check("碰撞: 真节点 A 不被误标成组/策略",
              !byName["A"].IsGroup && !byName["A"].IsBuiltin && byName["A"].Type == "SS");
        Check("碰撞: DIRECT 仍是内置策略", byName["DIRECT"].IsBuiltin);
        Check("碰撞: 延迟只挂在节点 A 上（嵌套组/策略无延迟）",
              delay.TryGetValue("A", out long d) && d == 88 && !delay.ContainsKey("嵌套组"),
              string.Join(",", delay.Select(kv => kv.Key + "=" + kv.Value)));

        // 流经 KernelView：组 G 的真节点数应为 1（A），嵌套组与 DIRECT 都不算节点。
        var vm = new List<KernelView.Member>();
        var g = groups.First(x => x.Name == "G");
        foreach (string name in g.Members)
        {
            byName.TryGetValue(name, out var i);
            vm.Add(new KernelView.Member(name, delay.TryGetValue(name, out long dd) ? dd : -1L,
                i?.Type ?? "", i?.IsGroup ?? false, i?.IsBuiltin ?? false, i?.Now ?? "",
                i?.ProviderName ?? ""));
        }
        var kg = new KernelView.Group(g.Name, g.Type, g.Now, vm);
        Check("碰撞: 组 G 真节点数=1（嵌套组/策略不计），分类稳健",
              kg.RealNodeCount == 1, "real=" + kg.RealNodeCount);
    }

    // ── ★【1.0.84】成员行的"组 vs 节点"判定（安卓版一列混排所需的数据面） ──
    // 症状：<c>🔰 选择节点</c> 这个<b>组</b>在成员区里是一行和真节点完全同款的按钮
    // （1.0.75 把分类算对了、但呈现层没变）⇒ 哥哥报的"选择节点还是显示成一个节点"。
    // 1.0.84 先按"三块分栏 + 进组按钮"做过一版，那是<b>自创</b>、不是安卓；已收回成
    // 安卓原形：顶部一排=组页签、下面一块=该组那一页（标题=组名）、<b>页内一列混排</b>，
    // 组行靠"加粗 + 灰字副标题"与节点行拉开层级。于是这里钉的是<b>那一列依赖的数据面</b>：
    //   ① 顺序必须还是内核给的 <c>all</c> 原序（分档/重排会把订阅作者的顺序弄丢）；
    //   ② 组行要能认出自己是组，并拿到<b>自己</b>的类型/现值/真节点数（副标题三样）；
    //   ③ 延迟只挂真节点，不挂组；
    //   ④ "能不能手选"全工程一份判据（Q7 分过叉）。
    // 夹具形状 = 装机实测那份 iKuuu_V2（📺 动画疯 = [🔰 选择节点, DIRECT]，
    // 🔰 选择节点 = 真节点 + DIRECT），节点数缩到 2 个好看清。

    private const string NestedJson = """
    {"proxies":{
      "🔰 选择节点":{"name":"🔰 选择节点","type":"Selector","now":"🇯🇵 日本1","all":["🇯🇵 日本1","🇯🇵 日本2","DIRECT"]},
      "📺 动画疯":{"name":"📺 动画疯","type":"Selector","now":"🔰 选择节点","all":["🔰 选择节点","DIRECT"]},
      "自动优选":{"name":"自动优选","type":"URLTest","now":"🇯🇵 日本1","all":["🇯🇵 日本1","DIRECT"]},
      "套壳自动":{"name":"套壳自动","type":"Selector","now":"自动优选","all":["自动优选","DIRECT"]},
      "🇯🇵 日本1":{"name":"🇯🇵 日本1","type":"Vmess","history":[{"delay":120}]},
      "🇯🇵 日本2":{"name":"🇯🇵 日本2","type":"Vmess"},
      "DIRECT":{"name":"DIRECT","type":"Direct","all":["DIRECT"]}
    }}
    """;

    private static void MemberRows()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(NestedJson);
        var (gs, delay, byName, dup) = MihomoController.ReadProxiesTable(doc.RootElement);
        var groups = KernelView.BuildGroups(gs, byName, delay);

        Check("行: 夹具本身无同名碰撞（判据不掺水）", dup == 0, "duplicates=" + dup);

        var anime = KernelView.Find(groups, "📺 动画疯");
        Check("行: 成员顺序 = 内核给的 all 原序（一列混排、不重排不分块）",
              anime is not null &&
              anime.Members[0].Name == "🔰 选择节点" && anime.Members[1].Name == "DIRECT",
              string.Join(" | ", anime!.Members.Select(m => m.Name)));

        var head = anime.Members[0];
        Check("行: 组行认得出自己是组，且自带类型与现值（副标题三样齐）",
              head.IsGroup && !head.IsBuiltin && head.Type == "Selector" && head.Now == "🇯🇵 日本1",
              head.ToString());
        var sub = KernelView.Find(groups, head.Name);
        Check("行: 组行副标题里的『真节点』取子组自己的计数（=2），不是父组的（=0）",
              sub is not null && sub.RealNodeCount == 2 && anime.RealNodeCount == 0,
              $"子组={sub?.RealNodeCount} 父组={anime.RealNodeCount}");
        Check("行: 组行自己没有延迟（延迟只挂真节点，安卓同一口径）",
              head.DelayMs == -1 && anime.Members[1].DelayMs == -1,
              $"组={head.DelayMs} DIRECT={anime.Members[1].DelayMs}");
        var selector = KernelView.Find(groups, "🔰 选择节点")!;
        Check("行: 真节点行带协议类型与延迟，未测的节点 delay=-1（UI 据此不画胶囊）",
              selector.Members[0].Type == "Vmess" && selector.Members[0].DelayMs == 120 &&
              selector.Members[1].DelayMs == -1,
              $"n1={selector.Members[0].DelayMs} n2={selector.Members[1].DelayMs}");
        Check("行: 内置策略单列一档（IsBuiltin，既不是组也不算真节点）",
              anime.Members[1].IsBuiltin && !anime.Members[1].IsGroup &&
              selector.RealNodeCount == 2 && selector.Members.Count == 3,
              $"DIRECT={anime.Members[1].IsBuiltin} real={selector.RealNodeCount}");

        // 能不能手选：全工程一份判据，组与"成员是不是可手选的子组"共用同一个函数。
        var auto = KernelView.Find(groups, "自动优选")!;
        var shell = KernelView.Find(groups, "套壳自动")!;
        Check("行: 手选判据同源 —— Selector 可、URLTest 不可；自动组的成员照样算子组",
              selector.IsUserSelectable && anime.IsUserSelectable &&
              !auto.IsUserSelectable && KernelView.IsUserSelectableType("Selector") &&
              !KernelView.IsUserSelectableType("URLTest") &&
              shell.Members[0].IsGroup && shell.Members[0].Name == "自动优选",
              $"select={selector.IsUserSelectable} urltest={auto.IsUserSelectable}");

        // Find 必须 Ordinal 原样比：组名带 emoji、带空格是订阅常态，任何规范化都会找不到组。
        Check("行: Find 原样匹配（多一个前导空格 / 去掉空格 都找不到）",
              sub != null && KernelView.Find(groups, " 🔰 选择节点") is null &&
              KernelView.Find(groups, "🔰选择节点") is null);
    }

    // ── ★【1.0.93】组测速（安卓那枚 ⚡）：路由拼装 + 响应解析 ──────────────────
    // 【为什么这轮重写这里】旧实现打的是 <c>PUT /proxies/{组}/healthcheck</c> —— 那是
    //   <b>clash premium 的老 API，本内核 mihomo 没有这条路由</b>（官方
    //   hub/route/proxies.go:26-33 里 /proxies/{name} 只挂 GET /、GET /delay、PUT /、DELETE /），
    //   点一次必吃 404 ⇒ 这就是"测速不可用"的本体。
    //   新路 = GET /group/{组}/delay?timeout=&url=（官方 hub/route/groups.go:21-26、69-93）。
    // 【能离线钉的两件事】① URL 拼装：组名编码、timeout 必填、组自己的 testUrl 优先并编码；
    //   ② 响应解析：官方成功体是裸 map[string]uint16 且<b>只含拨通的节点</b>
    //   （groupbase.go:245-251 仅在 err==nil 时写表），504 的错误体同为对象但值是字符串
    //   ⇒ 靠"值必须是数字"把两种形状一次分开。

    private static void GroupHealthCheckUrl()
    {
        const string Prefix = "http://127.0.0.1:9090/group/";

        string plain = MihomoController.GroupHealthCheckUrl("G", "", 8000);
        Check("测速: 路由形状 = /group/{组}/delay（不再是 /proxies/…/healthcheck）",
              plain.StartsWith(Prefix + "G/delay?") &&
              !plain.Contains("/proxies/") && !plain.Contains("healthcheck"), plain);

        Check("测速: timeout 原样进 query（官方 ParseInt 失败直接 400，少它等于这一枪没打出去）",
              plain.Contains("?timeout=8000&url="), plain);

        Check("测速: 组没配 testUrl 时用兜底地址（与单节点 delay 通道同一个地址，不另立标准）",
              plain.EndsWith("&url=" + System.Uri.EscapeDataString(MihomoController.DefaultGroupTestUrl)),
              plain);

        string emoji = MihomoController.GroupHealthCheckUrl("🔰 选择节点", "", 5000);
        Check("测速: emoji + 空格的组名按 EscapeDataString 原样编码（不多编、不漏编）",
              emoji.StartsWith(Prefix + System.Uri.EscapeDataString("🔰 选择节点") + "/delay"), emoji);

        // 关键防线：组名里的 "/" 必须被编码掉，否则路由被切成两段、打到别的接口上。
        string slashy = MihomoController.GroupHealthCheckUrl("加速/代理", "", 5000);
        Check("测速: 组名里的 / 被编码成 %2F（否则一条请求落在两段路由）",
              slashy.StartsWith(Prefix + "%E5%8A%A0%E9%80%9F%2F%E4%BB%A3%E7%90%86/delay"), slashy);

        string own = MihomoController.GroupHealthCheckUrl("G",
            "http://x.example.com/generate_204?a=1&b=2", 8000);
        Check("测速: 组自己配的 testUrl 优先用（不覆盖机场的设定）",
              own.EndsWith("&url=" + System.Uri.EscapeDataString(
                  "http://x.example.com/generate_204?a=1&b=2")), own);
        Check("测速: 带 & 的测速地址编码后只剩一个裸 &（query 不被截断）",
              own.LastIndexOf('&') == own.IndexOf("&url=", StringComparison.Ordinal), own);

        using var ok = JsonDocument.Parse("""{"🇯🇵 日本1":123,"🇭🇰 香港2":45}""");
        var dm = MihomoController.ReadGroupDelayMap(ok.RootElement);
        Check("测速解析: 裸对象按 名字→延迟 收下（节点名原样，不做加工）",
              dm.Count == 2 && dm["🇯🇵 日本1"] == 123 && dm["🇭🇰 香港2"] == 45,
              string.Join(",", dm.Select(kv => kv.Key + "=" + kv.Value)));

        using var err = JsonDocument.Parse("""{"error":"get delay: all proxies timeout"}""");
        Check("测速解析: 错误体不算任何节点（全组不通时官方回 504 + 字符串值，两种形状靠值类型分）",
              MihomoController.ReadGroupDelayMap(err.RootElement).Count == 0);

        using var mixed = JsonDocument.Parse("""{"活的":7,"死的":0,"没测过":null}""");
        var dm2 = MihomoController.ReadGroupDelayMap(mixed.RootElement);
        Check("测速解析: 数字值原样留下（0 是官方「本轮不通」的写法，渲染归上层），非数字跳过",
              dm2.Count == 2 && dm2["死的"] == 0 && dm2["活的"] == 7,
              string.Join(",", dm2.Select(kv => kv.Key + "=" + kv.Value)));

        using var arr = JsonDocument.Parse("[]");
        Check("测速解析: 不是对象（异常形状）给空表，不抛",
              MihomoController.ReadGroupDelayMap(arr.RootElement).Count == 0);
    }

    private static string Join(List<string> lines) =>
        "[" + string.Join("⏎", lines.Where(l => l.Trim().Length > 0)) + "]";
}
