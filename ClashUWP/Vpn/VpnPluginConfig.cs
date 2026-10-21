using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// <c>VpnPlugInProfile.CustomConfiguration</c> 的解析结果与生成器。
    ///
    /// 【官方】<c>VpnPlugInProfile.CustomConfiguration</c>：
    ///   <i>"an XML string to configure the VPN plug-in. Refer to the documentation from
    ///      the VPN plug-in for format and support."</i>
    /// → 这段 XML 的格式<b>完全由插件作者自己定义</b>，官方不介入、不校验。
    ///   ClashUWP 的 schema 由本文件一手定义：前台侧 <see cref="ToXml"/> 生成，
    ///   插件侧 <see cref="Parse"/> 解析，两边对着改。
    ///
    /// sample 的 schema（<c><pluginschema></c> 里塞 port/ipAddress/transport/loglevel/routes…）
    /// 也是它自己造的，由它那份 1704 行的 CustomConfiguration.cs 解析 —— 没有任何官方约束，
    /// 所以这里不照抄它的字段名。
    /// </summary>
    internal sealed class VpnPluginConfig
    {
        // ── 官方硬上限（VpnChannel.Start 的 Remarks / 参数文档原文） ──────────────
        // mtuSize     : "should be configured to be at most 1400"
        // maxFrameSize: "should be <= 1500"
        // 且 Remarks  : "The mtuSize must always be smaller than the maxFrameSize."
        // 另外：Receive 池的 buffer 大小 = mtuSize，Send 池的 = maxFrameSize。
        public const uint MtuHardLimit = 1400;
        public const uint MaxFrameHardLimit = 1500;

        /// <summary>分配给 VPN 接口的 IPv4 地址（本机这一侧）。</summary>
        public string ClientIpV4 { get; set; } = "10.7.0.2";

        /// <summary>
        /// ★【1.0.39 · G7】分配给 VPN 接口的 IPv6 地址（本机这一侧），默认 ULA
        /// <c>fd66:6:6600::2</c>（RFC 4193 的 <c>fd00::/8</c>）。
        ///
        /// 【为什么必须是 ULA】隧道内地址是链路内约定，不是"猜本机该有哪个公网 v6"；
        ///   先遣项目 docs/96 的最小修复用的就是 <c>fd00::2</c>。
        /// 【生效条件】只有 <c>settings.txt: ipv6=1/2</c> 时才会把它放进
        ///   <c>StartWithTrafficFilter</c> 的 <c>assignedClientIPv6list</c>；
        ///   档 0（默认）时这个值原样存在但<b>不下发</b>，行为与本批之前逐字一致。
        /// 【为什么不能落进 fake-ip v6 池】<c>fdfe:dcba:9876::/64</c> 是 AAAA 假地址段，
        ///   隧道自身地址与它重叠后，日志里"假 IP / 本机地址"就没法区分了。
        /// ★ 与 <see cref="ClientIpV4"/> 一样，这是 profile 携带的连接参数；
        ///   <c>settings.txt: ipv6ClientAddress</c> 可以临时覆盖（见当地读取点）。
        /// </summary>
        public string ClientIpV6 { get; set; } = "fd66:6:6600::2";

        /// <summary>MTU。会被夹到 ≤ <see cref="MtuHardLimit"/>。</summary>
        public uint Mtu { get; set; } = MtuHardLimit;

        /// <summary>最大帧长。会被夹到 ≤ <see cref="MaxFrameHardLimit"/> 且 > Mtu。</summary>
        public uint MaxFrame { get; set; } = MaxFrameHardLimit;

        /// <summary>
        /// 要推给 VPN 接口的 DNS 服务器。
        ///
        /// ★ 类型必须是 <see cref="IList{T}"/> 而不是 <c>List&lt;T&gt;</c>：
        ///   本工程是 CsWinRT 组件，所有 public 类型都会被投影进 winmd，
        ///   而 <c>List&lt;T&gt;</c> 不是合法的 WinRT 类型，会报
///   <c>CsWinRT1006: The member 'DnsServers' has the type
    ///   'System.Collections.Generic.List`1' ... not a valid Windows Runtime type</c>。
        ///   IList&lt;T&gt; 会被投影成 <c>Windows.Foundation.Collections.IVector&lt;T&gt;</c>。
        /// ★ 默认值对齐安卓版（ClashMetaForAndroid 的 mihomo <c>config/defaults.go</c>
        ///   + <c>VpnService.Builder.addDnsServer</c>）：国内 DNS <c>223.5.5.5</c> /
        ///   <c>119.29.29.29</c>，不是 1.1.1.1 / 8.8.8.8。
        ///   原默认在国内网络直连不通，隧道内一旦 DNS 出问题就是纯黑洞；
        ///   这两个地址既能被 <c>tun.dns-hijack: [any:53]</c> 正常应答，也符合
        ///   「本机在国内」的基线（见 memory: Local network baseline）。
        /// </summary>
        public IList<string> DnsServers { get; } = new List<string> { "223.5.5.5", "119.29.29.29" };

        /// <summary>走 VPN 的路由（CIDR）。空 = 全走（默认路由）。</summary>
        public IList<string> IncludeRoutes { get; } = new List<string> { "0.0.0.0/0" };

        /// <summary>不走 VPN 的路由（CIDR），用于保留局域网直连。</summary>
        public IList<string> ExcludeRoutes { get; } = new List<string>
        {
            "192.168.0.0/16",
            "10.0.0.0/8",
            "172.16.0.0/12",
        };

        /// <summary>
        /// ★【1.0.62 · 每条订阅一个系统 profile】订阅 id。
        ///
        /// 空串 = 基础 profile（<c>ClashUWP</c>，插件按旧逻辑读 <c>config.yaml</c> 那一份生效副本）；
        /// 非空 = 该订阅对应的系统 VPN profile（<c>ClashUWP-{id}</c>），插件改读
        /// <c>subscriptions/{id}.yaml</c> 这一份<b>该订阅专属</b>的正文，
        /// 从而让 Windows 快速设置 VPN 面板里点哪个 profile 就连哪个订阅。
        ///
        /// 这个值由前台写进 <c>VpnPlugInProfile.CustomConfiguration</c> 的 XML，
        /// 插件在 <c>Connect</c> 里通过 <c>VpnChannel.Configuration.CustomField</c> 读回
        /// （名字不对称：写叫 CustomConfiguration、读叫 CustomField，见 ClashVpnPlugin 步 3 注释）。
        /// </summary>
        public string SubId { get; set; } = "";

        /// <summary>
        /// ★【1.0.81 · 通知优化】订阅显示名（从机场响应头解析出来的友好名称）。
        ///
        /// 用于在状态通知中显示"星链机场"而不是 "RyjBM4AAk7gx" 这种 ID。
        /// 空串时回退到 <see cref="SubId"/>。
        /// </summary>
        public string SubDisplayName { get; set; } = "";

        /// <summary>
        /// ★【快速面板改造】这段 CustomConfiguration 是不是<b>我们 schema</b> 的 XML（root 元素是 <c>clash</c>）。
        ///
        /// 【为什么要它】订阅 profile 的名字从这一版起<b>不再带 <c>ClashUWP · </c> 前缀</b>
        ///   （快速设置里直接显示订阅名），于是"按名字族认领"对它失效 —— 换签名证书
        ///   （＝换包家族哈希）之后，旧家族建的订阅 profile 既不同家族、也不同名字，
        ///   再没有第三条判据它们就是永久幽灵。CustomConfiguration 的 root <c>clash</c>
        ///   是我们自己造的元素名（见 <see cref="ToXml"/>），别人家的 VPN 插件不会写它。
        /// 【为什么不能拿 <see cref="Parse"/> 凑合】Parse 对<b>任何</b>合法 XML 都返回
        ///   默认值不抛（那是给插件侧兜底的契约），判不出"是不是我们的 schema"。
        /// 【纪律】只用于"认领/看见"（IsOurs、prune），不用于"取对象来连"——
        ///   连接永远先同家族，这条判据与 <c>GracefulTeardown.IsOurName</c> 同级兜底。
        /// </summary>
        internal static bool IsOurSchema(string? customConfiguration)
        {
            if (string.IsNullOrWhiteSpace(customConfiguration))
                return false;
            try
            {
                return XElement.Parse(customConfiguration).Name.LocalName == "clash";
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 解析前台下发的 CustomConfiguration。解析不出来就退回默认值，不抛 ——
        /// 插件侧抛异常会让 <c>Connect</c> 直接失败，用户只看到"连不上"。
        /// </summary>
        public static VpnPluginConfig Parse(string? customConfiguration)
        {
            var config = new VpnPluginConfig();
            if (string.IsNullOrWhiteSpace(customConfiguration))
                return config;

            XElement root;
            try
            {
                root = XElement.Parse(customConfiguration);
            }
            catch (Exception)
            {
                return config;
            }

            config.ClientIpV4 = Element(root, "clientIpV4") ?? config.ClientIpV4;

            // ★【1.0.39 · G7】<clientIpV6>：非法值回落默认，不抛。
            //   写错一个字符的代价必须是"这一档没按预期生效"，不能是 Connect 抛在平台回调里。
            string? rawV6 = Element(root, "clientIpV6");
            if (rawV6 != null && IsIpv6Literal(rawV6))
                config.ClientIpV6 = rawV6;

            if (uint.TryParse(Element(root, "mtu"), NumberStyles.None,
                              CultureInfo.InvariantCulture, out uint mtu))
            {
                config.Mtu = Math.Min(mtu, MtuHardLimit);
            }

            if (uint.TryParse(Element(root, "maxFrame"), NumberStyles.None,
                              CultureInfo.InvariantCulture, out uint maxFrame))
            {
                config.MaxFrame = Math.Min(maxFrame, MaxFrameHardLimit);
            }

var dns = Elements(root, "dns");
            if (dns.Count > 0)
            {
                config.DnsServers.Clear();
                // IList<string> 上没有 AddRange，只能逐个加。
                foreach (string d in dns) config.DnsServers.Add(d);
            }

            var routes = Elements(root, "route");
            if (routes.Count > 0)
            {
                config.IncludeRoutes.Clear();
                foreach (string r in routes) config.IncludeRoutes.Add(r);
            }

            var excludes = Elements(root, "excludeRoute");
            if (excludes.Count > 0)
            {
                config.ExcludeRoutes.Clear();
                foreach (string e in excludes) config.ExcludeRoutes.Add(e);
            }

            // ★【1.0.62】<subId>：订阅 id。只保留安全字符（[A-Za-z0-9._-]），
            //   插件会用它拼 subscriptions/{subId}.yaml 的路径，必须挡掉路径穿越。
            //   （值由我们自己生成的 CustomConfiguration 下发，本就是可信来源；
            //     这里再做一层消毒是纵深防御，避免畸形 XML 把插件引到意外文件。）
            string? rawSub = Element(root, "subId");
            if (!string.IsNullOrWhiteSpace(rawSub))
            {
                var sb2 = new StringBuilder();
                foreach (char c in rawSub)
                    if (char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-')
                        sb2.Append(c);
                config.SubId = sb2.ToString();
            }

            // ★【1.0.81】<subDisplayName>：订阅显示名。用于通知等 UI 展示，
            //   允许中文/emoji 等 Unicode 字符，不做字符过滤。
            string? rawName = Element(root, "subDisplayName");
            if (!string.IsNullOrWhiteSpace(rawName))
                config.SubDisplayName = rawName;

            // 兜底夹紧：mtu < maxFrame 是官方明文约束，配置写反了也不能让它违规。
            if (config.Mtu >= config.MaxFrame)
                config.Mtu = config.MaxFrame - 1;

            return config;
        }

        /// <summary>生成下发给插件的那段自定义 XML（前台侧用）。</summary>
        public string ToXml()
        {
            var sb = new StringBuilder();
            sb.Append("<clash>");
            sb.Append(CultureInfo.InvariantCulture, $"<clientIpV4>{ClientIpV4}</clientIpV4>");
            // ★【1.0.39 · G7】v6 地址随 profile 走；插件只在 ipv6=1/2 时才下发它。
            sb.Append(CultureInfo.InvariantCulture, $"<clientIpV6>{ClientIpV6}</clientIpV6>");
            sb.Append(CultureInfo.InvariantCulture, $"<mtu>{Mtu}</mtu>");
            sb.Append(CultureInfo.InvariantCulture, $"<maxFrame>{MaxFrame}</maxFrame>");
            foreach (string dns in DnsServers) sb.Append($"<dns>{dns}</dns>");
            foreach (string route in IncludeRoutes) sb.Append($"<route>{route}</route>");
            foreach (string route in ExcludeRoutes) sb.Append($"<excludeRoute>{route}</excludeRoute>");
            if (SubId.Length > 0)
                sb.Append($"<subId>{SubId}</subId>");
            // ★【1.0.81】订阅显示名：用于通知等 UI 展示
            if (SubDisplayName.Length > 0)
                sb.Append($"<subDisplayName>{SubDisplayName}</subDisplayName>");
            sb.Append("</clash>");
            return sb.ToString();
        }

        /// <summary>
        /// 严格 IPv6 字面量：必须是 <see cref="System.Net.Sockets.AddressFamily.InterNetworkV6"/>，
        /// 且不带 <c>%zone</c>（带作用域的地址不能直接做 <c>HostName</c> 的稳定身份）。
        /// 不用"含冒号"这种形状判断 —— <c>a:b</c> 也会过。
        /// </summary>
        internal static bool IsIpv6Literal(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.IndexOf(':') < 0 ||
                value.IndexOf('%') >= 0)
                return false;
            return System.Net.IPAddress.TryParse(value, out System.Net.IPAddress? ip) &&
                   ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
                   !ip.IsIPv4MappedToIPv6;
        }

        private static string? Element(XElement root, string name) =>
            root.Elements(name).FirstOrDefault()?.Value.Trim() is { Length: > 0 } v ? v : null;

        private static List<string> Elements(XElement root, string name) =>
            root.Elements(name)
                .Select(x => x.Value.Trim())
                .Where(x => x.Length > 0)
                .ToList();
    }
}
