using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;

using Windows.Storage;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// 一条 DNS 覆盖项（<see cref="DnsConfig"/> 的一行）。
    /// 两个字符串字段保持最普通的形状 —— 前台编辑器与插件读取共用同一个模型，
    /// 不在界面里再写一套"看起来一样"的规则。
    /// </summary>
    internal sealed class DnsEntry
    {
        /// <summary>键名，已归一成小写（<see cref="DnsConfig.Keys"/> 里的那批）。</summary>
        public string Key { get; set; } = "";

        /// <summary>值，原样（列表类键是逗号分隔的清单，写进 YAML 时变成行内流式序列）。</summary>
        public string Value { get; set; } = "";
    }

    /// <summary>
    /// 【DNS 可配】<c>LocalState\dns-config.txt</c> 的模型 —— 一行一条 <c>key = value</c>，
    /// 前台写、插件在"重新生成内核配置"的那一刻读（与 <see cref="AppSplit"/> 同一批纪律）。
    ///
    /// ══ 为什么单独一个文件，不塞进 settings.txt ══
    ///   <c>settings.txt</c> 是本项目唯一的<b>取证通道</b>（那一行 <c>channel=settings</c>
    ///   的证据句柄是整批实验臂的判据），前台 writer 一旦写坏一行，连坐的是一整批臂
    ///   （理由逐字沿用 Docs/20 §3.1 里 <c>split-apps.txt</c> 的那段）。
    ///   ⇒ 每个新功能自带一个文件、自带一个 writer：这里就是 <c>dns-config.txt</c>。
    ///
    /// ══ 为什么必须白名单 + 逐条校验（这是本类最要紧的防线）══
    ///   mihomo 的 <c>parseDNS</c>（pin 住的 v1.19.32，<c>config/config.go:1414-1618</c>）
    ///   里有<b>一整排"直接返回错误"的校验</b>：
    ///     · <c>dns.enable</c> 为真而 <c>nameserver</c> 为空 → :1416 报错；
    ///     · <c>default-nameserver</c> 为空 → :1466 报错（<b>与 dns.enable 无关，恒要求至少一条</b>）；
    ///     · <c>default-nameserver</c> 必须是<b>纯 IP</b>（<c>system</c> 例外）→ :1473-1485；
    ///     · <c>fake-ip-range</c> 必须是 IPv4 前缀 → :1488-1495；<c>fake-ip-range6</c> 必须是 IPv6 前缀 → :1498-1505；
    ///     · nameserver 的 scheme 不在白名单 → :1283 <c>unsupport scheme</c>；
    ///     · <c>respect-rules</c> 开了却没写 <c>proxy-server-nameserver</c> → :1420。
    ///   <b>任意一条不满足 = 整份配置解析失败</b> = 桥 <c>start rc=-2</c> = <c>failFast</c> 放弃连接。
    ///   "改一行 DNS 就连不上 VPN"不能留给用户，所以非法值在<b>写文件之前</b>就被挡掉，
    ///   并且每条拒绝都要有可读的原因（<see cref="Rejected"/>），界面与日志都看得见。
    ///
    /// ══ scheme 白名单是照源码抄的，不是猜的 ══
    ///   <c>config.go:1225-1284</c>：<c>udp</c>/<c>tcp</c>/<c>tls</c>/<c>http</c>/<c>https</c>/
    ///   <c>quic</c>/<c>system</c>/<c>ts</c>/<c>tailscale</c>/<c>et</c>/<c>easytier</c>/<c>dhcp</c>/<c>rcode</c>；
    ///   裸 IP 由 :1206 的 <c>parsePureDNSServer</c> 补成 <c>udp://ip:53</c>，所以"223.5.5.5"这种写法合法。
    ///   <c>rcode</c> 的主机名还必须是 <c>success|format_error|server_failure|name_error|not_implemented|refused</c>（:1272-1281）。
    ///   ★ 本文件<b>不</b>支持 <c>ts:/et:/dhcp:</c> 这三类（它们要求目标 profile 存在，
    ///     与"组名会随订阅变"是同一个坑，见 <see cref="AppSplit"/> 类注释 —— 等组名校验做完再加）。
    ///
    /// ══ 生效时机（与 AppSplit 同一句话）══
    ///   内核侧（<c>dns:</c> 块）：断开再连、或点「订阅 → 重新加载配置」热重载，都生效。
    ///   ★ NRPT 侧（<c>nrpt-dns</c>）：它<b>不在</b> <c>CustomConfiguration</c> 的 XML 里 ——
    ///     插件在每次 Connect 的 <c>BuildDomainNames()</c> 里把它构造成
    ///     <c>VpnDomainNameAssignment</c>，随 <c>StartWithTrafficFilter</c> 的
    ///     namespaceScope 交给平台。所以同样是<b>断开再连</b>生效，不需要重建 profile。
    ///     （此前文档里"只有重建 profile 才改写"的说法把 <c>_config.DnsServers</c> 的
    ///     来源与 NRPT 的下发路径混成了一件事，按代码路径更正。）
    ///   ★ 总闸：<c>settings.txt: dnsConfig=0</c> 时整个文件不读，内核与 NRPT 都不动。
    /// </summary>
    internal sealed class DnsConfig
    {
        /// <summary>配置文件名。<c>static 属性</c>而非 <c>const</c>：本类 public、会被投影进 winmd，
        /// 而 <c>const</c> 不穿过投影，前台 <c>DnsConfigStore</c> 要在运行期读到同一个名字。</summary>
        public static string FileName { get; } = "dns-config.txt";

        /// <summary>行数上限。DNS 覆盖项本来就是十来项，设上限只为挡住误粘贴。</summary>
        public static int MaxEntries { get; } = 40;

        /// <summary>白名单键（顺序 = 界面提示顺序）。★ 名单外的键一律拒绝，见类注释。</summary>
        public static IList<string> Keys { get; } = new List<string>
        {
            "enhanced-mode", "fake-ip-range", "fake-ip-range6", "fake-ip-filter", "fake-ip-ttl",
            "default-nameserver", "nameserver", "fallback", "proxy-server-nameserver",
            "direct-nameserver", "ipv6", "prefer-h3", "ipv6-timeout", "nrpt-dns",
        };

        /// <summary>解析出来的覆盖项（保持文件顺序；同名键后写的赢，与 settings.txt 同口径）。</summary>
        public IList<DnsEntry> Entries { get; } = new List<DnsEntry>();

        /// <summary>被丢掉的行与原因（★ 不静默丢弃）。</summary>
        public IList<string> Rejected { get; } = new List<string>();

        /// <summary>没有有效覆盖项 = 本功能完全不动配置。</summary>
        public bool IsEmpty => Entries.Count == 0;

        /// <summary>按键取值；没有这条返回空串。</summary>
        public string Get(string key)
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
                if (Entries[i].Key == key)
                    return Entries[i].Value;
            return "";
        }

        public bool Has(string key) => Get(key).Length > 0;

        // ── 与 fake-ip 池绑定的三处派生值 ──────────────────────────────────
        //
        // 【为什么不能让 fake-ip-range 只活在 YAML 里】
        //   假 IP 池这个概念在本项目有<b>三个读者</b>，此前都共用 <see cref="KernelConfig.FakeIpRange"/>
        //   那个常量：① 内核的 <c>dns.fake-ip-range</c>；② 窄 include 的路由
        //   （<c>ClashVpnPlugin.NarrowInclusionRoutes</c> 用 <see cref="KernelConfig.FakeIpRoute"/>）；
        //   ③ 会话看门狗的判据②"系统解析器给出的答案必须落在假 IP 池里"
        //   （<c>SessionWatchdog.cs:185</c> 原先硬写 <c>StartsWith("198.18.")</c>）。
        //   ⇒ 一旦把池子改成用户可配，只改①就是<b>埋雷</b>：②不跟着改 = 新池的域名流量根本不进隧道
        //     （比黑洞更糟，是"以为在代理其实直连"）；③不跟着改 = 看门狗把好端端的隧道判成死了，
        //     反过来自动拆线。三处必须一起走这一个入口，所以这里把派生值算干净。

        /// <summary>没配 <c>fake-ip-range</c> 时用的池子（= 本批改动之前的常量，逐字不变）。</summary>
        public const string DefaultFakeIpRange = "198.18.0.1/16";

        /// <summary>生效的 v4 假 IP 池（YAML 写法，主机位可以是 .1）。</summary>
        public string FakeIpRange => First(Get("fake-ip-range"), DefaultFakeIpRange);

        /// <summary>
        /// 生效池子的<b>路由</b>写法（网络号/掩码）。窄 include 下发用它。
        /// ★ 解析不出合法 IPv4 前缀时退回默认池 —— 宁可用旧值，也不能让路由表空着。
        /// </summary>
        public string FakeIpRoute => ToRouteCidr(FakeIpRange, DefaultFakeIpRange);

        /// <summary>假 IP 池的网络地址（看门狗判据②用它做"答案在不在池里"）。</summary>
        public string FakeIpNetwork => FakeIpRoute.Split('/')[0];

        /// <summary>假 IP 池的掩码位数；非法时 16。</summary>
        public int FakeIpPrefix => ToPrefixLength(FakeIpRoute, 16);

        /// <summary>目的 IP 是否落在生效的假 IP 池里（看门狗判据②）。</summary>
        public bool IsInFakeIpPool(string ipv4) => InCidr(ipv4, FakeIpRoute);

        // ── IPv6 相关（与 <see cref="LocalSettings"/> 的 ipv6 档同一份口径）──────

        /// <summary>
        /// v6 假 IP 池的默认值，与 <see cref="LocalSettings.DefaultIpv6FakeRange"/>
        /// <b>逐字相同</b>：池子的网络号还会被 <see cref="KernelConfig"/> 派生成
        /// <c>tun.inet6-address</c>（网络号 +1、/126），所以这边必须是网络地址形态
        /// （<c>::/64</c>），不能写成主机位非零的 <c>::1/64</c>。
        /// </summary>
        public const string DefaultFakeIpRange6 = "fdfe:dcba:9876::/64";

        /// <summary>用户配的 v6 假 IP 池（没配就空 —— <b>不</b>替 IPv6 功能做默认）。</summary>
        public string FakeIpRange6 => Get("fake-ip-range6");

        /// <summary>
        /// 档 2 实际要用的 v6 池：本文件的 <c>fake-ip-range6</c> 优先，没配/畸形用
        /// <paramref name="fallback"/>（= <see cref="LocalSettings.Ipv6FakeRange"/>）。
        /// ★ 归一化走 <see cref="LocalSettings.TryNormalizeV6Prefix"/>：这个值同时是
        ///   <c>dns.fake-ip-range6</c> 与 include 路由，必须与 settings 侧同一把尺子。
        /// </summary>
        public string EffectiveFakeIpRange6(string fallback)
        {
            string v = Get("fake-ip-range6");
            if (v.Length > 0 && LocalSettings.TryNormalizeV6Prefix(v, out string normalized))
                return normalized;
            return fallback;
        }

        // ── 解析 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 解析文件内容，<b>不抛</b>。畸形行/白名单外的键/值不合法一律进 <see cref="Rejected"/>。
        /// 支持 <c>#</c> 注释与空行；键大小写不敏感（归一小写），值原样保留。
        /// </summary>
        public static DnsConfig Parse(string? text)
        {
            var cfg = new DnsConfig();
            if (string.IsNullOrWhiteSpace(text))
                return cfg;

            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;

                int idx = line.IndexOf('=');
                if (idx <= 0)
                {
                    cfg.Rejected.Add($"不是 key = value 写法：{Cut(line)}");
                    continue;
                }

                string key = line.Substring(0, idx).Trim().ToLowerInvariant();
                string value = line.Substring(idx + 1).Trim();

                // ★ 不能写 Keys.ToArray()：本工程引了 Windows.Storage.Streams，
                //   WindowsRuntimeBufferExtensions.ToArray(IBuffer) 会把实例上的 ToArray 抢掉，
                //   对 IList<string> 变成 CS1061（与 ClashVpnPlugin 里记的同一个坑）。
                bool known = false;
                foreach (string k in Keys)
                {
                    if (string.Equals(k, key, StringComparison.Ordinal)) { known = true; break; }
                }
                if (!known)
                {
                    cfg.Rejected.Add($"键不在白名单里（白名单见文件头注释）：{key}");
                    continue;
                }
                if (value.Length == 0)
                {
                    cfg.Rejected.Add($"{key} 的值为空（留空不是'不改'：内核会把它当显式空列表，整份配置直接失败）");
                    continue;
                }

                string? error = Validate(key, value);
                if (error != null)
                {
                    cfg.Rejected.Add($"{key}: {error}");
                    continue;
                }
                if (cfg.Entries.Count >= MaxEntries)
                {
                    cfg.Rejected.Add($"超过上限 {MaxEntries} 行，其后全部忽略。");
                    break;
                }

                cfg.Entries.Add(new DnsEntry { Key = key, Value = value });
            }
            return cfg;
        }

        /// <summary>
        /// 逐键校验。<b>返回 null 才算通过</b>；返回的就是给用户看的原因。
        /// ★ 每一条都对着 mihomo v1.19.32 的 <c>parseDNS</c> 写，出处在行内注释里。
        /// </summary>
        public static string? Validate(string key, string value)
        {
            switch (key)
            {
                case "enhanced-mode":
                    // config.go:1508 与 constant/dns.go:25-30：内核两种都认。
                    // ★ 这里只放行 fake-ip：本项目的窄 include 路由、会话看门狗判据②、
                    //   "域名流量经假 IP 进隧道"这整条数据面都建立在 fake-ip 上
                    //   （Docs/08 §9 run 24 的结论链）。放行 redir-host 得到的是一份
                    //   "能启动、但 30 秒后被看门狗判死"的配置 —— 宁可当场拒绝。
                    return value.Equals("fake-ip", StringComparison.OrdinalIgnoreCase)
                        ? null : "只支持 fake-ip（redir-host 会让本项目的路由与看门狗判据失效）";

                case "fake-ip-range":
                    // config.go:1488-1495：netip.ParsePrefix + 必须 Is4。
                    return IsV4Prefix(value) ? null : "必须是 IPv4 前缀（如 198.18.0.1/16）";

                case "fake-ip-range6":
                    // config.go:1498-1505 只要求 Is6；本项目的闸门更严（ULA、/48~/64），
                    // 因为同一个值还要当 include 路由与 tun.inet6-address 的池子（G7）。
                    return LocalSettings.TryNormalizeV6Prefix(value, out _)
                        ? null : "必须是 ULA（fc00::/7）的 IPv6 前缀且长度 48~64（如 fdfe:dcba:9876::/64）";

                case "fake-ip-filter":
                    // 域名清单；写法照 KernelConfig 的行内流式序列，字符闸门同 AppSplit。
                    foreach (string d in SplitList(value))
                    {
                        if (d.Length == 0 || d.Length > 200) return $"域名长度非法：{Cut(d)}";
                        if (!IsSafeScalar(d)) return $"域名含非法字符（' , | 或控制字符）：{Cut(d)}";
                    }
                    return null;

                case "fake-ip-ttl":
                    // dns/enhancer.go:185-187 会夹到最小 1，这里就要求正整数。
                    return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int ttl) && ttl >= 1 && ttl <= 86400
                        ? null : "要 1~86400 的秒数";

                case "default-nameserver":
                    // config.go:1466-1486：恒要求非空，且每条必须是纯 IP（system 例外）。
                    foreach (string s in SplitList(value))
                    {
                        if (!IsPlainIp(s) && !s.Equals("system", StringComparison.OrdinalIgnoreCase))
                            return $"default-nameserver 必须是纯 IP（或 system），这条不是：{Cut(s)}";
                    }
                    return null;

                case "nrpt-dns":
                    // 这条进 NRPT（VpnDomainNameInfo 的 servers），必须是能构造 HostName 的 IP。
                    foreach (string s in SplitList(value))
                        if (!IsPlainIp(s)) return $"下发给系统的 DNS 必须是 IP，这条不是：{Cut(s)}";
                    return null;

                case "nameserver":
                case "fallback":
                case "proxy-server-nameserver":
                case "direct-nameserver":
                    foreach (string s in SplitList(value))
                    {
                        string? e = ValidateNameServer(s);
                        if (e != null) return e;
                    }
                    return null;

                case "ipv6":
                case "prefer-h3":
                    return ParseBool(value, out _) ? null : "只能是 0/1/true/false/on/off/yes/no";

                case "ipv6-timeout":
                    // config.go RawDNS.IPv6Timeout（uint）：竞跑窗口，给个 sane 区间。
                    return uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint ms) && ms <= 60000
                        ? null : "要 0~60000 的毫秒数";

                default:
                    return "白名单外的键（本类不支持）";
            }
        }

        /// <summary>
        /// 一条 nameserver 的校验。对着 <c>config.go:1202-1288</c>：<br/>
        /// 裸 IP（<c>parsePureDNSServer</c> 补成 udp）/ <c>system</c> /
        /// scheme 白名单（去掉 ts/et/dhcp）/ <c>rcode</c> 的固定主机名。<br/>
        /// ★ 另外挡掉会破坏 YAML 行内流式序列的字符（<c>'</c>、<c>,</c>、<c>|</c>、控制字符）。
        /// </summary>
        private static string? ValidateNameServer(string s)
        {
            if (s.Length == 0 || s.Length > 300)
                return $"条目长度非法：{Cut(s)}";
            if (!IsSafeScalar(s))
                return $"条目含非法字符（' , | 或控制字符）：{Cut(s)}";

            if (IsPlainIp(s)) return null;                       // 裸 IP = udp://ip:53
            if (s.Equals("system", StringComparison.OrdinalIgnoreCase)) return null;

            int schemeEnd = s.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0)
                return $"既不是纯 IP 也没带 scheme：{Cut(s)}";

            string scheme = s.Substring(0, schemeEnd).ToLowerInvariant();
            string rest = s.Substring(schemeEnd + 3);

            switch (scheme)
            {
                case "udp":
                case "tcp":
                case "tls":
                case "http":
                case "https":
                case "quic":
                    return rest.Length > 0 ? null : $"{scheme} 后面缺主机：{Cut(s)}";

                case "rcode":
                    // config.go:1272-1281 的固定清单，写错一个字母就整份配置失败。
                    string host = rest.Split('#')[0].TrimEnd('/');
                    return host is "success" or "format_error" or "server_failure" or
                                "name_error" or "not_implemented" or "refused"
                        ? null
                        : $"rcode 类型不在内核清单里：{Cut(host)}";

                case "ts":
                case "tailscale":
                case "et":
                case "easytier":
                case "dhcp":
                    return "本版本不支持 ts:/et:/dhcp: 这三类（要求目标 profile 存在，见类注释）";

                default:
                    return $"scheme 不在内核白名单里：{scheme}";
            }
        }

        // ── 写回文件 ────────────────────────────────────────────────────────

        /// <summary>生成文件正文（带头部说明，用户可以直接开着编辑器改）。</summary>
        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# ClashUWP DNS 覆盖（前台写；插件每次连接与热重载各读一次）");
            sb.AppendLine("# 一行一条：key = value　　# 开头是注释。★ 只列支持的键，名单外的键会被拒绝。");
            sb.AppendLine("#   enhanced-mode          = fake-ip（本项目不支持 redir-host：路由与看门狗都依赖假 IP）");
            sb.AppendLine("#   fake-ip-range          = IPv4 前缀，如 198.18.0.1/16　（改了它，窄 include 与看门狗判据会一起跟着走）");
            sb.AppendLine("#   fake-ip-range6         = ULA 且 /48~/64，如 fdfe:dcba:9876::/64　（只在 settings.txt: ipv6=2 时生效，见 Docs/22）");
            sb.AppendLine("#   fake-ip-filter         = 域名清单，逗号分隔（*.lan, +.msftconnecttest.com 这类写法照旧）");
            sb.AppendLine("#   fake-ip-ttl            = 秒，1~86400");
            sb.AppendLine("#   default-nameserver     = 纯 IP 清单，逗号分隔（内核硬性要求：不能空、必须纯 IP）");
            sb.AppendLine("#   nameserver / fallback / proxy-server-nameserver / direct-nameserver");
            sb.AppendLine("#                          = DNS 清单：裸 IP、system、或 udp:/tcp:/tls:/http:/https:/quic:/rcode: 前缀");
            sb.AppendLine("#   ipv6 / prefer-h3       = 0 | 1");
            sb.AppendLine("#   ipv6-timeout           = 毫秒，0~60000");
            sb.AppendLine("#   nrpt-dns               = 纯 IP 清单 —— 给 Windows 的 NRPT 用；需 settings.txt: dnsInTunnel=1，断开再连生效");
            sb.AppendLine("# 空文件 = 不改内核配置，行为与没这个文件时逐字相同。");
            sb.AppendLine("# 总闸：settings.txt 里写 dnsConfig=0 可整份摘掉本文件的全部覆盖。");
            foreach (DnsEntry e in Entries)
                sb.AppendLine($"{e.Key} = {e.Value}");
            return sb.ToString();
        }

        /// <summary>
        /// 同步读（插件在 Connect 这个同步回调里用）。缺文件/读不到 = 空配置，是正常路径。
        /// ★ 调用点与 <see cref="AppSplit.Load"/> 同一批：连接时 + 热重载时。
        /// </summary>
        public static DnsConfig Load()
        {
            try
            {
                StorageFile file = ApplicationData.Current.LocalFolder.GetFileAsync(FileName)
                    .AsTask().GetAwaiter().GetResult();
                string text = FileIO.ReadTextAsync(file).AsTask().GetAwaiter().GetResult();
                return Parse(text);
            }
            catch
            {
                return new DnsConfig();
            }
        }

        /// <summary>
        /// 一行式结果句，进 <c>channel=dns</c> 日志与诊断快照用：
        /// 把"读到了几条、哪几行被拒"原样交出去 —— 静默失效是本功能最大的风险。
        /// </summary>
        public string Describe()
        {
            if (IsEmpty && Rejected.Count == 0)
                return "无覆盖（文件缺或空）";
            var sb = new StringBuilder();
            foreach (DnsEntry e in Entries)
                sb.Append(sb.Length == 0 ? "" : " ").Append($"{e.Key}={Cut(e.Value, 40)}");
            if (Rejected.Count > 0)
                sb.Append(sb.Length == 0 ? "" : " ").Append($"拒绝 {Rejected.Count} 行");
            return sb.Length == 0 ? "无有效项" : sb.ToString();
        }

        // ── 小工具（IP/CIDR 判定只认 System.Net，不引外部依赖）──────────────

        internal static IList<string> SplitList(string value)
        {
            var list = new List<string>();
            foreach (string p in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string t = p.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        /// <summary>纯 IPv4 / IPv6 字面量。<br/>
        /// ★ 除了 <c>IPAddress.TryParse</c> 还要求串里出现 <c>.</c> 或 <c>:</c> ——
        ///   老 Framework 的 TryParse 会把 <c>"1"</c> 解析成 <c>0.0.0.1</c>，
        ///   而内核的 <c>parsePureDNSServer</c>（<c>config.go:1206</c>）走的是 Go 的 IP 判定，
        ///   两边口径不一样；宁可多挡一层，也别让"看着像 IP"的值进 YAML。</summary>
        public static bool IsPlainIp(string s)
        {
            if (s.Length == 0 || s.Length > 45) return false;
            if (s.IndexOf('.') < 0 && s.IndexOf(':') < 0) return false;
            if (s.IndexOfAny(new[] { '[', ']', '/', '@', ' ', '%' }) >= 0) return false;
            return IPAddress.TryParse(s, out IPAddress? ip) && ip != null;
        }

        /// <summary>IPv4 前缀（a.b.c.d/n）。</summary>
        public static bool IsV4Prefix(string s)
        {
            if (!TrySplitPrefix(s, out string addr, out int bits)) return false;
            return IsPlainIp(addr) && addr.IndexOf(':') < 0 && bits is >= 0 and <= 32;
        }

        /// <summary>IPv6 前缀（x::y/n）。</summary>
        public static bool IsV6Prefix(string s)
        {
            if (!TrySplitPrefix(s, out string addr, out int bits)) return false;
            return addr.IndexOf(':') >= 0 && IsPlainIp(addr) && bits is >= 0 and <= 128;
        }

        private static bool TrySplitPrefix(string s, out string addr, out int bits)
        {
            addr = "";
            bits = -1;
            int slash = s.LastIndexOf('/');
            if (slash <= 0 || slash == s.Length - 1) return false;
            addr = s.Substring(0, slash).Trim();
            return int.TryParse(s.Substring(slash + 1).Trim(), NumberStyles.None,
                                CultureInfo.InvariantCulture, out bits);
        }

        /// <summary>把 CIDR 规整成"网络地址/掩码"的路由写法（VpnRoute 要的是网络号）。
        /// ★ 与 <see cref="KernelConfig"/> 里 FakeIpRange / FakeIpRoute 分成两个常量的同一个理由。</summary>
        public static string ToRouteCidr(string cidr, string fallbackCidr)
        {
            if (!TrySplitPrefix(cidr, out string addr, out int bits)) return NetworkOf(fallbackCidr);
            if (addr.IndexOf(':') >= 0)
                return $"{addr}/{bits}";                       // v6：不做网络号归一（Windows 侧按字面下发）
            if (!IsV4Prefix(cidr)) return NetworkOf(fallbackCidr);
            uint ip = ToUInt32(addr);
            uint mask = bits == 0 ? 0u : (uint)(0xFFFFFFFF << (32 - bits));
            uint net = ip & mask;
            return $"{FromUInt32(net)}/{bits}";
        }

        /// <summary>取 CIDR 的网络地址；非法则原样返回入参的地址部分。</summary>
        private static string NetworkOf(string cidr)
        {
            if (TrySplitPrefix(cidr, out string addr, out int bits) && bits >= 0 && addr.IndexOf(':') < 0 && IsV4Prefix(cidr))
            {
                uint mask = bits == 0 ? 0u : (uint)(0xFFFFFFFF << (32 - bits));
                return FromUInt32(ToUInt32(addr) & mask);
            }
            return cidr.Split('/')[0];
        }

        public static int ToPrefixLength(string cidr, int fallback)
            => TrySplitPrefix(cidr, out _, out int bits) && bits >= 0 ? bits : fallback;

        /// <summary>IPv4 是否落在 CIDR 里（看门狗判据②："答案在不在生效的假 IP 池里"）。</summary>
        public static bool InCidr(string ipv4, string cidr)
        {
            if (!IsPlainIp(ipv4) || ipv4.IndexOf(':') >= 0) return false;
            if (!TrySplitPrefix(cidr, out _, out int bits) || bits < 0 || bits > 32) return false;
            uint a = ToUInt32(ipv4);
            uint n = ToUInt32(cidr.Split('/')[0]);
            uint mask = bits == 0 ? 0u : (uint)(0xFFFFFFFF << (32 - bits));
            return (a & mask) == (n & mask);
        }

        private static uint ToUInt32(string dotted)
        {
            string[] p = dotted.Split('.');
            if (p.Length != 4) return 0;
            uint v = 0;
            for (int i = 0; i < 4; i++)
            {
                if (!byte.TryParse(p[i], out byte b)) return 0;
                v = (v << 8) | b;
            }
            return v;
        }

        private static string FromUInt32(uint v) =>
            $"{(v >> 24) & 0xFF}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";

        internal static bool ParseBool(string value, out bool result)
        {
            string v = value.Trim().ToLowerInvariant();
            switch (v)
            {
                case "1": case "true": case "on": case "yes": result = true; return true;
                case "0": case "false": case "off": case "no": result = false; return true;
                default: result = false; return false;
            }
        }

        /// <summary>读 YAML 布尔用（内核要 <c>true</c>/<c>false</c> 字面量，不是 1/0）。</summary>
        public string GetBoolAsYaml(string key)
        {
            return ParseBool(Get(key), out bool b) ? (b ? "true" : "false") : "";
        }

        private static string First(string value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        /// <summary>写进 YAML 行内流式序列（<c>['a', 'b']</c>）；调用方已保证元素过 <see cref="IsSafeScalar"/>。</summary>
        public static string ToInlineList(string value)
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (string s in SplitList(value))
            {
                if (!first) sb.Append(", ");
                sb.Append('\'').Append(s).Append('\'');
                first = false;
            }
            return sb.Append(']').ToString();
        }

        /// <summary>这个值是不是列表类键（要包成 YAML 行内序列），而不是标量键。</summary>
        public static bool IsListKey(string key) => key switch
        {
            "fake-ip-filter" or "default-nameserver" or "nameserver" or "fallback" or
            "proxy-server-nameserver" or "direct-nameserver" or "nrpt-dns" => true,
            _ => false,
        };

        private static bool IsSafeScalar(string s)
        {
            if (s.Length == 0) return false;
            foreach (char c in s)
                if (c == '|' || c == ',' || c == '\'' || c < ' ')
                    return false;
            return true;
        }

        private static string Cut(string s) => Cut(s, 80);

        private static string Cut(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    }
}
