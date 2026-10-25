using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ClashUWP.Backend.Mihomo
{
    /// <summary>
    /// 【后端 · UI 用的结构化内核视图】
    ///
    /// 【为什么要有这一层，而不是让 UI 去解析 <c>MihomoController</c> 的文本】
    ///   <c>MihomoController</c> 那些 <c>RunXxxAsync</c> 返回的是<b>给人读的文本</b>：
    ///   带缩进、带"…"截断、带中文说明、还把错误也拼成一段话。
    ///   UI 要的是<b>数据</b>（有哪些组、每组哪些节点、当前选谁、延迟多少、连接几条）。
    ///   让 UI 去正则解析文本 = 把格式当契约，格式一改 UI 就静默出错。
    ///   ⇒ 这一层直接查 REST、返回 record，<b>与文本通道并存、互不影响</b>。
    ///
    /// 【★ 口径全部沿用 <c>MihomoController</c> 已实测的那几条，不另立标准】
    ///   · 组类型认 <c>Selector</c>/<c>URLTest</c>/<c>Fallback</c>/<c>LoadBalance</c>
    ///     （内核给的是驼峰形态）；
    ///   · 延迟取<b>每个节点自己的顶层条目</b>的 <c>history[]</c> 末元素，
    ///     组上的 <c>history</c> 恒为 <c>[]</c>；
    ///   · 没测过 = <c>-1</c>。★【1.0.75 对齐安卓】UI 这时<b>不显示延迟</b>，
    ///     而不是显示"未测" —— CMFA 的判据是 <c>if (proxy.delay in 0..Short.MAX_VALUE)</c>
    ///     才画那颗延迟胶囊，没测过就是没有；把"没有"写成两个字，在一整屏
    ///     <c>lazy: true</c> 的订阅里只会变成噪音（<c>select</c> 组首连必然全员未测）。
    ///   ★【1.0.93】<c>0</c> 是<b>本轮不通</b>这一档，不是"0 毫秒"：官方测速失败时往 history
    ///     写的就是 <c>Delay=0</c>（<c>adapter/adapter.go:170-177</c>、<c>:190</c> 的 defer），
    ///     UI 据此把它渲染成"不通"。这条<b>只引官方源码</b>，不引安卓 —— 本机没有 CMFA 源码
    ///     可查它那颗胶囊怎么画 0，见 Docs/49。
    ///     文本通道（debug <c>groups</c>）仍写 <c>delay=-</c>，那里要的是可复盘的明确性。
    ///   · 内置策略名（DIRECT/REJECT/GLOBAL/PASS…）不是组，不列；
    ///   · <b>成员要带分类</b>（组 / 内置策略 / 真节点），见 <see cref="Member"/>。
    ///
    /// 【为什么每条都自己 try/catch】UI 不能因为一次查询失败就整个白屏 ——
    ///   控制器没起是最常见的情形（内核没连上），那应当是"这一块显示未连接"，不是崩。
    /// </summary>
    internal static class KernelView
    {
        /// <summary>一个可切换的代理组。</summary>
        internal sealed record Group(string Name, string Type, string Now,
                                    IReadOnlyList<Member> Members)
        {
            /// <summary>
            /// 能不能手选：<b>只有 Selector 与 Relay 可以</b>（二者都是手动组，选择由用户决定且持久）
            /// —— 与安卓版一致（CMFA 的 <c>PagerFragment</c> 里只有可手选的组才把
            /// 点行动作接到 <c>patchSelector</c>）。url-test / fallback / load-balance
            /// 是只读页：自动组的选择由内核测速/故障转移维护，手选会在下一轮被改回去，
            /// 给一个"点了会自己变回去"的按钮不如说明白它是谁在选。
            /// ★【1.0.81 · M3/Q7】补 <c>Relay</c>/<c>relay</c>：relay 与 Selector 同为手动组，
            ///   官方 Mihomo 同样支持手选切换；<c>LoadBalance</c> 维持不可手选。
            /// ★【1.0.84 · 判据抽函数】"类型名 → 能不能手选"抽到 <see cref="IsUserSelectableType"/>，
            ///   与"某个成员是不是一个可手选的子组"共用同一份 —— Q7 那条"两处口径分叉"不能再犯一遍。
            /// </summary>
            internal bool IsUserSelectable => IsUserSelectableType(Type);

            /// <summary>成员里<b>真节点</b>的个数（嵌套组与内置策略不算，安卓口径）。</summary>
            internal int RealNodeCount
            {
                get
                {
                    int n = 0;
                    foreach (Member m in Members)
                        if (!m.IsGroup && !m.IsBuiltin) n++;
                    return n;
                }
            }
        }

        /// <summary>
        /// "这个类型名能不能手选"的<b>唯一</b>判据（组的类型、成员里嵌套组的类型都走这里）。
        /// 认内核给的驼峰形态，配置里的短写法也一并收（同 <c>MihomoController.IsSelectableGroupType</c>）。
        /// </summary>
        internal static bool IsUserSelectableType(string type) =>
            type is "Selector" or "select" or "Relay" or "relay";

        /// <summary>
        /// 按名字找组。<b>Ordinal</b> 原样比 —— 组名带 emoji、带空格、带前导空格都是订阅里的常态，
        /// 任何规范化都会让"点一个组"跳错或干脆找不到。
        /// 两处用得上：<c>MainComponent.MemberList</c> 的页头与组行（组行副标题里那个
        /// "真节点 N" 是这个组<b>自己</b>的 <see cref="Group.RealNodeCount"/>，不是父组的）。
        /// </summary>
        internal static Group? Find(IReadOnlyList<Group> groups, string name)
        {
            foreach (var g in groups)
                if (string.Equals(g.Name, name, StringComparison.Ordinal)) return g;
            return null;
        }

        /// <summary>
        /// 组里的一个成员。<see cref="DelayMs"/> 为 -1 表示<b>从没测过</b>，
        /// 为 0 表示<b>本轮不通</b>（官方失败时写 Delay=0，见文件顶部那条口径）。
        ///
        /// 【★ 为什么成员要带分类】安卓的分类是内核在 Go 里做好的
        ///   （<c>convertProxies</c>：能转成 <c>outboundgroup.ProxyGroup</c> 的就是组），
        ///   前端只负责呈现。我们以前把这层信息丢了 —— 成员表里"嵌套组 / 内置策略 /
        ///   真节点"三样长得完全一样，还统一显示"未测"，于是"代理组成了节点"。
        ///   现在把 <c>MihomoController.ReadProxiesTable</c> 的 <c>ByName</c> 带下来，
        ///   UI 才能照安卓那样给每行一条副标题（组显示 <c>Selector(现值)</c>、
        ///   节点显示协议类型），并且只对真节点显示延迟。
        /// ★【1.0.118 · 配置视图】<see cref="Server"/> / <see cref="Port"/> 只在
        ///   <c>ConfigNodeView</c>（内核未运行时读 config.yaml 的"配置视图"）里填 ——
        ///   REST <c>/proxies</c> 不回这两个字段。内核实时路径恒为空串，UI 据此
        ///   "有值才画"，两条来源共用同一个 Member 模型，不分叉出第二套数据。
        /// </summary>
        internal sealed record Member(string Name, long DelayMs, string Type,
                                     bool IsGroup, bool IsBuiltin, string Now,
                                     string ProviderName,
                                     string Server = "", string Port = "");

        /// <summary>
        /// 一条活动连接。
        /// ★【1.0.119 · G4】多了 <see cref="Id"/> —— 强断单个连接要靠它
        ///   （<c>DELETE /connections/{id}</c>）。这一列以前<b>在解析时被丢掉</b>：
        ///   内核原文里有 <c>id</c>，旧记录只留 <c>Target/Chain/Upload/Download</c>，
        ///   于是界面即使画出"断开"按钮也无从可断。
        ///   <c>Target</c> 在 metadata 缺失时仍会退化成 <c>id</c>（1.0.75 那条修复的口径），
        ///   但那只是显示兜底，<b>不能</b>拿它当 <see cref="Id"/> 用。
        /// ★【1.0.119】<see cref="Network"/>（<c>tcp</c>/<c>udp</c>）也一并读出来：
        ///   强断一条 UDP 与强断一条 TCP 的观感不同（UDP 会立刻被应用重头发起来），
        ///   界面不显示协议就没法解释"断开了又冒出来一条"。
        /// </summary>
        internal sealed record Conn(string Id, string Target, string Chain,
                                    long Upload, long Download, string Network);

        /// <summary>
        /// 把 <c>GET /connections</c> 的原文解析成 <see cref="Conn"/> 列表。
        /// ★【1.0.119 · 为什么单独成函数】这一层以前嵌在 <c>LoadAsync</c> 里，
        ///   于是"id 到底读没读""metadata 是对象还是字符串""端口是数字还是字符串"
        ///   这几条只能等装机后从界面上看一眼。抽成纯函数后
        ///   <c>Tools/YamlShapeTest</c> 能拿内核原文的样例 JSON 逐条钉住
        ///   （1.0.75 那次"metadata 被当字符串读、界面全是 UUID"的静默失效，
        ///   离线一条断言就能拦住）。
        /// 内核原文里 <c>connections</c> 不是数组（旧内核/错误响应）时返回空表，不抛。
        /// ★ 根元素<b>本身</b>不是对象时也一样返回空表：
        ///   <see cref="System.Text.Json.JsonElement.TryGetProperty(string, out System.Text.Json.JsonElement)"/>
        ///   对数组/字符串根<b>是抛</b> <c>InvalidOperationException</c> 而不是返回 false ——
        ///   这条不是想象出来的 —— 夹具里那句"不是数组→空表，不抛"第一次跑就抛在这，
        ///   根元素是数组时 <c>TryGetProperty</c> 直接炸（<c>Tools/YamlShapeTest</c> 的 G4 组）。
        /// </summary>
        internal static List<Conn> ParseConnections(System.Text.Json.JsonElement root)
        {
            var list = new List<Conn>();
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                return list;
            if (!root.TryGetProperty("connections", out var arr) ||
                arr.ValueKind != System.Text.Json.JsonValueKind.Array)
                return list;
            foreach (var c in arr.EnumerateArray())
            {
                if (c.ValueKind != System.Text.Json.JsonValueKind.Object)
                    continue;
                list.Add(new Conn(
                    GetStr(c, "id"),
                    ReadChainLabel(c),
                    ReadChainText(c),
                    ReadJsonInt(c, "upload", 0),
                    ReadJsonInt(c, "download", 0),
                    ReadNetwork(c)));
            }
            return list;
        }

        /// <summary><c>metadata.network</c>（<c>tcp</c>/<c>udp</c>）；读不到给空串，不猜。</summary>
        private static string ReadNetwork(System.Text.Json.JsonElement c)
        {
            if (c.TryGetProperty("metadata", out var md) &&
                md.ValueKind == System.Text.Json.JsonValueKind.Object)
                return GetStr(md, "network");
            return "";
        }

        /// <summary>
        /// 内核的累计流量与常驻内存（<c>GET /connections</c> 响应的<b>根级</b>字段）。
        ///
        /// 【口径来源 —— 逐字读的官方源码，不是猜的】
        ///   <c>mihomo v1.19.32 / tunnel/statistic/manager.go:124</c>：
        ///   <code>
        ///   type Snapshot struct {
        ///       DownloadTotal int64 `json:"downloadTotal"`
        ///       UploadTotal   int64 `json:"uploadTotal"`
        ///       Connections   []*TrackerInfo `json:"connections"`
        ///       Memory        uint64 `json:"memory"`
        ///   }
        ///   </code>
        ///   <c>Manager.Snapshot()</c>（同文件 <c>:84</c>）填的是 <c>downloadTotal.Load()</c> ——
        ///   ★ <b>是累计字节，不是速率</b>。速率在 Go 侧叫 blip（<c>:117 handle()</c> 里
        ///   每秒 <c>Blip.Store(Temp.Swap(0))</c>），<b>不在这个响应里</b>，
        ///   只经 <c>/traffic</c> 与 <c>/connections</c> 的 WebSocket 升级推送
        ///   （<c>hub/route/server.go:371</c>、<c>hub/route/connections.go:25</c>）。
        ///
        /// 【所以速率怎么来】<b>两次轮询差分</b>：<c>(cur - prev) / (now - prevAt)</c>。
        ///   这条路线的依据就是上面那段源码 —— 累计量单调增、且由内核自己维护，
        ///   我们不需要新开通道，只需要 3 秒轮询<b>已经打过</b>的那一个请求
        ///   （<c>/connections</c> 本来就在打，这一版只是把以前<b>丢掉的根级三个字段读出来</b>）。
        ///   差分的判据（计数器变小 = <c>ResetStatistic</c>/内核重启、间隔非正、首个样本）
        ///   在 <c>TrafficRate</c> 里，是纯函数、离线钉死。
        ///
        /// 【<see cref="Memory"/> 是白拿的】同一个响应里的 <c>memory</c> 是内核 RSS
        ///   （<c>manager.go:80 updateMemory()</c> 走 <c>memory.GetMemoryInfo(pid)</c>）。
        ///   Docs/60 那条内存实测一直在手工量，这一版起界面上常驻可见。
        ///   读不到给 <c>0</c>，UI 必须按"0 = 没有这个数"处理，不许画成"内存 0 B"。
        /// </summary>
        internal readonly record struct Traffic(long Upload, long Download, long Memory);

        /// <summary>
        /// 从 <c>GET /connections</c> 的根元素读出累计流量与内存。
        /// 纯函数（原文进、数据出）⇒ <c>Tools/YamlShapeTest</c> 直接钉字段名。
        /// 根不是对象、或键缺失/类型不对 ⇒ 对应值为 <c>0</c>，不抛、不猜。
        /// </summary>
        internal static Traffic ParseTraffic(System.Text.Json.JsonElement root)
        {
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                return new Traffic(0, 0, 0);
            return new Traffic(
                ReadJsonInt(root, "uploadTotal", 0),
                ReadJsonInt(root, "downloadTotal", 0),
                ReadJsonInt(root, "memory", 0));
        }

        /// <summary>内核运行态总览。</summary>
        /// <remarks>
        /// ★【1.0.118 · 节点浏览与 VPN 解耦】<see cref="FromConfigFile"/> 为真时，
        ///   <see cref="Groups"/> 里的数据来自<b>本地 config.yaml 的解析</b>（配置视图），
        ///   不是内核实时 —— <see cref="Ok"/> 照旧为 false（内核确实不可达），
        ///   <b>不许</b>有实现把"组列出来了"当成"内核起来了"。UI 必须按这两个字段
        ///   分来源画（横幅文案见 <c>MainComponent.ProxyPage</c>）。
        ///   之所以把配置视图折进同一个 Snapshot 而不是新开一路状态：3 秒轮询、
        ///   选择、测速判定的<b>唯一观察者</b>就是这份快照，分两处就会各说各话。
        /// </remarks>
        internal sealed record Snapshot(
            bool Ok,
            string Error,
            string Mode,
            string LogLevel,
            string InterfaceName,
            IReadOnlyList<Group> Groups,
            IReadOnlyList<Conn> Connections,
            IReadOnlyList<MihomoController.ProviderInfo> Providers,
            bool FromConfigFile = false,
            string ConfigNote = "",
            // ★【1.0.119 · 实时流量】累计字节与内核 RSS（<c>GET /connections</c> 根级三字段）。
            //   默认 <c>default(Traffic)</c> = 三个 0 ⇒ "没有这个数"，配置视图/失败快照都走这里，
            //   UI 不许把 0 画成"流量 0 / 内存 0"（见 <see cref="Traffic"/> 那条）。
            Traffic Totals = default)
        {
            internal static Snapshot Failed(string error) => new(
                false, error, "", "", "",
                System.Array.Empty<Group>(), System.Array.Empty<Conn>(),
                System.Array.Empty<MihomoController.ProviderInfo>());
        }

        /// <summary>
        /// 一次拉全：运行态三要素 + 可切换组 + 活动连接。
        /// 任何一段失败都只让<b>那一段</b>为空，不影响其余。
        /// </summary>
        internal static async Task<Snapshot> LoadAsync()
        {
            string mode = "", level = "", iface = "";
            var groups = new List<Group>();
            var conns = new List<Conn>();
            string error = "";

            // ① 运行态三要素：GET /configs
            try
            {
                using var http = MihomoController.NewLoopbackClient(10);
                string json = await http.GetStringAsync("http://127.0.0.1:9090/configs");
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                mode = GetStr(root, "mode");
                level = GetStr(root, "log-level");
                iface = GetStr(root, "interface-name");
            }
            catch (Exception ex)
            {
                error = $"读 /configs 失败: {ex.GetType().Name}: {ex.Message}";
            }

            // ② 可切换组：GET /proxies
            if (error.Length == 0)
            {
                try
                {
                    using var http = MihomoController.NewLoopbackClient(10);
                    string json = await http.GetStringAsync("http://127.0.0.1:9090/proxies");
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var (gs, delay, byName, _) = MihomoController.ReadProxiesTable(doc.RootElement);
                    groups = BuildGroups(gs, byName, delay);
                }
                catch (Exception ex)
                {
                    error = $"读 /proxies 失败: {ex.GetType().Name}: {ex.Message}";
                }
            }

            // ③ 活动连接：GET /connections
            //   ★【1.0.119】这一路<b>顺手</b>把根级的累计流量/内存也读出来（<see cref="ParseTraffic"/>）：
            //   请求本来就在打，以前只取 <c>connections</c> 数组、把三个根字段丢了。
            //   速率由 <c>TrafficRate</c> 在 UI 侧按两次采样差分算 —— 本层不做时间算术，
            //   因为"上一次采到的值"是界面那一条 3 秒循环的状态，不是内核响应的一部分。
            var totals = new Traffic();
            if (error.Length == 0)
            {
                try
                {
                    using var http = MihomoController.NewLoopbackClient(10);
                    string json = await http.GetStringAsync("http://127.0.0.1:9090/connections");
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    conns.AddRange(ParseConnections(doc.RootElement));
                    totals = ParseTraffic(doc.RootElement);
                }
                catch (Exception ex)
                {
                    error = $"读 /connections 失败: {ex.GetType().Name}: {ex.Message}";
                }
            }

            // ④ 提供方状态：GET /providers/proxies（M7/Q10，补充信息）
            //   ★ 独立 try：拉不到<b>不写全局 error</b> —— 它是补充信息，
            //   不该让"代理页"整体显示"未连接"；内核没起时本就为空列表。
            var providers = new List<MihomoController.ProviderInfo>();
            try
            {
                providers = await MihomoController.GetProvidersAsync();
            }
            catch (Exception ex)
            {
                ClashUWP.Vpn.RollingLog.Info("kernelview-providers",
                    $"读 /providers/proxies 失败（补充信息，忽略）: {ex.GetType().Name}");
            }

            // ★【1.0.118 · 节点浏览与 VPN 解耦】内核不可达时落回"配置视图"：
            //   读 config.yaml 里的组/节点定义交给 UI（Ok 仍为 false，FromConfigFile=true
            //   标住来源 —— 不谎称内核在跑）。回环端口关着是瞬时拒绝，这一路的代价
            //   只是读一次文件、行式解析一遍；活动连接与提供方在配置视图里没有，
            //   给空列表（提供方名字由 ConfigNote 一句"需内核运行"代答）。
            if (error.Length > 0)
            {
                var cv = await ConfigNodeView.LoadAsync();
                return new Snapshot(false, error, "", "", "", cv.Groups,
                    System.Array.Empty<Conn>(),
                    System.Array.Empty<MihomoController.ProviderInfo>(),
                    cv.Found, cv.Found ? cv.Note : cv.Error);
            }

            return new Snapshot(true, error, mode, level, iface, groups, conns, providers,
                false, "", totals);
        }

        /// <summary>
        /// 把 <c>ReadProxiesTable</c> 的三张表（组 / 分类 / 延迟）折成 UI 用的 <see cref="Group"/> 列表。
        ///
        /// 【★ 为什么单独抽出来（1.0.84）】这段原来内联在 <see cref="LoadAsync"/> 里。
        ///   抽出来不是为了好看：<c>LoadAsync</c> 要联网打回环，离线夹具（<c>Tools/YamlShapeTest</c>）
        ///   进不去，于是那边只能照抄一遍成员映射 —— "测试自己抄一份"就是测试会先绿后坏的原因。
        ///   现在生产路径与夹具走<b>同一个函数</b>，成员分类不再有两份。
        /// 【查不到条目的成员】按"未知节点"处理（Type 空串、不是组、不是策略）——
        ///   宁可少一条副标题，也不要把未知东西当成组藏起来（这条纪律自 1.0.75 起没变）。
        /// </summary>
        internal static List<Group> BuildGroups(
            List<MihomoController.ProxyGroup> gs,
            Dictionary<string, MihomoController.ProxyInfo> byName,
            Dictionary<string, long> delay)
        {
            var groups = new List<Group>(gs.Count);
            foreach (var g in gs)
            {
                var members = new List<Member>(g.Members.Count);
                foreach (string name in g.Members)
                {
                    byName.TryGetValue(name, out MihomoController.ProxyInfo? info);
                    members.Add(new Member(name,
                        delay.TryGetValue(name, out long d) ? d : -1L,
                        info?.Type ?? "",
                        info?.IsGroup ?? false,
                        info?.IsBuiltin ?? false,
                        info?.Now ?? "",
                        info?.ProviderName ?? ""));
                }
                groups.Add(new Group(g.Name, g.Type, g.Now, members));
            }
            return groups;
        }

        /// <summary>把 <c>chains</c> 数组拼成"规则 &gt; 代理组 &gt; 节点"的一行文本。</summary>
        private static string ReadChainText(System.Text.Json.JsonElement c)
        {
            if (!c.TryGetProperty("chains", out var chains) ||
                chains.ValueKind != System.Text.Json.JsonValueKind.Array)
                return "";
            var parts = new List<string>();
            foreach (var item in chains.EnumerateArray())
            {
                if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    string s = item.GetString() ?? "";
                    if (s.Length > 0) parts.Add(s);
                }
            }
            return string.Join(" > ", parts);
        }

        /// <summary>
        /// 连接的目标（<c>host:port</c>，没 host 才退 <c>IP:port</c>）。
        ///
        /// ★【1.0.75 修的一处静默失效】这里原先写的是 <c>GetStr(c, "metadata")</c> 再
        ///   <c>JsonDocument.Parse</c> 那个字符串 —— 但 <c>metadata</c> 在
        ///   <c>GET /connections</c> 的原文里是<b>对象</b>不是字符串（官方 API 文档口径），
        ///   于是 <c>GetStr</c> 恒返回空串，整条"解析 metadata"的分支从来没进过，
        ///   界面每个连接显示的是 UUID（<c>id</c>）而不是目标。<b>不报错、不崩溃</b>，
        ///   所以一直没人发现 —— 这正是"UI 别解析文本、要看结构化字段"的又一例。
        ///   现在按对象取字段，端口两种形态（数字 / 字符串）都读。
        /// </summary>
        private static string ReadChainLabel(System.Text.Json.JsonElement c)
        {
            if (c.TryGetProperty("metadata", out var md) &&
                md.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                string host = GetStr(md, "host");
                if (host.Length == 0) host = GetStr(md, "sniffHost");
                if (host.Length == 0) host = GetStr(md, "destinationIP");
                if (host.Length > 0)
                {
                    string port = GetStr(md, "destinationPort");
                    if (port.Length == 0)
                    {
                        long p = ReadJsonInt(md, "destinationPort", 0);
                        port = p > 0 ? p.ToString() : "";
                    }
                    return port.Length > 0 ? $"{host}:{port}" : host;
                }
            }
            // ★【1.0.119】兜底从 <c>GetStr(c,"id")</c> 改成明说"目标未知"：
            //   这一列以前就是"界面每个连接显示成 UUID"（Docs/26 那条）的第二个来源 ——
            //   主路径修好之后，metadata 真缺的那几条仍然会被 UUID 顶上去。
            //   id 现在有自己的一列（强断要用），不该再冒充目标。
            return "(目标未知)";
        }

        private static string GetStr(System.Text.Json.JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v)) return "";
            return v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
        }

        /// <summary>
        /// 读一个整数字段，缺/类型不对给 <paramref name="whenMissing"/>。
        /// ★【1.0.119】数字也可能是<b>字符串</b>（老 Clash 的 downloadTraffic 那一族）。
        ///   只认 Number 的那种原文下"↑0 ↓0"会一直显示成 0 —— 读不出来和读出来是 0，
        ///   在界面上长得一模一样。解析按不变文化（订阅里的数字串不该受系统区域影响）。
        /// ★ 这一份是全仓唯一的"数字或字符串"读数；<c>MihomoController.ReadNumber</c>
        ///   只是它的 -1 包装（两份实现迟早漂，读出来的 0 与"没读到"就是这么混起来的）。
        /// </summary>
        internal static long ReadJsonInt(System.Text.Json.JsonElement e, string name, long whenMissing)
        {
            if (!e.TryGetProperty(name, out var v)) return whenMissing;
            if (v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetInt64(out long n))
                return n;
            if (v.ValueKind == System.Text.Json.JsonValueKind.String &&
                long.TryParse(v.GetString(), System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out long s))
                return s;
            return whenMissing;
        }
    }
}
