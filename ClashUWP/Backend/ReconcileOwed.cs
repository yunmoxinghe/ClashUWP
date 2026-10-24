using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClashUWP.Backend
{
    /// <summary>
    /// 【后端 · 欠对账台账】记下"被会话闸欠下来的 profile 写入"，等没有会话的那一轮补。
    ///
    /// ★★【1.0.101 · Docs/52】为什么从此需要一本台账
    ///   Docs/45 §八 + Docs/51 §四 的第一手形状是：<b>会话挂着的时候往 profile 存储里 Add 了一条</b>
    ///   （03:05:25.830 <c>profile-sub 订阅profile[星链机场] 新建=Ok</c>，隧道那时还在 iKuuu 上），
    ///   之后那条新条目的 <c>ConnectionStatus</c> 从此抛 0x80070006，跨进程、删了重盖都不自愈。
    ///   ⇒ 1.0.101 起 <see cref="VpnProfileSetup"/> 的 Add/Update 一律先过"此刻没有活会话"这道闸。
    ///   闸一拦，面板就少一条目 —— 而"少一条目且没人记得它欠着"正是本项目最忌的静默失败，
    ///   所以欠下的每一条都落一行账，由对账轮补齐后销账。
    ///
    /// 【落盘形状】<c>LocalState\profile-reconcile-owed.txt</c>，一行一条：
    ///   <c>{key}\t{首次欠下时间}\t{最后一次判据时间}\t{判据}</c>；<c>key</c> = 订阅 id，基础 profile 用 <c>基础profile</c>。
    ///   为什么是文件不是内存静态：写入方是"拉取/切换/挂载对账"三条不同调用路径，
    ///   其中挂载那轮可能在另一个进程里跑（关窗后由无头宿主续着的会话，Docs/31 §4.3），
    ///   进程内静态位会随宿主一起消失 —— 口径同 Docs/48 §三"只有跨进程契约才允许共享可变态"。
    /// 【不会无限长】同一 key 只留一行（覆盖判据、不覆盖首次时间）；补齐即销账；
    ///   订阅删除时由 <see cref="VpnProfileSetup.RemoveSubscriptionProfileAsync"/> 销账。
    /// </summary>
    internal static class ReconcileOwed
    {
        internal const string FileName = "profile-reconcile-owed.txt";
        internal const string BaseKey = "基础profile";

        /// <summary>读—改—写之间不许交叉，否则两条并发的欠账会互相覆盖。</summary>
        private static readonly SemaphoreSlim Gate = new(1, 1);

        /// <summary>
        /// ★ "台账里还有东西"的<b>进程内提示位</b>，不是数据源（数据源永远是那个文件）。
        ///   存在的理由只有一个：前台 3 秒轮询要知道"值不值得再补扫一轮"，
        ///   而它不该每 3 秒去 stat 一次盘。谁都可能算错它，也只会造成"多扫/少扫一轮"，
        ///   下一轮对账会用文件真值覆盖它（<see cref="SetDirty"/>）。
        ///   口径同 <c>VpnSession.SwitchInProgress</c>：进程内的动作窗口提示，不跨进程承诺。
        /// </summary>
        private static int _dirty;

        /// <summary>提示位：台账可能非空（前台轮询据此决定要不要补扫）。</summary>
        internal static bool Dirty => System.Threading.Volatile.Read(ref _dirty) != 0;

        /// <summary>用文件真值刷新提示位（对账轮每轮做一次，防上面那个"可能"漂着）。</summary>
        internal static void SetDirty(bool dirty) =>
            System.Threading.Volatile.Write(ref _dirty, dirty ? 1 : 0);

        /// <summary>
        /// 记一条欠账（已存在同 key 则只更新判据与时间，保留首次欠下时间）。
        /// ★【1.0.104】<b>判据逐字相同就不碰盘</b>：对账每轮对每个被拦的 key 都会调这里，
        ///   若每轮都盖一个新时间戳，台账永远在变 ⇒ 前台无法判断"这一轮到底有没有进展"，
        ///   只能无休止白扫（判据相同恰恰意味着"卡在同一件事上"）。
        ///   这也是 <see cref="MainComponent"/> 补账轮能做"连续两轮快照一致 ⇒ 暂停补扫"
        ///   的前提。
        /// </summary>
        internal static async Task MarkAsync(string key, string proof)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            await Gate.WaitAsync();
            try
            {
                var lines = await ReadLinesAsync();
                string stamp = DateTime.Now.ToString("HH:mm:ss");
                int idx = lines.FindIndex(l => KeyOf(l) == key);
                if (idx >= 0)
                {
                    string[] parts = lines[idx].Split('\t');
                    string first = parts.Length > 1 ? parts[1] : stamp;
                    // 判据没变 ⇒ 这一轮和上一轮拦在同一件事上，保持台账原样直接返回。
                    if (parts.Length > 3 && parts[3] == proof) return;
                    lines[idx] = $"{key}\t{first}\t{stamp}\t{proof}";
                    ClashUWP.Vpn.RollingLog.Info("profile-owed",
                        $"欠账判据变化：{key} → {proof}（首次 {first}）");
                }
                else
                {
                    lines.Add($"{key}\t{stamp}\t{stamp}\t{proof}");
                    ClashUWP.Vpn.RollingLog.Info("profile-owed",
                        $"记一笔欠对账：{key} 判据={proof}");
                }

                await WriteAsync(lines);
                SetDirty(true);
            }
            finally { Gate.Release(); }
        }

        /// <summary>
        /// 销一条欠账。★ 台账为空/文件不存在时<b>不碰盘</b> ——
        /// 对账每轮对每个 key 都会调它，不能让它变成 3 秒一次的无谓写盘。
        /// </summary>
        internal static async Task<bool> ClearAsync(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            await Gate.WaitAsync();
            try
            {
                Windows.Storage.StorageFile? f = await LocalState.TryGetFileAsync(FileName);
                if (f == null) return false;

                var lines = await ReadLinesAsync();
                int removed = lines.RemoveAll(l => KeyOf(l) == key);
                if (removed == 0) return false;

                await WriteAsync(lines);
                SetDirty(lines.Count > 0);
                ClashUWP.Vpn.RollingLog.Info("profile-owed", $"销账：{key}（补齐了）");
                return true;
            }
            finally { Gate.Release(); }
        }

        /// <summary>当前欠账原文（无文件回空串）。给对账汇总行与界面用。</summary>
        internal static async Task<string> SnapshotAsync()
        {
            try
            {
                var lines = await ReadLinesAsync();
                return string.Join(" / ", lines);
            }
            catch { return ""; }
        }

        /// <summary>欠了几条。读失败按 0 算（这是旁路，不能把主流程带崩）。</summary>
        internal static async Task<int> CountAsync()
        {
            try { return (await ReadLinesAsync()).Count; }
            catch { return 0; }
        }

        /// <summary>
        /// ★【1.0.104】丢掉<b>本地清单里已经没有</b>的欠账 key。
        /// 【为什么必须有】对账轮只会为"清单里还在的订阅"跑 Ensure，而 Ensure 是唯一会销账的地方。
        ///   于是有一条欠账的订阅被删掉之后（尤其"profile 还没建出来就被删"这种：
        ///   <see cref="VpnProfileSetup.RemoveSubscriptionProfileAsync"/> 找不到条目就提前返回，
        ///   根本走不到删除那一步），那个 key 永远不会有人再碰 ⇒ 台账非空 ⇒
        ///   <see cref="Dirty"/> 一直真 ⇒ 前台每 15 秒白扫一轮，无休止。
        /// 【清单为空时不动】口径与 prune 那一条相同：空清单更可能是"还没拉/文件读失败"，
        ///   此时把台账清空属于二次事故（丢了证物还顺手把欠账抹了）。
        /// ★【1.0.105 · 装机才抓到的反向 bug】1.0.104 那一版这里写成了
        ///   <c>RemoveAll(l =&gt; set.Contains(...))</c> —— 判据整个反了：<b>留下来的清单</b>被删光、
        ///   该丢的 ghost key 原样躺着，可日志却报"销掉 1 条"。本机 07:23:44 那一轮
        ///   <c>profile-owed 销掉清单里已不存在的欠账 1 条：ghost-sub-for-prune…</c> 与
        ///   台账文件里那条还在，两件事同时成立 ⇒ 证死。
        ///   【教训】"日志说了什么"不能当"文件里是什么"的替代 —— 这个方法第一次上机就靠
        ///   读文件本体（而不是看汇总行）验，才没有把一条谎报当成通过。
        /// </summary>
        internal static async Task<string> PruneStaleAsync(IReadOnlyCollection<string> keepKeys)
        {
            if (keepKeys == null || keepKeys.Count == 0) return "";
            await Gate.WaitAsync();
            try
            {
                Windows.Storage.StorageFile? f = await LocalState.TryGetFileAsync(FileName);
                if (f == null) return "";

                var set = new HashSet<string>(keepKeys, StringComparer.OrdinalIgnoreCase);
                var lines = await ReadLinesAsync();
                var dropped = lines.Where(l => !set.Contains(KeyOf(l))).ToList();
                if (dropped.Count == 0) return "";

                lines.RemoveAll(l => !set.Contains(KeyOf(l)));
                await WriteAsync(lines);
                SetDirty(lines.Count > 0);
                string r = $"销掉清单里已不存在的欠账 {dropped.Count} 条：{string.Join(" / ", dropped)}";
                ClashUWP.Vpn.RollingLog.Info("profile-owed", r);
                return r;
            }
            finally { Gate.Release(); }
        }

        private static string KeyOf(string line)
        {
            int tab = line.IndexOf('\t');
            return tab < 0 ? line : line[..tab];
        }

        private static async Task<List<string>> ReadLinesAsync()
        {
            var list = new List<string>();
            Windows.Storage.StorageFile? f = await LocalState.TryGetFileAsync(FileName);
            if (f == null) return list;
            string text = await LocalState.ReadTextAsync(f);
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length > 0) list.Add(line);
            }
            return list;
        }

        private static async Task WriteAsync(List<string> lines)
        {
            var sb = new StringBuilder();
            foreach (string l in lines) sb.Append(l).Append('\n');
            await LocalState.WriteTextQuietAsync(FileName, sb.ToString());
        }
    }
}
