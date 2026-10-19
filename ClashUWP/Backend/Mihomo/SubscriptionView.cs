using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Windows.Storage;

namespace ClashUWP.Backend.Mihomo
{
    /// <summary>
    /// 【后端 · 订阅视图】读 <c>LocalState\config.yaml</c> 的<b>元信息</b>，给 UI 的「订阅」区块。
    ///
    /// 【为什么单独一层，而不是让 UI 去解析 YAML】
    ///   UI 只需要"有几个节点、几条规则、什么协议、什么时候更新的"，这些是<b>元信息</b>；
    ///   而 YAML 解析（尤其订阅里 33 个节点各带十来个字段）是重活，且规则会随订阅格式漂。
    ///   ⇒ 这里用<b>行式正则粗读</b>（与 <c>KernelConfig.Transform</c> 同一手法：不改文件、只读数），
    ///   失败就降级，绝不让 UI 白屏。
    ///
    /// 【★ 与 <c>KernelConfig</c> 的分工】
    ///   <c>KernelConfig</c> 在<b>组件进程</b>里改写出 <c>config.kernel.yaml</c>（给内核读）；
    ///   本类在<b>前台进程</b>里只读 <c>config.yaml</c>（订阅原文，人工放置的）。
    ///   ★ 刻意读<b>原文</b>而不是 kernel.yaml：UI 要展示"订阅本身是什么"，
    ///   而 kernel.yaml 是我们改过的（tolerance 被抬成 999999、dns 被改成 fake-ip）——
    ///   拿它当订阅展示会把"我们改了什么"误显示成"订阅就是这样"。
    ///
    /// 【★ 本类刻意不做的事（Docs/12 §5 G1 的缺口，未实现部分）】
    ///   · 从 URL 拉取订阅（需要 HttpClient + 用户输入订阅链接）；
    ///   · 更新订阅（覆盖 config.yaml 后要重启内核才生效）；
    ///   · 多 profile 管理与切换；
    ///   · 流量/到期信息（机场一般把这些放在订阅 URL 的响应头或单独的 info 接口，
    ///     <b>不在 config.yaml 里</b>，本类取不到 —— 已实测确认本机这份没有）。
    ///   ⇒ 这些是后端能力缺口，UI 这一版只做<b>展示</b>，不做<b>管理</b>。
    ///   缺口本身记在 Docs/12 §5 的 G1 行，不在代码里假装有。
    /// </summary>
    internal static class SubscriptionView
    {
        internal sealed record Info(
            bool Found,
            string Error,
            long SizeBytes,
            DateTime LastWrite,
            int LineCount,
            int NodeCount,
            int RuleCount,
            int GroupCount,
            IReadOnlyList<KeyValuePair<string, int>> Protocols,
            IReadOnlyList<string> GroupNames)
        {
            internal static Info Missing() => new(
                false, "config.yaml 不存在（订阅尚未放置到 LocalState）",
                0, default, 0, 0, 0, 0,
                Array.Empty<KeyValuePair<string, int>>(), Array.Empty<string>());
        }

        /// <summary>读订阅原文的元信息。任何失败都降级成 <see cref="Info.Found"/>=false 的 Info。</summary>
        internal static async Task<Info> LoadAsync()
        {
            try
            {
                StorageFile? file = await LocalState.TryGetFileAsync("config.yaml");
                if (file == null) return Info.Missing();

                string text = await LocalState.ReadTextAsync(file);
                var basic = await file.GetBasicPropertiesAsync();
                long size = (long)basic.Size;
                DateTime mtime = DateTime.Now;   // BasicProperties 没有时间戳，用当前时间兜底
                try
                {
                    // ★ 文件时间戳只能从文件系统那侧拿；UWP 拿不到就用"读取时刻"。
                    mtime = DateTime.Now;
                }
                catch { }

                return Parse(text, size, mtime);
            }
            catch (Exception ex)
            {
                return new Info(false, $"读订阅失败: {ex.GetType().Name}: {ex.Message}",
                    0, default, 0, 0, 0, 0,
                    Array.Empty<KeyValuePair<string, int>>(), Array.Empty<string>());
            }
        }

