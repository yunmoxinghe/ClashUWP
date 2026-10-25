using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ClashUWP.Backend.Mihomo
{
    /// <summary>
    /// 【后端 · 配置视图】内核<b>不可达</b>时，从 <c>LocalState\config.yaml</c>（订阅原文）
    /// 解析出"有哪些组、每组哪些节点、什么类型、server:port、预设选了谁"，交给代理页。
    ///
    /// ★★【1.0.118 · 为什么要有这一类 —— 本批的主修】
    ///   代理页原先唯一的数据源是 <see cref="KernelView"/> 的 REST（127.0.0.1:9090），
    ///   而内核只在 VPN 插件的 Connect 步 7 里被起起来（<c>ClashVpnPlugin.StartMihomoBridge</c>）。
    ///   于是"从未连接过"这台机器上内核根本没有，代理页就只剩一句
    ///   "(没有可切换的组)" —— 节点浏览被<b>错误地等价成了隧道已建立</b>。
    ///   节点的<b>定义</b>本来就在磁盘上的订阅原文里，读它不需要内核、不需要隧道、不碰网络。
    ///
    /// 【★ 与 KernelView 的分工（不许混）】
    ///   · <see cref="KernelView"/>：内核实时（延迟、现值、连接数都是运行态事实）；
    ///   · 本类：配置文件静态定义（无延迟、无实时现值；组名/节点名/server/port 与
    ///     磁盘原文<b>逐字一致</b>，不做任何规范化）。
    ///   两者产出<b>同一套</b> <see cref="KernelView.Group"/>/<see cref="KernelView.Member"/>
    ///   模型（Docs/48"不另立第二套数据"），来源由 <c>Snapshot.FromConfigFile</c> 标注，
    ///   <c>Snapshot.Ok</c> 在内核不可达时照旧是 false —— 这一路绝不把"列出了组"
    ///   画成"内核在跑"。
    ///
    /// 【★ 读 config.yaml 而不是 config.kernel.yaml】口径同
    ///   <see cref="SubscriptionView"/>：kernel.yaml 是被我们改写过的（tolerance 锁死、
    ///   fake-ip 注入），拿它当"订阅里的节点"展示会把改写误显示成订阅原文。
    ///
    /// 【行式解析的边界（刻意保守）】
    ///   复用 <see cref="SubscriptionView"/> 的同一套段/列判据（顶层键、下一顶层键、
    ///   列表项列、行首空白）—— 两处数出来的条目必须一致，不能有第三套口径。
    ///   认两种条目形状：行内流式（<c>- { name: x, type: ss, … }</c>，订阅的常见写法）
    ///   与块写法（<c>- name: x</c> 换行缩进子键 / 子列表）。
    ///   认不了的形态（YAML 锚点、多文档 <c>---</c> 之后的段、顶层 <c>proxies:</c> 直接
    ///   跟一行 flow 列表）解析不出条目时<b>降级为空列表 + Note 说明</b>，绝不猜、绝不抛 ——
    ///   这一路失败的代价是"界面没数据"，不是"内核起不来"。
    ///
    /// ★【1.0.118 对照官方 Meta 分支（baseline/_mihomo，源码位置见 Docs/61 §七）】
    ///   · 顶层键在<b>内核里大小写敏感</b>（go.yaml.in/yaml/v3 直解，config.go 的
    ///     <c>yaml:"proxies"/"proxy-groups"/"proxy-providers"</c> 标签），也没有 <c>proxy</c>
    ///     单数别名 —— <c>Proxies:</c> 这类写法内核整段不加载。本类仍按忽略大小写列数据
    ///     （磁盘上确实有），但会在 Note 里点名"内核不认这个键"。
    ///   · 条目内部字段名走 structure decoder 的 <c>EqualFold</c>（parser.go:522）——
    ///     <c>Name:</c>/<c>type:</c> 混用内核照收，本类的标量表同样忽略大小写，<b>一致</b>；
    ///     但 <c>type</c> 的<b>值</b>（switch "ss"…）内核精确匹配、"SS" 内核报错，本类如实显示。
    ///   · inline 提供方的 <c>payload:</c> 节点内核真会读（parser.go 的 NewInlineProvider）⇒ 收进节点表；
    ///     <c>use:</c> 在内核是"把该提供方的节点<b>并进组成员</b>"（outboundgroup/parser.go 的
    ///     <c>GetProxies(provider.Name)</c>）⇒ inline 的按此展开成成员；http/file 的节点来自
    ///     远端/本地缓存文件、磁盘上没有 ⇒ 才留占位行；
    ///     <c>include-all*</c>/<c>filter</c> 类键的成员由内核运行态展开 ⇒ Note 声明"视图可能少于内核"；
    ///     节点重名是内核<b>硬失败</b>（config.go:909）⇒ 警示；提供方名 <c>default</c> 是保留名 ⇒ 警示。
    ///
    /// 【★ 选择预设走 SelectionStore，不是 UI 状态】
    ///   内核未运行时点节点 → <see cref="RecordPreset"/> 写 <c>selection.txt</c>（现有的
    ///   客户端代记通道），内核一可达就由 <c>MihomoController.ReplaySelectionsAsync</c>
    ///   回放生效。这不改内核运行态、不碰隧道、不发起 VPN 连接，也不是"只把按钮画亮"的
    ///   UI-only 假成功 —— 它落的是与实时选择同一个台账。
    /// </summary>
    internal static class ConfigNodeView
    {
        /// <summary>
        /// 一次配置解析的结果。<see cref="Found"/>=false 时 <see cref="Error"/> 给人看，
        /// Groups 为空列表；<see cref="Note"/> 是补充说明（订阅源 provider 需内核展开等），
        /// 可为空串。
        /// </summary>
        internal sealed record View(
            bool Found,
            string Error,
            IReadOnlyList<KernelView.Group> Groups,
            string Note);

        /// <summary>读 <c>config.yaml</c> 并解析。任何失败降级为 <see cref="View.Found"/>=false。</summary>
        internal static async Task<View> LoadAsync()
        {
            try
            {
                Windows.Storage.StorageFile? file = await LocalState.TryGetFileAsync("config.yaml");
                if (file == null)
                    return new View(false,
                        "config.yaml 不存在（还没拉取过订阅）——内核未运行时，磁盘上没有可列出的节点定义",
                        Array.Empty<KernelView.Group>(), "");

                string text = await LocalState.ReadTextAsync(file);
                return Parse(text, SelectionStore.LoadAll());
            }
            catch (Exception ex)
            {
                return new View(false,
                    $"读 config.yaml 失败: {ex.GetType().Name}: {ex.Message}",
                    Array.Empty<KernelView.Group>(), "");
            }
        }

        /// <summary>纯函数入口（离线夹具直测用；不带选择台账 = 所有组"未选"）。</summary>
        internal static View Parse(string text) => Parse(text, null);

        /// <summary>
        /// 行式解析。<paramref name="selections"/> 是 <c>selection.txt</c> 台账
        /// （组名 → 上次手选的节点名）；只给<b>手动组</b>（Selector/relay）落 <c>Now</c>，
        /// 自动组的"现值"是运行态事实，配置文件里没有，留空。
        /// </summary>
        internal static View Parse(string text, Dictionary<string, string>? selections)
        {
            var empty = new View(false, "", Array.Empty<KernelView.Group>(), "");
            if (string.IsNullOrWhiteSpace(text))
                return new View(false, "config.yaml 是空的",
                    Array.Empty<KernelView.Group>(), "");

            string trimmedStart = text.TrimStart();
            if (trimmedStart.StartsWith("{", StringComparison.Ordinal))
                // 内置兜底配置是 JSON（口径同 KernelConfig.Transform），不是行式 YAML 能碰的。
                return new View(false, "内置兜底配置（JSON）里没有可列出的代理组",
                    Array.Empty<KernelView.Group>(), "");

            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Length > 0 && lines[0].Length > 0 && lines[0][0] == '\uFEFF')
                lines[0] = lines[0].Substring(1);   // BOM 会让第一行的顶层键匹配不上

            // ── ① proxies 段：节点定义表（name → type/server/port）──
            var nodeMap = new Dictionary<string, NodeDetail>(StringComparer.Ordinal);
            var dupNames = new HashSet<string>(StringComparer.Ordinal);
            var dupSeen = new HashSet<string>(StringComparer.Ordinal);
            var (ps, pe) = SectionRange(lines, "proxies");
            if (ps >= 0)
                foreach (var (st, en, _) in ItemRanges(lines, ps + 1, pe))
                {
                    var (sc, _) = ReadItem(lines, st, en);
                    if (!sc.TryGetValue("name", out string? name) || name.Length == 0)
                        continue;
                    // ★ 重名在官方是<b>硬失败</b>（config.go:909 `proxy %s is the duplicate name`
                    //   ⇒ 整个内核起不来），不是"折叠显示"；视图先按第一份收，再如实警示。
                    if (!dupSeen.Add(name)) dupNames.Add(name);
                    nodeMap[name] = DetailOf(sc);
                }

            // ── ② proxy-providers 段：提供方名 + inline 的 payload 节点（内核真会读它）──
            var providerNames = new List<string>();
            var providerNodes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var (vs, ve) = SectionRange(lines, "proxy-providers");
            if (vs >= 0) providerNames = ProviderEntries(lines, vs, ve, nodeMap, providerNodes);

            // ── ③ proxy-groups 段：两遍 —— 先收组名（成员分类要用），再折成员 ──
            var (gs, ge) = SectionRange(lines, "proxy-groups");
            var raw = new List<RawGroup>();
            bool anyRuntimeExpand = false;   // include-all* / filter 类键（成员由内核运行态展开）
            if (gs >= 0)
                foreach (var (st, en, _) in ItemRanges(lines, gs + 1, ge))
                {
                    var (sc, lists) = ReadItem(lines, st, en);
                    if (!sc.TryGetValue("name", out string? name) || name.Length == 0)
                        continue;
                    sc.TryGetValue("type", out string? type);
                    lists.TryGetValue("proxies", out List<string>? members);
                    lists.TryGetValue("use", out List<string>? use);
                    if (sc.ContainsKey("include-all") || sc.ContainsKey("include-all-proxies") ||
                        sc.ContainsKey("include-all-providers") || sc.ContainsKey("filter") ||
                        sc.ContainsKey("exclude-filter") || sc.ContainsKey("exclude-type"))
                        anyRuntimeExpand = true;
                    raw.Add(new RawGroup(name, type ?? "",
                        members ?? new List<string>(), use ?? new List<string>()));
                }

            // ── 警示（逐条对照官方 Meta 分支源码，Docs/61 §七）──
            var warns = new List<string>();
            foreach (string k in UnknownTopLevelKeys(lines))
                warns.Add($"顶层键「{k}」内核不认（yaml.v3 顶层大小写敏感且无 `proxy` 单数别名），这部分定义内核不会加载");
            if (dupNames.Count > 0)
                warns.Add($"节点重名 {string.Join("、", dupNames)}：内核按硬失败处理（duplicate name），这份配置起不来");

            if (raw.Count == 0)
            {
                // 组一个都没解析出来：可能是纯节点订阅（无组），也可能是不认的写法。
                // 前者如实回"0 组 + 节点数"，后者如实回判据，不假装是空订阅。
                string emptyNote = nodeMap.Count > 0
                    ? $"配置里有 {nodeMap.Count} 个节点定义，但没有 proxy-groups 段（无代理组可切换）"
                    : providerNames.Count > 0
                        ? "配置只有 proxy-providers（节点由内核运行时拉取），内核未运行列不出节点"
                        : "config.yaml 里没有可解析的 proxies / proxy-groups 定义" +
                          "（不认的 YAML 形态（锚点/多文档/整段 flow 列表）不做猜测）";
                if (dupNames.Count > 0)
                    emptyNote = (emptyNote + "；节点重名 " + string.Join("、", dupNames) +
                                 "：内核按硬失败处理，这份配置起不来");
                foreach (string k in UnknownTopLevelKeys(lines))
                    emptyNote = (emptyNote + "；顶层键「" + k + "」内核不认（顶层大小写敏感），不会被加载");
                if (providerNames.Contains("default"))
                    emptyNote += "；提供方名 default 是内核保留名，会直接报错";
                if (emptyNote.Length > 300) emptyNote = emptyNote.Substring(0, 300) + "…";
                return new View(true, "", Array.Empty<KernelView.Group>(), emptyNote);
            }

            var groupNames = new Dictionary<string, string>(StringComparer.Ordinal); // name → type
            foreach (var g in raw) groupNames[g.Name] = g.Type;

            var sel = selections ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var groups = new List<KernelView.Group>(raw.Count);
            var noteSb = new System.Text.StringBuilder();

            foreach (var g in raw)
            {
                var members = new List<KernelView.Member>(g.Members.Count);
                foreach (string m in g.Members)
                {
                    if (groupNames.TryGetValue(m, out string? subType))
                    {
                        // 嵌套组：现值同样只取台账里的预设（它自己也是手动组时）。
                        string subNow = KernelView.IsUserSelectableType(subType) &&
                                         sel.TryGetValue(m, out string? sn) ? sn : "";
                        members.Add(new KernelView.Member(m, -1, subType,
                            true, false, subNow, ""));
                    }
                    else if (IsBuiltinName(m))
                        members.Add(new KernelView.Member(m, -1, "", false, true, "", ""));
                    else if (nodeMap.TryGetValue(m, out NodeDetail? nd))
                        members.Add(new KernelView.Member(m, -1, nd.Type,
                            false, false, "", "", nd.Server, nd.Port));
                    else
                        // 认不出来的成员名：如实列成"未知节点"（可能是 provider 展开名），
                        // 不藏 —— 藏起来的条目比多列的一行更难查。
                        members.Add(new KernelView.Member(m, -1, "", false, false, "", ""));
                }
                foreach (string u in g.Use)
                {
                    // ★ 官方 <c>use:</c> 是"把该提供方的节点并进组成员"（outboundgroup/parser.go
                    //   的 GetProxies(provider.Name)），不是"多一行提供方"。inline 的节点磁盘上
                    //   就有 ⇒ 按内核口径展开成成员；http/file 的磁盘上没有 ⇒ 才留占位行。
                    bool expanded = false;
                    if (providerNodes.TryGetValue(u, out List<string>? pn) && pn.Count > 0)
                    {
                        foreach (string nn in pn)
                            if (nodeMap.TryGetValue(nn, out NodeDetail? nd))
                            {
                                members.Add(new KernelView.Member(nn, -1, nd.Type,
                                    false, false, "", u, nd.Server, nd.Port));
                                expanded = true;
                            }
                    }
                    if (!expanded && !nodeMap.ContainsKey(u) && !groupNames.ContainsKey(u))
                        members.Add(new KernelView.Member(u, -1, "Provider",
                            false, false, "", u));
                }

                string now = "";
                if (KernelView.IsUserSelectableType(g.Type) &&
                    sel.TryGetValue(g.Name, out string? saved))
                {
                    foreach (var m in members)
                        if (string.Equals(m.Name, saved, StringComparison.Ordinal))
                        { now = saved; break; }
                }
                groups.Add(new KernelView.Group(g.Name, g.Type, now, members));

                if (g.Use.Count > 0)
                    noteSb.Append($"组「{g.Name}」挂了订阅源 {string.Join("、", g.Use)}：" +
                                  "成员里 inline 提供方的 payload 已按配置列出，http/file 的要内核跑起来才拉得到；");
            }

            if (anyRuntimeExpand)
                noteSb.Append("有组用了 include-all/filter 类键：成员名单由内核运行态展开，配置视图可能少于内核实际；");
            foreach (string w in warns)
                noteSb.Append(w + "；");
            if (providerNames.Contains("default"))
                noteSb.Append("提供方名 default 是内核保留名，会直接报错；");

            string note = noteSb.ToString();
            if (note.Length > 0 && note.EndsWith("；", StringComparison.Ordinal))
                note = note.Substring(0, note.Length - 1);
            if (providerNames.Count > 0 && note.Length == 0)
                note = $"配置里的订阅源（需内核运行才展开节点）：{string.Join("、", providerNames)}";
            if (note.Length > 300) note = note.Substring(0, 300) + "…";

            return new View(true, "", groups, note);
        }

        /// <summary>
        /// ★【1.0.118 · 内核未运行时的"切换"】把选择记进 <c>selection.txt</c> 台账
        /// （与实时切换成功后 <c>SelectCoreAsync</c> 记的是<b>同一个</b>台账、同一回放通道）。
        /// 校验在写之前：组得存在、得是手动组、节点得在成员表里 —— 口径对齐
        /// <c>MihomoController.SelectCoreAsync</c>，但判据来自配置文件而不是内核表。
        /// 返回文案必须<b>如实</b>："记为预设、下次内核可达回放生效"，不许写成"已切换"。
        /// </summary>
        internal static string RecordPreset(
            IReadOnlyList<KernelView.Group> groups, string group, string node)
        {
            KernelView.Group? g = KernelView.Find(groups, group);
            if (g == null)
                return $"本地配置里没有组“{group}”（配置视图）—— 先拉取/更新订阅，再回来看";
            if (!g.IsUserSelectable)
                return $"组“{g.Name}”类型是 {g.Type}（自动组）：选择由内核测速/故障转移自己维护，" +
                       "配置层面不存在手选预设（与实时视图同一口径，不可手选）";
            KernelView.Member? found = null;
            foreach (var m in g.Members)
                if (string.Equals(m.Name, node, StringComparison.Ordinal)) { found = m; break; }
            if (found == null)
                return $"组“{g.Name}”的成员表里没有“{node}”（配置视图，区分大小写）—— 请按节点区原样点选";
            if (found.Type == "Provider")
                // 提供方占位行不是真节点：记进台账会在回放时 PUT 一个内核不认的名字。
                return $"“{node}”是订阅源（provider）占位行，不是节点：它的成员要内核运行后才列出来，" +
                       "请先连上内核再在该提供方给出的节点里选";
            // 内置策略名（DIRECT/REJECT…）允许预设：回放时内核认这些名字，口径与实时路径一致。
            SelectionStore.Record(group, node);
            return $"内核未运行：已把「{group} → {node}」记为「预设选择」，" +
                   "下次内核可达（连接或常驻重启）时由回放机制生效；" +
                   "本次没有改动内核，也没有发起 VPN 连接";
        }

        /// <summary>内置策略名判定（与内核 /proxies 表同一份保留名清单）。</summary>
        private static bool IsBuiltinName(string name)
        {
            foreach (string b in MihomoController.BuiltinProxyNames)
                if (string.Equals(b, name, StringComparison.Ordinal)) return true;
            return false;
        }

        private sealed record NodeDetail(string Type, string Server, string Port);

        /// <summary>
        /// 从一条目的标量表取画面上要用的三个字段。★ 三处收表（<c>proxies</c>、
        ///   <c>proxy-providers</c> 的 <c>use</c>、其 <c>payload</c> 子列表）共用这一份 ——
        ///   各写一遍的话，"提供方里的 server 字段算不算可见信息"这类口径迟早三处不一样。
        /// </summary>
        private static NodeDetail DetailOf(Dictionary<string, string> sc) =>
            new(sc.GetValueOrDefault("type") ?? "",
                sc.GetValueOrDefault("server") ?? "",
                sc.GetValueOrDefault("port") ?? "");
        private sealed record RawGroup(
            string Name, string Type, List<string> Members, List<string> Use);

        // ── 段与条目（全部走 SubscriptionView 那一套判据，见类头"行式解析的边界"）──

        private static (int Start, int End) SectionRange(string[] lines, string key)
        {
            // ★ 顶层<b>键名</b>忽略大小写（订阅里见过 `Proxy-Groups:`），但<b>值一律原样</b> ——
            //   与 ReadItem 的标量表同一口径；SubscriptionView.FindTopLevel 是大小写敏感的
            //   （那边历史行为已装机验证，不改它），所以这条回退路自己认一遍键形。
            int start = FindTopLevelCi(lines, key);
            if (start < 0) return (-1, -1);
            return (start, SubscriptionView.FindNextTopLevel(lines, start));
        }

        private static int FindTopLevelCi(string[] lines, string key)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i];
                if (l.Length == 0 || char.IsWhiteSpace(l[0])) continue;
                string t = l.TrimEnd();
                if (string.Equals(t, key + ":", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith(key + ": ", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith(key + ":\t", StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// 段内每个<b>列表项</b>的行区间 [Start,End) 与所在列。判据与数节点同一条：
        /// 只有与 <see cref="SubscriptionView.FirstItemColumn"/> 同列的 <c>- </c> 行才是条目，
        /// 更深的是项内部子列表（更深 dash 不当新条目，1.0.75 那条纠偏的原样沿用）。
        /// </summary>
        private static List<(int Start, int End, int Col)> ItemRanges(
            string[] lines, int from, int to)
        {
            var res = new List<(int, int, int)>();
            int col = SubscriptionView.FirstItemColumn(lines, from, to);
            if (col < 0) return res;
            int cur = -1;
            for (int i = from; i < to; i++)
            {
                string t = lines[i].Trim();
                if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal)) continue;
                if (SubscriptionView.LeadingSpaces(lines[i]) != col) continue;
                if (!(t.StartsWith("- ") || t == "-" || t.StartsWith("-\t", StringComparison.Ordinal)))
                    continue;
                if (cur >= 0) res.Add((cur, i, col));
                cur = i;
            }
            if (cur >= 0) res.Add((cur, to, col));
            return res;
        }

        /// <summary>
        /// proxy-providers 是<b>映射</b>（<c>p1:</c>）或<b>列表</b>（<c>- name: p1</c>）两种写法，
        /// 段内第一列的判定口径抄 <see cref="SubscriptionView.Parse"/> 的提供方段：
        /// 只认本层条目，嵌套的 type/payload 子键不算。
        /// ★【1.0.118 对照官方】inline 提供方的 <c>payload:</c> 节点<b>就在配置原文里</b>
        ///   （<c>adapter/provider/parser.go</c> 的 <c>case "inline": NewInlineProvider(name, schema.Payload, …)</c>），
        ///   所以把它们收进节点表，而不是只留占位行。
        /// </summary>
        private static List<string> ProviderEntries(
            string[] lines, int start, int end, Dictionary<string, NodeDetail> nodeMap,
            Dictionary<string, List<string>> providerNodes)
        {
            var names = new List<string>();
            int keyIndent = SubscriptionView.LeadingSpaces(lines[start]);
            int entryIndent = -1;
            for (int i = start + 1; i < end; i++)
            {
                string t = lines[i].Trim();
                if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal)) continue;
                int sp = SubscriptionView.LeadingSpaces(lines[i]);
                if (sp > keyIndent) { entryIndent = sp; break; }
            }
            if (entryIndent < 0) return names;

            for (int i = start + 1; i < end; i++)
            {
                string raw = lines[i];
                string t = raw.TrimStart();
                if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal)) continue;
                if (SubscriptionView.LeadingSpaces(raw) != entryIndent) continue;

                int itemEnd = NextEntryLine(lines, i + 1, end, entryIndent);
                string pname;
                Dictionary<string, string> sc;
                if (t.StartsWith("- "))
                {
                    (sc, _) = ReadItem(lines, i, itemEnd);
                    pname = sc.TryGetValue("name", out string? n) ? n : "";
                }
                else
                {
                    int colon = t.IndexOf(':');           // 映射键：条目体从键行下一行起
                    pname = colon > 0 ? Unquote(t.Substring(0, colon).Trim()) : "";
                    sc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sc.TryAdd("type", "");
                    // 本条目内部的子键（type / payload 父键…）：取条目体内第一种非 dash 行的缩进列
                    int subCol = -1;
                    for (int j = i + 1; j < itemEnd; j++)
                    {
                        string s2 = lines[j].Trim();
                        if (s2.Length == 0 || s2.StartsWith("#", StringComparison.Ordinal)) continue;
                        if (SubscriptionView.LeadingSpaces(lines[j]) <= entryIndent) continue;
                        if (s2.StartsWith("-", StringComparison.Ordinal)) break;
                        subCol = SubscriptionView.LeadingSpaces(lines[j]);
                        break;
                    }
                    if (subCol > 0)
                        for (int j = i + 1; j < itemEnd; j++)
                        {
                            string s2 = lines[j].Trim();
                            if (s2.Length == 0 || s2.StartsWith("#", StringComparison.Ordinal)) continue;
                            if (SubscriptionView.LeadingSpaces(lines[j]) != subCol) continue;
                            if (s2.StartsWith("-", StringComparison.Ordinal)) continue;
                            var kv = SplitKeyValue(s2);
                            if (!kv.HasValue) continue;
                            sc[kv.Value.Key] = kv.Value.Value;
                        }
                }
                if (pname.Length == 0) continue;
                names.Add(pname);

                // inline 的 payload: 节点 → 收进节点表（父键写法看子列表，行内写法直接拆元素）。
                // ★ 官方 <c>use:</c> 的语义是把提供方的节点<b>并进组成员</b>
                //   （<c>outboundgroup/parser.go</c> 的 <c>GetProxies(provider.Name)</c>），
                //   所以这里按提供方记一份节点名单，组段折算成员时据此展开。
                var thisProviderNodes = new List<string>();
                if (sc.TryGetValue("type", out string? ptype) &&
                    string.Equals(ptype.Trim(), "inline", StringComparison.OrdinalIgnoreCase))
                {
                    string? payloadValue = FindEntryPayloadValue(lines, i, itemEnd, entryIndent, out int payloadLine);
                    if (payloadValue != null && payloadValue.Length == 0)
                        CollectPayloadNodes(lines, payloadLine, itemEnd, nodeMap, thisProviderNodes);
                    else if (payloadValue != null && payloadValue.Trim().StartsWith("[", StringComparison.Ordinal))
                        foreach (string el in FlowList(payloadValue))
                            AddNodeFromFlowItem(el, nodeMap, thisProviderNodes);
                }
                providerNodes[pname] = thisProviderNodes;
            }
            return names;
        }

        /// <summary>
        /// 在提供方条目内部找 <c>payload:</c> 的值：行内 flow 值原样带回（列表写法与映射写法
        /// 都走这一条），空值 = 块序列父键（带回空串并点名键行），没有该键带回 null。
        /// 只在条目顶层子键那一列匹配，条目内部更深的 <c>payload</c> 不算。
        /// </summary>
        private static string? FindEntryPayloadValue(
            string[] lines, int entryStart, int entryEnd, int entryIndent, out int payloadLine)
        {
            payloadLine = -1;
            int subCol = -1;
            for (int j = entryStart + 1; j < entryEnd; j++)
            {
                string s = lines[j].Trim();
                if (s.Length == 0 || s.StartsWith("#", StringComparison.Ordinal)) continue;
                if (SubscriptionView.LeadingSpaces(lines[j]) <= entryIndent) continue;
                if (s.StartsWith("-", StringComparison.Ordinal)) break;
                subCol = SubscriptionView.LeadingSpaces(lines[j]);
                break;
            }
            if (subCol < 0) return null;
            for (int j = entryStart + 1; j < entryEnd; j++)
            {
                string s = lines[j].Trim();
                if (s.Length == 0 || s.StartsWith("#", StringComparison.Ordinal)) continue;
                if (SubscriptionView.LeadingSpaces(lines[j]) != subCol) continue;
                if (s.StartsWith("-", StringComparison.Ordinal)) continue;
                var kv = SplitKeyValue(s);
                if (kv.HasValue && kv.Value.Key.Equals("payload", StringComparison.OrdinalIgnoreCase))
                {
                    payloadLine = j;
                    return kv.Value.Value;
                }
            }
            return null;
        }

        /// <summary>行内 flow 元素（<c>{ name: x, type: ss, … }</c>）收一个节点；非 map 元素跳过。</summary>
        private static void AddNodeFromFlowItem(
            string el, Dictionary<string, NodeDetail> nodeMap, List<string> namesOut)
        {
            string s = el.Trim();
            if (!s.StartsWith("{", StringComparison.Ordinal)) return;
            int close = s.LastIndexOf('}');
            string inner = close > 0 ? s.Substring(1, close - 1) : s.Substring(1);
            var sc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in FlowSplit(inner))
            {
                var kv = SplitKeyValue(entry);
                if (!kv.HasValue) continue;
                sc[kv.Value.Key] = Unquote(kv.Value.Value);
            }
            if (!sc.TryGetValue("name", out string? name) || name.Length == 0) return;
            nodeMap[name] = DetailOf(sc);
            if (!namesOut.Contains(name)) namesOut.Add(name);
        }

        /// <summary>收 <c>payload:</c> 键行下方的 dash 子列表，把每个节点收表并登记进提供方名单。</summary>
        private static void CollectPayloadNodes(
            string[] lines, int payloadLine, int entryEnd,
            Dictionary<string, NodeDetail> nodeMap, List<string> namesOut)
        {
            foreach (var (st, en, _) in ItemRanges(lines, payloadLine + 1, entryEnd))
            {
                var (sc, _) = ReadItem(lines, st, en);
                if (!sc.TryGetValue("name", out string? name) || name.Length == 0) continue;
                nodeMap[name] = DetailOf(sc);
                if (!namesOut.Contains(name)) namesOut.Add(name);
            }
        }

        /// <summary>
        /// 磁盘上有、但<b>内核不会加载</b>的顶层键（等值小写键存在、而原样大小写没有）。
        /// ★ 官方是 <c>go.yaml.in/yaml/v3</c> 直解（<c>common/yaml/yaml.go</c>），顶层键<b>大小写敏感</b>、
        ///   也没有 <c>proxy</c> 单数别名 —— <c>Proxies:</c> 这类写法整段被内核无视。
        ///   视图照旧按忽略大小写把数据列出来（数据确实在磁盘），但必须把这条差异说出来。
        /// </summary>
        private static List<string> UnknownTopLevelKeys(string[] lines)
        {
            var res = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string[] stems = { "proxies", "proxy-groups", "proxy-providers" };
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i];
                if (l.Length == 0 || char.IsWhiteSpace(l[0]) || l[0] == '#') continue;
                string t = l.TrimEnd();
                int colon = t.IndexOf(':');
                if (colon <= 0) continue;
                string key = t.Substring(0, colon).Trim();
                if (!seen.Add(key)) continue;
                if (key == "proxy") { res.Add(key); continue; }   // 单数：内核没有这个字段
                foreach (string s in stems)
                {
                    if (!string.Equals(key, s, StringComparison.OrdinalIgnoreCase)) continue;
                    // 大小写不同 ⇒ 内核的 FieldsMap（OrdinaryKey）匹配不上，整段不会加载；
                    // 原样键（逐字同大小写）在磁盘上存在才算数——段内容能不能解析是另一回事。
                    if (SubscriptionView.FindTopLevel(lines, s) < 0 && !res.Contains(key)) res.Add(key);
                    break;
                }
            }
            return res;
        }

        private static int NextEntryLine(string[] lines, int from, int end, int entryIndent)
        {
            for (int i = from; i < end; i++)
            {
                string t = lines[i].Trim();
                if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal)) continue;
                if (SubscriptionView.LeadingSpaces(lines[i]) <= entryIndent) return i;
            }
            return end;
        }

        /// <summary>
        /// 读一个列表项：标量键值表 + 子列表表。两种形状（行内流式 / 块写法）见类头。
        /// ★ 键名统一按<b>忽略大小写</b>存（订阅里见过 <c>Name:</c>），值原样保留
        ///   （组名/节点名任何规范化都会让回放与内核表对不上，<c>KernelView.Find</c> 同纪律）。
        /// </summary>
        private static (Dictionary<string, string> Scalars,
                        Dictionary<string, List<string>> Lists)
            ReadItem(string[] lines, int start, int end)
        {
            var scalars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lists = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            string t = lines[start].Trim();
            if (t.StartsWith("-")) t = t.Substring(1).Trim();   // "-"、"- x"、"-{…}"

            if (t.StartsWith("{", StringComparison.Ordinal))
            {
                int close = t.LastIndexOf('}');
                string inner = close > 0 ? t.Substring(1, close - 1) : t.Substring(1);
                foreach (string entry in FlowSplit(inner))
                {
                    var kv = SplitKeyValue(entry);
                    if (!kv.HasValue) continue;
                    var (k, v) = kv.Value;
                    if (v.StartsWith("[", StringComparison.Ordinal))
                        lists[k] = FlowList(v);
                    else
                        scalars[k] = Unquote(v);
                }
                return (scalars, lists);   // 行内项自包含，下面的块逻辑不掺和
            }

            {
                var kv = SplitKeyValue(t);
                if (kv.HasValue)
                {
                    var (k, v) = kv.Value;
                    if (v.StartsWith("[", StringComparison.Ordinal))
                        lists[k] = FlowList(v);
                    else if (v.Length > 0)
                        scalars[k] = Unquote(v);
                }
            }

            int i = start + 1;
            while (i < end)
            {
                string s = lines[i].Trim();
                if (s.Length == 0 || s.StartsWith("#", StringComparison.Ordinal)) { i++; continue; }
                if (s.StartsWith("-", StringComparison.Ordinal)) { i++; continue; }
                var kvOuter = SplitKeyValue(s);
                if (!kvOuter.HasValue) { i++; continue; }
                var (k, v) = kvOuter.Value;

                if (v.Length > 0)
                {
                    if (v.StartsWith("[", StringComparison.Ordinal))
                        lists[k] = FlowList(v);
                    else
                        scalars[k] = Unquote(v);
                    i++;
                    continue;
                }

                // 空值键 = 块序列/块映射的父键；往下看更深缩进的 dash 行收进子列表。
                int keySp = SubscriptionView.LeadingSpaces(lines[i]);
                var items = new List<string>();
                int j = i + 1;
                while (j < end)
                {
                    string s2 = lines[j].Trim();
                    if (s2.Length == 0 || s2.StartsWith("#", StringComparison.Ordinal)) { j++; continue; }
                    if (SubscriptionView.LeadingSpaces(lines[j]) <= keySp) break;
                    if (s2.StartsWith("-", StringComparison.Ordinal))
                    {
                        string item = s2.Substring(1).Trim();
                        if (item.Length > 0) items.Add(Unquote(item));
                        j++;
                        continue;
                    }
                    break;   // 更深但非 dash（块映射的子项）不认，交回外层按子键处理
                }
                if (items.Count > 0) lists[k] = items;
                i = j;
            }
            return (scalars, lists);
        }

        // ── 流式（flow style）小解析：逗号只在<b>深度 0 且引号外</b>处切 ──────────

        /// <summary>把 <c>a, b: [x, y], c</c> 这类行内内容按顶层逗号切开（尊重引号与 {}[] 嵌套）。</summary>
        private static List<string> FlowSplit(string s)
        {
            var parts = new List<string>();
            int depth = 0;
            char quote = '\0';
            int begin = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (quote != '\0')
                {
                    if (ch == quote) quote = '\0';
                    continue;
                }
                if (ch is '"' or '\'') { quote = ch; continue; }
                if (ch is '{' or '[') { depth++; continue; }
                if (ch is '}' or ']') { depth--; continue; }
                if (ch == ',' && depth == 0)
                {
                    parts.Add(s.Substring(begin, i - begin).Trim());
                    begin = i + 1;
                }
            }
            string last = s.Substring(begin).Trim();
            if (last.Length > 0) parts.Add(last);
            return parts;
        }

        /// <summary><c>[a, 'b, c', {name: d}]</c> → 元素表（只去外层括号，元素按 FlowSplit 切）。</summary>
        private static List<string> FlowList(string value)
        {
            string v = value.Trim();
            int open = v.IndexOf('[');
            int close = v.LastIndexOf(']');
            if (open < 0 || close <= open) return new List<string>();
            var res = new List<string>();
            foreach (string e in FlowSplit(v.Substring(open + 1, close - open - 1)))
            {
                string e2 = Unquote(e);
                if (e2.Length > 0) res.Add(e2);
            }
            return res;
        }

        /// <summary>
        /// 拆 <c>key: value</c>：冒号必须在<b>深度 0 且引号外</b>（节点名里可能带冒号，
        /// 如 <c>"name: 机场: 东京"</c> 这种带引号的值，第一个引号外冒号才是分隔）。
        /// 无冒号或冒号在行首 ⇒ null（不是键值行）。
        /// </summary>
        private static (string Key, string Value)? SplitKeyValue(string s)
        {
            int depth = 0;
            char quote = '\0';
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (quote != '\0')
                {
                    if (ch == quote) quote = '\0';
                    continue;
                }
                if (ch is '"' or '\'') { quote = ch; continue; }
                if (ch is '{' or '[') { depth++; continue; }
                if (ch is '}' or ']') { depth--; continue; }
                if (ch == ':' && depth == 0 && i + 1 < s.Length && i > 0)
                {
                    string key = Unquote(s.Substring(0, i).Trim());
                    string val = s.Substring(i + 1).Trim();
                    // 尾注只在"值前面有空白"时剥（YAML 规则：<c>a:b</c> 的冒号不是注释语境，
                    // 而 <c>port: 80 # x</c> 的 # 是注释）。这里只保守处理带空白的形态。
                    int h = IndexCommentOutside(val);
                    if (h >= 0) val = val.Substring(0, h).TrimEnd();
                    // 键值都可能是 ""：key 空则整行不算键值。
                    if (key.Length == 0) return null;
                    return (key, val);
                }
            }
            // 收尾：冒号在最后一字符（`key:` 空值父键）。
            string tt = s.TrimEnd();
            if (tt.EndsWith(":", StringComparison.Ordinal) && tt.Length > 1)
            {
                string key = Unquote(tt.Substring(0, tt.Length - 1).Trim());
                if (key.Length != 0) return (key, "");
            }
            return null;
        }

        /// <summary>找引号/嵌套外、且前面是空白的 <c>#</c>（行内尾注）；找不到回 -1。</summary>
        private static int IndexCommentOutside(string s)
        {
            char quote = '\0';
            int depth = 0;
            for (int i = 1; i < s.Length; i++)
            {
                char ch = s[i];
                if (quote != '\0')
                {
                    if (ch == quote) quote = '\0';
                    continue;
                }
                if (ch is '"' or '\'') { quote = ch; continue; }
                if (ch is '{' or '[') { depth++; continue; }
                if (ch is '}' or ']') { depth--; continue; }
                if (ch == '#' && depth == 0 && (s[i - 1] == ' ' || s[i - 1] == '\t'))
                    return i;
            }
            return -1;
        }

        /// <summary>去成对的外层引号（单双都行）；内部引号原样保留，不猜转义。</summary>
        private static string Unquote(string s)
        {
            if (s.Length >= 2 &&
                ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
                return s.Substring(1, s.Length - 2);
            return s;
        }
    }
}
