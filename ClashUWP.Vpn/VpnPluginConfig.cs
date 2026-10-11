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
    public sealed class VpnPluginConfig
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
        /// </summary>
        public IList<string> DnsServers { get; } = new List<string> { "1.1.1.1", "8.8.8.8" };

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
            sb.Append(CultureInfo.InvariantCulture, $"<mtu>{Mtu}</mtu>");
            sb.Append(CultureInfo.InvariantCulture, $"<maxFrame>{MaxFrame}</maxFrame>");
            foreach (string dns in DnsServers) sb.Append($"<dns>{dns}</dns>");
            foreach (string route in IncludeRoutes) sb.Append($"<route>{route}</route>");
            foreach (string route in ExcludeRoutes) sb.Append($"<excludeRoute>{route}</excludeRoute>");
            sb.Append("</clash>");
            return sb.ToString();
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