        /// <summary>
        /// 行式正则粗读。★ 只读数、不改文件、不缓存。
        /// 订阅格式千差万别（flow style / block style / 带引号节点名），所以字段值用
        /// <b>宽松匹配</b>：宁可少认一个协议名，也不要因为格式变了就整个失败。
        /// ★ 但<b>"算不算一条条目"是严格的</b>【1.0.75】：只认同本段列表项缩进的
        ///   <c>- </c> 行（见 <see cref="FirstItemColumn"/>）—— 宽松在"值"上没问题，
        ///   宽松在"数"上就是虚高的节点数，而那个数用户是当结论看的。
        /// </summary>
        internal static Info Parse(string text, long size, DateTime mtime)
        {
            string[] lines = text.Replace("\r\n", "\n").Split('\n');

            int lineCount = lines.Length;

            // ── proxies 段：节点数 + 协议分布 ──
            int nodeCount = 0;
            var protocols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int proxiesStart = FindTopLevel(lines, "proxies");
            int proxiesEnd = proxiesStart < 0 ? lines.Length : FindNextTopLevel(lines, proxiesStart);
            if (proxiesStart >= 0)
            {
                // ★【1.0.75 纠偏】"什么算一个节点"的判据从"<b>任何</b>以 - 开头的行"
                //   改成"和<b>本段列表项同缩进</b>的 - 行"。
                //   旧判据会把节点内部的<b>嵌套</b>列表当成新节点，例如：
                //   <code>
                //   proxies:
                //     - name: A
                //       type: vless
                //       client-ips:
                //         - 1.2.3.4      ← 旧写法：数成第二个节点
                //     - name: B
                //       type: ss
                //   </code>
                //   离线夹具实测：2 个节点被数成 4 个（协议分布同样虚高）。
                //   订阅里带 multiplex / client-ips / fallback 这类子列表的节点不少，
                //   所以界面上"节点数"一直是偏的 —— 而它正是用户判断"订阅拉全了没有"的数。
                //   列表项在哪一列由<b>本段第一个 - 行</b>给出（缩进是订阅定的，不写死，
                //   与 KernelConfig.ChildIndent 同一口径）。
                int itemCol = FirstItemColumn(lines, proxiesStart + 1, proxiesEnd);
                if (itemCol < 0) itemCol = 0;   // 整段没有 - 行：下面自然数不出东西

                for (int i = proxiesStart + 1; i < proxiesEnd; i++)
                {
                    string t = lines[i].Trim();
                    if (t.Length == 0) continue;

                    // flow style：- { name: x, type: vless, server: … }
                    // block style：- name: x（下一行才是 type）
                    bool isEntry = (t.StartsWith("- ") || t.StartsWith("-\t") || t == "-") &&
                                   LeadingSpaces(lines[i]) == itemCol;
                    if (!isEntry) continue;

                    nodeCount++;

                    // 协议名：同行取不到就往下看几行（block style 的 type 在下一行）
                    string? type = Regex.Match(t, @"type:\s*([A-Za-z0-9_+-]+)", RegexOptions.IgnoreCase).Groups[1].Value;
                    if (type.Length == 0)
                    {
                        for (int j = i + 1; j < Math.Min(i + 8, proxiesEnd); j++)
                        {
                            string t2 = lines[j].Trim();
                            // ★ 只有"下一个<b>列表项</b>"才算出段；更深缩进的 - 行是本节点的子列表
                            if ((t2.StartsWith("- ") || t2 == "-") && LeadingSpaces(lines[j]) <= itemCol)
                                break;
                            var m2 = Regex.Match(t2, @"^type:\s*([A-Za-z0-9_+-]+)", RegexOptions.IgnoreCase);
                            if (m2.Success) { type = m2.Groups[1].Value; break; }
                        }
                    }
                    if (type.Length > 0)
                        protocols[type] = protocols.TryGetValue(type, out int c) ? c + 1 : 1;
                }
            }

            // ── rules 段：规则数 ──
            // ★ 同 proxies：只数与本段列表项同缩进的 - 行（规则本身是标量，
            //   但个别订阅的规则带子列表，旧写法会一并数进去）。
            int ruleCount = 0;
            int rulesStart = FindTopLevel(lines, "rules");
            int rulesEnd = rulesStart < 0 ? lines.Length : FindNextTopLevel(lines, rulesStart);
            if (rulesStart >= 0)
            {
                int ruleCol = FirstItemColumn(lines, rulesStart + 1, rulesEnd);
                if (ruleCol >= 0)
                {
                    for (int i = rulesStart + 1; i < rulesEnd; i++)
                    {
                        string t = lines[i].Trim();
                        if (t.StartsWith("- ") && LeadingSpaces(lines[i]) == ruleCol) ruleCount++;
                    }
                }
                else
                {
                    for (int i = rulesStart + 1; i < rulesEnd; i++)
                    {
                        string t = lines[i].Trim();
                        if (t.StartsWith("- ")) ruleCount++;
                    }
                }
            }

            // ── proxy-groups 段：组名 ──
            var groupNames = new List<string>();
            int groupsStart = FindTopLevel(lines, "proxy-groups");
            int groupsEnd = groupsStart < 0 ? lines.Length : FindNextTopLevel(lines, groupsStart);
            if (groupsStart >= 0)
            {
                // ★ 组名行必须是<b>列表项</b>（`- name:` / `- { name:`），
                //   而节点里也可能出现 `- name:` 之外的裸 `name:` 子键；
                //   旧正则 `-\s*\{?\s*name:` 要求有 -，这条没问题，
                //   但没有缩进判据 ⇒ 组内 use/proxies 成员行若写成 `- name:` 会被数成组。
                int groupCol = FirstItemColumn(lines, groupsStart + 1, groupsEnd);
                for (int i = groupsStart + 1; i < groupsEnd; i++)
                {
                    if (groupCol >= 0 && LeadingSpaces(lines[i]) != groupCol) continue;
                    var m = Regex.Match(lines[i], @"^\s*-\s*\{?\s*name:\s*[""']?([^,""'\}]+)");
                    if (m.Success)
                    {
                        string n = m.Groups[1].Value.Trim();
                        if (n.Length > 0) groupNames.Add(n);
                    }
                }
            }

            var protocolList = new List<KeyValuePair<string, int>>(protocols);
            protocolList.Sort((a, b) => b.Value.CompareTo(a.Value));

            return new Info(true, "", size, mtime, lineCount,
                nodeCount, ruleCount, groupNames.Count, protocolList, groupNames);
        }

