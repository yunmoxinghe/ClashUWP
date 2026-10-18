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
        /// 订阅格式千差万别（flow style / block style / 带引号节点名），所以每条都用
        /// <b>宽松匹配</b>：宁可少数几个节点，也不要因为格式变了就整个失败。
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
                for (int i = proxiesStart + 1; i < proxiesEnd; i++)
                {
                    string t = lines[i].Trim();
                    if (t.Length == 0) continue;

                    // flow style：- { name: x, type: vless, server: … }
                    // block style：- name: x（下一行才是 type）
                    bool isEntry = t.StartsWith("- ") || t.StartsWith("-\t");
                    if (!isEntry) continue;

                    nodeCount++;

                    // 协议名：同行取不到就往下看几行（block style 的 type 在下一行）
                    string? type = Regex.Match(t, @"type:\s*([A-Za-z0-9_+-]+)", RegexOptions.IgnoreCase).Groups[1].Value;
                    if (type.Length == 0)
                    {
                        for (int j = i + 1; j < Math.Min(i + 8, proxiesEnd); j++)
                        {
                            string t2 = lines[j].Trim();
                            if (t2.StartsWith("- ")) break;                    // 下一条目
                            var m2 = Regex.Match(t2, @"^type:\s*([A-Za-z0-9_+-]+)", RegexOptions.IgnoreCase);
                            if (m2.Success) { type = m2.Groups[1].Value; break; }
                        }
                    }
                    if (type.Length > 0)
                        protocols[type] = protocols.TryGetValue(type, out int c) ? c + 1 : 1;
                }
            }

            // ── rules 段：规则数 ──
            int ruleCount = 0;
            int rulesStart = FindTopLevel(lines, "rules");
            int rulesEnd = rulesStart < 0 ? lines.Length : FindNextTopLevel(lines, rulesStart);
            if (rulesStart >= 0)
            {
                for (int i = rulesStart + 1; i < rulesEnd; i++)
                {
                    string t = lines[i].Trim();
                    if (t.StartsWith("- ")) ruleCount++;
                }
            }

            // ── proxy-groups 段：组名 ──
            var groupNames = new List<string>();
            int groupsStart = FindTopLevel(lines, "proxy-groups");
            int groupsEnd = groupsStart < 0 ? lines.Length : FindNextTopLevel(lines, groupsStart);
            if (groupsStart >= 0)
            {
                for (int i = groupsStart + 1; i < groupsEnd; i++)
                {
                    var m = Regex.Match(lines[i], @"-\s*\{?\s*name:\s*[""']?([^,""'\}]+)");
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

        private static int FindNextTopLevel(string[] lines, int start)
        {
            for (int i = start + 1; i < lines.Length; i++)
            {
                string l = lines[i];
                if (l.Length == 0 || char.IsWhiteSpace(l[0])) continue;
                return i;
            }
            return lines.Length;
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
