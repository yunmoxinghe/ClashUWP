using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets; // AddressFamily 在这个命名空间（IPAddress.AddressFamily 的比较目标）
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Windows.Storage;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// 节点服务器 IP 提取器 —— 安卓版 <c>VpnService.protect()</c> 在 Windows 上的等价物。
    ///
    /// 【为什么需要它】
    ///   安卓 clash 防自环靠 OS 原语：TunModule.attach 把 <c>vpn::protect</c> 当 markSocket
    ///   交给内核，内核每条出站 socket 都被系统强制走物理网卡（mihomo 侧
    ///   <c>AutoDetectInterface: false // implements by VpnService::protect</c>）。
    ///   Windows VPN Plugin 平台<b>没有</b> per-socket protect 原语，唯一等价手段是路由表：
    ///   把每个节点 server 的 IP 作为 <c>/32</c> 塞进 <c>VpnRouteAssignment.Ipv4ExclusionRoutes</c>
    ///   —— 更特定的路由优先，拨号包从路由层面就进不了自己的隧道。
    ///
    /// 【数据来源】
    ///   订阅渲染出的 <c>LocalState\config.yaml</c> 里的 <c>server:</c> 字段，
    ///   同时兼容流式（<c>- { server: 1.2.3.4, ... }</c>）与块式（<c>server: example.com</c>）写法。
    ///   IPv4 字面量直接采纳；域名走 <see cref="Dns"/> 解析（必须在隧道起来<b>之前</b>做，
    ///   此时解析走物理网卡，结果才可信）；IPv6 节点不处理 —— 本插件只下发 IPv4 路由，
    ///   v6 出站天然走物理网卡，不构成自环。
    ///
    /// 【缓存】
    ///   解析结果写 <c>LocalState\node-exclusions.txt</c>（<c>host=ip,ip</c> + 时间戳）。
    ///   Connect 是平台同步回调，不能在这儿长时间等 DNS：命中未过期缓存就用缓存，
    ///   只有缓存缺失/过期才现场解析，且整体限时。刷新失败一律回退上一份缓存 ——
    ///   「少几条排除路由」远好于「插件抛异常导致连接失败」。
    /// </summary>
    internal static partial class NodeRoutes
    {
        /// <summary>缓存有效期。机场换 IP 不频繁，6 小时足够，且能在隧道外刷新几次。</summary>
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

        /// <summary>排除路由上限。Windows 侧路由条数不是无限的，节点数暴涨时截断保命。</summary>
        private const int MaxExclusionRoutes = 240;

        /// <summary>单次刷新里 DNS 解析的总预算（Connect 是同步回调，必须硬限时）。</summary>
        private const int ResolveBudgetMs = 4000;

        private const string ConfigFileName = "config.yaml";
        private const string CacheFileName = "node-exclusions.txt";

        [GeneratedRegex(@"server:\s*['""]?([A-Za-z0-9._:][A-Za-z0-9._:\-]*)['""]?",
                        RegexOptions.CultureInvariant)]
        private static partial Regex ServerFieldRegex();

        /// <summary>
        /// 取本次连接应下发的排除路由（CIDR 列表，全部是 <c>/32</c>）。
        /// 任何异常都返回空列表或旧缓存，绝不抛 —— 调用方在平台回调里。
        /// </summary>
        internal static List<string> BuildExclusionCidrs(string configYaml)
        {
            try
            {
                HashSet<string> hosts = ExtractHosts(configYaml);
                Dictionary<string, List<string>> map = LoadOrCreateCache(out bool fresh, out bool stale);

                // ★【1.0.38 · G6】stale（文件里的 Stale= 行，或进程内 _staleReason）优先级高于 TTL：
                //   它的语义是"这批结果是在上一个网络里量出来的"。见 Invalidate 的注释。
                bool forceAll = stale || _staleReason.Length > 0;
                bool allOk = true;
                if (forceAll || (!fresh && hosts.Count > 0))
                    allOk = RefreshBlocking(map, hosts, forceAll);

                // 只有"这一轮真的把所有人都重解析成功"才清掉作废标记：
                //   预算用完 / 个别域名解析失败时保留旧值，同时把标记留到下一次连接 ——
                //   否则 SaveCache 会写一个新的 Time=，失败的那几条就要再等 6 小时才会重试。
                _staleReason = (forceAll && !allOk)
                    ? (_staleReason.Length > 0 ? _staleReason : "上轮重解析未全部成功")
                    : "";

                // 落盘（尽力而为，写失败不影响本次连接）
                SaveCache(map, _staleReason);

                var cidrs = new List<string>();
                var seen = new HashSet<string>();
                foreach (List<string> ips in map.Values)
                {
                    foreach (string ip in ips)
                    {
                        if (seen.Add(ip))
                            cidrs.Add(ip + "/32");
                        if (cidrs.Count >= MaxExclusionRoutes)
                            return cidrs;
                    }
                }
                return cidrs;
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// ★【1.0.38 · G6】作废这份缓存 —— 换网络之后，"节点域名/IP → 可达 IPv4"这个映射
        /// 是在<b>上一个网络</b>里量出来的，不能接着信（不同运营商对同一域名给的答案可以完全不同，
        /// 而排除路由错一条就是节点拨号包被自家隧道抓回去）。
        ///
        /// 【为什么只打标记、不删文件】本类注释里那条口径不能丢：
        ///   "刷新失败一律回退上一份缓存 —— 少几条排除路由远好于插件抛异常导致连接失败"。
        ///   删掉文件 = 下一次连接如果解析失败，连"旧值"这个兜底都没有。
        ///   所以这里写一行 <c>Stale=原因</c> 进缓存文件：
        ///   <see cref="LoadOrCreateCache"/> 读到它就把<b>所有</b> host 重新解析一遍
        ///   （<see cref="RefreshBlocking"/> 的 <c>forceAll</c>），
        ///   解析失败的 host 保留旧值，成功后连这行标记一起清掉。
        ///
        /// 【为什么不在这里立刻重解析】解析必须在<b>隧道外</b>做才可信（类注释同一句），
        ///   而调用点是会话<b>中途</b>的网络变化事件 —— 此刻解析走的是隧道，
        ///   结果比旧值更不可信（memory「VPN forensics metric traps」那条口径）。
        ///   所以本方法只是把"下一次连接必须重量"这件事写进档案，不动网络。
        /// ★ 进程内也同时记一份：文件写不了（极少见）时，本进程的下次连接仍会重解析。
        /// </summary>
        internal static void Invalidate(string reason)
        {
            _staleReason = string.IsNullOrWhiteSpace(reason) ? "未记原因" : reason.Replace("\r", " ").Replace("\n", " ");
            try
            {
                StorageFile file = ApplicationData.Current.LocalFolder
                    .GetFileAsync(CacheFileName).AsTask().GetAwaiter().GetResult();
                string text = FileIO.ReadTextAsync(file).AsTask().GetAwaiter().GetResult();

                // 把旧的 Stale= 行去掉，新的插在开头（读的时候只看第一处，保持一行一个键的形态）。
                var sb = new StringBuilder();
                sb.AppendLine($"Stale={_staleReason}");
                foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
                {
                    string l = raw.Trim();
                    if (l.StartsWith("Stale=", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (l.Length > 0)
                        sb.AppendLine(l);
                }
                FileIO.WriteTextAsync(file, sb.ToString()).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // 标记写不进去不影响进程内那份，只是"进程重建后可能沿用旧缓存"，
                // 这条得留在日志里，不然就成了静默失效。
                RollingLog.Warn("node-routes", $"作废标记写入失败={ex.GetType().Name}（进程内标记仍有效：{_staleReason}）");
            }
        }

        private static string _staleReason = "";

        /// <summary>缓存档案的头两行（<c>Time=…</c> / 有没有 <c>Stale=…</c>），诊断快照用。</summary>
        internal static string DescribeCacheAge()
        {
            try
            {
                StorageFile file = ApplicationData.Current.LocalFolder
                    .GetFileAsync(CacheFileName).AsTask().GetAwaiter().GetResult();
                string text = FileIO.ReadTextAsync(file).AsTask().GetAwaiter().GetResult();
                string time = "无时间戳";
                bool stale = false;
                foreach (string raw in text.Split('\n'))
                {
                    string l = raw.Trim();
                    if (stale && time != "无时间戳") break;
                    if (l.StartsWith("Time=", StringComparison.OrdinalIgnoreCase))
                        time = l.Substring(5);
                    else if (l.StartsWith("Stale=", StringComparison.OrdinalIgnoreCase))
                        stale = true;
                }
                return time + (stale ? " (已作废待重解析)" : "");
            }
            catch { return _staleReason.Length > 0 ? "无缓存（进程内已标作废）" : "无缓存"; }
        }

        /// <summary>从 YAML 文本里抠出所有 server 字段值（去重、去空）。</summary>
        internal static HashSet<string> ExtractHosts(string configYaml)
        {
            var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(configYaml))
                return hosts;

            foreach (Match m in ServerFieldRegex().Matches(configYaml))
            {
                string v = m.Groups[1].Value.Trim().TrimEnd('.', ',');
                if (v.Length == 0)
                    continue;
                // 排除明显不是主机名的值（如端口拼接残留）
                if (v.IndexOfAny(new[] { '\r', '\n', '{', '}', ':', '/' }) >= 0 && !IsIpv6(v))
                    continue;
                hosts.Add(v);
            }
            return hosts;
        }

        /// <summary>把已算好的排除路由拼成给 CustomConfiguration 用的 XML 片段（诊断/前台复用）。</summary>
        internal static string Describe(Dictionary<string, List<string>> map)
        {
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, List<string>> kv in map.OrderBy(k => k.Key))
                sb.AppendLine($"{kv.Key} = {string.Join(",", kv.Value)}");
            return sb.ToString();
        }

        // ── 内部实现 ────────────────────────────────────────────────────────

        private static bool IsIpv6(string v) => v.Contains(':') && v.IndexOf('.') < 0;

        /// <param name="forceAll">
        /// true = 缓存就算没过期也重解析（换网后的作废标记）；false = 只补缓存里缺的。
        /// </param>
        /// <returns>true = 所有待解析 host 都拿到了结果；false = 有失败/预算用完（保留旧值）。</returns>
        private static bool RefreshBlocking(Dictionary<string, List<string>> map, HashSet<string> hosts, bool forceAll)
        {
            // 需要解析的：缓存里没有的；forceAll 时连缓存里有的也重来。
            var pending = new List<string>();
            foreach (string host in hosts)
            {
                if (!forceAll &&
                    map.TryGetValue(host, out List<string>? cached) && cached.Count > 0)
                    continue;

                if (IPAddress.TryParse(host, out IPAddress? literal))
                {
                    if (literal.AddressFamily == AddressFamily.InterNetwork)
                    {
                        map[host] = new List<string> { literal.ToString() };
                        continue;
                    }
                    // IPv6 节点：v4 隧道不捕获，留空占位避免每次重连再解析
                    map[host] = new List<string>();
                    continue;
                }
                pending.Add(host);
            }

            // 缓存里已有的 IPv4 字面量条目不重复解析；只补差集。
            if (pending.Count == 0)
                return true;

            bool allOk = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (string host in pending)
            {
                int left = ResolveBudgetMs - (int)sw.ElapsedMilliseconds;
                if (left <= 200)
                {
                    allOk = false;
                    break; // 预算用完：剩下的沿用旧值/空缺，下次连接再补
                }
                try
                {
                    var task = Dns.GetHostAddressesAsync(host);
                    if (!task.Wait(left))
                    {
                        allOk = false;
                        break; // 超时说明本网段解析已经不通，继续也没意义
                    }
                    List<string> v4 = task.Result
                        .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.ToString())
                        .Distinct()
                        .ToList();
                    map[host] = v4;
                }
                catch
                {
                    // 解析失败保留旧值（不覆盖成空，避免一次抖动把排除路由全丢掉）
                    allOk = false;
                }
            }
            return allOk;
        }

        private static Dictionary<string, List<string>> LoadOrCreateCache(out bool fresh, out bool stale)
        {
            fresh = false;
            stale = false;
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                StorageFile file = ApplicationData.Current.LocalFolder
                    .GetFileAsync(CacheFileName).AsTask().GetAwaiter().GetResult();
                string text = FileIO.ReadTextAsync(file).AsTask().GetAwaiter().GetResult();
                foreach (string line in text.Split('\n'))
                {
                    string l = line.Trim();
                    if (l.StartsWith("Time=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (DateTime.TryParse(l.Substring(5), out DateTime t) &&
                            DateTime.Now - t < CacheTtl)
                            fresh = true;
                        continue;
                    }
                    // ★【1.0.38 · G6】作废标记：只置位、不当 host 解析
                    //   （否则 map 里会多出一条 "Stale = 网络变化:…"，它还会被当成节点 IP 拼成路由）。
                    if (l.StartsWith("Stale=", StringComparison.OrdinalIgnoreCase))
                    {
                        stale = true;
                        continue;
                    }
                    if (l.Length == 0 || !l.Contains('='))
                        continue;
                    int idx = l.IndexOf('=');
                    string host = l.Substring(0, idx).Trim();
                    string ips = l.Substring(idx + 1).Trim();
                    map[host] = ips.Length == 0
                        ? new List<string>()
                        : ips.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                }
            }
            catch
            {
                fresh = false;
                stale = false;
                map.Clear();
            }
            return map;
        }

        private static void SaveCache(Dictionary<string, List<string>> map, string staleReason)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Time={DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                // ★【1.0.38 · G6】重解析没全部成功时，把这行原样写回去
                //   —— Time= 是"上次成功落盘时间"，它不能替 Stale= 表达"结果还是旧网络的"。
                if (!string.IsNullOrEmpty(staleReason))
                    sb.AppendLine($"Stale={staleReason}");
                foreach (KeyValuePair<string, List<string>> kv in map.OrderBy(k => k.Key))
                    sb.AppendLine($"{kv.Key}={string.Join(",", kv.Value)}");

                StorageFile file = ApplicationData.Current.LocalFolder
                    .CreateFileAsync(CacheFileName, CreationCollisionOption.ReplaceExisting)
                    .AsTask().GetAwaiter().GetResult();
                FileIO.WriteTextAsync(file, sb.ToString()).AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // 诊断缓存，写不了就算了
            }
        }
    }
}
