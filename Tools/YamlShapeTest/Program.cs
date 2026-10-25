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

using Kind = ClashUWP.Backend.SelectionReplayPolicy.Kind;
using Stage = ClashUWP.Backend.ConfigApplyPolicy.ApplyStage;
using Act = ClashUWP.Backend.ConfigApplyPolicy.NextAction;

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

        // ★【1.0.118 · 配置视图】内核不可达时从 config.yaml 出组/节点/预设的整条判据
        ConfigViewShapes();
        ConfigViewFallback();
        ConfigViewPreset();
        ConfigViewKernelParity();

        // ★【当前批次】会话保险丝不再因固定运行时长拆健康隧道。
        SessionWatchdogTrips();
        // ★【当前批次】节点选择台账的回放判据（旧实现用一只 bool 闩，回放不到的那两条在这里钉死）。
        SelectionReplayPlan();
        // ★【当前批次】G6 P3：绑定网卡体检 —— "桥绑的卡已经断了"必须是动手的正证据。
        AdapterBoundHealth();
        // ★【当前批次】G7：路由表四桶的形状 —— v6 排除路由整条缺失、::/0 必须被拒。
        Ipv6RoutePlanShapes();
        // ★【当前批次】G4/G5：活动连接强断的三段纯判据 —— id 读得到、URL 会转义、
        //   日志等级只在认识的值上恢复（这一条错了会把用户的 warning 悄悄改成 info）。
        ConnectionKillShapes();
        LogLevelPreserveShapes();
        // ★【当前批次】G6 · 实时流量：累计字节 → 速率的差分判据（三条全零=没读到，
        //   计数器变小=不许取绝对值，首样=不许画 0 B/s）。
        TrafficRateShapes();
        // ★【当前批次】G6 · 代理页排序：默认必须是原序（作者排布），
        //   按延迟时"未测的 -1"绝不能排到最前、组与内置策略绝不当真节点参与序列。
        NodeSortShapes();
        // ★【当前批次】G6 · 订阅定时自动更新：默认关、到点就动手（1.0.122 对齐 Verge，
        //   不再有"会话中不动手"这一项）、失败必须退避。
        SubscriptionAutoShapes();
        // ★【当前批次】通知点击的落点：clashuwp://open / disconnect?n= 必须在被当成
        //   "不认识的协议"之前分流出去 —— 这一条错了就是"点了通知没反应"。
        ToastActionShapes();

        // ★【1.0.123 · 更新生效阶梯】"正文落盘之后怎么真的生效"的分支表。
        //   这一张表的两端都动过用户的网络（该重拨时不重拨=更新永远不生效；
        //   不该重拨时重拨=把还能用的隧道拆了），而中间那几档在装机上要么难复现
        //   要么代价是整机断网 ⇒ 只能在离线钉死。
        ConfigApplyLadderShapes();

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

    // ── ★【1.0.118 · 配置视图】ConfigNodeView：内核不可达时的节点浏览判据 ──────
    // 【为什么这组测试值得存在】本批的主修是把代理页的数据源从"只有内核 REST"
    //   扩成"REST 不可达时回退到磁盘订阅原文"。这条回退全是行式解析（flow/块/
    //   引号/注释/BOM/嵌套子列表），与 ②③⑤ 同属"不需要设备、不碰网络就能钉死"的
    //   判据 —— 而它一旦数错，界面上"预设选了谁"就会与下次回放的 PUT 内容分叉。

    /// <summary>两种节点写法共用的块形状预期：G=[日本 01(ss,1.2.3.4,8443), 自动, DIRECT, 未知]。</summary>
    private const string FlowConfig =
        "mode: rule\n" +
        "proxies:\n" +
        "  - { name: '日本 01', type: ss, server: 1.2.3.4, port: 8443, cipher: 'aes-256-gcm', password: 'a:1 #2' }\n" +
        "  - { name: 香港 02, type: trojan, server: 5.6.7.8, port: 443 }\n" +
        "proxy-groups:\n" +
        "  - { name: 节点选择, type: select, proxies: ['日本 01', 香港 02, DIRECT] }\n" +
        "  - { name: 自动, type: url-test, proxies: ['日本 01', 香港 02], url: 'http://x/g', interval: 60 }\n" +
        "  - { name: 门户, type: select, proxies: [节点选择, 自动, DIRECT] }\n" +
        "rules:\n" +
        "  - MATCH,节点选择\n";

    /// <summary>与 FlowConfig 同语义的块写法（含裸破折号条目与行尾注释）。</summary>
    private const string BlockConfig =
        "proxies:\n" +
        "  - name: '日本 01'\n" +
        "    type: ss\n" +
        "    server: 1.2.3.4\n" +
        "    port: 8443   # 端口带尾注\n" +
        "    client-ips:\n" +
        "      - 10.0.0.1\n" +
        "      - 10.0.0.2\n" +
        "  -\n" +
        "    name: 香港 02\n" +
        "    type: trojan\n" +
        "    server: 5.6.7.8\n" +
        "    port: 443\n" +
        "proxy-groups:\n" +
        "  - name: 节点选择\n" +
        "    type: select\n" +
        "    proxies:\n" +
        "      - '日本 01'\n" +
        "      - 香港 02\n" +
        "      - DIRECT\n" +
        "  - name: 自动\n" +
        "    type: url-test\n" +
        "    proxies: ['日本 01', 香港 02]\n";

    private static KernelView.Member? MemberOf(KernelView.Group? g, string name)
    {
        if (g == null) return null;
        foreach (var m in g.Members)
            if (m.Name == name) return m;
        return null;
    }

    private static void ConfigViewShapes()
    {
        // ── 行内流式 ──
        var v = ConfigNodeView.Parse(FlowConfig);
        Check("配置视图: flow 订阅解析出 3 个组",
              v.Found && v.Groups.Count == 3, $"found={v.Found} n=" + v.Groups.Count);

        var sel = KernelView.Find(v.Groups, "节点选择");
        var jp = MemberOf(sel, "日本 01");
        Check("配置视图: 带引号含空格的原样保真，server/port 详情字段带出（§4.1）",
              jp != null && !jp.IsGroup && !jp.IsBuiltin && jp.Type == "ss" &&
              jp.Server == "1.2.3.4" && jp.Port == "8443",
              jp?.ToString() ?? "(缺)");
        Check("配置视图: 引号里的 ': ' 与 '#' 不是分隔/注释（密码原样）",
              jp != null && KernelView.Find(v.Groups, "自动") != null &&
              MemberOf(KernelView.Find(v.Groups, "自动"), "日本 01")?.Type == "ss");
        Check("配置视图: 嵌套组成员标成组、拿到子组类型（不混排成节点）",
              MemberOf(KernelView.Find(v.Groups, "门户"), "节点选择") is { IsGroup: true } ng &&
              ng.Type == "select",
              MemberOf(KernelView.Find(v.Groups, "门户"), "节点选择")?.ToString() ?? "(缺)");
        Check("配置视图: DIRECT 标成内置策略、不算真节点",
              MemberOf(sel, "DIRECT") is { IsBuiltin: true } && sel!.RealNodeCount == 2,
              $"real={sel?.RealNodeCount}");

        // ── 台账只给手动组落现值 ──
        var led = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["节点选择"] = "日本 01",
            ["自动"] = "香港 02",        // 自动组：台账本不该有；就算有也不落 Now
            ["节点选择2"] = "x",          // 不存在的组：无害
        };
        var v2 = ConfigNodeView.Parse(FlowConfig, led);
        Check("配置视图: Selector 组现值=台账预设；自动组照旧无现值",
              KernelView.Find(v2.Groups, "节点选择")!.Now == "日本 01" &&
              KernelView.Find(v2.Groups, "自动")!.Now == "",
              $"sel={KernelView.Find(v2.Groups, "节点选择")?.Now} auto={KernelView.Find(v2.Groups, "自动")?.Now}");
        Check("配置视图: 嵌套组行也带自己的预设现值（副标题那一样）",
              MemberOf(KernelView.Find(v2.Groups, "门户"), "节点选择")?.Now == "日本 01");
        Check("配置视图: 台账点名不在成员表里 → Now 留空（不显示一个点不到的预设）",
              ConfigNodeView.Parse(FlowConfig, new Dictionary<string, string> { ["节点选择"] = "不存在的节点" })
                  .Groups.First(g => g.Name == "节点选择").Now == "");

        // ── 块写法：与 flow 同一套条目判据 ──
        var b = ConfigNodeView.Parse(BlockConfig);
        var bsel = KernelView.Find(b.Groups, "节点选择");
        Check("配置视图: 块写法解析出组与成员（裸破折号条目也收）",
              b.Found && b.Groups.Count == 2 && bsel != null && bsel.Members.Count == 3,
              $"n={b.Groups.Count} members={bsel?.Members.Count}");
        var bj = MemberOf(bsel, "日本 01");
        Check("配置视图: 块写法节点带 type/server/port，行尾注释被剥掉",
              bj != null && bj.Type == "ss" &&
              bj.Server == "1.2.3.4" && bj.Port == "8443",
              bj?.ToString() ?? "(缺)");
        Check("配置视图: 节点内部嵌套列表（client-ips）不当成员（1.0.75 同款纠偏）",
              bsel!.Members.All(m => m.Name != "10.0.0.1") && bsel.Members.Count == 3);
        Check("配置视图: 块写法的 url-test 组也拿得到成员",
              MemberOf(KernelView.Find(b.Groups, "自动"), "香港 02") is { Type: "trojan" });

        // ── 键名大小写（订阅里见过 Name:/Type:）──
        var caps = ConfigNodeView.Parse(
            "Proxies:\n  - { Name: A, Type: ss, Server: 9.9.9.9, Port: 8388 }\n" +
            "Proxy-Groups:\n  - { Name: G, Type: select, Proxies: [A] }\n");
        Check("配置视图: 键名忽略大小写，值原样（不做规范化）",
              MemberOf(KernelView.Find(caps.Groups, "G"), "A") is { Type: "ss", Server: "9.9.9.9" });
    }

    private static void ConfigViewFallback()
    {
        // 无组/仅提供方/兜底 JSON/空文件/BOM+CRLF —— 每种"列不出"都要有明确出口，绝不空白。
        var nodesOnly = ConfigNodeView.Parse(
            "proxies:\n  - { name: a, type: ss, server: 1.1.1.1, port: 443 }\n");
        Check("回退: 纯节点订阅如实报『有节点无组』，不猜组",
              nodesOnly.Found && nodesOnly.Groups.Count == 0 &&
              nodesOnly.Note.Contains("proxy-groups"), nodesOnly.Note);

        var provOnly = ConfigNodeView.Parse(
            "proxy-providers:\n  p1:\n    type: http\n    url: https://x/y\n");
        Check("回退: 纯提供方订阅如实报『需内核运行才展开』",
              provOnly.Found && provOnly.Groups.Count == 0 &&
              provOnly.Note.Contains("proxy-providers"), provOnly.Note);

        var json = ConfigNodeView.Parse("{\"mode\":\"rule\",\"proxies\":[]}");
        Check("回退: 内置兜底 JSON 不当 YAML 碰（Found=false + 人话理由）",
              !json.Found && json.Groups.Count == 0 && json.Error.Contains("JSON"), json.Error);

        Check("回退: 空内容 Found=false 不抛", !ConfigNodeView.Parse("").Found);

        var bom = ConfigNodeView.Parse("\uFEFF" + FlowConfig.Replace("\n", "\r\n"));
        Check("回退: BOM+CRLF 的订阅照样解析出 3 组", bom.Found && bom.Groups.Count == 3,
            $"found={bom.Found} n={bom.Groups.Count}");

        // use: 提供方 → 占位行 + Note，且占位行绝不该被当成可选节点（见 ConfigViewPreset）。
        var withUse = ConfigNodeView.Parse(
            "proxy-providers:\n  p1:\n    type: http\n    url: https://x/y\n" +
            "proxy-groups:\n  - { name: G, type: select, proxies: [DIRECT], use: [p1] }\n");
        var g1 = KernelView.Find(withUse.Groups, "G");
        Check("回退: use 的提供方成占位行（Type=Provider、带来源名）",
              MemberOf(g1, "p1") is { Type: "Provider" } pm && pm.ProviderName == "p1",
              MemberOf(g1, "p1")?.ToString() ?? "(缺)");
        Check("回退: 提供方占位有 Note 出口（不是静默多出一行）",
              withUse.Note.Contains("订阅源"), withUse.Note);
    }

    private static void ConfigViewPreset()
    {
        var groups = ConfigNodeView.Parse(FlowConfig).Groups;

        string ok = ConfigNodeView.RecordPreset(groups, "节点选择", "日本 01");
        Check("预设: 合法手动组+成员 → 文案是『记为预设/回放生效』，不是『已切换』",
              ok.Contains("预设选择") && ok.Contains("回放") && !ok.Contains("已切换"), ok);
        // ★ 离线环境下 SelectionStore.Record 的落盘被编译桩兜成"读写都失败"，
        //   RecordPreset 不抛即是预期（IO 失败不影响文案的校验分支 —— 与产品同 catch）。

        string miss = ConfigNodeView.RecordPreset(groups, "没有这个组", "日本 01");
        Check("预设: 组不存在被拒", miss.Contains("没有组") || miss.Contains("配置里没有"), miss);

        string auto = ConfigNodeView.RecordPreset(groups, "自动", "日本 01");
        Check("预设: 自动组不可手选（与实时视图同一口径）", auto.Contains("自动组"), auto);

        string ghost = ConfigNodeView.RecordPreset(groups, "节点选择", "不在成员表");
        Check("预设: 成员不在表里被拒（大小写敏感原样比）", ghost.Contains("成员表里没有"), ghost);

        var withUse = ConfigNodeView.Parse(
            "proxy-groups:\n  - { name: G, type: select, proxies: [DIRECT], use: [p1] }\n").Groups;
        string prov = ConfigNodeView.RecordPreset(withUse, "G", "p1");
        Check("预设: 提供方占位行被拒（回放会 PUT 一个内核不认的名字）",
              prov.Contains("占位行"), prov);

        string builtin = ConfigNodeView.RecordPreset(groups, "节点选择", "DIRECT");
        Check("预设: 内置策略允许（回放时内核认这些名字，与实时路径一致）",
              builtin.Contains("预设选择"), builtin);
    }

    // ── ★【1.0.118 对照官方】与内核 (mihomo Meta 分支) 解析语义的一致性判据 ──────
    // 每条都在 baseline/_mihomo 源码里对过位置（Docs/61 §七）：
    //   inline payload 内核真读（adapter/provider/parser.go NewInlineProvider）⇒ 视图必须列成节点；
    //   顶层键 go-yaml v3 大小写敏感、无 proxy 单数（config/config.go 的 yaml 标签）⇒ 警示；
    //   节点重名 = 硬失败（config.go:909）；include-all*/filter = 运行态展开（outboundgroup/parser.go:69-118）；
    //   提供方名 default = 保留名（config.go:934）。

    private static void ConfigViewKernelParity()
    {
        // ① inline 提供方（映射写法 + 块 payload）→ 节点表收全，成员成真节点
        var inlineMap = ConfigNodeView.Parse(
            "proxy-providers:\n" +
            "  p1:\n" +
            "    type: inline\n" +
            "    payload:\n" +
            "      - { name: n1, type: ss, server: 1.1.1.1, port: 8008 }\n" +
            "      - name: n2\n" +
            "        type: trojan\n" +
            "        server: 2.2.2.2\n" +
            "        port: 443\n" +
            "proxy-groups:\n" +
            "  - { name: G, type: select, proxies: [DIRECT], use: [p1] }\n");
        var g1 = KernelView.Find(inlineMap.Groups, "G");
        string g1m = g1 == null ? "(noG)"
            : string.Join("|", g1.Members.Select(mm => mm.Name + ":" + mm.Type + "@" + mm.Server));
        Check("内核对照: inline payload 节点收进表（n1/n2 成真节点带 server/port）",
              MemberOf(g1, "n1") is { IsGroup: false, IsBuiltin: false, Type: "ss", Server: "1.1.1.1", Port: "8008" } &&
              MemberOf(g1, "n2")?.Type == "trojan",
              "members=[" + g1m + "]");
        Check("内核对照: inline 提供方不再多出占位行（use 仍进 Note 说明）",
              MemberOf(g1, "p1") == null && inlineMap.Note.Contains("订阅源"), inlineMap.Note);

        // inline 提供方（列表写法 + 行内 payload）
        var inlineList = ConfigNodeView.Parse(
            "proxy-providers:\n" +
            "  - name: lp\n" +
            "    type: inline\n" +
            "    payload: [ { name: m1, type: vless, server: 3.3.3.3, port: 8443 } ]\n" +
            "proxy-groups:\n" +
            "  - { name: G, type: select, use: [lp] }\n");
        Check("内核对照: 列表写法 + 行内 payload 也收节点",
              MemberOf(KernelView.Find(inlineList.Groups, "G"), "m1") is { Type: "vless", Server: "3.3.3.3" },
              MemberOf(KernelView.Find(inlineList.Groups, "G"), "m1")?.ToString() ?? "(缺)");

        // ② 顶层键大小写：`Proxies:` 内核不加载 ⇒ 数据照样列（磁盘事实）+ 警示必须出口
        var capsWarn = ConfigNodeView.Parse(
            "Proxies:\n  - { name: a, type: ss, server: 1.1.1.1, port: 1 }\n" +
            "Proxy-Groups:\n  - { name: G, type: select, proxies: [a] }\n");
        Check("内核对照: 大写顶层键仍列数据且 Note 警示『内核不认』",
              capsWarn.Found && capsWarn.Groups.Count == 1 &&
              (capsWarn.Note.Contains("Proxies:") || capsWarn.Note.Contains("Proxies")), capsWarn.Note);

        // `proxy:` 单数（legacy 写法）：内核同样不认
        var singular = ConfigNodeView.Parse(
            "proxy:\n  - { name: a, type: ss }\nproxy-groups:\n  - { name: G, type: select, proxies: [a] }\n");
        Check("内核对照: proxy 单数键点名『不会被加载』，成员成『未知节点』不猜类型",
              singular.Note.Contains("proxy") && MemberOf(KernelView.Find(singular.Groups, "G"), "a") is { } ua &&
              !ua.IsGroup && !ua.IsBuiltin && ua.Type == "", singular.Note);

        // ③ 节点重名 = 内核硬失败 ⇒ 视图如实警示
        var dup = ConfigNodeView.Parse(
            "proxies:\n  - { name: A, type: ss, server: 1.1.1.1, port: 1 }\n" +
            "  - { name: A, type: trojan, server: 2.2.2.2, port: 2 }\n" +
            "proxy-groups:\n  - { name: G, type: select, proxies: [A] }\n");
        Check("内核对照: 节点重名按『内核起不来』警示（config.go:909 硬失败）",
              dup.Note.Contains("重名") && dup.Note.Contains("起不来"), dup.Note);

        // ④ include-all / filter：成员由内核运行态展开 ⇒ 视图声明"可能少于内核"
        var incl = ConfigNodeView.Parse(
            "proxies:\n  - { name: a, type: ss, server: 1.1.1.1, port: 1 }\n" +
            "proxy-groups:\n  - { name: G, type: select, include-all: true }\n");
        Check("内核对照: include-all 组给出『运行态展开』警示",
              incl.Groups.Count == 1 && incl.Note.Contains("运行态展开"), incl.Note);
        var filt = ConfigNodeView.Parse(
            "proxies:\n  - { name: a, type: ss, server: 1.1.1.1, port: 1 }\n" +
            "proxy-groups:\n  - { name: G, type: select, proxies: [a], filter: '香港' }\n");
        Check("内核对照: filter 组同样给『运行态展开』警示", filt.Note.Contains("运行态展开"), filt.Note);

        // ⑤ 提供方保留名 default（config.go:934 直接报错）
        var reserved = ConfigNodeView.Parse(
            "proxy-providers:\n  default:\n    type: http\n    url: https://x/y\n" +
            "proxy-groups:\n  - { name: G, type: select, proxies: [DIRECT], use: [default] }\n");
        Check("内核对照: 提供方名 default 的保留名警示",
              reserved.Note.Contains("default"), reserved.Note);
    }

    // ── ★ 会话保险丝：健康会话不得被固定时长拆掉 ────────────────────────
    private static void SessionWatchdogTrips()
    {
        Check("保险丝: 健康会话超过 30 分钟不触发",
              SessionWatchdogPolicy.EvaluateTrip(1801, 0, 0, 0) == null);
        Check("保险丝: 健康会话长时间运行仍不触发",
              SessionWatchdogPolicy.EvaluateTrip(86_400, 0, 0, 0) == null);
        Check("保险丝: 一轮探针抖动不触发",
              SessionWatchdogPolicy.EvaluateTrip(10, 1, 0, 0) == null);
        Check("保险丝: 连续两轮隧道探针失败触发",
              SessionWatchdogPolicy.EvaluateTrip(20, 2, 0, 0) != null);
        Check("保险丝: fake-ip 宽限期不判死",
              SessionWatchdogPolicy.EvaluateTrip(20, 0, 2, 0) == null);
        Check("保险丝: fake-ip 过宽限期连续两轮判死",
              SessionWatchdogPolicy.EvaluateTrip(31, 0, 2, 0) != null);
        Check("保险丝: 允许一次平台自愈重拨",
              SessionWatchdogPolicy.EvaluateTrip(10, 0, 0, 1) == null);
        Check("保险丝: 第二次非本次重拨触发",
              SessionWatchdogPolicy.EvaluateTrip(10, 0, 0, 2) != null);
    }

    // ── ★ 台账回放判据：内核现值 vs selection.txt ────────────────────────
    // 【这条为什么值得离线钉死】旧实现在 ReplaySelectionsAsync 里内联判据，两个缺陷
    //   都没被任何测试拦住：① 用一只 bool 闩当"已回放"，置位在读台账之前 ⇒ 内核起来后
    //   新写入的每条记录永不回放；② 忽略 PUT 的成败，失败也计入 applied。
    //   判据抽成 SelectionReplayPolicy 后，"什么算漂移"离线就能逐字钉住。
    private static void SelectionReplayPlan()
    {
        var ledger = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["美国 01"] = "🇺🇸 LA-1",          // 组名带空格、节点名带 emoji：订阅里的常态
            ["香港 02"] = "HK-2",
            ["自动优选"] = "JP-3",
            ["已消失的组"] = "X-9",
        };
        var groups = new List<KernelView.Group>
        {
            // 内核刚重启：手动组的 now 退回配置默认值（home 只读，cache.db 不跨重启）
            KGroup("美国 01", "Selector", "🇺🇸 NY-0", "🇺🇸 LA-1", "🇺🇸 NY-0"),
            KGroup("香港 02", "Selector", "HK-2", "HK-1", "HK-2"),   // 本就连着台账值
            KGroup("自动优选", "URLTest", "JP-3", "JP-3", "JP-4"),   // 自动组：不回放
            KGroup("直连中转", "Relay", "CN-1", "CN-1", "CN-2"),     // relay 是手动组
        };

        var plan = SelectionReplayPolicy.Plan(
            ledger, SelectionReplayPolicy.StatesFromKernelGroups(groups));

        Check("回放: 内核重启后 now 退回默认值 ⇒ 判为漂移，需要 PUT",
              plan.Single(i => i.Group == "美国 01").Decision == Kind.Put);
        Check("回放: 内核本就选着台账值 ⇒ AlreadySelected，一个 PUT 都不发",
              plan.Single(i => i.Group == "香港 02").Decision == Kind.AlreadySelected);
        Check("回放: 自动组（URLTest）不回放，即便台账值和 now 恰好相同",
              plan.Single(i => i.Group == "自动优选").Decision == Kind.SkipNotManual);
        Check("回放: 内核组表里没有这个组 ⇒ 跳过（订阅换了，不删台账）",
              plan.Single(i => i.Group == "已消失的组").Decision == Kind.SkipGroupMissing);
        Check("回放: 计划覆盖台账每一条，不多不少", plan.Count == ledger.Count, "count=" + plan.Count);
        Check("回放: 组名/节点名原样比对（Ordinal，emoji 与空格不参与规范化）",
              plan.Single(i => i.Group == "美国 01").Node == "🇺🇸 LA-1");

        // 节点不在成员表（订阅换了、组还在）→ 跳过而非 PUT：PUT 一个内核不认的名字是错的
        var moved = new Dictionary<string, string>(StringComparer.Ordinal) { ["美国 01"] = "🇺🇸 LA-9" };
        Check("回放: 节点已不在该组成员表 ⇒ SkipNodeMissing，不拿脏名字去 PUT",
              SelectionReplayPolicy.Plan(moved,
                  SelectionReplayPolicy.StatesFromKernelGroups(groups))
                  .Single().Decision == Kind.SkipNodeMissing);
        // 大小写敏感的同一判据（与实时切换口径一致）
        var cased = new Dictionary<string, string>(StringComparer.Ordinal) { ["美国 01"] = "🇺🇸 la-1" };
        Check("回放: 节点名区分大小写（与 SelectCoreAsync 同一口径）",
              SelectionReplayPolicy.Plan(cased,
                  SelectionReplayPolicy.StatesFromKernelGroups(groups))
                  .Single().Decision == Kind.SkipNodeMissing);
        // relay 与 Selector 同为手动组（1.0.81 M3 那条口径）
        var relayLedger = new Dictionary<string, string>(StringComparer.Ordinal) { ["直连中转"] = "CN-2" };
        Check("回放: Relay 组算手动组，漂移后照样 PUT",
              SelectionReplayPolicy.Plan(relayLedger,
                  SelectionReplayPolicy.StatesFromKernelGroups(groups))
                  .Single().Decision == Kind.Put);
        // 成员表里的嵌套组/内置策略是合法目标（内核认这些名字），不该判成"节点不存在"
        var nested = new List<KernelView.Group>
        {
            KGroup("套娃", "Selector", "🇺🇸 LA-1", "内层组", "DIRECT", "🇺🇸 LA-1"),
        };
        var nestedLedger = new Dictionary<string, string>(StringComparer.Ordinal) { ["套娃"] = "内层组" };
        Check("回放: 成员是嵌套组名也照常回放（PUT 组名对内核合法）",
              SelectionReplayPolicy.Plan(nestedLedger,
                  SelectionReplayPolicy.StatesFromKernelGroups(nested))
                  .Single().Decision == Kind.Put);

        // ── 台账快照：驱动"这一版试过没有"的比对，必须与字典枚举顺序无关 ──
        var a = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["美国 01"] = "🇺🇸 LA-1", ["香港 02"] = "HK-2",
        };
        var b = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["香港 02"] = "HK-2", ["美国 01"] = "🇺🇸 LA-1",   // 同内容、插入顺序相反
        };
        Check("回放: 快照与字典枚举顺序无关（否则'试过没有'永远比不中）",
              SelectionReplayPolicy.Snapshot(a) == SelectionReplayPolicy.Snapshot(b));
        b["美国 01"] = "🇺🇸 NY-0";
        Check("回放: 台账内容一变，快照就变（冷却判据跟着失效才重试）",
              SelectionReplayPolicy.Snapshot(a) != SelectionReplayPolicy.Snapshot(b));
        Check("回放: 空台账快照是空串、计划是空表",
              SelectionReplayPolicy.Snapshot(new Dictionary<string, string>()).Length == 0 &&
              SelectionReplayPolicy.Plan(new Dictionary<string, string>(),
                  SelectionReplayPolicy.StatesFromKernelGroups(groups)).Count == 0);
        // 快照的编码与落盘同一份（EncodeLine），组名里的 = 与空格不会把比对搞乱
        var tricky = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["A=B 组"] = "节点=1",
        };
        string trickyLine = SelectionReplayPolicy.Snapshot(tricky).TrimEnd('\n');
        var back = SelectionStore.DecodeLine(trickyLine);
        Check("回放: 组名/节点名含 = 时快照仍可解（与 selection.txt 同一编码）",
              back.Group == "A=B 组" && back.Node == "节点=1", trickyLine);
    }

    private static KernelView.Group KGroup(
        string name, string type, string now, params string[] members)
    {
        var ms = new List<KernelView.Member>();
        foreach (string m in members)
            ms.Add(new KernelView.Member(m, -1L, "", false, false, "", ""));
        return new KernelView.Group(name, type, now, ms);
    }

    // ── ★ G6 P3：绑定网卡体检（常驻桥复用那条假信号）─────────────────────
    // 【钉住什么】Docs/32 §四 P3 的实录形态：bound_iface=Meta（别的程序留下的 wintun，
    //   已经 Disconnected），出口早回到 WLAN，而旧判据只看 WinRT 的 profile 枚举 ⇒
    //   报"指纹=未变"，连"可疑"都没说出来。判据现在分两半：有正证据的坏（断了/没 IP/没绑）
    //   才动手，不确定的（名字找不到、落在隧道上）只报 —— 这条分工比"能不能重绑"更重要。
    private static void AdapterBoundHealth()
    {
        var table = new List<AdapterHealth.Adapter>
        {
            Adp("WLAN", "Intel(R) Wi-Fi 6 AX201", "Up", "Wireless80211", true),
            Adp("Meta", "Wintun Userspace Tunnel", "Up", "Ethernet", true),
            Adp("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", "Up", "Ethernet", true),
            Adp("蓝牙网络连接", "Bluetooth Device Pane Adapter", "Down", "Ethernet", false),
            Adp("以太网 3", "Realtek USB GbE Family Controller", "Up", "Ethernet", false),
        };

        Check("P3: 绑定的卡 Up 且有 IPv4 又是物理卡 ⇒ 正常，不动手",
              AdapterHealth.Evaluate("WLAN", table) == AdapterHealth.Bound.Ok &&
              !AdapterHealth.ShouldRebind(AdapterHealth.Bound.Ok));
        Check("P3: 绑定的卡断开（Meta 那次就是这个形态）⇒ 动手重绑",
              AdapterHealth.Evaluate("Meta", DownMeta(table)) == AdapterHealth.Bound.NotUp &&
              AdapterHealth.ShouldRebind(AdapterHealth.Bound.NotUp));
        Check("P3: Up 但没有 IPv4 ⇒ 拨出去必然不通，动手",
              AdapterHealth.Evaluate("以太网 3", table) == AdapterHealth.Bound.NoIpv4 &&
              AdapterHealth.ShouldRebind(AdapterHealth.Bound.NoIpv4));
        Check("P3: 桥回话里没有绑定名 ⇒ 本来就没绑，动手",
              AdapterHealth.Evaluate("", table) == AdapterHealth.Bound.None &&
              AdapterHealth.ShouldRebind(AdapterHealth.Bound.None));
        Check("P3: 名字在表里找不到 ⇒ 只报不猜、不动手（可能只是命名口径不一致）",
              AdapterHealth.Evaluate("不存在的卡", table) == AdapterHealth.Bound.Missing &&
              !AdapterHealth.ShouldRebind(AdapterHealth.Bound.Missing));
        Check("P3: 空表（采集失败）也归到『找不到』那一档，绝不据它以动手",
              AdapterHealth.Evaluate("WLAN", new List<AdapterHealth.Adapter>())
                  == AdapterHealth.Bound.Missing);
        Check("P3: 绑在隧道设备上（wintun 的 Meta）⇒ 只报，重跑探测也挑不出它",
              AdapterHealth.Evaluate("Meta", table) == AdapterHealth.Bound.Tunnelish &&
              !AdapterHealth.ShouldRebind(AdapterHealth.Bound.Tunnelish));
        Check("P3: Hyper-V 虚拟网卡同判『不像物理出口』（描述里的 hyper-v 命中）",
              AdapterHealth.Evaluate("vEthernet (Default Switch)", table) == AdapterHealth.Bound.Tunnelish);
        Check("P3: 名字忽略大小写能对上（Go 与 .NET 都取友好名，大小写差不能判成『找不到』)",
              AdapterHealth.Evaluate("wlan", table) == AdapterHealth.Bound.Ok);
        Check("P3: 名字对不上时用驱动描述兜一层（bound 写成描述也不算找不到）",
              AdapterHealth.Evaluate("Intel(R) Wi-Fi 6 AX201", table) == AdapterHealth.Bound.Ok);

        // 指纹那一段：链路状态必须改指纹，否则"卡掉了"在前后两次读数里长得一样
        string f1 = AdapterHealth.Format(table);
        var shuffled = new List<AdapterHealth.Adapter>(table);
        shuffled.Reverse();
        Check("P3: 指纹里的适配器串与枚举顺序无关（比的是内容不是顺序）",
              f1 == AdapterHealth.Format(shuffled), f1);
        Check("P3: 指纹带状态与有无 IPv4（Down / 没 IP 都必须改串）",
              f1.Contains("WLAN:Up+4") && f1.Contains("蓝牙网络连接:Down-4") &&
              f1.Contains("以太网 3:Up-4"));
        Check("P3: 卡掉了 ⇒ 指纹变（旧判据漏掉的就是这一步）",
              AdapterHealth.Format(DownMeta(table)) != f1);
        Check("P3: 采集失败（空表）在指纹里是可见的 sys=?，不是空段",
              AdapterHealth.Format(new List<AdapterHealth.Adapter>()) == "sys=?");
        var many = new List<AdapterHealth.Adapter>();
        for (int i = 0; i < 30; i++) many.Add(Adp($"卡{i}", "", "Up", "Ethernet", true));
        Check("P3: 指纹截断要说出来（+N 条没列），不能静默少几张卡",
              AdapterHealth.Format(many).Contains("…(+" ), AdapterHealth.Format(many));
    }

    /// <summary>把表里的 Meta 改成断开（Docs/32 P3 那次实录的形态）。</summary>
    private static List<AdapterHealth.Adapter> DownMeta(List<AdapterHealth.Adapter> table)
    {
        var copy = new List<AdapterHealth.Adapter>(table);
        for (int i = 0; i < copy.Count; i++)
            if (copy[i].Name == "Meta")
                copy[i] = Adp("Meta", "Wintun Userspace Tunnel", "Down", "Ethernet", false);
        return copy;
    }

    private static AdapterHealth.Adapter Adp(
        string name, string desc, string status, string type, bool hasIpv4) =>
        new(name, desc, status, type, hasIpv4);

    // ── ★ G7：路由表四桶的形状（IPv6 绕过 VPN 那条缺口）────────────────────
    // 【钉住什么】BuildRoutes 造的是平台对象，离线拿不到；搬进 RouteInclusion 之后
    //   "哪些 CIDR 进哪个桶"第一次能被断言。要钉的四条都是装机读不回来的：
    //   ① arm=0 必须逐字旧行为（v6 两个桶都空）；
    //   ② ipv6≥1 时 v6 节点服务器的宿主路由必须存在且是 /128 —— 旧实现整条缺失，
    //      拨向 v6 节点的外层包会落进自家 2000::/3（自环黑洞），而这台机器没有 v6 网络；
    //   ③ 任何前缀 0 的 v6 段都不许进 include（先遣 docs/96：::/0 会把 VPN 变成 v4 默认路由）；
    //   ④ profile 里的 v6 段不许再被塞进 v4 桶。
    private static void Ipv6RoutePlanShapes()
    {
        var profileIn = new List<string> { "0.0.0.0/0" };
        var narrowIn = new List<string> { "198.18.0.0/16", "223.5.5.5/32" };
        var profileEx = new List<string> { "192.168.0.0/16", "10.0.0.0/8", "172.16.0.0/12" };
        var nodeEx = new List<string> { "1.2.3.4/32", "2408:800:1234::1/32" };   // 第二条是 NodeRoutes 旧拼法
        const string Pool = "fdfe:dcba:9876::/64";

        RouteInclusion.Plan In(int arm, bool narrow = false,
            List<string>? ex = null, List<string>? nodes = null, bool nodeSwitch = true,
            string pool = Pool) =>
            RouteInclusion.Build(new RouteInclusion.Input(
                profileIn, narrowIn, narrow, arm, pool,
                ex ?? profileEx, nodes ?? nodeEx, nodeSwitch));

        RouteInclusion.Plan p0 = In(0);
        Check("G7: arm=0 时 v6 的 include 桶为空（逐字旧行为）",
              p0.Ipv6Includes.Count == 0, string.Join(",", p0.Ipv6Includes));
        Check("G7: arm=0 时 v6 的 exclude 桶也为空（接口上没有 v6 地址，不顺手加 v6 路由）",
              p0.Ipv6Excludes.Count == 0, string.Join(",", p0.Ipv6Excludes));
        Check("G7: arm=0 时 v6 节点排除被丢弃但必须点名（不是静默少一条防线）",
              p0.Dropped.Any(d => d.Contains("节点排除 v6 且 arm=0")), string.Join(" | ", p0.Dropped));
        Check("G7: arm=0 的 v4 桶与旧实现一致：0.0.0.0/0 + 三条局域网段 + v4 节点 /32",
              p0.Ipv4Includes.SequenceEqual(new[] { "0.0.0.0/0" }) &&
              p0.Ipv4Excludes.Count == 4 && p0.Ipv4Excludes.Contains("1.2.3.4/32"),
              p0.Describe());

        RouteInclusion.Plan p1 = In(1);
        Check("G7: arm=1 只下发 2000::/3（不是 ::/0）",
              p1.Ipv6Includes.SequenceEqual(new[] { "2000::/3" }), string.Join(",", p1.Ipv6Includes));
        Check("G7: arm=1 时 v6 节点服务器进 v6 exclude 且前缀纠正成 /128",
              p1.Ipv6Excludes.Contains("2408:800:1234::1/128") &&
              !p1.Ipv6Excludes.Any(x => x.EndsWith("/32") && x.Contains(":")),
              string.Join(",", p1.Ipv6Excludes));
        Check("G7: arm=1 时 v4 节点排除不受影响（仍在 v4 桶、仍是 /32）",
              p1.Ipv4Excludes.Contains("1.2.3.4/32"));

        RouteInclusion.Plan p2 = In(2);
        Check("G7: arm=2 额外把 ULA 假 IP 池拉进隧道（它不在 2000::/3 里）",
              p2.Ipv6Includes.SequenceEqual(new[] { "2000::/3", Pool }),
              string.Join(",", p2.Ipv6Includes));

        var globalPool = RouteInclusion.Ipv6IncludeRoutes(2, "2400:3200::/64");
        Check("G7: 假 IP 池落在全局单播段里时不重复下发（已被 2000::/3 覆盖）",
              globalPool.SequenceEqual(new[] { "2000::/3" }), string.Join(",", globalPool));
        var zero = RouteInclusion.Ipv6IncludeRoutes(2, "::/0");
        Check("G7: ★ 铁律 —— 池子写成 ::/0 也绝不进 include（前缀 0 一律拒绝）",
              zero.SequenceEqual(new[] { "2000::/3" }) && !zero.Any(c => c.EndsWith("/0")),
              string.Join(",", zero));
        Check("G7: IsBlockedV6Include 只认前缀 0",
              RouteInclusion.IsBlockedV6Include(0) && !RouteInclusion.IsBlockedV6Include(3));
        Check("G7: arm=1 不含池前缀（档 1 不答 AAAA，加了也没流量）",
              !p1.Ipv6Includes.Contains(Pool));
        Check("G7: 非法池串出声且不崩",
              RouteInclusion.Ipv6IncludeRoutes(2, "不是 CIDR").SequenceEqual(new[] { "2000::/3" }));

        RouteInclusion.Plan v6exInProfile = In(1, ex: new List<string> { "192.168.0.0/16", "fd00::/8" });
        Check("G7: profile 里的 v6 排除段进 v6 桶，不再被塞进 v4 桶",
              v6exInProfile.Ipv6Excludes.Contains("fd00::/8") &&
              !v6exInProfile.Ipv4Excludes.Any(x => x.Contains(":")),
              v6exInProfile.Describe());
        RouteInclusion.Plan v6exDrop = In(0,
            ex: new List<string> { "2001:db8::/32" }, nodes: new List<string>());
        Check("G7: arm=0 时 profile 的 v6 段被丢弃并点名（旧实现会把它当 v4 路由装出去）",
              v6exDrop.Ipv4Excludes.All(x => !x.Contains(":")) && v6exDrop.Ipv6Excludes.Count == 0 &&
              v6exDrop.Dropped.Any(d => d.Contains("exclude v6 且 arm=0")),
              string.Join(" | ", v6exDrop.Dropped));
        RouteInclusion.Plan narrow = In(0, narrow: true);
        Check("G7: narrow 时 v4 include 用假 IP 池 + 隧道 DNS，不再用 0.0.0.0/0",
              narrow.Ipv4Includes.SequenceEqual(narrowIn), string.Join(",", narrow.Ipv4Includes));
        RouteInclusion.Plan mixed = In(0, ex: profileEx,
            nodes: new List<string> { "1.1.1.1/32", "1.1.1.1/32" });
        Check("G7: 排除路由去重（大小写不敏感，重复节点 IP 不重复下发）",
              mixed.Ipv4Excludes.Count(x => x.Equals("1.1.1.1/32", StringComparison.OrdinalIgnoreCase)) == 1);
        RouteInclusion.Plan off = In(1, nodeSwitch: false);
        Check("G7: nodeExclusions=0 时 v4/v6 两个桶都不带节点宿主路由（单变量开关语义不变）",
              !off.Ipv4Excludes.Contains("1.2.3.4/32") && off.Ipv6Excludes.Count == 0,
              off.Describe());
        RouteInclusion.Plan mixedFam = RouteInclusion.Build(new RouteInclusion.Input(
            new List<string> { "0.0.0.0/0", "fe80::/10" }, narrowIn, false, 0, Pool,
            profileEx, nodeEx, true));
        Check("G7: v6 段混进 include 名单时不进 v4 桶、并留一条剔除记录",
              !mixedFam.Ipv4Includes.Any(x => x.Contains(":")) &&
              mixedFam.Dropped.Any(d => d.Contains("include v6 不进 v4 桶")),
              string.Join(" | ", mixedFam.Dropped));

        // ── 地址族判据 ────────────────────────────────────────────────────
        Check("G7: 全局单播 v6 判定 —— 2408:/2000: 是，fe80::/::1/fd00::/ff02:: 都不是",
              RouteInclusion.IsGlobalUnicastV6("2408:800:1234::1") &&
              RouteInclusion.IsGlobalUnicastV6("2000::") &&
              !RouteInclusion.IsGlobalUnicastV6("fe80::1") &&
              !RouteInclusion.IsGlobalUnicastV6("::1") &&
              !RouteInclusion.IsGlobalUnicastV6("fd00::1") &&
              !RouteInclusion.IsGlobalUnicastV6("ff02::1") &&
              !RouteInclusion.IsGlobalUnicastV6("1fff::"));
        Check("G7: v6 字面量带 %zone 也能判（链路本地常带区域 id）",
              !RouteInclusion.IsGlobalUnicastV6("fe80::1%12") &&
              RouteInclusion.TryParseV6("fe80::1%12", out _));
        Check("G7: HasGlobalUnicastV6 混合清单里挑得出全局地址",
              RouteInclusion.HasGlobalUnicastV6(new[] { "::1", "fe80::a", "1.2.3.4", "240e::1" }) &&
              !RouteInclusion.HasGlobalUnicastV6(new[] { "::1", "fe80::a" }));
        Check("G7: 采集失败必须判 Unknown，不能判成『没有 v6』（读不到 ≠ 安全）",
              RouteInclusion.ClassifyV6Face(new List<string> { "2408::1" }, collectFailed: true)
                  == RouteInclusion.V6Face.Unknown &&
              RouteInclusion.ClassifyV6Face(new List<string>(), collectFailed: false)
                  == RouteInclusion.V6Face.None &&
              RouteInclusion.ClassifyV6Face(new List<string> { "2408::1" }, collectFailed: false)
                  == RouteInclusion.V6Face.Global);
        Check("G7: 泄漏说明三档各有各的话，arm≥1 时不输出",
              RouteInclusion.LeakNote(0, RouteInclusion.V6Face.Global).Contains("泄漏") &&
              RouteInclusion.LeakNote(0, RouteInclusion.V6Face.None).Contains("没有 v6 泄漏面") &&
              RouteInclusion.LeakNote(0, RouteInclusion.V6Face.Unknown).Contains("未知") &&
              RouteInclusion.LeakNote(1, RouteInclusion.V6Face.Global).Length == 0);
        Check("G7: SplitCidr 按族校验前缀上限（v4 /33、v6 /129 都算非法）",
              !RouteInclusion.SplitCidr("1.2.3.4/33", out _, out _, out _) &&
              !RouteInclusion.SplitCidr("::1/129", out _, out _, out _) &&
              RouteInclusion.SplitCidr("::1/128", out _, out _, out bool v6) && v6);
        Check("G7: HostRoute 按族给宿主前缀（NodeRoutes 的候选串用它生成）",
              RouteInclusion.HostRoute("1.2.3.4") == "1.2.3.4/32" &&
              RouteInclusion.HostRoute("2408::1") == "2408::1/128");
        Check("G7: 摘要一行、可进快照（不换行、四桶计数都在）",
              !p1.Describe().Contains('\n') && p1.Describe().Contains("v6in=[2000::/3]") &&
              p1.Describe().Contains("v6ex="), p1.Describe());
    }

    // ── 【当前批次】G4：活动连接强断 ─────────────────────────────────────
    // 这一组的三条都是"装机会静默失效"的形状：id 没读出来 → 按钮画得出来但无断可依；
    // metadata 被当字符串 → 整列显示 UUID（1.0.75 真栽过）；端口/流量是字符串 → 显示 0。
    // ParseConnections 是纯函数（内核原文进、Conn 列表出），所以能在这里逐字钉住。
    private static void ConnectionKillShapes()
    {
        static List<KernelView.Conn> Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return KernelView.ParseConnections(doc.RootElement);
        }

        // ① 官方形态：metadata 是对象、端口是数字、流量是数字。
        var a = Parse(@"{""connections"":[{
            ""id"":""1f2c3d4e-5566-7788-99aa-bb0011223344"",
            ""chains"":[""直接连接"",""DIRECT""],
            ""metadata"":{""network"":""tcp"",""host"":""www.google.com"",
                          ""destinationPort"":443,""destinationIP"":""142.250.0.1""},
            ""upload"":209,""download"":102400}],
            ""up"":209,""down"":102400}");
        Check("G4: ★ id 读得出来（旧记录没有这一列 ⇒ 界面画得出「断开」也无从可断）",
              a.Count == 1 && a[0].Id == "1f2c3d4e-5566-7788-99aa-bb0011223344",
              a.Count == 0 ? "(空表)" : a[0].Id);
        Check("G4: metadata 按对象取（1.0.75 那次当字符串读 ⇒ 整列显示 UUID）",
              a[0].Target == "www.google.com:443", a[0].Target);
        Check("G4: network 读出来（断 UDP 会被应用马上重发，不显示协议就没法解释）",
              a[0].Network == "tcp", a[0].Network);
        Check("G4: 链路拼成 chains 的一行文本",
              a[0].Chain == "直接连接 > DIRECT", a[0].Chain);
        Check("G4: 上下行是数字时照读",
              a[0].Upload == 209 && a[0].Download == 102400,
              $"{a[0].Upload}/{a[0].Download}");

        // ② 老 Clash 那一族：数字写成字符串。旧实现只认 Number，于是界面永远"↑0 ↓0"，
        //   而"读不出来"和"读出来是 0"在界面上长得一模一样。
        var b = Parse(@"{""connections"":[{
            ""id"":""x1"",""metadata"":{""network"":""udp"",""host"":""a.b.c"",""destinationPort"":""53""},
            ""upload"":""1024"",""download"":""2048""}]}");
        Check("G4: ★ 端口/流量是字符串数字时也读得出来（不再静默成 0）",
              b[0].Target == "a.b.c:53" && b[0].Upload == 1024 && b[0].Download == 2048,
              $"{b[0].Target} ↑{b[0].Upload} ↓{b[0].Download}");
        Check("G4: udp 也照读", b[0].Network == "udp", b[0].Network);

        // ③ 兜底行的身份：host 缺失时退 sniffHost、再退 destinationIP；全都没有时
        //   必须是"目标未知"这句人话，不能再拿 id 顶上（id 现在有自己那一列）。
        var c1 = Parse(@"{""connections"":[{""id"":""u1"",""metadata"":{""sniffHost"":""sniffed.example""}}]}");
        Check("G4: 没有 host 时退 sniffHost（增强模式的真实来源）",
              c1[0].Target == "sniffed.example", c1[0].Target);
        var c2 = Parse(@"{""connections"":[{""id"":""u2"",""metadata"":{""destinationIP"":""1.1.1.1"",""destinationPort"":853}}]}");
        Check("G4: 连 sniffHost 也没有时退 destinationIP:port",
              c2[0].Target == "1.1.1.1:853", c2[0].Target);
        var c3 = Parse(@"{""connections"":[{""id"":""u3-uuid-like-0000""}]}");
        Check("G4: ★ metadata 整块缺失时明说「目标未知」，不再拿 id 冒充目标",
              c3[0].Target == "(目标未知)" && !c3[0].Target.Contains("u3") &&
              c3[0].Id == "u3-uuid-like-0000", c3[0].Target);

        // ④ 形状不对时不抛：这是 UI 每 3 秒都要走的读路径，抛一次就是整块白屏。
        Check("G4: connections 不是数组（旧内核/错误响应）→ 空表，不抛",
              Parse(@"{""error"":""boom""}").Count == 0 && Parse("[]").Count == 0);
        Check("G4: 数组里混进非对象条目时跳过那一条，不牵连其余",
              Parse(@"{""connections"":[""junk"",{""id"":""ok1""}]}").Count == 1);

        // ⑤ URL 转义：id 现在有三个来源（内核回话 / 命令文件原文 / 界面缓存的旧列表），
        //   带 / ? # 的值会把 DELETE 打到别的路径上去。
        Check("G4: ★ DELETE 的 URL 对 id 转义 —— 斜杠/问号/井号都被压成 %XX",
              MihomoController.ConnectionDeleteUri("a/b?c#d") ==
                  "http://127.0.0.1:9090/connections/a%2Fb%3Fc%23d",
              MihomoController.ConnectionDeleteUri("a/b?c#d"));
        Check("G4: 正常 UUID 转义后逐字不变（转义不能把好值改坏）",
              MihomoController.ConnectionDeleteUri("1f2c3d4e-5566-7788-99aa-bb0011223344") ==
                  "http://127.0.0.1:9090/connections/1f2c3d4e-5566-7788-99aa-bb0011223344");
        Check("G4: 空 id 仍是一个合法 URL（拒绝它是对上层的职责，不在这条里静默）",
              MihomoController.ConnectionDeleteUri("") == "http://127.0.0.1:9090/connections/");
    }

    // ── 【当前批次】G5：日志等级不被"看日志"这个动作改掉 ─────────────────
    // 旧实现无条件抬 debug、无条件恢复成 info —— 用户把内核设成 warning，
    // 界面上点一次"抓取 12 秒"就把它变成 info，且界面上不会有任何痕迹。
    private static void LogLevelPreserveShapes()
    {
        static (bool, string) Plan(string lvl) => MihomoController.LogStreamLevelPlan(lvl);

        Check("G5: 内核本已是 debug → 不抬、恢复也是 debug（不做无谓的 PATCH）",
              Plan("debug") == (false, "debug"));
        Check("G5: 认识的四档各自恢复回自己，一律不再硬编码 info",
              Plan("warning") == (true, "warning") && Plan("error") == (true, "error") &&
              Plan("silent") == (true, "silent") && Plan("info") == (true, "info"));
        Check("G5: 大小写与前后空格先归一再判（DEBUG 也是 debug，不该被当脏值改掉）",
              Plan("  Debug ") == (false, "debug") && Plan("WARNING") == (true, "warning"));
        Check("G5: ★ 脏值/空串一律恢复成 info，绝不把原样字符串拼进 PATCH 的 JSON 体",
              Plan("") == (true, "info") &&
              Plan("verbo") == (true, "info") &&
              Plan("debug\",\"interface-name\":\"") == (true, "info"),
              Plan("debug\",\"interface-name\":\"").Item2);
        Check("G5: 白名单就是内核那五个值，多一个都不认",
              !MihomoController.IsKnownLogLevel("trace") &&
              new[] { "silent", "error", "warning", "info", "debug" }
                  .All(MihomoController.IsKnownLogLevel));
    }

    // ── 【当前批次】G6 · 实时流量：累计 → 速率 ───────────────────────────
    // 官方 mihomo v1.19.32 的 GET /connections 响应体里那三个根字段是<b>累计量</b>
    // （tunnel/statistic/manager.go:124），每秒 blip 只在 WebSocket 推送里。
    // 我们不开新通道，走差分 —— 于是差分的三条"会把绿读反"的形状必须离线钉死。
    private static void TrafficRateShapes()
    {
        static KernelView.Traffic Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return KernelView.ParseTraffic(doc.RootElement);
        }

        // ① 字段名逐字对照官方 struct 的 json tag（写错一个字母就是永远 0，且不会报错）。
        var t = Parse(@"{""downloadTotal"":4096,""uploadTotal"":1024,""memory"":56623104,""connections"":[]}");
        Check("流量: 按官方 tag 读 uploadTotal / downloadTotal / memory",
              t.Upload == 1024 && t.Download == 4096 && t.Memory == 56623104,
              $"{t.Upload}/{t.Download}/{t.Memory}");
        Check("流量: 三字段全缺 → 三个 0（= 没读到，不是读到 0）",
              Parse(@"{""connections"":[]}").Equals(new KernelView.Traffic(0, 0, 0)));
        Check("流量: 根不是对象 → 三个 0，不抛",
              Parse("[]").Equals(default(KernelView.Traffic)) &&
              Parse(@"{""error"":""x""}").Equals(default(KernelView.Traffic)));
        Check("流量: 部分缺键只让那一项为 0，不牵连其余",
              Parse(@"{""downloadTotal"":7,""memory"":9}").Upload == 0 &&
              Parse(@"{""downloadTotal"":7,""memory"":9}").Download == 7);

        var t0 = new DateTime(2026, 10, 25, 10, 0, 0);
        var t3 = t0.AddSeconds(3);

        // ② 正常差分：3 秒里走了 3072 下行 / 1536 上行 ⇒ 1024 B/s 与 512 B/s。
        var ok = TrafficRate.Between(1000, 2000, t0, 1000 + 1536, 2000 + 3072, t3);
        Check("流量: 差分 = 增量/间隔（3 秒 3072B ⇒ 1024 B/s）",
              ok.Known && ok.Down == 1024 && ok.Up == 512, ok.Describe());

        // ③ 三条"不许报 0"的未知形状 —— 0 和"读不到"在界面上必须长得不一样。
        Check("流量: ★ 首个样本判未知（不画 0 B/s：刚连上和真没流量界面同形）",
              !TrafficRate.Between(0, 0, default, 5000, 9000, t0).Known &&
              TrafficRate.Between(0, 0, default, 5000, 9000, t0).WhyUnknown!.Contains("首次采样"));
        Check("流量: ★ 计数器变小判未知，绝不取绝对值（重启会被读成一次突发大流量）",
              !TrafficRate.Between(9000, 9000, t0, 10, 10, t3).Known &&
              TrafficRate.Between(9000, 9000, t0, 10, 10, t3).WhyUnknown!.Contains("重启"));
        Check("流量: ★ 上行跌而下行没跌也算重启（任一计数器回退就整组不可信，不半真半假）",
              !TrafficRate.Between(9000, 100, t0, 10, 500, t3).Known);
        Check("流量: 间隔非正判未知且不做除零（时钟回拨/同轮重复采）",
              !TrafficRate.Between(1, 1, t3, 5, 5, t0).Known &&
              !TrafficRate.Between(1, 1, t0, 5, 5, t0).Known);
        Check("流量: 空载是真值 —— 增量 0 必须判 Known 且速率真是 0",
              TrafficRate.Between(1000, 2000, t0, 1000, 2000, t3).Known &&
              TrafficRate.Between(1000, 2000, t0, 1000, 2000, t3).Down == 0);
        Check("流量: 未知时的文本是「↑—　↓—（原因）」，不是数字",
              TrafficRate.Between(0, 0, default, 5, 5, t0).Describe().Contains("↑—"),
              TrafficRate.Between(0, 0, default, 5, 5, t0).Describe());

        // ④ 单位表
        Check("流量: 速率单位分档（B/s / KB/s / MB/s）",
              TrafficRate.Rate.PerSecond(512) == "512B/s" &&
              TrafficRate.Rate.PerSecond(2048) == "2.0KB/s" &&
              TrafficRate.Rate.PerSecond(5 * 1024 * 1024) == "5.0MB/s",
              TrafficRate.Rate.PerSecond(5 * 1024 * 1024));
        Check("流量: 累计单位不带 /s",
              TrafficRate.Bytes(1024) == "1.0KB" && TrafficRate.Bytes(0) == "0B",
              TrafficRate.Bytes(1024));

        // ⑤ "三条全零才是没读到"这条判据：内存是把它和"隧道空载"分开的那一项。
        Check("流量: ★ 三条全零 → 读不到；有内存没流量 → 是真空载，不是没读到",
              TrafficRate.DescribeTotals(0, 0, 0).Contains("读不到") &&
              !TrafficRate.DescribeTotals(0, 0, 56623104).Contains("累计 读不到") &&
              TrafficRate.DescribeTotals(0, 0, 56623104).Contains("累计 ↑0B ↓0B"),
              TrafficRate.DescribeTotals(0, 0, 56623104));
        Check("流量: 老内核没有 memory 字段时内存单独判读不到，流量照画",
              TrafficRate.DescribeTotals(2048, 4096, 0).Contains("内存 读不到") &&
              TrafficRate.DescribeTotals(2048, 4096, 0).Contains("↑2.0KB"),
              TrafficRate.DescribeTotals(2048, 4096, 0));
    }

    private static string Join(List<string> lines) =>
        "[" + string.Join("⏎", lines.Where(l => l.Trim().Length > 0)) + "]";

    // ── ★【1.0.119 · 代理页排序】────────────────────────────────────────

    private static KernelView.Member Mem(string name, long delay,
                                         bool group = false, bool builtin = false) =>
        new(name, delay, group ? "url-test" : "ss", group, builtin, "", "");

    private static string Names(List<KernelView.Member> ms) =>
        string.Join(",", ms.Select(m => m.Name));

    private static void NodeSortShapes()
    {
        // 一组"作者有意排布"的真实形状：组在第一、内置策略在中间、节点混排。
        var src = new List<KernelView.Member>
        {
            Mem("🔰 选择节点", -1, group: true),
            Mem("📺 动画疯", -1, group: true),
            Mem("DIRECT", -1, builtin: true),
            Mem("B-慢", 300),
            Mem("a-中", 120),
            Mem("C-不通", 0),
            Mem("D-未测", -1),
            Mem("E-未测", -1),
        };

        // ① 默认必须是原序：界面拿的就是 Mode.Original，Order 对它一条都不动。
        Check("排序: ★ Original 逐字不动 —— 作者顺序（🔰 在第一位）是订阅的有意排布",
              Names(NodeSort.Order(src, NodeSort.Mode.Original)) == Names(src));

        // ② 分档判据本身（-1 组 / 0 不通 / >0 真延迟 / -1 普通 = 未测）
        Check("排序: 分档 —— 真延迟 0 档、不通 1 档、未测 2 档、组与内置不进序列",
              NodeSort.DelayBucket(Mem("x", 120)) == 0 &&
              NodeSort.DelayBucket(Mem("x", 0)) == 1 &&
              NodeSort.DelayBucket(Mem("x", -1)) == 2 &&
              NodeSort.DelayBucket(Mem("g", -1, group: true)) == -1 &&
              NodeSort.DelayBucket(Mem("DIRECT", -1, builtin: true)) == -1);

        // ③ ★ 本版最容易犯的错：拿 DelayMs 升序排会把"没测过"的 -1 排到最前。
        var byDelay = NodeSort.Order(src, NodeSort.Mode.Delay);
        Check("排序: ★ 按延迟时未测（-1）绝不排在有延迟的节点前面",
              byDelay.IndexOf(byDelay.First(m => m.Name == "D-未测")) >
              byDelay.IndexOf(byDelay.First(m => m.Name == "a-中")),
              Names(byDelay));
        Check("排序: 按延迟的完整序列 —— 组/内置留在原索引，真节点 120→300→不通→未测",
              Names(byDelay) == "🔰 选择节点,📺 动画疯,DIRECT,a-中,B-慢,C-不通,D-未测,E-未测",
              Names(byDelay));

        // ④ 排序只换顺序，不增删（"排序顺手过滤掉几条"是最阴的失效形状）
        Check("排序: 三种模式长度都等于输入（不增删）",
              NodeSort.Order(src, NodeSort.Mode.Original).Count == src.Count &&
              byDelay.Count == src.Count &&
              NodeSort.Order(src, NodeSort.Mode.Name).Count == src.Count);

        // ⑤ 名称排序：大小写不敏感（订阅里中英混排），且全表参与（组也按名排，
        //    因为这一档的语义就是"给我一个字母序的查找视图"，与延迟档的"别把组当节点"不同）。
        var byName = NodeSort.Order(src, NodeSort.Mode.Name);
        Check("排序: 按名称忽略大小写，a-中 排在 B-慢 前",
              byName.IndexOf(byName.First(m => m.Name == "a-中")) <
              byName.IndexOf(byName.First(m => m.Name == "B-慢")),
              Names(byName));

        // ⑥ 同档保持原序，不制造假比较（两条都未测 ⇒ D 在 E 前）
        Check("排序: 同为未测保持原序（D 在 E 前，不产生随机换位）",
              byDelay.IndexOf(byDelay.First(m => m.Name == "D-未测")) <
              byDelay.IndexOf(byDelay.First(m => m.Name == "E-未测")));

        // ⑦ 摘要必须把"多少条没测过"说出来 —— 否则 30/32 未测时前几名会被读成"最快"
        Check("排序: 延迟摘要 —— 有延迟 2 / 不通 1 / 未测 2（组与内置不计入）",
              NodeSort.DelaySummary(src) == "有延迟 2　不通 1　未测 2",
              NodeSort.DelaySummary(src));
    }

    // ── ★【1.0.119 · 订阅定时自动更新】──────────────────────────────────

    private static void SubscriptionAutoShapes()
    {
        var t0 = new DateTime(2026, 10, 25, 10, 0, 0);

        // ① 档位归一：认不出来一律「关」，不"就近取一档"。
        Check("自动: NormalizeHours 只认白名单；认不出来/越界一律=关（不就近取一档）",
              SubscriptionAutoPolicy.NormalizeHours("6") == 6 &&
              SubscriptionAutoPolicy.NormalizeHours(" 24 ") == 24 &&
              SubscriptionAutoPolicy.NormalizeHours("0") == 0 &&
              SubscriptionAutoPolicy.NormalizeHours("") == 0 &&
              SubscriptionAutoPolicy.NormalizeHours(null) == 0 &&
              SubscriptionAutoPolicy.NormalizeHours("1") == 0 &&
              SubscriptionAutoPolicy.NormalizeHours("1000") == 0 &&
              SubscriptionAutoPolicy.NormalizeHours("abc") == 0);
        Check("自动: 关闭那一档永远排第一（默认值必须是「什么都不改」）",
              SubscriptionAutoPolicy.IntervalHours[0] == 0 &&
              !SubscriptionAutoPolicy.IsOn(0) && SubscriptionAutoPolicy.IsOn(6));

        // ② ★★【1.0.122 · 口径换了】连着 VPN 也照样动手：判据里再没有"会话"这一项。
        //   现读 clash-verge-rev：自动与手动是同一个 feat::update_profile（timer.rs 到点只调它），
        //   其判据只有"远程订阅 + 有 URL + allow_auto_update + 到点"四条，没有一条看在不在连；
        //   更新的是当前那份就走 update_config_with_force（PUT /configs?force=true 原地热重载），
        //   不重启内核、不断流。我们这一路本来就等价（FetchAsync(activate:true) + 写 profile
        //   那条照旧过 1.0.101 的写入闸）。证据逐条在 Docs/63 §四。
        Check("自动: ★ 到点就动手 —— 形参里没有会话状态（1.0.122 对齐 Verge，旧「会话中 Skip」已删）",
              SubscriptionAutoPolicy.Decide(6, t0.AddHours(7), t0, false, true).ShouldRun &&
              SubscriptionAutoPolicy.Decide(6, t0.AddHours(7), t0, false, true)
                  .Why.Contains("已过"));
        Check("自动: 切订阅进行中同样 Skip（两条流程叠在一起写 config.yaml 就是交叉写）",
              !SubscriptionAutoPolicy.Decide(6, t0.AddHours(7), t0, true, true).ShouldRun &&
              SubscriptionAutoPolicy.Decide(6, t0.AddHours(7), t0, true, true)
                  .Why.Contains("切订阅"));

        // ③ 关闭 / 没有生效订阅 / 读不到上次时间 —— 三种"没有对象"，理由各不同
        Check("自动: 关闭 → Skip 且理由是「关闭」（不许静默）",
              !SubscriptionAutoPolicy.Decide(0, t0.AddYears(1), t0, false, true).ShouldRun &&
              SubscriptionAutoPolicy.Decide(0, t0.AddYears(1), t0, false, true)
                  .Why == "自动更新=关闭");
        Check("自动: 没有生效订阅 → Skip（不许自己去拉「最近一份」）",
              !SubscriptionAutoPolicy.Decide(6, t0.AddHours(7), t0, false, false).ShouldRun &&
              SubscriptionAutoPolicy.Decide(6, t0.AddHours(7), t0, false, false)
                  .Why.Contains("没有生效订阅"));
        Check("自动: ★ 上次拉取时间读不到 → Skip，不拿 DateTime.Now 猜一个",
              !SubscriptionAutoPolicy.Decide(6, t0.AddHours(7), null, false, true).ShouldRun &&
              SubscriptionAutoPolicy.Decide(6, t0.AddHours(7), null, false, true)
                  .Why.Contains("不猜"));

        // ④ 到点判据：边界"正好到点"必须算到点（>=），差一分钟不算
        Check("自动: 没到点 → Skip 且把下次时间写进理由",
              !SubscriptionAutoPolicy.Decide(6, t0.AddMinutes(359), t0, false, true).ShouldRun &&
              SubscriptionAutoPolicy.Decide(6, t0.AddMinutes(359), t0, false, true)
                  .Why.Contains("还没到点"));
        Check("自动: 正好到点 → Run（边界不吞一次）",
              SubscriptionAutoPolicy.Decide(6, t0.AddHours(6), t0, false, true).ShouldRun);

        // ⑤ ★ 失败退避：按 6×2^(n-1) 加宽、48h 封顶；成功一次（n=0）回到正常间隔
        Check("自动: 退避序列 6→12→24→48→48（封顶，不指数爆炸）",
              Shift(t0, 6, 1) == t0.AddHours(6 + 6) &&
              Shift(t0, 6, 2) == t0.AddHours(6 + 12) &&
              Shift(t0, 6, 3) == t0.AddHours(6 + 24) &&
              Shift(t0, 6, 4) == t0.AddHours(6 + 48) &&
              Shift(t0, 6, 9) == t0.AddHours(6 + 48));
        Check("自动: 失败 0 次就是正常间隔（退避不是常态）",
              Shift(t0, 6, 0) == t0.AddHours(6));
        Check("自动: ★ 退避期间「没到点」是真的没到点 —— 一次 403 不能变成 5 分钟一次的骚扰",
              !SubscriptionAutoPolicy.Decide(6, t0.AddHours(8), t0, false, true, 1).ShouldRun &&
              SubscriptionAutoPolicy.Decide(6, t0.AddHours(8), t0, false, true, 1)
                  .Why.Contains("退避中") &&
              Shift(t0, 6, 1) == t0.AddHours(12));

        // ⑥ 关闭时"下次"必须是 null，界面那行必须写「关闭」而不是空白
        Check("自动: 关闭 → NextDueAt=null（不是「现在」）",
              SubscriptionAutoPolicy.NextDueAt(t0, 0, 0) == null &&
              SubscriptionAutoPolicy.NextDueAt(t0, 0, 3) == null);
        Check("自动: 界面那行 —— 关闭写「关闭」、退避写清退避、正常写下次时间",
              SubscriptionAutoPolicy.StatusLine(0, null, 0) == "自动更新 关闭" &&
              SubscriptionAutoPolicy.StatusLine(6, t0.AddHours(6), 0).Contains("下次 10-25 16:00") &&
              SubscriptionAutoPolicy.StatusLine(6, t0.AddHours(12), 2).Contains("连续失败 2 次"),
              SubscriptionAutoPolicy.StatusLine(6, t0.AddHours(12), 2));
    }

    private static DateTime Shift(DateTime fetchedAt, int hours, int failStreak) =>
        SubscriptionAutoPolicy.DueAt(fetchedAt, hours, failStreak);

    // ── ★【1.0.122 · 通知点击的落点】────────────────────────────────────

    /// <summary>
    /// 状态通知的两条点击（正文 <c>clashuwp://open</c>、按钮 <c>clashuwp://disconnect?n=…</c>）
    /// 必须被认成"动作"，且<b>不许</b>把订阅导入截走。
    /// ★ 为什么这一组判据值得离线钉：装机上"点了通知没反应"和"点了通知导入被吞了"
    ///   是同一种症状，只有分流顺序本身能在没有设备的情况下钉死。
    /// </summary>
    private static void ToastActionShapes()
    {
        // ① 正文点击 = open，不带凭据
        var open = SubscriptionProtocol.TryParseAppAction("clashuwp://open");
        Check("通知: clashuwp://open 认成 open 动作（无凭据）",
              open != null && open.Value.Name == "open" && !open.Value.IsDisconnect && open.Value.Nonce == "",
              open?.ToString() ?? "null");

        // ② 按钮点击 = disconnect + ?n= 那串凭据
        var disc = SubscriptionProtocol.TryParseAppAction("clashuwp://disconnect?n=a1b2c3d4e5f60718");
        Check("通知: clashuwp://disconnect?n=… 认成 disconnect 且带出凭据",
              disc != null && disc.Value.IsDisconnect &&
              disc.Value.Nonce == "a1b2c3d4e5f60718",
              disc?.ToString() ?? "null");

        // ③ ★★ 分流顺序的代价：TryParse 认不了这两个 URI。
        //   前台若先跑 TryParse，点击会被记成"clashuwp:// 里没有 url 参数"然后整条返回，
        //   窗口不会动 —— 这就是"点了没反应"的形状。这条判据钉的是顺序不能反。
        Check("通知: ★ TryParse 认不了动作 URI（所以动作必须排在它前面分流）",
              !SubscriptionProtocol.TryParse("clashuwp://open").Ok &&
              !SubscriptionProtocol.TryParse("clashuwp://disconnect?n=abc").Ok);

        // ④ 带 url / u 参数的是导入，不许被截走
        var imp = SubscriptionProtocol.TryParseAppAction(
            "clashuwp://subscribe?url=https%3A%2F%2Fexample.com%2Fsub");
        Check("通知: 带 url 参数的 clashuwp:// 不当动作（留给导入）",
              imp == null &&
              SubscriptionProtocol.TryParse(
                  "clashuwp://subscribe?url=https%3A%2F%2Fexample.com%2Fsub").Ok);
        Check("通知: 带 u 参数的别名同样不当动作",
              SubscriptionProtocol.TryParseAppAction("clashuwp://open?u=https%3A%2F%2Fa.com%2Fb") == null);

        // ⑤ 只认这两个词：别的动作词、别的 scheme 一律 null
        Check("通知: 认识之外的动作词一律 null（clashuwp://fetch / settings / 空 host）",
              SubscriptionProtocol.TryParseAppAction("clashuwp://fetch") == null &&
              SubscriptionProtocol.TryParseAppAction("clashuwp://settings") == null &&
              SubscriptionProtocol.TryParseAppAction("clashuwp://") == null);
        Check("通知: 不是 clashuwp 的一律 null（含 clash:// 那一族与裸 URL）",
              SubscriptionProtocol.TryParseAppAction(
                  "clash://install-config?url=https%3A%2F%2Fexample.com%2Fsub") == null &&
              SubscriptionProtocol.TryParseAppAction("https://example.com/sub") == null &&
              SubscriptionProtocol.TryParseAppAction("file:///C:/windows/win.ini") == null);

        // ⑥ ★ 参数是不可信输入：null / 空串 / 垃圾一律 null 且永不抛
        bool threw = false;
        try
        {
            _ = SubscriptionProtocol.TryParseAppAction(null);
            _ = SubscriptionProtocol.TryParseAppAction("");
            _ = SubscriptionProtocol.TryParseAppAction("   ");
            _ = SubscriptionProtocol.TryParseAppAction("::::");
            _ = SubscriptionProtocol.TryParseAppAction("clashuwp://disconnect?n=%E4%B8%AD");
            _ = SubscriptionProtocol.TryParseAppAction("clashuwp://open#frag");
        }
        catch (Exception ex)
        {
            threw = true;
            Console.WriteLine("   异常: " + ex.GetType().Name + ": " + ex.Message);
        }
        Check("通知: ★ 畸形/恶意输入不抛异常（协议参数是不可信输入）", !threw);

        // ⑦ 动作词大小写不敏感（界面文案以后可能写成 Open），凭据大小写<b>敏感</b>
        Check("通知: 动作词认大小写不敏感，凭据串原样取出（比对在 StatusToast 侧）",
              SubscriptionProtocol.TryParseAppAction("CLASHUWP://Open") is { Name: "open" } &&
              SubscriptionProtocol.TryParseAppAction("clashuwp://DISCONNECT?n=AbC") is
                  { IsDisconnect: true } a2 && a2.Nonce == "AbC");
    }

    /// <summary>
    /// ★【1.0.123 · 更新生效阶梯】"正文已经落盘，接下来怎么让它真的生效"这张分支表。
    ///
    /// 【为什么这张表必须在离线钉】它的两个失败方向都要拿用户的网络才看得见：
    ///   · 少动一次（会话活着而内核不认这份配置，只回一句"下次连接自动加载"）
    ///     ⇒ 用户的订阅永远停在旧的那份，而他以为自动更新在跑；
    ///   · 多动一次（核心明确拒收、或会话状态根本读不出，照样拆隧道重拨）
    ///     ⇒ 把一条还能用的隧道换成起不来的那条。
    ///   而"会话活着 + 内核不可达"这一格要装机复现，得先把隧道搞成半死 ——
    ///   那是不能拿用户网络试的形状。
    ///
    /// 【与 Verge 的对照】<c>reload_or_restart</c>（manager/config.rs:443-464）是同一张表的原型：
    ///   原地 reload 成功就到此为止；原地不成才 restart 内核。差别只在我们没有 Verge 那道
    ///   apply 前的 <c>CoreConfigValidator</c>，所以"被拒收"这一格我们不动隧道（细则 Docs/64 §二）。
    /// </summary>
    private static void ConfigApplyLadderShapes()
    {
        Stage[] stages = { Stage.Landed, Stage.Accepted, Stage.Refused, Stage.SilentAfterAccept };
        SessionPresence[] presences =
            { SessionPresence.NotAsked, SessionPresence.NoSession,
              SessionPresence.LiveSession, SessionPresence.Unreadable };

        // ① 原地成功是唯一"不看会话状态也算成"的一格，而且不许为它多读一次 profile 清单
        var okInPlace = ConfigApplyPolicy.Decide(Stage.Accepted, SessionPresence.NotAsked, false);
        Check("阶梯: Accepted ⇒ 不动隧道、算成功（会话状态压根没读）",
              okInPlace.Action == Act.None && okInPlace.Ok &&
              okInPlace.Why.Contains("原地生效", StringComparison.Ordinal), okInPlace.Why);
        Check("阶梯: Accepted 时 presence/切订阅中都改不了结论（成功路不被后续判据翻案）",
              stages.Length > 0 && presences.All(pr =>
                  new[] { false, true }.All(sw =>
                  {
                      var d = ConfigApplyPolicy.Decide(Stage.Accepted, pr, sw);
                      return d.Action == Act.None && d.Ok;
                  })));

        // ② ★ 被拒收 = 这份配置 mihomo 不要。重拨只会把还能用的隧道换成起不来的那条 ⇒ 永不动
        Check("阶梯: Refused ⇒ 任何会话状态都不重拨，且判失败",
              presences.All(pr =>
              {
                  var d = ConfigApplyPolicy.Decide(Stage.Refused, pr, false);
                  return d.Action == Act.None && !d.Ok;
              }) && presences.All(pr =>
                  ConfigApplyPolicy.Decide(Stage.Refused, pr, true).Action == Act.None));
        Check("阶梯: Refused 的回话带出「不自动重拨」的出口（不许只留一句失败）",
              ConfigApplyPolicy.Decide(Stage.Refused, SessionPresence.LiveSession, false)
                  .Why.Contains("不自动重拨", StringComparison.Ordinal));

        // ③ ★★ 核心那一格：会话活着 + 内核不认这份配置 ⇒ "下次连接自动加载"是谎，必须自己补一次重拨
        var liveLand = ConfigApplyPolicy.Decide(Stage.Landed, SessionPresence.LiveSession, false);
        Check("阶梯: ★ 内核不可达但会话活着 ⇒ 自动重拨一次（对齐 reload_or_restart 的 restart 一支）",
              liveLand.Action == Act.ReconnectOnce, liveLand.Why);
        var liveSilent = ConfigApplyPolicy.Decide(Stage.SilentAfterAccept,
            SessionPresence.LiveSession, false);
        Check("阶梯: ★ 重载被接受后核心失联、会话仍记着 ⇒ 同样重拨一次",
              liveSilent.Action == Act.ReconnectOnce, liveSilent.Why);

        // ④ 台上没会话 ⇒ 只保证文件就位，绝不能凭空拨出一条隧道（"空壳不拨号"是已验收口径）
        var noLand = ConfigApplyPolicy.Decide(Stage.Landed, SessionPresence.NoSession, false);
        Check("阶梯: 内核不可达且无会话 ⇒ 不动手、算成功（下次连接自然加载）",
              noLand.Action == Act.None && noLand.Ok &&
              noLand.Why.Contains("不凭空拨号", StringComparison.Ordinal), noLand.Why);
        Check("阶梯: 核心失联但无会话 ⇒ 同样不拨（会话自己没了，不属于本阶梯的动作）",
              ConfigApplyPolicy.Decide(Stage.SilentAfterAccept,
                  SessionPresence.NoSession, false) is (var a, true, _) && a == Act.None);

        // ⑤ ★ 读数坏掉 ⇒ "有没有会话"不可知。Docs/51 §七.4 实测过"判据逐字成立而平台回
        //   AlreadyConnected"，所以这一格绝不能当"没会话"，更不能当"有会话"去拨
        var unLand = ConfigApplyPolicy.Decide(Stage.Landed, SessionPresence.Unreadable, false);
        Check("阶梯: ★ 会话状态读不出 ⇒ 绝不动隧道，且如实报「未能确认已应用」（不写成成功）",
              unLand.Action == Act.None && !unLand.Ok &&
              unLand.Why.Contains("未能确认已应用", StringComparison.Ordinal), unLand.Why);
        Check("阶梯: 读不出 + 核心失联 同样不拨不报成功",
              ConfigApplyPolicy.Decide(Stage.SilentAfterAccept,
                  SessionPresence.Unreadable, false) is (var a2, false, _));

        // ⑥ 调用方漏读状态（NotAsked）⇒ 保守到"不动 + 未确认"，不许拿没读的状态去拨号
        Check("阶梯: presence=NotAsked 落在要动隧道的支上 ⇒ 按不可知处理（漏读不等于可以拨）",
              ConfigApplyPolicy.Decide(Stage.Landed, SessionPresence.NotAsked, false)
                  .Action == Act.None &&
              ConfigApplyPolicy.Decide(Stage.SilentAfterAccept,
                  SessionPresence.NotAsked, false).Action == Act.None);

        // ⑦ 切订阅正在进行 ⇒ 三段式自己会重拨，本阶梯绝不另开一次（两条流程叠着拨就是重拨环的苗子）
        var switching = ConfigApplyPolicy.Decide(Stage.Landed, SessionPresence.LiveSession, true);
        Check("阶梯: ★ 切订阅进行中 ⇒ 不另开重拨，交给那一次的「先断开→后修改→再重连」",
              switching.Action == Act.None && switching.Ok &&
              switching.Why.Contains("切订阅", StringComparison.Ordinal), switching.Why);
        Check("阶梯: 切订阅进行中 + 核心失联 ⇒ 同样交给那一次（不拨第二次）",
              ConfigApplyPolicy.Decide(Stage.SilentAfterAccept,
                  SessionPresence.LiveSession, true).Action == Act.None);

        // ⑧ ★★ 穷举闸门：整张表里<b>只有</b>两格会动隧道 ——
        //   (Landed | SilentAfterAccept) × LiveSession × 非切订阅中。多一格就是回归。
        var dialCells = (from st in stages
                         from pr in presences
                         from sw in new[] { false, true }
                         where ConfigApplyPolicy.Decide(st, pr, sw).Action == Act.ReconnectOnce
                         select $"{st}/{pr}/{sw}").ToList();
        Check("阶梯: ★★★ 全表只有 2 格产出「自动重拨」（多一格或少一格都是判据漂了）",
              dialCells.Count == 2 &&
              dialCells.Contains("Landed/LiveSession/False") &&
              dialCells.Contains("SilentAfterAccept/LiveSession/False"),
              string.Join("、", dialCells));

        // ⑨ 每条结论都带人话理由（Skip 不许静默，口径同 SubscriptionAutoPolicy）；
        //   且回话里不许漏 HTML 标签进界面（1.0.120 那次 <b> 直接印在按钮上的同族坑）
        int whyBad = 0;
        foreach (Stage st in stages)
            foreach (SessionPresence pr in presences)
                foreach (bool sw in new[] { false, true })
                {
                    var d = ConfigApplyPolicy.Decide(st, pr, sw);
                    if (d.Why.Length == 0 || d.Why.IndexOf('<') >= 0 || d.Why.IndexOf('>') >= 0)
                        whyBad++;
                }
        Check("阶梯: 每一格都给出非空理由，且理由里没有标签/尖括号（界面文案契约）",
              whyBad == 0, "不合格的格数=" + whyBad);
    }
}
