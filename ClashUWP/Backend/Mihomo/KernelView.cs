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
    ///   · 没测过 = <c>-1</c>，<b>显示成"未测"</b>，不显示 0、也不显示"挂了"；
    ///   · 内置策略名（DIRECT/REJECT/GLOBAL/PASS…）不是组，不列。
    ///
    /// 【为什么每条都自己 try/catch】UI 不能因为一次查询失败就整个白屏 ——
    ///   控制器没起是最常见的情形（内核没连上），那应当是"这一块显示未连接"，不是崩。
    /// </summary>
    internal static class KernelView
    {
        /// <summary>一个可切换的代理组。</summary>
        internal sealed record Group(string Name, string Type, string Now,
                                    IReadOnlyList<Member> Members);

        /// <summary>组里的一个成员（节点）。<see cref="DelayMs"/> 为 -1 表示<b>从没测过</b>。</summary>
        internal sealed record Member(string Name, long DelayMs);

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
                    var (gs, delay) = MihomoController.ReadProxiesTable(doc.RootElement);
                    foreach (var g in gs)
                    {
                        var members = new List<Member>(g.Members.Count);
                        foreach (string name in g.Members)
                        {
                            members.Add(new Member(name,
                                delay.TryGetValue(name, out long d) ? d : -1L));
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

        /// <summary>连接的目标（host:port 或 host）。</summary>
        private static string ReadChainLabel(System.Text.Json.JsonElement c)
        {
            string meta = GetStr(c, "metadata");
            if (meta.Length > 0)
            {
                try
                {
                    using var md = System.Text.Json.JsonDocument.Parse(meta);
                    string host = GetStr(md.RootElement, "host");
                    string port = GetStr(md.RootElement, "destinationPort");
                    if (host.Length > 0) return port.Length > 0 ? $"{host}:{port}" : host;
                }
                catch { /* 元数据不是 JSON 就退回下面那条 */ }
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
