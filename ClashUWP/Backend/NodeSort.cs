using System;
using System.Collections.Generic;

using ClashUWP.Backend.Mihomo;

namespace ClashUWP.Backend
{
    /// <summary>
    /// ★【1.0.119 · 代理页排序】"这一组的成员按什么顺序画"的判据 —— 纯函数。
    ///
    /// 【为什么默认必须是原序】1.0.84 那条纪律逐字有效：
    ///   内核 <c>/proxies</c> 给的成员顺序 <b>就是订阅里的作者顺序</b>，
    ///   像「📺 动画疯 第一个成员是 🔰 选择节点」这种排布是作者有意为之的，
    ///   重排会把它弄丢。所以排序是<b>用户主动选</b>的额外视角，不是新默认。
    ///   本类没有任何"默认按延迟"的分支 —— 这一点在 <c>Tools/YamlShapeTest</c> 里钉着。
    ///
    /// 【★ 未测与不通必须排到最后，而且是两种不同的"最后"】
    ///   <c>DelayMs == -1</c> = <b>从没测过</b>（口径见 <c>KernelView</c> 文件顶部：
    ///     安卓 <c>ProxyView</c> 的 <c>if (proxy.delay in 0..Short.MAX_VALUE)</c> 才画胶囊），
    ///   <c>DelayMs == 0</c> = <b>本轮不通</b>（官方测速失败往 history 写的就是 0，
    ///     <c>adapter/adapter.go:170-177,190</c>，Docs/49 只引官方不引安卓）。
    ///   拿 <c>DelayMs</c> 直接升序排，会把"没测过"的 -1 排到<b>最前</b>——
    ///   那是这一版最容易犯的错：一屏"看起来 0ms 的神秘节点"，
    ///   而它们其实只是没被测过。所以排序键是<b>分档</b>的：
    ///   <c>0ms…Nms</c>（真延迟，越小越前）→ <c>不通</c>（有测量但失败）→ <c>未测</c>（没有测量）。
    ///   不通排在未测<b>前面</b>：至少这是个被测过且能用列表里的节点，
    ///   "测过但不通"和"根本没测"是两件事，界面把它们分成两段。
    ///
    /// 【★ 嵌套组与内置策略不参与"按延迟"排序】
    ///   组的 <c>Delay</c> 是它自己的历史值、内置策略（DIRECT/REJECT）根本没有延迟概念；
    ///   把它们混进真节点的延迟序列里，会出现"🔰 选择节点 排在一个真节点前面因为它 history 末位是 0"
    ///   这种把组当节点的读反 —— 正是 1.0.84 修过的那一类。
    ///   所以：真节点按所选模式排，组与内置策略按<b>原序</b>留在<b>它们原来的位置</b>上。
    ///
    /// 【为什么单独成文件】<c>MemberList</c> 在 <c>MainComponent</c> 里，而前端文件
    ///   link 不进离线夹具（Reactor/UI 依赖）。判据与画分开，"分档顺序"这种
    ///   一旦装机就"看着挺对其实排反"的东西才有证据。口径同本批
    ///   <c>RouteInclusion</c> / <c>TrafficRate</c> / <c>AdapterHealth</c>。
    /// </summary>
    internal static class NodeSort
    {
        /// <summary>排序模式。<see cref="Original"/> 是唯一默认值。</summary>
        internal enum Mode
        {
            Original,
            Delay,
            Name,
        }

        /// <summary>
        /// "延迟档"：0 = 有真延迟（越小越前）、1 = 本轮不通、2 = 从没测过。
        /// 组与内置策略拿 <c>-1</c> 表示"不进这个序列"，由 <see cref="Order"/> 单独安置。
        /// </summary>
        internal static int DelayBucket(KernelView.Member m)
        {
            if (m.IsGroup || m.IsBuiltin) return -1;
            if (m.DelayMs > 0) return 0;
            if (m.DelayMs == 0) return 1;
            return 2;
        }

        /// <summary>
        /// 排好序的成员表。长度与内容一定和输入一致（只换顺序，不增删）——
        /// 这一条有断言钉，因为"排序顺手过滤掉几条"是这类函数最阴的失效形状。
        /// </summary>
        internal static List<KernelView.Member> Order(IReadOnlyList<KernelView.Member> members, Mode mode)
        {
            var list = new List<KernelView.Member>(members);
            if (mode == Mode.Original) return list;

            if (mode == Mode.Name)
            {
                // OrdinalIgnoreCase：节点名里中英混排、全角空格、emoji 都是订阅常态，
                // 文化敏感排序会让同一个名字在不同区域设置下换位置。
                // ★ 这里<b>不</b>沿用 KernelView.Find 的 Ordinal —— 那边是"按名字选中这一条"，
                //   差一个大小写就该选不中，绝不能近似；这里是给人看的排列次序，
                //   近似（a 与 B 挨着）才是想要的效果。同一个项目里两种口径各自成立。
                list.Sort((a, b) =>
                {
                    int byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    return byName != 0 ? byName
                        : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
                });
                return list;
            }

            // mode == Delay：真节点按 (档, 毫秒) 排，组与内置策略留在原索引上。
            var reals = new List<(int Index, KernelView.Member M)>();
            for (int i = 0; i < members.Count; i++)
                if (DelayBucket(members[i]) >= 0) reals.Add((i, members[i]));

            // ★ "留在原索引上"是<b>槽位</b>不变，不是<b>成员</b>回自己的旧位置：
            //   真节点占的索引集合（这里 3..7）保持被占，组/内置占的 0..2 一个字节都不动。
            //   写成 outList[r.Index] = r.M 等于"每个节点回自己家" —— 排序白做，
            //   夹具第一轮就把它抓出来了（B-慢 仍在 a-中 前面）。
            var slots = new List<int>(reals.Count);
            foreach (var r in reals) slots.Add(r.Index);

            // 末位用原索引做决胜：List.Sort 是不稳定排序，同档两条若比较返回 0
            // 就会随机换位 —— 用户每刷一次看到"未测那批"换个顺序，比不排更难受。
            reals.Sort((x, y) =>
            {
                int bx = DelayBucket(x.M), by = DelayBucket(y.M);
                if (bx != by) return bx.CompareTo(by);
                int byDelay = x.M.DelayMs.CompareTo(y.M.DelayMs);
                return byDelay != 0 ? byDelay : x.Index.CompareTo(y.Index);
            });

            var outList = new List<KernelView.Member>(members);
            for (int k = 0; k < reals.Count; k++) outList[slots[k]] = reals[k].M;
            return outList;
        }

        /// <summary>
        /// 延迟档的一句话摘要（给排序按钮旁边那行小字用）。
        /// ★ 把"多少条没测过"说出来是这一档的实际价值：一排"延迟"视图里
        ///   若 30/32 条是未测，用户看到的"前几名"其实是那 2 条测过的，
        ///   不说这句就会被读成"这两个节点最快"。
        /// </summary>
        internal static string DelaySummary(IReadOnlyList<KernelView.Member> members)
        {
            int measured = 0, dead = 0, untested = 0;
            foreach (var m in members)
            {
                switch (DelayBucket(m))
                {
                    case 0: measured++; break;
                    case 1: dead++; break;
                    case 2: untested++; break;
                }
            }
            return $"有延迟 {measured}　不通 {dead}　未测 {untested}";
        }
    }
}
