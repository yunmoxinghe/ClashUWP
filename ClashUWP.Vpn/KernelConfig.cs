using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

using Windows.Storage;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// 「交给 mihomo 的那份 YAML」的就地改写器 —— 只动两个块：<c>dns:</c> 与 <c>tun:</c>。
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
        /// </summary>
        internal static string Transform(string yaml)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(yaml))
                    return yaml;
                // 内置兜底配置是 JSON（首字符 '{'），不该用行式 YAML 解析器碰它。
                if (yaml.TrimStart().StartsWith("{"))
                    return yaml;

                List<string> lines = new(yaml.Replace("\r\n", "\n").Split('\n'));
                EnsureDnsBlock(lines);
                EnsureTunBlock(lines);
                LockUrlTestTolerance(lines);

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
        private static void EnsureDnsBlock(List<string> lines)
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
                lines.Add("    ipv6: false");
                lines.Add("    default-nameserver: [223.5.5.5, 119.29.29.29]");
                lines.Add("    enhanced-mode: fake-ip");
                lines.Add($"    fake-ip-range: {FakeIpRange}");
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
        }

        /// <summary>
        /// <c>tun:</c> 块：<c>enable</c> 强制 false（executor 见 true 会建 wintun 设备，
        /// UWP 沙箱内必失败，见 <see cref="ClashVpnPlugin"/> 步 7 的注释），
        /// 并确保 <c>dns-hijack</c> 含 <c>any:53</c> —— fake-ip 靠的就是这个劫持器。
        /// 完全没有 tun 块时补一个。
        /// </summary>
        private static void EnsureTunBlock(List<string> lines)
        {
            int start = FindTopLevelKey(lines, "tun");
            if (start >= 0 && IsInlineBlock(lines, start))
            {
                // 行内流式写法（tun: {enable: true, ...}）：只做两处行内替换，
                // 不整块重写 —— enable=true 在这里是"必须改"（wintun 在沙箱里建不出来），
                // 而整行重写会把用户写的其余字段一起丢掉。
                lines[start] = InlineRegex().Replace(lines[start], "enable: false");
                if (!lines[start].Contains("any:53") && !lines[start].Contains("0.0.0.0:53"))
                    lines[start] = HijackRegex().Replace(lines[start], "dns-hijack: [any:53]");
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
                return;
            }

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
            if (hasAny)
                return;
            string itemIndent = last > hijack
                ? LeadingSpaces(lines[last])
                : LeadingSpaces(lines[hijack]) + "  ";
            lines.Insert(last + 1, itemIndent + "- any:53");
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
        private static void LockUrlTestTolerance(List<string> lines)
        {
            const string locked = "999999";

            int start = FindTopLevelKey(lines, "proxy-groups");
            if (start < 0)
                return;

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

                    // 从这一行往下找本组的 tolerance（到下一个 "- " 开头为止）。
                    for (int j = i + 1; j < end; j++)
                    {
                        string t = lines[j].Trim();
                        if (t.StartsWith("- ")) break;          // 下一个组
                        if (t.StartsWith("tolerance:"))
                        {
                            lines[j] = KeyLine(lines[j], "tolerance", locked);
                            break;
                        }
                    }
                    continue;
                }

                // ① 行内流式：`- { name: x, type: url-test, ..., tolerance: 30 }`
                if (line.Contains("type: url-test") || line.Contains("type:url-test"))
                {
                    if (line.Contains("tolerance:"))
                    {
                        lines[i] = ToleranceRegex().Replace(line, "tolerance: " + locked);
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
                        }
                    }
                    continue;
                }
            }
        }

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