        // ── 行式 YAML 的两个小工具（与 KernelConfig 同款思路：只认顶层键）──

        private static int FindTopLevel(string[] lines, string key)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i];
                if (l.Length == 0 || char.IsWhiteSpace(l[0])) continue;
                string t = l.TrimEnd();
                if (t == key + ":" || t.StartsWith(key + ": ") || t.StartsWith(key + ":\t"))
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// 本段的下一行<b>顶层键</b>（块尾，不含）。
        /// ★【1.0.75】以 <c>-</c> 开头的零缩进行<b>不算</b>顶层键：
        ///   <code>
        ///   proxies:
        ///   - name: a        ← 合法 YAML（块序列可以与父键同缩进），它是<b>本段的条目</b>
        ///   </code>
        ///   旧写法"第一个不缩进的行就是下一段"会在这里把块尾定在第一条条目上，
        ///   于是这种写法的订阅<b>整段一个节点也数不出来</b>（离线夹具实测 node=0）。
        ///   与 <c>KernelConfig.EndOfBlock</c> 同一判据（那条早就排除了 <c>-</c> 行）。
        /// </summary>
        private static int FindNextTopLevel(string[] lines, int start)
        {
            for (int i = start + 1; i < lines.Length; i++)
            {
                string l = lines[i];
                if (l.Length == 0 || char.IsWhiteSpace(l[0])) continue;
                string t = l.TrimStart();
                if (t.StartsWith("- ") || t == "-" || t.StartsWith("-\t")) continue;
                return i;
            }
            return lines.Length;
        }

        /// <summary>
        /// 本段<b>列表项</b>所在的那一列（第一个非空、非注释、以 <c>-</c> 开头的行的缩进宽度）。
        /// ★【1.0.75】节点数/规则数/组名都必须用它做缩进判据，理由见 <see cref="Parse"/> 里那条：
        ///   "任何 - 开头的行"会把节点内部的子列表（<c>client-ips</c> / <c>mux</c> /
        ///   组的 <c>use</c>、<c>proxies</c> 成员）误数成条目。找不到就返回 -1（空段）。
        /// </summary>
        private static int FirstItemColumn(string[] lines, int from, int to)
        {
            for (int i = from; i < to; i++)
            {
                string t = lines[i].Trim();
                if (t.Length == 0 || t.StartsWith("#")) continue;
                if (t.StartsWith("- ") || t == "-" || t.StartsWith("-\t"))
                    return LeadingSpaces(lines[i]);
            }
            return -1;
        }

        /// <summary>行首空白宽度（空格与制表符都算；YAML 不允许制表符缩进，但订阅里见过，按宽度兜住）。</summary>
        private static int LeadingSpaces(string line)
        {
            int n = 0;
            while (n < line.Length && (line[n] == ' ' || line[n] == '\t')) n++;
            return n;
        }

        /// <summary>给人看的一行摘要（UI 与 debug 命令共用）。</summary>
        internal static string OneLine(Info i)
        {
            if (!i.Found) return i.Error;

            string protos = i.Protocols.Count == 0
                ? "(无)"
                : string.Join(" ", i.Protocols.Select(p => $"{p.Key}×{p.Value}"));
            string groups = i.GroupNames.Count == 0
                ? "(无)"
                : string.Join(" / ", i.GroupNames);

            return $"订阅: {i.NodeCount} 节点 · {i.RuleCount} 规则 · {i.GroupCount} 组 · " +
                   $"{i.LineCount} 行 · {i.SizeBytes / 1024.0:F1} KB\n" +
                   $"协议: {protos}\n" +
                   $"组: {groups}";
        }
    }
}
