using System;
using System.Collections.Generic;
using System.Text;

using Windows.Storage;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// 一条应用分流规则（G9）。三个字段全是字符串，是为了让形状保持最普通
    /// （CsWinRT 组件时代它还得能被投影；1.0.76 合并进主工程后这条约束没了，
    /// 但"前台写、插件读，两边共用这一份模型，不再有两套解析器"这条理由不变）。
    /// </summary>
    internal sealed class AppSplitRule
    {
        /// <summary><c>name</c> = 进程名（<c>chrome.exe</c>）；<c>path</c> = 可执行文件全路径。</summary>
        public string Kind { get; set; } = "name";

        /// <summary>标识：进程名或全路径。不允许出现 <c>|</c> <c>,</c> <c>'</c>（都会破坏行式/规则格式）。</summary>
        public string Identity { get; set; } = "";

        /// <summary>去向。★ 本版本只收内核内建策略 <c>DIRECT</c>（旁路直连）/ <c>REJECT</c>（屏蔽）。</summary>
        public string Target { get; set; } = "DIRECT";
    }

    /// <summary>
    /// 【应用分流名单】<c>LocalState\split-apps.txt</c> 的模型 —— 一行一条
    /// <c>kind|identity|target</c>，由前台写、插件在 Connect 时读。
    ///
    /// ══ 为什么走 mihomo 的规则层，而不是 Windows 的 traffic filter ══
    /// 平台这一侧的按应用分流原语（<c>VpnTrafficFilter</c> + <c>VpnAppId</c>）我们**有**，
    /// 而且已经接过一次：<c>settings.txt: selfSplitTunnel</c>（<see cref="LocalSettings"/>，
    /// 1.0.21 / run 18~20）。两次独立实测都把它判死了，判据是同一个形状：
    ///
    ///   · 先遣项目 docs/76 的 tfmode 三臂：<b>空 assignment</b> ⇒ 数据面<b>默认 deny</b>
    ///     （GenFail=23、只有装 filter 前瞬间溜过 1 个包）；<b>Allow*=true</b> ⇒ reqSeen=0；
    ///     只有 <b>传 null</b> 才是"全部进隧道"的正常形态（reqSeen=24）。
    ///   · 我们自己的 run 19（Docs/08 §5.2）：filter 列表<b>非空</b>的两臂
    ///     <c>Encapsulated</c> 从数千掉到 <b>17</b>、<c>Injected=0</c>，宿主所有应用
    ///     20~30 ms <b>本地瞬败</b>（WSAEACCES 那类），连本包的 <c>127.0.0.1:9090</c>
    ///     回路都变成"积极拒绝"。
    ///
    /// 合起来就是一句话：<b>assignment 一非空，平台就把"没匹配上 filter 的应用"按 deny 处理，
    /// 而不是"照旧进隧道"</b>。官方文档在这里是空的（<c>VpnTrafficFilterAssignment</c> 的
    /// <c>AllowInbound/AllowOutbound</c> 只有 "Gets or sets whether … is allowed or not" 一句，
    /// <c>VpnRoutingPolicyType</c> 也只有两个成员、没说不匹配时怎么办），
    /// 所以这两次实测就是我们的口径。⇒ 想表达"这些应用例外、其余照常"，
    /// 在 traffic filter 这条路上要付出<b>整机断网</b>的代价，不能做成产品功能。
    ///
    /// ══ 规则层为什么能做成 ══
    /// 内核在隧道出口那一侧看得见每条连接的<b>源地址+源端口</b>，而 Windows 侧
    /// <c>GetExtendedTcpTable</c> 能按 (本地地址, 本地端口) 反查<b>拥有它的进程</b> ——
    /// mihomo 正是这么做的：<c>component/process/process_windows.go</c>（我们 pin 的
    /// v1.19.32 源码，非记忆）用 <c>iphlpapi!GetExtendedTcpTable/GetExtendedUdpTable</c>
    /// + <c>kernel32!QueryFullProcessImageNameW</c>（<c>OpenProcess</c> 只取
    /// <c>PROCESS_QUERY_LIMITED_INFORMATION</c>），查到的名字写进
    /// <c>Metadata.Process / ProcessPath</c>。规则类型是
    /// <c>PROCESS-NAME</c> / <c>PROCESS-PATH</c>（另有 <c>-REGEX</c> / <c>-WILDCARD</c> 两种，
    /// 本名单不用：它们的 <c>*</c> 会匹配到"解析不出进程"的空串，见
    /// <c>component/wildcard/wildcard.go</c>，那是个会把"没查到"误判成"命中"的坑）。
    /// <c>find-process-mode</c> 默认 <c>strict</c>：只在真撞上 PROCESS 规则时才查、每连接一次，
    /// 所以名单为空时内核行为与本批改动之前<b>逐字相同</b>；查不到进程 ⇒ 空串 ⇒ <b>不匹配</b>（安全的一侧）。
    ///
    /// 【与安卓的差别，如实写】安卓在 <c>TunService</c> 用 <c>addAllowedApplication</c> /
    /// <c>addDisallowedApplication</c> 把应用<b>整个排除在隧道之外</b>；我们是在<b>已进隧道的
    /// 流量</b>上按进程判，命中 <c>DIRECT</c> 由内核自己从物理网卡发出去
    /// （<c>interface-name</c> 绑定那条路，见 Docs/09）。用户看到的结果一样
    /// "这个应用走直连"，机制不同，且这条机制<b>不碰</b> VPN 会话的建立。
    ///
    /// ══ 为什么去向只收内建策略 ══
    /// <c>config/config.go</c> 的 <c>parseRules</c> 会校验规则的 target 必须存在于 proxies，
    /// <b>一条不存在的组名会让整份配置解析失败</b> —— 于是桥 <c>start rc=-2</c>、
    /// <c>failFast</c> 放弃连接（不是黑洞，但"改个名单就连不上"不能留给用户）。
    /// 组名会随订阅变（换一份订阅，"星链机场"可能就没了），所以"强制走某个组"这一档
    /// 必须等有组名校验之后再做。<c>DIRECT</c> / <c>REJECT</c> 是内核内建、恒存在，
    /// 恰好覆盖"旁路"和"屏蔽"这两个真实需求。
    /// </summary>
    internal sealed class AppSplit
    {
        /// <summary>名单文件名（与 <c>settings.txt</c> 同目录，故意<b>不</b>混进那个把手文件：
        /// 它是取证通道的命门，写坏一行等于一整批实验臂失效，见 Docs/19 §6）。</summary>
        /// <remarks>
        /// ★ 用 <c>static 属性</c>而不是 <c>const</c>：本类是 public、会被投影进 winmd，
        ///   而 <c>const</c> 是编译期内联字面量、<b>不穿过投影</b> ——
        ///   前台要拿同一个文件名读写（<c>AppSplitStore</c>），必须是运行期读得到的成员。
        ///   （<see cref="AllowedTargets"/> 同理；<see cref="MaxRules"/> 也是。）
        /// </remarks>
        public static string FileName { get; } = "split-apps.txt";

        /// <summary>条数上限。规则是逐条线性扫描的，100 条对内核毫无压力，
        /// 设上限只为挡住"手滑把整个目录列表粘进来"。</summary>
        public static int MaxRules { get; } = 100;

        /// <summary>合法去向。★ 只有内核内建策略（理由见类注释）。</summary>
        /// <remarks>
        /// ★ 类型是 <see cref="IList{T}"/> 而不是 <c>string[]</c>，也不是 <c>public static readonly</c> 字段：
        ///   本类是 <c>public</c>（前台要用同一份解析器，见类注释"单源"），会被 CsWinRT 投影进 winmd，
        ///   而 WinRT 元数据里<b>没有"字段"这个东西</b>，数组成员投影出来是 <c>IVectorView&lt;string&gt;</c>。
        ///   （<see cref="MaxRules"/> 这类 <c>const</c> 是编译期内联的字面量，不占投影成员，所以照旧可用。）
        /// </remarks>
        public static IList<string> AllowedTargets { get; } = new List<string> { "DIRECT", "REJECT" };

        /// <summary>解析出来的规则（按文件里的顺序，内核也是按这个顺序第一条命中即停）。</summary>
        public IList<AppSplitRule> Rules { get; } = new List<AppSplitRule>();

        /// <summary>被丢掉的行数与原因（★ 不静默丢弃：每行一条原因，界面与日志原样显示）。</summary>
        public IList<string> Rejected { get; } = new List<string>();

        /// <summary>名单为空 = 本功能完全不动配置。</summary>
        public bool IsEmpty => Rules.Count == 0;

        /// <summary>
        /// 解析文件内容。<b>不抛</b> —— 畸形行只进 <see cref="Rejected"/>。
        /// 支持 <c>#</c> 注释与空行；kind 认 <c>name</c>/<c>path</c>（大小写不敏感），
        /// target 认 <see cref="AllowedTargets"/>（大小写不敏感，输出统一大写）。
        /// </summary>
        public static AppSplit Parse(string? text)
        {
            var split = new AppSplit();
            if (string.IsNullOrWhiteSpace(text))
                return split;

            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;

                string[] parts = line.Split('|');
                if (parts.Length != 3)
                {
                    split.Rejected.Add($"字段数不是 3（应为 kind|标识|去向）：{Trim(line)}");
                    continue;
                }

                string kind = parts[0].Trim().ToLowerInvariant();
                string identity = parts[1].Trim();
                string target = parts[2].Trim().ToUpperInvariant();

                if (kind != "name" && kind != "path")
                {
                    split.Rejected.Add($"kind 只认 name / path：{Trim(line)}");
                    continue;
                }
                if (!IsSafeToken(identity))
                {
                    split.Rejected.Add($"标识非法（空 / 含 | , ' 或超长）：{Trim(line)}");
                    continue;
                }
                if (target != "DIRECT" && target != "REJECT")
                {
                    // ★ 这条拒绝是本类最要紧的一条防线，见类注释"为什么去向只收内建策略"。
                    split.Rejected.Add($"去向只认 DIRECT / REJECT（写组名会让整份配置解析失败）：{Trim(line)}");
                    continue;
                }
                if (split.Rules.Count >= MaxRules)
                {
                    split.Rejected.Add($"超过上限 {MaxRules} 条，本行起全部忽略。");
                    break;
                }

                split.Rules.Add(new AppSplitRule { Kind = kind, Identity = identity, Target = target });
            }

            return split;
        }

        /// <summary>写回文件的形式（含说明头，方便用户直接开着改）。</summary>
        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# ClashUWP 应用分流名单（前台写；插件每次连接读一次，热重载也重读一次）");
            sb.AppendLine("# 一行一条：kind|标识|去向");
            sb.AppendLine("#   kind   = name（进程名，如 chrome.exe） | path（可执行文件全路径）");
            sb.AppendLine("#   去向   = DIRECT（旁路直连） | REJECT（屏蔽）");
            sb.AppendLine("#   标识与去向里不能出现 | , ' ；进程名不区分大小写、要带 .exe 全名。");
            sb.AppendLine("# 空名单 = 不改内核配置，行为与没这个文件时逐字相同。");
            foreach (AppSplitRule r in Rules)
                sb.AppendLine($"{r.Kind}|{r.Identity}|{r.Target}");
            return sb.ToString();
        }

        /// <summary>
        /// 转成 mihomo 的规则行（不含 YAML 缩进与引号，由 <see cref="KernelConfig"/> 拼）。
        /// 一条名单 = 一行规则（官方没有"竖线并列多个应用"的写法，见
        /// <c>rules/common/process.go</c>：整串大小写不敏感相等比较）。
        /// </summary>
        public IList<string> ToMihomoRules()
        {
            var list = new List<string>(Rules.Count);
            foreach (AppSplitRule r in Rules)
            {
                string type = r.Kind == "path" ? "PROCESS-PATH" : "PROCESS-NAME";
                list.Add($"{type},{r.Identity},{r.Target}");
            }
            return list;
        }

        /// <summary>
        /// 同步读名单（插件在 Connect 这个同步回调里用；读不到就返回空名单，
        /// 与 <see cref="LocalSettings.Load"/> 同一个口径：缺文件是正常路径，不是错误）。
        /// ★ 调用点有<b>两处</b>，都是"重新生成内核配置"的那一刻：
        ///   <c>ClashVpnPlugin</c> 步 4.1（连接时）与 <see cref="ClashUWP.Backend.SubscriptionReload"/>（热重载时）。
        ///   ⇒ "前台改完名单"要么断开再连、要么点一次热重载，<b>不需要</b>重建 profile 或重装。
        /// </summary>
        public static AppSplit Load()
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
                return new AppSplit();
            }
        }

        /// <summary>
        /// 标识/去向的字符闸门。拒绝 <c>|</c>（拆行符）、<c>,</c>（mihomo 规则的分隔符）、
        /// <c>'</c>（我们生成的 YAML 用单引号包住整条规则）、控制字符与换行；长度上限 260
        /// （路径的现实上限，也挡住把整段文本粘进来）。
        /// </summary>
        internal static bool IsSafeToken(string s)
        {
            if (s.Length == 0 || s.Length > 260)
                return false;
            foreach (char c in s)
            {
                if (c == '|' || c == ',' || c == '\'' || c < ' ')
                    return false;
            }
            return true;
        }

        /// <summary>拒绝原因里回显原文时截一下，免得一行几百字把界面撑爆。</summary>
        private static string Trim(string s) => s.Length <= 80 ? s : s[..80] + "…";
    }
}
