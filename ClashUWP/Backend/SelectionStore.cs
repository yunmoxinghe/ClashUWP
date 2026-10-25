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
    /// <c>LocalState\selection.txt</c> 记下「每个可切换组上次选了哪个节点」，UI 轮询每轮拿
    /// 内核现值跟这本账比，<b>有漂移就对 REST 控制器回放</b> <c>PUT /proxies/{组}</c>
    /// （见 <see cref="MihomoController.ReplaySelectionsAsync"/> 与判据
    /// <see cref="SelectionReplayPolicy"/>）。
    /// ★【1.0.119】从前这里写的是"内核第一次可被轮询探到时回放一次"，那个边沿实现是坏的
    /// （一只 bool 闩在读账之前就置位，复位又只挂在轮询的 catch 上，而内核不可达并不抛）——
    /// 判据换成漂移后，"内核起来之后才写进台账的那条"也放得到。</para>
    ///
    /// <para><b>存储格式</b>：每行 <c>UrlEncode(组名)=UrlEncode(节点名)</c>。
    /// 用 UrlEncode 是为了让组名 / 节点名里的 <c>=</c>、空格、中文、emoji 不破坏
    /// 「按第一个 <c>=</c> 切」的解析（订阅派生的组名常带空格，实测 11 个组里 10 个带空格）。</para>
    ///
    /// <para>★ 只记<b>手动组</b>（Selector / relay）的选择：url-test / fallback / load-balance
    /// 由内核自管，记了也没用（下一轮测速/故障转移就改回赢家），回放时还会主动跳过它们。</para>
    ///
    /// <para>★【1.0.119】读写两头都<b>出声</b>（原先是两处裸 <c>catch { }</c>）：这本账是
    /// "已记为预设，下次内核可达回放生效"这句话的唯一凭据，写失败却静默就等于骗人。
    /// 读侧挂在 3 秒轮询上，所以只在错误形态<b>变化</b>时记一行，热路径禁刷屏；
    /// "文件不存在"是首次使用的正常路径，不报错。</para>
    /// </summary>
    internal static class SelectionStore
    {
        private const string FileName = "selection.txt";
        private static readonly object _lock = new();

        /// <summary>上一次<b>读</b>台账时的错误形态（空 = 没有未处理的读错误）；用来给 3 秒轮询去重。</summary>
        private static string _lastReadError = "";

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

        /// <summary>
        /// 读出全部已记选择（组名 → 节点名）。无文件 / 读不到 = 空字典，属正常路径。
        /// ★【1.0.119】"读不到"原先是裸 <c>catch { }</c>：真出错（占用、ACL、损坏）和
        ///   "还没有这个文件"长得一样，于是界面写着"已记为预设"、内核那边永远等不到回放，
        ///   日志里查不到痕迹。现在文件不存在仍算正常（不报错），其余错误出声。
        /// ★ 这个方法挂在 <b>3 秒轮询</b>上（<c>ReplaySelectionsAsync</c> 每轮读一次台账），
        ///   所以只在错误形态<b>变化</b>时记一行；读成功把基线清掉，下一轮发作还能再记一次。
        /// </summary>
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
                _lastReadError = "";
            }
            catch (Exception ex)
            {
                // E_FILE_NOT_FOUND（0x80070002）：还没有这本账，首次使用的正常路径。
                if (ex.HResult != unchecked((int)0x80070002))
                {
                    string kind = OneLine($"{ex.GetType().Name}: {ex.Message}");
                    if (kind != _lastReadError)
                    {
                        _lastReadError = kind;
                        ClashUWP.Vpn.RollingLog.Warn("selection-store",
                            $"读 {FileName} 失败（台账按空处理，预设不会回放）：{kind}");
                    }
                }
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
            catch (Exception ex)
            {
                // 写入失败不影响本次选择本身（UI 切节点已经成功），但<b>必须出声</b>：
                // 这本账是"下次内核可达回放生效"的唯一凭据，写不进去就等于那句承诺落空。
                // 这条挂在用户动作（点选 / 记预设）上，不是 3 秒轮询，所以不刷屏，直接记。
                ClashUWP.Vpn.RollingLog.Warn("selection-store",
                    $"写 {FileName} 失败（本次选择不会在重启后回放）：{OneLine(ex.Message)}");
            }
        }

        /// <summary>压成一行并去掉控制字符：滚动日志的行格式是看门狗的机器契约，禁内嵌换行。</summary>
        private static string OneLine(string s) =>
            s.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    }
}
