using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// ★【1.0.119 · G7 收口】隧道路由表的<b>纯判据</b>：给定"订阅带来的路由 + 各开关档位"，
    /// 算出该往 <c>VpnRouteAssignment</c> 四个桶（v4/v6 × include/exclude）里各放什么。
    ///
    /// ══ 为什么要从 <c>ClashVpnPlugin.BuildRoutes</c> 里搬出来 ══
    ///   <c>VpnRouteAssignment</c> 是平台对象：离线拿不到，拨号又不允许在这台机器上跑，
    ///   于是"放哪些路由"这段判断此前<b>没有任何一条判据被钉住过</b>
    ///   （<c>Tools/YamlShapeTest</c> 只调 <c>KernelConfig.Transform</c>，路由那一层是空白）。
    ///   搬成纯函数后，四桶的形状、族别、去重、被剔掉的项和剔除原因都能在装机前逐条断言。
    ///
    /// ══ 这一版真正修掉的两条族别错配（都在 <c>ipv6≥1</c> 才会咬人）══
    ///   ① <b>v6 节点服务器的排除路由整条缺失</b>。防自环靠"节点外层 IP 的宿主路由"
    ///     （安卓 <c>VpnService.protect()</c> 在本平台的路由层等价物）。旧代码往
    ///     <c>Ipv4ExclusionRoutes</c> 里塞的时候用 <c>address.IndexOf(':') &lt; 0</c>
    ///     把 v6 项<b>全跳过</b>，而 <see cref="NodeRoutes"/> 生成的候选串又是
    ///     <c>ip + "/32"</c> —— 对 v6 地址来说这个前缀长度本来就是错的（宿主路由在 v6 上是 /128）。
    ///     结果：<c>ipv6=1</c> 下发 <c>2000::/3</c> 之后，拨向 <b>v6 节点</b>的外层包
    ///     落进自己那条 include ⇒ 回环黑洞，而这条路上没有任何排除路由兜它。
    ///     订阅里有 v6 节点才会复现；<c>FirstNodeOuterTarget</c> 那句"v6 节点与环回都不适合
    ///     做 v4 socket 的外层目标"正是同一批数据留下的痕迹。
    ///   ② <b>profile 里的 v6 排除段会被塞进 v4 桶</b>。<c>config.xml</c> 的
    ///     <c>&lt;excludeRoute&gt;</c> 逐条原样读进来，旧循环不分辨族别，
    ///     于是一条 <c>fd00::/8</c> 会变成 <c>Ipv4ExclusionRoutes</c> 里
    ///     一个"v4 路由对象装了 v6 地址"的垃圾项（平台侧行为未知，正是最该避免的那类未知）。
    ///
    /// ══ ★ 铁律：v6 的 include 里绝不出现 <c>::/0</c> ══
    ///   先遣项目 docs/96 §6.2.1 四臂实测：<c>::/0</c> 一下发，Windows 会把这条 VPN 同时变成
    ///   <b>IPv4 默认路由</b>（4/4 臂一致）；<c>2000::/3</c> 不触发（0/2 臂）。
    ///   这条判据以前只写在注释里，现在写进代码并单测：<see cref="IsBlockedV6Include"/>
    ///   对前缀 0 一律拒绝，理由进 <see cref="Plan.Dropped"/> 落日志。
    ///
    /// ══ arm=0 必须逐字等于旧行为 ══
    ///   <see cref="LocalSettings.Ipv6"/> 默认 0，而"v6 完全不碰"是唯一被装机验证过的形态。
    ///   ⇒ <c>arm&lt;1</c> 时 v6 的两个桶都<b>空</b>：不给 v6 include（旧行为），
    ///     也不给 v6 exclude —— 接口上没有 v6 地址时，v6 路由装不装得上、装失败会不会
    ///     连带整个 <c>Start</c> 失败，都<b>没实测过</b>，不该顺手去碰。
    ///   ⇒ 泄漏面不靠改默认档解决，靠 <see cref="LeakNote"/> 把它说出口。
    /// </summary>
    internal static class RouteInclusion
    {
        /// <summary>全局单播覆盖段 —— v6 侧唯一允许下发的"广义"include（理由见类注释里的铁律）。</summary>
        internal const string GlobalV6Include = "2000::/3";

        /// <summary>路由表的输入：全部是插件在步 4/步 5 已经算好的东西，本类不做 IO。</summary>
        internal readonly record struct Input(
            IEnumerable<string> ProfileIncludes,
            IEnumerable<string> NarrowIncludes,
            bool UseNarrowIncludes,
            int Ipv6Arm,
            string Ipv6Pool,
            IEnumerable<string> ProfileExcludes,
            IEnumerable<string> NodeExcludes,
            bool ApplyNodeExcludes);

        /// <summary>
        /// 路由表的结论。四个桶里每一项都是 <c>地址/前缀</c>（族别已纠正），
        /// 由调用方 <c>BuildRoutes</c> 逐条 <see cref="SplitCidr"/> 后放进 <c>VpnRoute</c>。
        /// </summary>
        internal readonly record struct Plan(
            IReadOnlyList<string> Ipv4Includes,
            IReadOnlyList<string> Ipv6Includes,
            IReadOnlyList<string> Ipv4Excludes,
            IReadOnlyList<string> Ipv6Excludes,
            IReadOnlyList<string> Dropped)
        {
            /// <summary>一行短摘要，进 <c>dataface-plugin.txt</c> 快照（行格式是机器契约：不换行、不刷长文本）。</summary>
            internal string Describe() =>
                $"v4in=[{string.Join(",", Ipv4Includes)}] v6in=[{string.Join(",", Ipv6Includes)}] " +
                $"v4ex={Ipv4Excludes.Count} v6ex={Ipv6Excludes.Count}" +
                (Dropped.Count == 0 ? "" : $" 剔除={Dropped.Count}");
        }

        /// <summary>算出四个桶。纯函数，可离线单测。</summary>
        internal static Plan Build(in Input i)
        {
            var dropped = new List<string>();

            // ── include：v4 ────────────────────────────────────────────────
            //   narrow / wide 的取舍理由在调用方（Docs/28-A），这里只管"别把 v6 项塞进 v4 桶"。
            var v4Includes = new List<string>();
            var seenIn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IEnumerable<string> includes = i.UseNarrowIncludes ? i.NarrowIncludes : i.ProfileIncludes;
            foreach (string cidr in includes)
            {
                if (!SplitCidr(cidr, out string addr, out int prefix, out bool isV6))
                {
                    dropped.Add("include 非法=" + One(cidr));
                    continue;
                }
                if (isV6)
                {
                    dropped.Add("include v6 不进 v4 桶=" + One(cidr));
                    continue;
                }
                string n = Normalize(addr, prefix, false);
                if (seenIn.Add(n))
                    v4Includes.Add(n);
            }

            // ── include：v6 ────────────────────────────────────────────────
            List<string> v6Includes = Ipv6IncludeRoutes(i.Ipv6Arm, i.Ipv6Pool, dropped);

            // ── exclude：v4（先 profile 的局域网段，再节点宿主路由）────────────
            //   去重跨两次循环共用一个集合 —— 沿用旧实现：节点 IP 撞上局域网段时不重复下发。
            var v4Excludes = new List<string>();
            var v6Excludes = new List<string>();
            var seenEx = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string cidr in i.ProfileExcludes)
            {
                if (!SplitCidr(cidr, out string addr, out int prefix, out bool isV6))
                {
                    dropped.Add("exclude 非法=" + One(cidr));
                    continue;
                }
                if (!isV6)
                {
                    string n = Normalize(addr, prefix, false);
                    if (seenEx.Add(n))
                        v4Excludes.Add(n);
                    continue;
                }
                if (i.Ipv6Arm < 1)
                {
                    dropped.Add("exclude v6 且 arm=0 丢弃=" + One(cidr));
                    continue;
                }
                if (prefix == 0)
                {
                    dropped.Add("exclude ::/0 拒绝=" + One(cidr));
                    continue;
                }
                string nn = Normalize(addr, prefix, true);
                if (seenEx.Add(nn))
                    v6Excludes.Add(nn);
            }

            // ── exclude：节点宿主路由（防自环的路由层防线）─────────────────────
            if (i.ApplyNodeExcludes)
            {
                foreach (string cidr in i.NodeExcludes)
                {
                    if (!SplitCidr(cidr, out string addr, out int prefix, out bool isV6))
                    {
                        dropped.Add("节点排除 非法=" + One(cidr));
                        continue;
                    }
                    // ★ 候选串是 NodeRoutes 拼的 ip+"/32"，对 v6 地址而言这个长度是错的。
                    //   按<b>地址族</b>取宿主前缀，不信任尾数 —— 旧缓存文件里的 v6 项也一起被纠正。
                    string n = Normalize(addr, isV6 ? 128 : prefix, isV6);
                    if (!isV6)
                    {
                        if (seenEx.Add(n))
                            v4Excludes.Add(n);
                        continue;
                    }
                    if (i.Ipv6Arm < 1)
                    {
                        dropped.Add("节点排除 v6 且 arm=0 丢弃=" + One(n));
                        continue;
                    }
                    if (seenEx.Add(n))
                        v6Excludes.Add(n);
                }
            }

            return new Plan(v4Includes, v6Includes, v4Excludes, v6Excludes, dropped);
        }

        /// <summary>
        /// v6 的 include 清单（对应 <see cref="LocalSettings.Ipv6"/> 三档）。
        /// <c>arm&lt;1</c> → 空，一个 <c>VpnRoute</c> 都不加（逐字旧行为；代价就是 IPv6 泄漏，
        /// 那条由 <see cref="LeakNote"/> 说出口，不在这里偷偷抬档）。
        /// </summary>
        internal static List<string> Ipv6IncludeRoutes(int arm, string pool) =>
            Ipv6IncludeRoutes(arm, pool, new List<string>());

        /// <summary>带出声版本：被拒绝/被覆盖的项写进 <paramref name="dropped"/>。</summary>
        internal static List<string> Ipv6IncludeRoutes(int arm, string pool, List<string> dropped)
        {
            var list = new List<string>();
            if (arm < 1)
                return list;

            list.Add(GlobalV6Include);
            if (arm < 2)
                return list;

            // 假 IP v6 池（ULA）不在 2000::/3 里，必须单独下发，否则解析出的假 v6 走物理网卡。
            if (!SplitCidr(pool, out string addr, out int prefix, out bool isV6))
            {
                dropped.Add("v6 假 IP 池非法=" + One(pool));
                return list;
            }
            if (!isV6)
            {
                dropped.Add("v6 假 IP 池不是 v6=" + One(pool));
                return list;
            }
            if (IsBlockedV6Include(prefix))
            {
                dropped.Add("v6 include 拒绝 ::/0=" + One(pool));
                return list;
            }
            if (IsCoveredByGlobalV6Include(prefix, addr))
            {
                dropped.Add($"v6 假 IP 池已被 {GlobalV6Include} 覆盖=" + One(pool));
                return list;
            }
            list.Add(Normalize(addr, prefix, true));
            return list;
        }

        /// <summary>
        /// ★ 铁律判据：v6 的 include 里前缀 0（<c>::/0</c>）一律不许出现。
        /// 依据是先遣 docs/96 §6.2.1 的四臂实测，不是风格问题 —— 见类注释。
        /// </summary>
        internal static bool IsBlockedV6Include(int prefix) => prefix == 0;

        /// <summary>该 v6 前缀是否已落在 <c>2000::/3</c> 里（落在就不必重复下发池前缀）。</summary>
        internal static bool IsCoveredByGlobalV6Include(int prefix, string address) =>
            prefix <= 3 || IsGlobalUnicastV6(address);

        /// <summary>
        /// 地址是不是 IPv6 字面量（含 <c>:</c> 即 v6；v4 字面量不含冒号）。
        /// 与旧代码里散布的 <c>IndexOf(':') &lt; 0</c> 同一口径，集中到一处。
        /// </summary>
        internal static bool IsV6Address(string address) => address.Length > 0 && address.IndexOf(':') >= 0;

        /// <summary>切 <c>地址/前缀</c>。非法（段数不对、前缀不是数、超出该族上限、空地址）返回 false。</summary>
        internal static bool SplitCidr(string cidr, out string address, out int prefix, out bool isV6)
        {
            address = "";
            prefix = 0;
            isV6 = false;
            if (string.IsNullOrEmpty(cidr))
                return false;
            string[] parts = cidr.Trim().Split('/');
            if (parts.Length != 2)
                return false;
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix < 0)
                return false;
            if (parts[0].Length == 0)
                return false;
            address = parts[0];
            isV6 = IsV6Address(parts[0]);
            return prefix <= (isV6 ? 128 : 32);
        }

        /// <summary>规范成族别正确的 <c>地址/前缀</c>：v4 上限 32、v6 上限 128。</summary>
        internal static string Normalize(string address, int prefix, bool v6)
        {
            int cap = v6 ? 128 : 32;
            if (prefix > cap) prefix = cap;
            if (prefix < 0) prefix = 0;
            return address + "/" + prefix.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 宿主路由的 <c>地址/前缀</c>：v4 是 /32，v6 是 /128。
        /// <see cref="NodeRoutes"/> 生成候选串时用它，避免再统一拼 <c>/32</c>。
        /// </summary>
        internal static string HostRoute(string ip)
        {
            bool v6 = IsV6Address(ip);
            return ip + "/" + (v6 ? 128 : 32);
        }

        /// <summary>
        /// 把节点地址台账收成宿主路由<b>候选</b>清单（去重、按 <paramref name="max"/> 截断）。
        /// ★【1.0.126】这一步只决定<b>前缀长度</b>（族别），不决定<b>要不要产出</b>：
        ///   旧版在 <c>NodeRoutes</c> 里按族筛 —— v6 字面量与 AAAA 解析结果被存成<b>空列表</b>，
        ///   于是候选清单里永远没有 v6，档 1/2 下发 <c>2000::/3</c> 之后
        ///   v6 节点服务器的外层拨号包正好落进自家 include、没有排除路由兜它（自环黑洞）。
        ///   "arm=0 不拿 v6 排除"这个决定只留给 <see cref="Build"/> 一处做（并且按名出声丢弃）。
        /// </summary>
        internal static List<string> HostRouteCandidates(IEnumerable<string> addresses, int max)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string ip in addresses)
            {
                if (ip.Length == 0)
                    continue;
                if (seen.Add(ip))
                    list.Add(HostRoute(ip));
                if (list.Count >= max)
                    return list;
            }
            return list;
        }

        /// <summary>
        /// 读台账时补回<b>被上一代代码写空</b>的那一类条目。
        /// ★【1.0.127 · 1.0.126 的第三处漏项】1.0.126 只修了<b>生产端</b>
        ///   （<c>NodeRoutes</c> 里两处按族筛掉 v6 的地方），漏了<b>读取端</b>：
        ///   <c>NodeRoutes</c> 在台账没过 TTL（6 小时）时<b>整轮跳过解析</b>
        ///   （<c>if (forceAll || (!fresh && hosts.Count > 0))</c>），
        ///   于是 1.0.126 之前落盘的 "v6 字面量键 = 空值" 行被原样读回、再原样写回，
        ///   装机取证直接读到 <c>v6ex=0</c> —— 修好的生产端从未跑过。
        ///   判据：<b>键本身是不是地址字面量</b>。是，就不需要解析，键就是答案；
        ///   域名键的空值<b>不许</b>凭空填（那是"解析失败/无 AAAA"的真实记录，
        ///   下一次进解析分支自然会重量）。
        ///   补出来的形态与解析器的规范输出一致（<c>IPAddress.ToString()</c>），
        ///   所以台账落盘行、<c>Describe</c>、滚动快照三处同时变诚实。
        /// </summary>
        internal static List<string> RepairLedgerEntry(string host, List<string> addresses)
        {
            if (addresses.Count > 0)
                return addresses;
            if (!IPAddress.TryParse(host, out IPAddress? literal))
                return addresses;
            if (literal.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                return addresses;
            return new List<string> { literal.ToString() };
        }

        /// <summary>
        /// 单个 v6 地址是不是全局单播（<c>2000::/3</c>，即首字节高 3 位是 <c>001</c>）。
        /// 环回 <c>::1</c>、链路本地 <c>fe80::/10</c>（含废弃的 site-local <c>fec0::/10</c>）、
        /// ULA <c>fc00::/7</c>、组播 <c>ff00::/8</c>、未指定 <c>::</c> 都不满足这个位型，
        /// 也就是都不算"能从物理网卡溜出去"的地址。
        /// </summary>
        internal static bool IsGlobalUnicastV6(string address)
        {
            if (!TryParseV6(address, out byte[] b) || b.Length != 16)
                return false;
            return (b[0] & 0xE0) == 0x20;
        }

        /// <summary>解析 v6 字面量（链接本地地址带区域 id，先去掉 <c>%zone</c>）。</summary>
        internal static bool TryParseV6(string address, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (string.IsNullOrEmpty(address))
                return false;
            string s = address.Trim();
            int pct = s.IndexOf('%');
            if (pct > 0)
                s = s[..pct];
            if (!IPAddress.TryParse(s, out IPAddress? ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
                return false;
            bytes = ip.GetAddressBytes();
            return true;
        }

        /// <summary>
        /// 本机地址清单里有没有"会绕过隧道的 v6" —— 即存在全局单播 v6 地址。
        /// 输入是一串地址字面量（采集归调用方），纯函数可离线断言。
        /// </summary>
        internal static bool HasGlobalUnicastV6(IEnumerable<string> addresses)
        {
            foreach (string a in addresses)
            {
                if (a.Length > 0 && IsV6Address(a) && IsGlobalUnicastV6(a))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 把"读到的本机 v6 地址清单"归成 <see cref="V6Face"/>。
        /// <paramref name="collectFailed"/> 为真 = 采集本身抛了（AppContainer 里能不能枚举
        /// 地址没实测过），此时判 <see cref="V6Face.Unknown"/>，<b>不</b>当成"没有 v6"。
        /// ★ "读不到"只由这个开关表达，所以清单本身不许为 null（空清单 = 确实一个都没有）。
        /// </summary>
        internal static V6Face ClassifyV6Face(IReadOnlyList<string> v6Addresses, bool collectFailed)
        {
            if (collectFailed)
                return V6Face.Unknown;
            return HasGlobalUnicastV6(v6Addresses)
                ? V6Face.Global
                : V6Face.None;
        }

        /// <summary>
        /// 本机 v6 面貌的三种读数。<b>分开"没有"和"读不到"不是洁癖</b>：
        /// 前者说明当前网络下 arm=0 无泄漏面，后者说明我们<b>不知道自己有没有泄漏</b> ——
        /// 在 AppContainer 里能不能枚举到地址本身就没实测过（口径同 <see cref="NetworkFollow"/>）。
        /// </summary>
        internal enum V6Face { Unknown, None, Global }

        /// <summary>
        /// arm=0 时的<b>如实说明</b>（进 <c>_ipv6Diag</c> 与快照）。
        /// 这一档的行为是"v6 完全不碰"，后果不是"v6 用不了"而是 <b>v6 绕过整条隧道</b>：
        /// 代理、规则、fake-ip 对这些流量完全无感。旧实现只写 <c>arm=0 未下发</c>，
        /// 等于把这条缺口藏在一行中性字样里。
        /// <c>arm≥1</c> 返回空串 —— 那时候该说的都在路由表里说了。
        /// </summary>
        internal static string LeakNote(int arm, V6Face face) => arm >= 1 ? "" : face switch
        {
            V6Face.Global => "⚠ 泄漏：本机有全局 IPv6 而 ipv6=0 ⇒ v6 出站走物理网卡，代理/规则/fake-ip 对其无感（要堵：settings.txt ipv6=1）",
            V6Face.None => "本机无全局 IPv6 ⇒ 当前网络下没有 v6 泄漏面（ipv6=0 仍不碰 v6）",
            _ => "本机 v6 地址读不到 ⇒ 泄漏面未知（不据此判断安全）",
        };

        /// <summary>
        /// 界面用的<b>一行</b>本机 v6 面貌读数（设置页「IPv6 通道」那一节）。
        /// ★【1.0.128】和 <see cref="LeakNote"/> 分开的理由：那条是 arm=0 的<b>后果播报</b>
        ///   （只在 arm=0 有话，arm≥1 回空串），这条是<b>与档位无关的输入读数</b> ——
        ///   用户站在设置页上要判的是"我这条网络抬档到底有没有收益"，
        ///   那句话在<b>三档下都要能读</b>，不然档 1/2 就看不见自己堵住的是不是真存在的面。
        /// ★ 三档文案都必须自带"这一档意味着什么"，<c>Unknown</c> 尤其不许写成"没有 v6"：
        ///   AppContainer 里能不能枚举到地址至今没有实物（1.0.119 写下这条时就没验过），
        ///   读不到只说明我们不知道。
        /// </summary>
        internal static string FaceLine(V6Face face, string detail) => face switch
        {
            V6Face.Global => "本机有全局 IPv6 ⇒ 泄漏面存在，0 档下这些流量整条绕过隧道｜" + detail,
            V6Face.None => "本机无全局 IPv6 ⇒ 当前网络没有 v6 泄漏面，抬档在这条网络上不改变任何东西｜" + detail,
            _ => "本机 v6 地址读不到 ⇒ 泄漏面未知，这不等于安全｜" + detail,
        };

        private static string One(string s)
        {
            s = s.Replace('\r', ' ').Replace('\n', ' ');
            return s.Length <= 48 ? s : s[..48] + "…";
        }
    }
}
