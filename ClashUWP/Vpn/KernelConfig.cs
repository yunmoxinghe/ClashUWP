using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

using Windows.Storage;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// 「交给 mihomo 的那份 YAML」的就地改写器。五件事，各自只认一个块：
    ///   <c>dns:</c> 强制 fake-ip、<c>tun:</c> 关掉内核自己的网卡、
    ///   <c>proxy-groups:</c> 把 url-test 的 tolerance 锁死（只测速不换节点）、
    ///   <c>rules:</c> 在开头注入应用分流规则（<see cref="AppSplit"/>，1.0.35 / G9）、
    ///   <c>dns:</c> 再叠一层用户覆盖（<see cref="DnsConfig"/>，1.0.69 / DNS 可配）。
    ///
    /// 【为什么必须在代码里改，而不是手改 LocalState\config.yaml】
    ///   2026-10-16 run 24 的结论链（Docs/08 §9）：
    ///     · <c>redir-host</c>（订阅默认）下，应用连的是<b>真 IP</b>。本机三个国内解析器
    ///       （路由器 192.168.0.1、223.5.5.5、119.29.29.29）对 www.google.com 全给污染答案
    ///       （24-B 实测拿到 <c>2001::1,104.244.42.197</c>），域名流量因此必死；
    ///     · <c>fake-ip</c> 让劫持器<b>立刻回假 IP</b>（24-A：<c>www.google.com → 198.18.0.15</c>），
    ///       不需要先解析出真 IP，污染根本没有机会参与；应用连 198.18.x → 命中
    ///       include 0.0.0.0/0 → 进隧道 → 内核按<b>域名</b>选规则经节点出站。
    ///       头条判据第一次成立：<c>https://www.google.com/generate_204 = 204 / 2.87 s</c>，
    ///       <c>raw.githubusercontent.com = 200</c>（未连接时两条都是 8 s 超时）。
    ///   这条路径<b>只有内核配置知道</b>，所以必须写进代码；前台生成的订阅配置一直是
    ///   redir-host，实测时靠手改文件才验出结论，不能作为交付形态。
    ///
    /// 【为什么改文本而不是 PATCH /configs】
    ///   mihomo 的 REST 控制器不能改 <c>dns.enhanced-mode</c>（<c>PATCH /configs</c> 只接受
    ///   mixed-port/mode/interface-name 等少数键），换 enhanced-mode 只能走
    ///   <c>PUT /configs?path=</c> 的整份重载 —— 即"磁盘上那份文件"说了算。
    ///   所以这里把改写结果<b>落成一个新文件</b>（<c>config.kernel.yaml</c>），
    ///   冷启动用内存文本、常驻热重载用它的绝对路径，两者指向同一份内容，
    ///   热重载不会把 fake-ip 覆盖回订阅原文（run 24 就是靠 hotReload=0 才没被覆盖，
    ///   那是实验形态，不能留给产品）。
    ///
    /// 【为什么按块解析而不是正则全文替换】
    ///   <c>enable: true</c> 这一行在 <c>dns:</c> 块里是"DNS 总开关"（该留 true），
    ///   在 <c>tun:</c> 块里是"wintun 设备"（在 UWP 沙箱内必然失败，必须 false）。
    ///   全文替换会把前者一起改掉。这里只认"顶层键 → 其后直到下一个顶层键"的行区间。
    /// </summary>
    internal static partial class KernelConfig
    {
        /// <summary>改写后落盘的文件名（与订阅原文 <c>config.yaml</c> 同目录，便于 proxy-provider 相对路径照旧）。</summary>
        internal const string EffectiveFileName = "config.kernel.yaml";

        /// <summary>
        /// fake-ip 地址池（mihomo 侧的写法，主机位非零也无妨，mihomo 按 /16 划池）。
        /// 与 <see cref="VpnPluginConfig"/> 的 include 0.0.0.0/0 天然相容；换网卡、换网络都不失效。
        /// </summary>
        internal const string FakeIpRange = "198.18.0.1/16";

        /// <summary>
        /// 同一个池的<b>路由写法</b>（网络地址 /16）。
        /// 为什么必须分两个常量：<c>VpnRoute</c> 要的是网络号，把 <c>198.18.0.1/16</c>
        /// 直接塞给 <c>StartWithTrafficFilter</c> 的路由表是拿主机位当网络号 ——
        /// mihomo 那边无所谓，路由层可不无所谓。窄 include（<c>includeMode=narrow</c>）用它。
        /// </summary>
        internal const string FakeIpRoute = "198.18.0.0/16";

        /// <summary>
        /// 不进 fake-ip 名单的域名（这些走真解析，直连才有意义）：
        /// msn 探针与局域网名必须留真 IP，否则 Windows 的联网探测和本机名解析会被假 IP 糊住。
        /// </summary>
        private const string FakeIpFilter =
            "['*.lan', 'localhost', '+.localdomain', '+.msftconnecttest.com', '+.msftncsi.com']";

        /// <summary>
        /// 把订阅/前台 YAML 改成"本插件能跑通数据面"的形态。
        /// 输入不是 YAML（比如内置兜底那份 JSON）时原样返回。任何异常原样返回 ——
        /// 改写失败最多退回到 redir-host 的老行为，绝不能让 Connect 抛在平台回调里。
        ///
        /// ★【1.0.35】不再有"只传文本"的重载：应用分流名单是这次改写的<b>输入之一</b>，
        ///   漏传就等于"静默不注入"。<see cref="ClashVpnPlugin"/> 步 4.1 与
        ///   <see cref="ClashUWP.Backend.SubscriptionReload"/> 两条路必须都走这一个入口、
        ///   产出<b>逐字相同</b>的那份 <c>config.kernel.yaml</c>
        ///   （Docs/12 §9.4 的"单源"约定：少一条注入，热重载就会把名单覆盖回订阅原文）。
        /// </summary>
        internal static string Transform(string yaml, AppSplit? split, DnsConfig? dns,
                                         int ipv6Arm, string ipv6FakeRange,
                                         out string splitNote, out string dnsNote)
        {
            splitNote = "未处理";
            dnsNote = "未处理";
            try
            {
                if (string.IsNullOrWhiteSpace(yaml))
                {
                    splitNote = split == null || split.IsEmpty ? "无名单，配置未动" : "配置为空，名单忽略";
                    dnsNote = dns == null || dns.IsEmpty ? "无覆盖" : "配置为空，覆盖忽略";
                    return yaml;
                }
                // 内置兜底配置是 JSON（首字符 '{'），不该用行式 YAML 解析器碰它。
                if (yaml.TrimStart().StartsWith("{"))
                {
                    splitNote = split == null || split.IsEmpty ? "无名单，配置未动" : "兜底 JSON 不注入名单";
                    dnsNote = dns == null || dns.IsEmpty ? "无覆盖" : "兜底 JSON 不叠 DNS 覆盖";
                    return yaml;
                }

                List<string> lines = new(yaml.Replace("\r\n", "\n").Split('\n'));
                // ★【1.0.39 · G7】顶层 ipv6 必须在 dns/tun 之前 —— mihomo 的
                //   config.go:716 `parseIPV6(rawCfg)` 的注释原文就是 "must before DNS and Tun"，
                //   而它的实现（:1680-1682）在开关关闭或本机无全局 v6 时会把
                //   `tun.inet6-address` / `dns.fake-ip-range6` 整个清空。
                // ★【1.0.126】档 1 也写（写 false），不再"不碰订阅原文"：
                //   不碰的后果是同一个档 1 在不同机场下内核 v6 能力不同（订阅写 true 就是 true），
                //   而且档 2 用过再降回 1 时磁盘上留着上一轮写的 true。
                //   档 1 的语义本来就是"堵泄漏、内核不转发 v6"（顶层 false 让 <c>parseIPV6</c>
                //   把 <c>inet6-address</c>/<c>fake-ip-range6</c> 清空，桥侧 gvisor 栈因此没有 v6），
                //   写 false 只是让这个档与订阅无关。
                // ★【1.0.129 · 对照 Verge 源码】档 0 的"不写"不等于"关"：mihomo 的顶层
                //   <c>ipv6</c> 默认是 <b>true</b>（<c>DefaultRawConfig</c>），且
                //   <c>RawTun.Inet6Address</c> 默认 <c>fdfe:dcba:9876::1/126</c> —— 所以在
                //   有全局 v6 的宿主上，档 0 的内核栈本来就可能带 v6 地址。快照里的
                //   <c>v6栈=</c> 只反映<b>档位意图</b>，不是实测值；别拿它当"本机没有 v6"的证据。
                if (ipv6Arm >= 1)
                    EnsureTopLevelIpv6(lines, ipv6Arm >= 2);
                EnsureDnsBlock(lines, ipv6Arm, ipv6FakeRange);
                EnsureTunBlock(lines, ipv6Arm, ipv6FakeRange);
                // ★【1.0.40 · DNS 可配】覆盖必须叠在 EnsureDnsBlock 之后：
                //   后者为了数据面会强制 enhanced-mode=fake-ip 并补默认池，
                //   用户在这份文件里写的值应当<b>覆盖</b>那些默认，而不是被它们覆盖。
                ApplyDnsOverrides(lines, dns, ipv6Arm, ipv6FakeRange, out dnsNote);
                LockUrlTestTolerance(lines);
                InjectAppSplitRules(lines, split, out splitNote);

                StringBuilder sb = new(lines.Count * 24);
                for (int i = 0; i < lines.Count; i++)
                {
                    if (i > 0)
                        sb.Append('\n');
                    sb.Append(lines[i]);
                }
                return sb.ToString();
            }
            catch
            {
                splitNote = "改写异常，退回原文";
                dnsNote = "改写异常，覆盖未应用";
                return yaml;
            }
        }

        /// <summary>把改写结果写进 LocalState，返回绝对路径；写失败返回 null（调用方据此跳过热重载）。</summary>
        internal static string? WriteEffectiveFile(string text)
        {
            try
            {
                StorageFile file = ApplicationData.Current.LocalFolder
                    .CreateFileAsync(EffectiveFileName, CreationCollisionOption.ReplaceExisting)
                    .AsTask().GetAwaiter().GetResult();
                FileIO.WriteTextAsync(file, text).AsTask().GetAwaiter().GetResult();
                return file.Path;
            }
            catch
            {
                return null;
            }
        }

        // ── 实现细节 ────────────────────────────────────────────────────────

        /// <summary>
        /// <c>dns:</c> 块：enhanced-mode 强制 fake-ip，缺 fake-ip-range / fake-ip-filter 就补。
        /// 没有 dns 块时（订阅只写代理不写 DNS 的情况）追加一个最小可用块。
        /// </summary>
        private static void EnsureDnsBlock(List<string> lines, int ipv6Arm, string ipv6FakeRange)
        {
            int start = FindTopLevelKey(lines, "dns");
            if (start >= 0 && IsInlineBlock(lines, start))
                return;   // 见 IsInlineBlock：行内流式写法别改，一改就把 YAML 改坏
            if (start < 0)
            {
                // 顶层没有 dns：插到文件末尾。注意前面必须已有换行收尾。
                if (lines.Count > 0 && lines[^1].Trim().Length > 0)
                    lines.Add("");
                lines.Add("dns:");
                lines.Add("    enable: true");
                // 档 2 要 AAAA 回假 IP；档 1 明确不答 AAAA（泄漏封堵的一半）。
                // 档 0 不写：mihomo 的 <c>dns.ipv6</c> 默认就是 false（Meta config.go
                // 的 <c>DefaultRawConfig</c>），写出来等于把"档 0 一个字节都不碰"的契约
                // 破在新建块里（对照 Verge 源码时发现的）。
                if (ipv6Arm >= 1)
                    lines.Add(ipv6Arm >= 2 ? "    ipv6: true" : "    ipv6: false");
                lines.Add("    default-nameserver: [223.5.5.5, 119.29.29.29]");
                lines.Add("    enhanced-mode: fake-ip");
                lines.Add($"    fake-ip-range: {FakeIpRange}");
                if (ipv6Arm >= 2)
                    lines.Add($"    fake-ip-range6: {ipv6FakeRange}");
                lines.Add($"    fake-ip-filter: {FakeIpFilter}");
                lines.Add("    nameserver: ['https://doh.pub/dns-query', 'https://dns.alidns.com/dns-query']");
                lines.Add("    fallback: ['https://223.5.5.5/dns-query', 'https://223.6.6.6/dns-query']");
                return;
            }

            int end = EndOfBlock(lines, start);
            int indent = ChildIndent(lines, start, end);

            int enhanced = FindKeyInRange(lines, start + 1, end, "enhanced-mode");
            if (enhanced < 0)
            {
                lines.Insert(start + 1, new string(' ', indent) + "enhanced-mode: fake-ip");
                enhanced = start + 1;
                end++;
            }
            else
            {
                // 同 tun 的 dns-hijack：值为空说明它是"块序列/块映射的父键"，
                // 整行替换会留下孤儿行把 YAML 改坏（run 25 的内核启动失败就是这么来的）。
                if (ValueOf(lines[enhanced]).Length == 0)
                    return;
                lines[enhanced] = KeyLine(lines[enhanced], "enhanced-mode", "fake-ip");
            }

            // fake-ip-range / fake-ip-filter 紧跟 enhanced-mode，缩进照它，YAML 读起来也顺。
            string keyIndent = LeadingSpaces(lines[enhanced]);
            if (FindKeyInRange(lines, start + 1, end, "fake-ip-range") < 0)
            {
                lines.Insert(enhanced + 1, keyIndent + $"fake-ip-range: {FakeIpRange}");
                end++;
            }
            if (FindKeyInRange(lines, start + 1, end, "fake-ip-filter") < 0)
            {
                int at = FindKeyInRange(lines, start + 1, end, "fake-ip-range");
                lines.Insert(at + 1, keyIndent + $"fake-ip-filter: {FakeIpFilter}");
                end++;
            }

            // ★【1.0.39 · G7】以下两项只在档 ≥1 时动。档 0（默认）逐字保持订阅原文，
            //   连"订阅里写了 ipv6: true"也不碰 —— 那是本批之前的行为。
            if (ipv6Arm >= 1)
            {
                string want = ipv6Arm >= 2 ? "true" : "false";
                int ipv6Key = FindKeyInRange(lines, start + 1, end, "ipv6");
                if (ipv6Key < 0)
                {
                    lines.Insert(enhanced + 1, keyIndent + "ipv6: " + want);
                    end++;
                }
                else if (ValueOf(lines[ipv6Key]).Length > 0)
                {
                    lines[ipv6Key] = KeyLine(lines[ipv6Key], "ipv6", want);
                }
                // 值为空 ⇒ 是块形状，别整行替换（同上面 enhanced-mode 的理由）。

                if (ipv6Arm >= 2 &&
                    FindKeyInRange(lines, start + 1, end, "fake-ip-range6") < 0)
                {
                    int at = FindKeyInRange(lines, start + 1, end, "fake-ip-range");
                    if (at < 0) at = enhanced;
                    lines.Insert(at + 1, keyIndent + $"fake-ip-range6: {ipv6FakeRange}");
                    end++;
                }
            }
        }

        /// <summary>
        /// 顶层 <c>ipv6: true/false</c>。★【1.0.39 · G7】mihomo 的 <c>parseIPV6</c>
        /// （<c>config.go:716/:1679-1682</c>）在它关闭时会把 <c>tun.inet6-address</c> 与
        /// <c>dns.fake-ip-range6</c> 清空 —— 所以"想开 v6"必须开这一项（档 2），
        /// 而【1.0.126】起"档 1"也显式写 <c>false</c>：档 1 的语义是堵泄漏且内核不转发 v6，
        /// 不碰订阅原文会让同一个档 1 在不同机场下内核 v6 能力不同，
        /// 且 2 降回 1 时磁盘上留着上一轮的 true。
        /// ★ 档 0 仍然一个字节都不碰（那是"与 1.0.69 逐字一致"的凭据）。
        /// 已有键且是标量就改写；值为空（块形状）则不动，不拿 YAML 冒险。
        /// </summary>
        private static void EnsureTopLevelIpv6(List<string> lines, bool wantV6)
        {
            string want = wantV6 ? "true" : "false";
            int at = FindTopLevelKey(lines, "ipv6");
            if (at >= 0)
            {
                if (ValueOf(lines[at]).Length > 0)
                    lines[at] = "ipv6: " + want;
                return;
            }

            // 插到"文件头注释 / 文档分隔符"之后，保持 YAML 可读且不破坏 ---。
            int insert = 0;
            while (insert < lines.Count)
            {
                string t = lines[insert].Trim();
                if (t.Length == 0 || t.StartsWith("#") || t == "---" || t == "...")
                    insert++;
                else
                    break;
            }
            lines.Insert(insert, "ipv6: " + want);
        }

        /// <summary>
        /// <c>tun:</c> 块：<c>enable</c> 强制 false（executor 见 true 会建 wintun 设备，
        /// UWP 沙箱内必失败，见 <see cref="ClashVpnPlugin"/> 步 7 的注释），
        /// 并确保 <c>dns-hijack</c> 含 <c>any:53</c> —— fake-ip 靠的就是这个劫持器。
        /// 完全没有 tun 块时补一个。
        /// </summary>
        private static void EnsureTunBlock(List<string> lines, int ipv6Arm, string ipv6FakeRange)
        {
            // ★【1.0.39 · G7】档 2 要内核 v6 栈地址。取 fake 池的网络号 +1（/126），
            //   与 mihomo 默认的 fdfe:dcba:9876::1/126 同形态；池子从 ::4 开始分配，
            //   ::2 是 DNS 劫持地址，因此 ::1/126 与假地址不冲突（fakeip/pool.go）。
            string inet6 = ipv6Arm >= 2 ? Inet6FromPool(ipv6FakeRange) : "";

            int start = FindTopLevelKey(lines, "tun");
            if (start >= 0 && IsInlineBlock(lines, start))
            {
                // 行内流式写法（tun: {enable: true, ...}）：只做行内替换，
                // 不整块重写 —— enable=true 在这里是"必须改"（wintun 在沙箱里建不出来），
                // 而整行重写会把用户写的其余字段一起丢掉。
                lines[start] = InlineRegex().Replace(lines[start], "enable: false");
                if (!lines[start].Contains("any:53") && !lines[start].Contains("0.0.0.0:53"))
                    lines[start] = HijackRegex().Replace(lines[start], "dns-hijack: [any:53]");

                if (ipv6Arm >= 2 && !lines[start].Contains("inet6-address"))
                {
                    int close = lines[start].LastIndexOf('}');
                    if (close > 0)
                    {
                        string head = lines[start].Substring(0, close).TrimEnd().TrimEnd(',');
                        string tail = lines[start].Substring(close);
                        // `tun: {}` 这种空映射不能加逗号，否则变成 `{, ...}`。
                        string sep = head.EndsWith("{", StringComparison.Ordinal) ? "" : ",";
                        lines[start] = head + sep + $" inet6-address: [{inet6}] " + tail;
                    }
                }
                return;
            }
            if (start < 0)
            {
                if (lines.Count > 0 && lines[^1].Trim().Length > 0)
                    lines.Add("");
                lines.Add("tun:");
                lines.Add("    enable: false");
                lines.Add("    stack: gvisor");
                lines.Add("    dns-hijack: [any:53]");
                if (ipv6Arm >= 2)
                    lines.Add($"    inet6-address: [{inet6}]");
                lines.Add("    mtu: 1400");
                return;
            }

            int end = EndOfBlock(lines, start);

            int enable = FindKeyInRange(lines, start + 1, end, "enable");
            if (enable >= 0 && ValueOf(lines[enable]).Length > 0)
                lines[enable] = KeyLine(lines[enable], "enable", "false");

            int hijack = FindKeyInRange(lines, start + 1, end, "dns-hijack");
            if (hijack < 0)
            {
                string indent = enable >= 0 ? LeadingSpaces(lines[enable]) : "    ";
                lines.Insert(start + 1, indent + "dns-hijack: [any:53]");
                // 不 return：档 2 还要补 inet6-address。插在 dns-hijack 之后即可。
                end++;
                if (ipv6Arm >= 2 && FindKeyInRange(lines, start + 1, end, "inet6-address") < 0)
                    lines.Insert(start + 2, indent + $"inet6-address: [{inet6}]");
                return;
            }

            // ★ run 25 咬人的地方：dns-hijack 有两种合法写法，
            //   <c>dns-hijack: [any:53]</c>（行内流式）与
            //   <c>dns-hijack:</c> 换行后跟 <c>- any:53</c>（块序列）。
            //   把后者整行改写成前者，会把它下面那些 <c>- xxx</c> 变成孤儿行 ⇒
            //   mihomo <c>parse mihomo config: yaml: line 576: did not find expected key</c> ⇒
            //   内核没起来，但隧道照装 ⇒ 快照 Encapsulated=883 / Injected=0 的整机黑洞。
            //   所以：行内值才允许整行替换；块序列只能往下看条目、缺了就在条目末尾<b>补一行</b>。
            string value = ValueOf(lines[hijack]);
            if (value.Length > 0)
            {
                if (!value.Contains("any:53") && !value.Contains("0.0.0.0:53"))
                    lines[hijack] = KeyLine(lines[hijack], "dns-hijack", "[any:53]");
            }
            else
            {
                int last = hijack;
                bool hasAny = false;
                for (int i = hijack + 1; i < lines.Count; i++)
                {
                    string t = lines[i].Trim();
                    if (t.Length == 0)
                        continue;
                    if (!t.StartsWith("-"))
                        break;
                    last = i;
                    if (t.Contains("any:53") || t.Contains("0.0.0.0:53"))
                        hasAny = true;
                }
                if (!hasAny)
                {
                    string itemIndent = last > hijack
                        ? LeadingSpaces(lines[last])
                        : LeadingSpaces(lines[hijack]) + "  ";
                    lines.Insert(last + 1, itemIndent + "- any:53");
                    end++;
                }
            }

            if (ipv6Arm >= 2 && FindKeyInRange(lines, start + 1, end, "inet6-address") < 0)
            {
                int anchor = enable >= 0 ? enable : hijack;
                string indent = LeadingSpaces(lines[anchor]);
                lines.Insert(anchor + 1, indent + $"inet6-address: [{inet6}]");
            }
        }

        /// <summary>
        /// ★【1.0.40 · DNS 可配】把 <see cref="DnsConfig"/> 的白名单项叠进 <c>dns:</c> 块。
        ///
        /// 【为什么一条一条就地改，不整块重写】订阅的 dns 块里有大量本功能不认识的键
        ///   （geodata、fallback-filter、cache-algorithm…），整块重写等于把它们丢掉；
        ///   而"丢掉一个 mihomo 需要的键"与 run 25 的孤儿行同属"改坏 YAML"一类，
        ///   代价是内核起不来、隧道照装。这里只碰白名单里的键。
        ///
        /// 【三条防护】
        ///   · <c>dns:</c> 是行内流式写法（<c>dns: {…}</c>）⇒ <b>整份跳过</b>，一条不改；
        ///   · 要改的键存在但其值是<b>空</b>（块序列/块映射的父键，如
        ///     <c>nameserver:</c> 换行跟 <c>- …</c>）⇒ 跳过该键，绝不留孤儿行；
        ///   · <c>ipv6</c> 与 <c>fake-ip-range6</c> 的归属由 G7 档位决定：
        ///     档 ≥1 时 dns.ipv6 是泄漏封堵的一部分，档 &lt;2 时 fake-ip-range6 是死配置
        ///     —— 这两种情况跳过并写进 <paramref name="note"/>，让快照里看得见。
        ///
        /// 【覆盖语义】列表类键是<b>整条替换</b>（不是追加）；想保留订阅默认就把原值一起抄进来。
        /// </summary>
        private static void ApplyDnsOverrides(List<string> lines, DnsConfig? dns,
                                              int ipv6Arm, string ipv6FakeRange, out string note)
        {
            if (dns == null)
            {
                note = "总闸关闭（dnsConfig=0）";
                return;
            }
            if (dns.IsEmpty)
            {
                note = dns.Rejected.Count > 0
                    ? $"0 项有效（{dns.Rejected.Count} 行被拒，见 channel=dns 日志）"
                    : "无覆盖";
                return;
            }

            int start = FindTopLevelKey(lines, "dns");
            if (start < 0)
            {
                note = $"{dns.Entries.Count} 项未应用：配置里没有 dns 块";
                return;
            }
            if (IsInlineBlock(lines, start))
            {
                note = $"{dns.Entries.Count} 项未应用：dns 是行内流式写法";
                return;
            }

            int applied = 0;
            int nrptOnly = 0;
            var skipped = new List<string>();

            foreach (DnsEntry e in dns.Entries)
            {
                string key = e.Key;
                if (key == "nrpt-dns")
                {
                    nrptOnly++;     // 只给 NRPT，不进 YAML
                    continue;
                }

                if (key == "ipv6" && ipv6Arm >= 1)
                {
                    // 档 1/2 的 AAAA 开关是数据面安全属性，不接受覆盖。
                    skipped.Add("ipv6(由 settings 档位决定)");
                    continue;
                }
                if (key == "fake-ip-range6" && ipv6Arm < 2)
                {
                    skipped.Add("fake-ip-range6(需 ipv6=2)");
                    continue;
                }

                string value;
                if (key == "fake-ip-range6")
                    value = ipv6FakeRange;          // 已由调用方归一（ULA + 网络号/前缀长）
                else if (key == "enhanced-mode")
                    value = "fake-ip";
                else if (key == "ipv6" || key == "prefer-h3")
                    value = dns.GetBoolAsYaml(key);
                else if (DnsConfig.IsListKey(key))
                    value = DnsConfig.ToInlineList(e.Value);
                else
                    value = e.Value;

                // 每次都重新定位：上一条可能已经插过行，行号不能复用。
                int s = FindTopLevelKey(lines, "dns");
                if (s < 0)
                {
                    skipped.Add($"{key}(dns 块消失)");
                    continue;
                }
                int end = EndOfBlock(lines, s);
                int at = FindKeyInRange(lines, s + 1, end, key);
                if (at >= 0 && ValueOf(lines[at]).Length == 0)
                {
                    skipped.Add($"{key}(块写法，未替换)");
                    continue;
                }

                if (at >= 0)
                {
                    lines[at] = KeyLine(lines[at], key, value);
                }
                else
                {
                    int indent = ChildIndent(lines, s, end);
                    lines.Insert(end, new string(' ', indent) + key + ": " + value);
                }
                applied++;
            }

            string nrptPart = nrptOnly > 0 ? $"，nrpt-dns→NRPT×{nrptOnly}" : "";
            if (applied == 0 && skipped.Count == 0 && nrptOnly > 0)
            {
                note = $"内核 dns 块未动（只有 NRPT 覆盖 {nrptOnly} 项）" +
                       (dns.Rejected.Count > 0 ? $"，另拒 {dns.Rejected.Count} 行" : "");
                return;
            }
            note = applied == 0
                ? $"{dns.Entries.Count} 项均未应用" +
                  (skipped.Count > 0 ? $"：{string.Join("、", skipped)}" : "") + nrptPart
                : $"应用 {applied} 项" +
                  (skipped.Count > 0 ? $"（跳过 {string.Join("、", skipped)}）" : "") +
                  nrptPart +
                  (dns.Rejected.Count > 0 ? $"，另拒 {dns.Rejected.Count} 行" : "");
        }

        /// <summary>
        /// 由 fake-ip v6 池推出内核栈自己的 v6 地址（网络号 +1，/126）。
        /// 例：<c>fdfe:dcba:9876::/64</c> → <c>fdfe:dcba:9876::1/126</c>。
        /// 池值已经过 <see cref="LocalSettings.TryNormalizeV6Prefix"/> 归一；这里再兜一次底。
        /// </summary>
        private static string Inet6FromPool(string pool)
        {
            if (!LocalSettings.TryNormalizeV6Prefix(pool, out string normalized))
                normalized = LocalSettings.DefaultIpv6FakeRange;

            string network = normalized.Substring(0, normalized.IndexOf('/'));
            byte[] bytes = System.Net.IPAddress.Parse(network).GetAddressBytes();
            for (int i = bytes.Length - 1; i >= 0; i--)
            {
                if (++bytes[i] != 0)
                    break;
            }
            return $"{new System.Net.IPAddress(bytes)}/126";
        }

        /// <summary>
        /// ★【1.0.48】把 <c>proxy-groups</c> 里 <b>url-test</b> 组的 <c>tolerance</c> 提到极大值
        /// ⇒ <b>只测速、不换节点</b>。
        ///
        /// 【为什么需要它 —— 2026-10-18 实测出来的真问题】
        ///   订阅给的组是（实测原文）：
        ///   <c>{ name: 星链机场, type: url-test, proxies: [32 个节点], url: 'http://www.gstatic.com/generate_204',
        ///   interval: 300, lazy: false, tolerance: 30 }</c>
        ///   <c>tolerance</c> 的语义（mihomo 官方）：<b>新节点比当前节点快出这个毫秒数才切</b>。
        ///   30ms 的容差在 32 个节点、延迟普遍 200~500ms 的池子里 = <b>几乎每轮都切</b>。
        ///   实测后果：<b>出口 IP 每 5 分钟在德国/印度/俄罗斯/美国之间跳</b>
        ///   （实测取到过 85.209.48.141 / 115.187.16.7 / 45.90.216.242 / 107.150.1.135）。
        ///   出口 IP 乱跳的直接后果是 Cloudflare / OpenAI 的风控把连接打成 challenge：
        ///   <c>chatgpt.com</c> 返回 <c>403</c> + <c>Cf-Mitigated: challenge</c>，
        ///   页面表现就是"<b>连了一半</b>"（TCP/TLS 都通，HTTP 层被拦）。
        ///   ⇒ <b>IP 稳定是风控通过的前提</b>，而"稳定"只能靠"不自动换节点"拿到。
        ///
        /// 【★ 为什么不用 tolerance=0 或改组类型】
        ///   · <c>tolerance: 0</c> = 严格跟最低延迟 ⇒ <b>换得更勤</b>，正好相反。
        ///   · 改成 <c>type: select</c> = 不再自动测速 ⇒ <b>延迟列全空</b>，
        ///     反而丢了"只测速"这件事（用户要的就是测速照做、节点不动）。
        ///   ⇒ 把 tolerance 抬到"实际不可能达到"的量级，是<b>唯一同时满足两个要求</b>的改法：
        ///     <b>测速照常进行</b>（history 一直更新，UI 的 delay 列照常有值），
        ///     <b>但永远不会因为延迟差而切换</b>。
        ///
        /// 【★ 取值 999999 的理由】mihomo 判定是"新节点快出 tolerance 毫秒才切"，
        ///   实测节点延迟在 0~1760ms 量级，999999ms（≈16.6 分钟）远大于任何真实差值
        ///   ⇒ 等价于"永不自动切换"，但仍走同一条测速路径（不改变内核行为形态，只改阈值）。
        ///
        /// 【★ 改写范围：只动 url-test 组的 tolerance】
        ///   · <c>fallback</c> 组是"当前节点挂了才换"（顺序敏感），那个语义<b>正是我们要的</b>，不动。
        ///   · <c>select</c> 组不测速，没有 tolerance 键，不动。
        ///   · 只在<b>行内流式</b>的组定义里替换（订阅的写法就是行内），
        ///     块写法（<c>- name: x</c> 换行 <c>  type: url-test</c>）另有一支处理。
        /// </summary>
        /// <summary>
        /// ★【1.0.75 纠偏】块写法组的边界判据。
        ///
        /// 旧写法是"往下扫到<b>任何</b>以 <c>- </c> 开头的行就停"，本意是"到下一个组为止"。
        /// 但块写法的组长这样：
        /// <code>
        ///   - name: 美国自动
        ///     type: url-test
        ///     proxies:
        ///       - 🇺🇸 01        ← 也是 "- " 开头，可它是<b>本组的成员行</b>
        ///       - 🇺🇸 02
        ///     tolerance: 30
        /// </code>
        /// <c>type:</c> 行往下第一眼就撞见 <c>proxies:</c> 里的成员行，于是 break，
        /// <c>tolerance</c> 永远扫不到 —— 实测这份订阅的块写法组到内核里仍是
        /// <c>tolerance: 30</c>，出口 IP 照跳（1.0.35 那次"锁死切换"对块写法<b>从未生效</b>）。
        ///
        /// 【新判据】真正的"下一个组"是<b>与本组列表项同一列</b>（或更左）的 <c>- </c>；
        ///   更深的是本组内部的成员/子键，continue 越过去。
        ///   本组列表项在哪一列，由 <c>type:</c> 行<b>往上</b>找第一个更左的 <c>- </c> 得出
        ///   —— 与 <see cref="ChildIndent"/> 同一思路：缩进是订阅给的，不写死。
        /// ★ 判不出列（向上找不到更左的 <c>- </c>）就<b>整组不动</b>：
        ///   这里宁可漏改一条 tolerance，也不能把 YAML 改坏（run 25 的教训见
        ///   <see cref="InjectAppSplitRules"/> 注释：改坏 YAML 的代价是"内核没起来但隧道照装"）。
        /// </summary>
        /// <returns>本次锁定/补写的 tolerance 行数（诊断与离线测试用）。</returns>
        internal static int LockUrlTestTolerance(List<string> lines)
        {
            const string locked = "999999";
            int touched = 0;

            int start = FindTopLevelKey(lines, "proxy-groups");
            if (start < 0)
                return 0;

            int end = EndOfBlock(lines, start);

            for (int i = start + 1; i < end; i++)
            {
                string line = lines[i];
                if (line.Trim().Length == 0)
                    continue;

                // 块写法的组：`- name: x` 之后才是 `type: url-test` 等子键。
                // ★ 必须先判"这一行是不是 type 行"，否则块写法会被下面的行内分支抢走
                //   （块写法的 type 行内容也是 "type: url-test"，Contains 一样成立），
                //   而它的 tolerance 在**另一行** ⇒ 行内分支找不到、块分支又进不去 ⇒ 漏改。
                if (line.TrimStart().StartsWith("type:") ||
                    line.TrimStart().StartsWith("type :"))
                {
                    if (!line.Contains("url-test"))
                        continue;

                    int typeCol = LeadingSpaces(line).Length;

                    // ① 定界：本组列表项的 "- " 在哪一列、在哪一行（向上找第一个比 type 行更左的）。
                    int itemCol = -1;
                    int itemLine = -1;
                    for (int k = i - 1; k > start; k--)
                    {
                        string up = lines[k].TrimStart();
                        if (!(up.StartsWith("- ") || up == "-"))
                            continue;
                        int col = LeadingSpaces(lines[k]).Length;
                        if (col < typeCol) { itemCol = col; itemLine = k; break; }
                    }
                    if (itemCol < 0)
                        continue;   // 定不了界就整组不动（见上面那条"宁可漏改"）

                    // ② 先往<b>上</b>看：有的订阅把 tolerance 写在 type 之前
                    //   （`- name / tolerance / type / proxies`）。这一条必须查，
                    //   否则下面只会往下找 → 找不到 → 补一行 → <b>同一个映射里两个 tolerance 键</b>，
                    //   而 yaml.v3 对重复键是<b>直接报错</b>的 —— 那就是"改坏 YAML"，
                    //   代价见 InjectAppSplitRules 的注释（内核起不来但隧道照装）。
                    int tol = -1;
                    for (int k = i - 1; k > itemLine; k--)
                    {
                        string t = lines[k].Trim();
                        if (t.StartsWith("tolerance:")) { tol = k; break; }
                    }

                    // ③ 在本组的区间内找 tolerance；没有就在 type 行同缩进<b>补一行</b>。
                    //    补这行是有原因的：mihomo 的 url-test 默认 tolerance = 0，
                    //    等于"只要有人更快就切"，比 30 更糟；订阅里不写这个键的组
                    //    以前会被整个跳过，行为上就是"这份订阅没被锁过"。
                    if (tol < 0)
                    {
                        for (int j = i + 1; j < end; j++)
                        {
                            string raw = lines[j];
                            string t = raw.Trim();
                            if (t.Length == 0)
                                continue;

                            bool isDash = t.StartsWith("- ") || t == "-";
                            if (isDash && LeadingSpaces(raw).Length <= itemCol)
                                break;                      // 这才是下一个组
                            if (isDash)
                                continue;                   // ★ 本组的成员行，越过（旧写法在这里 break ⇒ 漏改）

                            // 比本组列表项还左的非空行 = 这块已经结束（理论上 EndOfBlock 已挡）
                            if (!isDash && LeadingSpaces(raw).Length <= itemCol && t.Contains(':'))
                                break;

                            if (t.StartsWith("tolerance:"))
                            {
                                tol = j;
                                break;
                            }
                        }
                    }

                    if (tol >= 0)
                    {
                        lines[tol] = KeyLine(lines[tol], "tolerance", locked);
                        touched++;
                    }
                    else
                    {
                        lines.Insert(i + 1, LeadingSpaces(line) + "tolerance: " + locked);
                        touched++;
                        end++;   // 插了一行，块尾要跟着挪，否则后续循环少扫一行
                    }
                    continue;
                }

                // ① 行内流式：`- { name: x, type: url-test, ..., tolerance: 30 }`
                if (line.Contains("type: url-test") || line.Contains("type:url-test"))
                {
                    if (line.Contains("tolerance:"))
                    {
                        lines[i] = ToleranceRegex().Replace(line, "tolerance: " + locked);
                        touched++;
                    }
                    else
                    {
                        // 没有 tolerance 键 ⇒ 补一个（插在行尾的 " }" 之前）。
                        int close = line.LastIndexOf('}');
                        if (close > 0)
                        {
                            string head = line.Substring(0, close).TrimEnd();
                            string tail = line.Substring(close);
                            lines[i] = head + ", tolerance: " + locked + " " + tail;
                            touched++;
                        }
                    }
                    continue;
                }
            }
            return touched;
        }

        /// <summary>
        /// ★【1.0.75 · 空壳不拨号】这份配置<b>能不能真把流量送出去</b>？
        /// 能就回空串；不能就回一句可直接给用户/日志看的理由。
        ///
        /// 【为什么必须有这一条】安卓版在这一点上是<b>硬失败</b>的：
        ///   <c>ClashService.kt</c> 的启动流程是
        ///   <c>try { Clash.start() } catch (e: Exception) { reason = e.message }
        ///   finally { withContext(NonCancellable) { stopSelf() } }</c> ——
        ///   配置不可用就<b>把服务停掉并带上原因</b>，绝不"先接着，回头再说"。
        ///   我们以前相反：订阅正文读不到就回落内置兜底（那份兜底里<b>一个节点都没有</b>），
        ///   于是平台侧是一条<b>状态 Connected、却没有任何出站能承接流量</b>的隧道 ——
        ///   用户看到的"空回环连接"就是它：面板显示已连接、图标是盾、可哪儿都打不开，
        ///   而健康哨兵要 30s×10 轮才兜住（memory「Never hog the machine's network」）。
        ///   隧道期间的 0.0.0.0/0 没有内核承接就是黑洞（run 25 原话），
        ///   所以"连接成功但配置是空的"比"连接失败"更贵。
        ///
        /// 【判据：有没有可用的<b>出站</b>，不是有没有文件】
        ///   · <c>proxies:</c> 段里至少一条<b>列表项</b>（缩进判据同
        ///     <see cref="FindTopLevelKey"/>/<see cref="EndOfBlock"/>：嵌套子列表不算条目）；
        ///     行内写法（flow 列表 / 锚点 / <c>!include</c>）写在键行冒号后面 ——
        ///     这种形态数不清，但<b>一律放行</b>，见下面"假阳性危险"；
        ///   · 或者 <c>proxy-providers:</c> 段非空 —— 那种订阅的节点由内核自己拉，
        ///     行数上看不出来，不能误杀；
        ///   · 内置兜底那份是 JSON（首字符 <c>{</c>），里面没有 <c>"proxies"</c> ⇒ 不可用。
        ///
        /// 【★ 判据本身失败时一律放行】返回空串 = "不拦"。这里<b>假阴性是安全的、
        ///   假阳性是危险的</b>：判据抛异常就拒绝连接，等于把"我们解析不懂的订阅格式"
        ///   变成"用户连不上网"。宁可放过一个空壳（还有 failFast 那条内核启动失败兜着），
        ///   也不能拦掉一份能用的订阅。
        /// </summary>
        internal static string ReasonIfNoOutbound(string config)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(config))
                    return "交给内核的配置是空的";

                string trimmed = config.TrimStart();

                // ① 内置兜底（JSON）：没有 proxies 数组就是零出站。
                if (trimmed.StartsWith("{"))
                    return config.Contains("\"proxies\"")
                        ? ""
                        : "内置兜底配置不含任何节点（订阅正文缺失或读失败）";

                string[] lines = config.Replace("\r\n", "\n").Split('\n');
                // BOM 会让第一行的键匹配不上（<see cref="FindTopLevelKey"/> 按零缩进精确匹配），
                // 而"匹配不到 proxies"在这里是<b>拒绝连接</b>的判据 —— 先把它摘掉。
                if (lines.Length > 0 && lines[0].Length > 0 && lines[0][0] == '\uFEFF')
                    lines[0] = lines[0].Substring(1);

                int start = FindTopLevelKey(new List<string>(lines), "proxies");
                if (start >= 0)
                {
                    // 行内写法：flow 列表、YAML 锚点、<c>!include</c> 全把内容写在键行冒号后面。
                    // 这些形态<b>数不清条目，但"数不清"绝不等于"没有"</b> ——
                    // 只要冒号后有真内容就放行（拦掉一份能用的订阅比放过一个空壳严重得多）。
                    if (InlineAfterKey(lines[start]).Length > 0) return "";

                    int end = EndOfBlock(new List<string>(lines), start);
                    int itemCol = -1;
                    for (int i = start + 1; i < end; i++)
                    {
                        string t = lines[i].Trim();
                        if (t.Length == 0 || t.StartsWith("#")) continue;
                        int col = LeadingSpaces(lines[i]).Length;
                        bool isDash = t.StartsWith("- ") || t == "-" || t.StartsWith("-\t");
                        if (!isDash) continue;
                        if (itemCol < 0) itemCol = col;
                        if (col != itemCol) continue;              // 更深的是节点自己的子列表
                        // 到这儿就放行：本判据问的是"<b>有没有</b>条目"，
                        // 不是"能不能数出节点名"（数名字是 SubscriptionView 的活）。
                        // 只写 <c>-</c> 把 name 换到下一行的合法写法不能当空。
                        return "";
                    }
                }

                // ② proxy-providers：节点由内核运行时拉取，行数上看不见，放行。
                List<string> all = new(lines);
                int pp = FindTopLevelKey(all, "proxy-providers");
                if (pp >= 0 && InlineAfterKey(all[pp]).Length > 0)
                    return "";
                if (pp >= 0)
                {
                    int ppEnd = EndOfBlock(all, pp);
                    for (int i = pp + 1; i < ppEnd; i++)
                    {
                        string t = all[i].Trim();
                        if (t.Length == 0 || t.StartsWith("#")) continue;
                        return "";     // 段里有任何内容就按"有节点来源"处理
                    }
                }

                return start < 0
                    ? "配置里没有 proxies 段（一个可用出站都没有）"
                    : "配置的 proxies 段是空的（0 个节点）";
            }
            catch
            {
                // 判据自己出错 ⇒ 不拦（见上条"假阴性安全、假阳性危险"）。
                return "";
            }
        }

        /// <summary>
        /// 键行冒号后面的<b>行内内容</b>：flow 列表（<c>[{name: A}]</c>）、空列表（<c>[]</c>/<c>{}</c>）、
        /// YAML 锚点（<c>*base</c>）、外部引入（<c>!include x.yaml</c>）、尾注（<c># …</c>）都写在这里。
        /// 纯键行（冒号后什么都没有，或只有一个注释/空集合）返回空串。
        /// 给 <see cref="ReasonIfNoOutbound"/> 用：它只需要知道"冒号后面到底有没有东西"。
        /// </summary>
        private static string InlineAfterKey(string line)
        {
            int c = line.IndexOf(':');
            if (c < 0 || c + 1 >= line.Length) return "";
            string rest = line.Substring(c + 1).Trim();
            if (rest.Length == 0 || rest.StartsWith("#")) return "";
            if (rest is "[]" or "{}") return "";
            return rest;
        }

        /// <summary>
        /// ★【1.0.35 · G9 应用分流】把 <see cref="AppSplit"/> 名单转成
        /// <c>PROCESS-NAME</c> / <c>PROCESS-PATH</c> 规则，<b>插到订阅 <c>rules:</c> 的最前面</b>。
        ///
        /// 【为什么插在开头】mihomo 是<b>第一条命中即停</b>（<c>tunnel/tunnel.go</c> 的
        ///   <c>match()</c> 按顺序遍历 <c>Rules</c>），而订阅末尾恒有一条兜底
        ///   （<c>MATCH</c> 或 <c>GEOIP/…</c> 序列）。插在末尾等于永远轮不到。
        ///
        /// 【为什么单引号包住整条规则】照抄订阅自己的写法（实测
        ///   <c>.local/config.yaml:52</c> 是 <c>    - 'DOMAIN,xxx,DIRECT'</c>，四空格缩进 + 单引号）。
        ///   单引号标量里 <c>\</c> 是字面量，不做转义处理 ⇒ Windows 路径
        ///   <c>C:\Program Files\...\x.exe</c> 能原样进规则；
        ///   而 <see cref="AppSplit.IsSafeToken"/> 已经挡掉了 <c>'</c>，不存在提前闭合引号的可能。
        ///
        /// 【缩进从订阅的第一条规则行取，不写死】订阅有两派写法（四空格 / 两空格），
        ///   写死会把 YAML 改成另一个层级 —— 与 <see cref="EnsureDnsBlock"/> 里
        ///   <c>ChildIndent</c> 同一个理由。
        ///
        /// ★【三种"不注入"都要把原因交回调用方】没有 <c>rules:</c> 块、
        ///   <c>rules:</c> 是行内流式写法（<see cref="IsInlineBlock"/>）、块里一条子项都没有 ——
        ///   前两种改下去就是拿 YAML 冒险（run 25 的 <c>yaml: line 576</c> 教训：
        ///   改坏 YAML 的代价是"内核没起来但隧道照装"的整机黑洞），
        ///   第三种说明这份配置本来就不完整。三档一律<b>宁可漏改</b>，
        ///   但必须让 <c>note</c> 说明白，界面与日志里看得见"名单写了却没生效"。
        /// </summary>
        private static void InjectAppSplitRules(List<string> lines, AppSplit? split, out string note)
        {
            note = "无名单，rules 未动";
            if (split == null || split.IsEmpty)
                return;

            int ruleCount = split.Rules.Count;
            int badCount = split.Rejected.Count;

            int start = FindTopLevelKey(lines, "rules");
            if (start < 0)
            {
                note = DescribeSkip(ruleCount, badCount, "配置里没有 rules 块");
                return;
            }
            if (IsInlineBlock(lines, start))
            {
                note = DescribeSkip(ruleCount, badCount, "rules 是行内流式写法，不动它");
                return;
            }

            int end = EndOfBlock(lines, start);

            int firstItem = -1;
            for (int i = start + 1; i < end; i++)
            {
                if (lines[i].TrimStart().StartsWith("-"))
                {
                    firstItem = i;
                    break;
                }
            }
            if (firstItem < 0)
            {
                note = DescribeSkip(ruleCount, badCount, "rules 块里没有任何条目");
                return;
            }

            string indent = LeadingSpaces(lines[firstItem]);
            IList<string> rules = split.ToMihomoRules();

            // 倒序插入同一个位置 = 正序排在订阅规则之前（List.Insert 会把已插入的下推）。
            for (int k = rules.Count - 1; k >= 0; k--)
                lines.Insert(start + 1, indent + "- '" + rules[k] + "'");

            int nameCount = 0;
            int pathCount = 0;
            foreach (AppSplitRule r in split.Rules)
            {
                if (r.Kind == "path") pathCount++;
                else nameCount++;
            }

            note = $"注入 {rules.Count} 条（PROCESS-NAME×{nameCount}, PROCESS-PATH×{pathCount}）" +
                   (badCount > 0 ? $"，畸形 {badCount} 行已忽略" : "");
        }

        /// <summary>没注入时的原因句式，把"名单有几条、几行畸形"一起带上，界面直接显示。</summary>
        private static string DescribeSkip(int ruleCount, int badCount, string reason) =>
            $"{ruleCount} 条未注入：{reason}" + (badCount > 0 ? $"（另有畸形 {badCount} 行）" : "");

        /// <summary>
        /// 这一行是不是"行内流式"写法 —— 即 <c>dns: {a: 1, b: 2}</c> / <c>tun: {enable: true}</c>。
        /// 这种写法不能按"块内子行"去增删（会把 YAML 改成两义），所以 dns 块遇到它直接放弃改写，
        /// tun 块只允许两处行内替换（见 <see cref="EnsureTunBlock"/>）。
        /// </summary>
        private static bool IsInlineBlock(List<string> lines, int start)
        {
            string t = lines[start].TrimEnd();
            int idx = t.IndexOf(':');
            return idx >= 0 && t.Substring(idx + 1).Trim().Length > 0;
        }

        /// <summary>找顶层键（缩进 0 且形如 <c>key:</c>）的行号，找不到返回 -1。</summary>
        private static int FindTopLevelKey(List<string> lines, string key)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                string l = lines[i];
                if (l.Length == 0 || char.IsWhiteSpace(l[0]))
                    continue;
                string t = l.TrimEnd();
                if (t == key + ":" || t.StartsWith(key + ": ") || t.StartsWith(key + ":\t"))
                    return i;
            }
            return -1;
        }

        /// <summary>块尾（不含）：下一个顶层键所在行，或文件尾。</summary>
        private static int EndOfBlock(List<string> lines, int start)
        {
            for (int i = start + 1; i < lines.Count; i++)
            {
                string l = lines[i];
                if (l.Length == 0)
                    continue;
                if (!char.IsWhiteSpace(l[0]) && l.Contains(':') && !l.TrimStart().StartsWith("-"))
                    return i;
            }
            return lines.Count;
        }

        /// <summary>块内第一个子键的缩进宽度；块是空的话退回 4。</summary>
        private static int ChildIndent(List<string> lines, int start, int end)
        {
            for (int i = start + 1; i < end; i++)
            {
                string l = lines[i];
                if (l.Trim().Length == 0 || l.TrimStart().StartsWith("#"))
                    continue;
                int n = LeadingSpaces(l).Length;
                if (n > 0)
                    return n;
            }
            return 4;
        }

        /// <summary>在 [from, to) 行区间内找子键（忽略缩进层级，取第一个命中）。</summary>
        private static int FindKeyInRange(List<string> lines, int from, int to, string key)
        {
            for (int i = from; i < to; i++)
            {
                string t = lines[i].Trim();
                if (t.StartsWith(key + ":") || t.StartsWith(key + " :"))
                    return i;
            }
            return -1;
        }

        private static string LeadingSpaces(string line)
        {
            int n = 0;
            while (n < line.Length && (line[n] == ' ' || line[n] == '\t'))
                n++;
            return line.Substring(0, n);
        }

        /// <summary>冒号右边的值（去空格），用于判断 dns-hijack 里有没有 any:53。</summary>
        private static string ValueOf(string line)
        {
            int idx = line.IndexOf(':');
            return idx < 0 ? "" : line.Substring(idx + 1).Trim();
        }

        /// <summary>保留原缩进，把某行的键值改成给定值。</summary>
        private static string KeyLine(string line, string key, string value) =>
            LeadingSpaces(line) + key + ": " + value;

        /// <summary>行内写法里的 <c>enable: true</c>（只可能出现在已经锁定的那一行）。</summary>
        [GeneratedRegex(@"enable:\s*true", RegexOptions.CultureInvariant)]
        private static partial Regex InlineRegex();

        /// <summary>行内写法里的 <c>dns-hijack: [...]</c>。</summary>
        [GeneratedRegex(@"dns-hijack:\s*\[[^\]]*\]", RegexOptions.CultureInvariant)]
        private static partial Regex HijackRegex();

        /// <summary>
        /// 行内写法里的 <c>tolerance: &lt;数字&gt;</c>（url-test 组的切换容差）。
        /// ★ 只匹配<b>数字</b>值：订阅里若有 <c>tolerance: '30'</c>（带引号）不匹配，
        ///   那种写法留着别动 —— 宁可漏改也不能把 YAML 改坏。
        /// </summary>
        [GeneratedRegex(@"tolerance:\s*\d+", RegexOptions.CultureInvariant)]
        private static partial Regex ToleranceRegex();
    }
}
