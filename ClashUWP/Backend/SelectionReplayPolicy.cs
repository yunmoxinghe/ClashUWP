using System;
using System.Collections.Generic;

using ClashUWP.Backend.Mihomo;

namespace ClashUWP.Backend
{
    /// <summary>
    /// ★【1.0.119 · 台账回放的判据】<c>selection.txt</c> 里每条记录该怎么落到内核上 —— 纯函数。
    ///
    /// 【为什么单独抽出来】这条判断原先内联在 <c>MihomoController.ReplaySelectionsAsync</c>
    ///   的一段 <c>if / continue</c> 里，那个方法同时干三件事：读台账（IO）、问内核（网络）、
    ///   决定某条记录该 PUT 还是该跳过（判据）。判据那件因此无法离线钉死，于是两个缺陷
    ///   一路带到装机：
    ///   ① 触发用的是"内核可达边沿"一只 bool 闩，而且<b>闩在读台账之前就置位</b> ——
    ///      "内核起来（台账还空）→ 用户在这次会话里点选"这个顺序里，后写入的每一条
    ///      都再也没有回放周期；
    ///   ② 复位只挂在轮询的 <c>catch</c> 上，可内核不可达时 <c>KernelView.LoadAsync</c>
    ///      是<b>返回 <c>Ok=false</c> 而不抛</b>（1.0.118 起还带回退的配置视图），
    ///      catch 那条路压根不走 ⇒ 闩一旦置上就再也不回放。
    ///   现在判据独立成这一份（<c>Tools/YamlShapeTest</c> 逐字编译同一文件），触发改成
    ///   <b>"内核现值与台账有漂移"</b>：不依赖有没有观察到掉线边沿 —— 内核重启后手动组的
    ///   <c>now</c> 自己会退回配置默认值（home 只读，内核的 cache.db 跨重启不保留），
    ///   漂移一出现就把该放的放回去。
    ///
    /// 【口径同源】"手动组 = Selector / select / Relay / relay"用的是
    ///   <see cref="KernelView.IsUserSelectableType"/> 同一份判据；实时切换那一路
    ///   （<c>MihomoController.SelectCoreAsync</c>）也是这一份，三处不许再各写各的。
    /// </summary>
    internal static class SelectionReplayPolicy
    {
        /// <summary>一条台账记录在内核当前组表下的处置。</summary>
        internal enum Kind
        {
            /// <summary>内核现值 ≠ 台账值：需要 <c>PUT /proxies/{组}</c>。</summary>
            Put,
            /// <summary>内核已经选着这个节点：什么都不用做。</summary>
            AlreadySelected,
            /// <summary>内核组表里没有这个组（订阅换了 / 内核还没加载）：跳过，不删台账。</summary>
            SkipGroupMissing,
            /// <summary>非手动组（url-test / fallback / load-balance / 内置策略）：选择由内核自管。</summary>
            SkipNotManual,
            /// <summary>组在，但节点不在它的成员表里（订阅换了）：跳过，不删台账。</summary>
            SkipNodeMissing,
        }

        /// <summary>
        /// 回放计划需要的最小内核视图：<b>组名 / 类型 / 现值 / 成员名</b>。
        /// 不直接吃 <c>MihomoController.ProxyGroup</c>，是为了让判据不绑网络层数据结构。
        /// </summary>
        internal readonly record struct GroupState(
            string Name, string Type, string Now, IReadOnlyList<string> Members);

        /// <summary>计划的一项：一条台账记录 + 它的处置。</summary>
        internal readonly record struct Item(string Group, string Node, Kind Decision);

        /// <summary>手动组判定（与 <see cref="KernelView.IsUserSelectableType"/> 同一份）。</summary>
        internal static bool IsManualType(string type) => KernelView.IsUserSelectableType(type);

        /// <summary>
        /// 把 UI 轮询已经拿到的 <see cref="KernelView.Group"/> 列表折成 <see cref="GroupState"/>。
        /// 成员只取名字（判据不看延迟、不看提供方），嵌套组与内置策略原样留在成员表里 ——
        /// 与实时切换的校验口径一致：<c>PUT</c> 一个组名或策略名是合法的（内核认这些名字）。
        /// </summary>
        internal static List<GroupState> StatesFromKernelGroups(
            IReadOnlyList<KernelView.Group> groups)
        {
            var res = new List<GroupState>(groups.Count);
            foreach (KernelView.Group g in groups)
            {
                var names = new List<string>(g.Members.Count);
                foreach (KernelView.Member m in g.Members) names.Add(m.Name);
                res.Add(new GroupState(g.Name, g.Type, g.Now, names));
            }
            return res;
        }

        /// <summary>
        /// 台账的规范化快照：按组名 Ordinal 排序，逐行 <see cref="SelectionStore.EncodeLine"/>。
        /// 【为什么存在】"这一版台账是不是刚试过"要能比对，就不能依赖
        ///   <c>Dictionary</c> 的枚举顺序；编码直接复用落盘那一份，免得两处各定一套转义
        ///   —— 组名/节点名里的 <c>=</c>、空格、中文、emoji 都靠它不破坏格式。
        /// </summary>
        internal static string Snapshot(IReadOnlyDictionary<string, string> ledger)
        {
            var keys = new List<string>(ledger.Keys);
            keys.Sort(StringComparer.Ordinal);
            var sb = new System.Text.StringBuilder();
            foreach (string k in keys)
                sb.Append(SelectionStore.EncodeLine(k, ledger[k])).Append('\n');
            return sb.ToString();
        }

        /// <summary>
        /// 台账 × 内核组表 → 逐条处置。输出按组名 Ordinal 稳定排序（与 <see cref="Snapshot"/> 同序），
        /// 这样"计划长什么样"在离线夹具里是可逐字比对的，而不是随字典枚举顺序漂移。
        /// </summary>
        internal static List<Item> Plan(
            IReadOnlyDictionary<string, string> ledger, IReadOnlyList<GroupState> groups)
        {
            var ordered = new List<KeyValuePair<string, string>>(ledger);
            ordered.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            var res = new List<Item>(ordered.Count);
            foreach (KeyValuePair<string, string> kv in ordered)
            {
                GroupState? g = Find(groups, kv.Key);
                if (g == null)
                {
                    res.Add(new Item(kv.Key, kv.Value, Kind.SkipGroupMissing));
                    continue;
                }
                if (!IsManualType(g.Value.Type))
                {
                    res.Add(new Item(kv.Key, kv.Value, Kind.SkipNotManual));
                    continue;
                }
                bool member = false;
                foreach (string m in g.Value.Members)
                    if (string.Equals(m, kv.Value, StringComparison.Ordinal)) { member = true; break; }
                if (!member)
                {
                    res.Add(new Item(kv.Key, kv.Value, Kind.SkipNodeMissing));
                    continue;
                }
                res.Add(new Item(kv.Key, kv.Value,
                    string.Equals(g.Value.Now, kv.Value, StringComparison.Ordinal)
                        ? Kind.AlreadySelected : Kind.Put));
            }
            return res;
        }

        /// <summary>按名字找组：<b>Ordinal</b> 原样比（组名带空格/emoji/前导空格是订阅常态）。</summary>
        private static GroupState? Find(IReadOnlyList<GroupState> groups, string name)
        {
            foreach (GroupState g in groups)
                if (string.Equals(g.Name, name, StringComparison.Ordinal)) return g;
            return null;
        }
    }
}
