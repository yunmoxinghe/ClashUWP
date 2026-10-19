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
            /// 能不能手选：<b>只有 Selector 可以</b> —— 与安卓版一致
            /// （CMFA 的 <c>PagerFragment</c> 里只有 <c>group.type == "Selector"</c> 才把
            /// 点行动作接到 <c>patchSelector</c>，url-test / fallback / load-balance
            /// 是只读页）。自动组的选择由内核测速/故障转移维护，手选会在下一轮被改回去，
            /// 给一个"点了会自己变回去"的按钮不如说明白它是谁在选。
            /// ★ 类型名认内核给的驼峰形态，两套写法都收（同 <c>IsSelectableGroupType</c>）。
            /// </summary>
            internal bool IsUserSelectable =>
                Type is "Selector" or "select";

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
        /// 组里的一个成员。<see cref="DelayMs"/> 为 -1 表示<b>从没测过</b>。
        ///
        /// 【★ 为什么成员要带分类】安卓的分类是内核在 Go 里做好的
        ///   （<c>convertProxies</c>：能转成 <c>outboundgroup.ProxyGroup</c> 的就是组），
        ///   前端只负责呈现。我们以前把这层信息丢了 —— 成员表里"嵌套组 / 内置策略 /
        ///   真节点"三样长得完全一样，还统一显示"未测"，于是"代理组成了节点"。
        ///   现在把 <c>MihomoController.ReadProxiesTable</c> 的 <c>ByName</c> 带下来，
        ///   UI 才能照安卓那样给每行一条副标题（组显示 <c>Selector(现值)</c>、
        ///   节点显示协议类型），并且只对真节点显示延迟。
        /// </summary>
        internal sealed record Member(string Name, long DelayMs, string Type,
                                     bool IsGroup, bool IsBuiltin, string Now);

        /// <summary>一条活动连接。</summary>
        internal sealed record Conn(string Target, string Chain, long Upload, long Download);

        /// <summary>内核运行态总览。</summary>
        internal sealed record Snapshot(
            bool Ok,
            string Error,
            string Mode,
            string LogLevel,
            string InterfaceName,
            IReadOnlyList<Group> Groups,
            IReadOnlyList<Conn> Connections)
        {
            internal static Snapshot Failed(string error) => new(
                false, error, "", "", "",
                System.Array.Empty<Group>(), System.Array.Empty<Conn>());
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
                    var (gs, delay, byName) = MihomoController.ReadProxiesTable(doc.RootElement);
                    foreach (var g in gs)
                    {
                        var members = new List<Member>(g.Members.Count);
                        foreach (string name in g.Members)
                        {
                            // ★ 每个成员都去 ByName 查自己的分类；查不到就按"未知节点"处理
                            //   （Type 空串、不是组、不是策略）—— 宁可少一条副标题，
                            //   也不要把未知东西当成组藏起来。
                            byName.TryGetValue(name, out MihomoController.ProxyInfo? info);
                            members.Add(new Member(name,
                                delay.TryGetValue(name, out long d) ? d : -1L,
                                info?.Type ?? "",
                                info?.IsGroup ?? false,
                                info?.IsBuiltin ?? false,
                                info?.Now ?? ""));
                        }
                        groups.Add(new Group(g.Name, g.Type, g.Now, members));
                    }
                }
                catch (Exception ex)
                {
                    error = $"读 /proxies 失败: {ex.GetType().Name}: {ex.Message}";
                }
            }

            // ③ 活动连接：GET /connections
            if (error.Length == 0)
            {
                try
                {
                    using var http = MihomoController.NewLoopbackClient(10);
                    string json = await http.GetStringAsync("http://127.0.0.1:9090/connections");
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("connections", out var arr) &&
                        arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var c in arr.EnumerateArray())
                        {
                            conns.Add(new Conn(
                                ReadChainLabel(c),
                                ReadChainText(c),
                                ReadNum(c, "upload"),
                                ReadNum(c, "download")));
                        }
                    }
                }
                catch (Exception ex)
                {
                    error = $"读 /connections 失败: {ex.GetType().Name}: {ex.Message}";
                }
            }

            return new Snapshot(error.Length == 0, error, mode, level, iface, groups, conns);
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
                        long p = ReadNum(md, "destinationPort");
                        port = p > 0 ? p.ToString() : "";
                    }
                    return port.Length > 0 ? $"{host}:{port}" : host;
                }
            }
            return GetStr(c, "id");
        }

        private static string GetStr(System.Text.Json.JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v)) return "";
            return v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
        }

        private static long ReadNum(System.Text.Json.JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v)) return 0;
            return v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt64() : 0;
        }
    }
}
