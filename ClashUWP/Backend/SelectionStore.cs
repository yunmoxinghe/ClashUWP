using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Windows.Storage;

namespace ClashUWP.Backend
{
    /// <summary>
    /// ★【1.0.81 · Q4 修复】节点选择持久化（纯客户端侧）。
    ///
    /// <para><b>问题</b>：内核把选择写进 <c>&lt;home&gt;/cache.db</c>，而 home =
    /// <c>Package.Current.InstalledLocation</c>（只读），所以重启 / 重装后选择不保留
    /// （<c>MihomoController.SelectCoreAsync</c> 的返回值里已写明这一点，对应 G10）。
    /// 迁 home 到可写 <c>LocalState</c> 属于运行态改动，触碰 UWP 包 / VPN 数据面边界，
    /// 本轮不擅自改。</para>
    ///
    /// <para><b>方案</b>（不改 home、不碰包结构、不动内核 ABI）：客户端在
    /// <c>LocalState\selection.txt</c> 记下「每个可切换组上次选了哪个节点」，
    /// 内核每次（重新）起来、第一次可被轮询探到时，对 REST 控制器回放
    /// <c>PUT /proxies/{组}</c>（见 <see cref="MihomoController.ReplaySelectionsAsync"/>）。</para>
    ///
    /// <para><b>存储格式</b>：每行 <c>UrlEncode(组名)=UrlEncode(节点名)</c>。
    /// 用 UrlEncode 是为了让组名 / 节点名里的 <c>=</c>、空格、中文、emoji 不破坏
    /// 「按第一个 <c>=</c> 切」的解析（订阅派生的组名常带空格，实测 11 个组里 10 个带空格）。</para>
    ///
    /// <para>★ 只记<b>手动组</b>（Selector / relay）的选择：url-test / fallback / load-balance
    /// 由内核自管，记了也没用（下一轮测速/故障转移就改回赢家），回放时还会主动跳过它们。</para>
    /// </summary>
    internal static class SelectionStore
    {
        private const string FileName = "selection.txt";
        private static readonly object _lock = new();

        /// <summary>把 (组, 节点) 编码成一行（UrlEncode 两端）。纯函数，可离线单测。</summary>
        internal static string EncodeLine(string group, string node)
            => Uri.EscapeDataString(group) + "=" + Uri.EscapeDataString(node);

        /// <summary>
        /// 解析一行。返回 <c>(group, node)</c>；行非法（空 / 注释 / 没有 <c>=</c> /
        /// 解码后任一侧为空）返回 <c>(null, null)</c>。纯函数，可离线单测。
        /// </summary>
        internal static (string? Group, string? Node) DecodeLine(string line)
        {
            string s = line.Trim();
            if (s.Length == 0 || s.StartsWith("#", StringComparison.Ordinal))
                return (null, null);
            int idx = s.IndexOf('=');
            if (idx <= 0)
                return (null, null);
            string g = Uri.UnescapeDataString(s.Substring(0, idx));
            string n = Uri.UnescapeDataString(s.Substring(idx + 1));
            if (g.Length == 0 || n.Length == 0)
                return (null, null);
            return (g, n);
        }

        /// <summary>读出全部已记选择（组名 → 节点名）。无文件 / 读不到 = 空字典，属正常路径。</summary>
        internal static Dictionary<string, string> LoadAll()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                StorageFile file = ApplicationData.Current.LocalFolder
                    .GetFileAsync(FileName).AsTask().GetAwaiter().GetResult();
                string text = FileIO.ReadTextAsync(file).AsTask().GetAwaiter().GetResult();
                foreach (string raw in text.Split('\n'))
                {
                    var (g, n) = DecodeLine(raw);
                    if (g != null && n != null)
                        map[g] = n;   // 同组后者覆盖前者（文件本身是按 key 排序重写的，正常不会重复）
                }
            }
            catch
            {
                // 没有文件 / 读不到 → 空。这是正常路径，不是错误。
            }
            return map;
        }

        /// <summary>记录一次成功的选择（覆盖同组的旧值）。线程安全。</summary>
        internal static void Record(string group, string node)
        {
            if (group.Length == 0 || node.Length == 0)
                return;
            lock (_lock)
            {
                var map = LoadAll();
                map[group] = node;
                Save(map);
            }
        }

        /// <summary>删掉某组（例如组被移除）。线程安全。</summary>
        internal static void Forget(string group)
        {
            if (group.Length == 0)
                return;
            lock (_lock)
            {
                var map = LoadAll();
                if (map.Remove(group))
                    Save(map);
            }
        }

        private static void Save(Dictionary<string, string> map)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("# ClashUWP 节点选择持久化（组=节点）；内核只读 home，故客户端代记。\n");
                foreach (var kv in map.OrderBy(k => k.Key, StringComparer.Ordinal))
                    sb.Append(EncodeLine(kv.Key, kv.Value)).Append('\n');
                StorageFile file = ApplicationData.Current.LocalFolder
                    .CreateFileAsync(FileName, CreationCollisionOption.ReplaceExisting)
                    .AsTask().GetAwaiter().GetResult();
                FileIO.WriteTextAsync(file, sb.ToString()).AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // 写入失败不应影响本次选择本身（UI 切节点已成功，只是下次重启记不住）。
            }
        }
    }
}
