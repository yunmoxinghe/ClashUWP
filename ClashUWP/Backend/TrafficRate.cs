using System;

namespace ClashUWP.Backend
{
    /// <summary>
    /// ★【1.0.119 · 实时流量】把内核的<b>累计字节</b>换算成<b>速率</b>的判据 —— 纯函数，不做 IO。
    ///
    /// 【为什么不能直接把内核的数当速率】官方 <c>mihomo v1.19.32</c>
    ///   <c>tunnel/statistic/manager.go:124</c> 的 <c>Snapshot</c> 里
    ///   <c>downloadTotal</c>/<c>uploadTotal</c> 是 <c>*Total.Load()</c> —— 累计量；
    ///   真正的每秒速率是 Go 侧那个 <c>blip</c>（同文件 <c>:117 handle()</c> 每秒
    ///   <c>Blip.Store(Temp.Swap(0))</c>），<b>不在这个响应里</b>，只经 <c>/traffic</c>
    ///   或 <c>/connections</c> 的 WebSocket 升级推送。
    ///   产品里没有任何 WebSocket 代码，也不想为了一根进度条新增一条传输通道
    ///   ⇒ 走差分：<c>(cur - prev) / (now - prevAt)</c>。
    ///   代价说清楚：拿到的是<b>两次采样之间的平均速率</b>（本包 = 3 秒一档），
    ///   不是每秒瞬时值。尖峰会被抹平，稳态流量读起来是对的。
    ///
    /// 【四条判据，每条都对应一种会把"绿"读反的形状】
    ///   ① <b>首个样本</b>（没有 prev）⇒ 速率"未知"，★ 不许画成 0 B/s：
    ///      刚连上就显示"↓ 0 B/s"，和"隧道里真的一条都没走"在界面上长得一样。
    ///   ② <b>计数器变小</b> ⇒ "未知"，★ 不许取绝对值也不许报负数：
    ///      <c>ResetStatistic()</c>（<c>manager.go:104</c>）会把六个量清零，内核重启同样是清零。
    ///      取 <c>Math.Abs</c> 会把一次重启读成一次突发大流量。
    ///   ③ <b>间隔 ≤ 0</b>（时钟回拨、同一轮里被调两次）⇒ "未知"，不做除零。
    ///   ④ <b>累计量</b>本身照实带出 —— 它不依赖差分，第一个样本起就是可信的。
    ///
    /// 【为什么单独成一个文件】<c>KernelView.LoadAsync</c> 要联网打回环，离线夹具进不去；
    ///   这套判据恰好是<b>最容易在装机上读错</b>的那类（0 / 负 / 假尖峰），
    ///   必须能在 <c>Tools/YamlShapeTest</c> 里逐条钉死。口径同本批的
    ///   <c>SessionWatchdogPolicy</c> / <c>RouteInclusion</c> / <c>AdapterHealth</c>。
    /// </summary>
    internal static class TrafficRate
    {
        private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

        /// <summary>字节数转人话。<b>不</b>带 <c>/s</c> —— 速率另走 <see cref="Rate.PerSecond"/>。</summary>
        internal static string Bytes(double value)
        {
            if (value < 0) value = 0;
            if (value < 1024) return $"{value:F0}B";
            double v = value / 1024;
            int i = 0;
            while (v >= 1024 && i < Units.Length - 2) { v /= 1024; i++; }
            return $"{v:F1}{Units[i + 1]}";
        }

        /// <summary>一次差分的结果。<see cref="Known"/> 为假时 <see cref="Up"/>/<see cref="Down"/> 无意义。</summary>
        internal readonly record struct Rate(double Up, double Down, bool Known, string? WhyUnknown)
        {
            /// <summary>速率文本。未知时给<b>点名原因</b>的短句，不给 0。</summary>
            internal string Describe() => Known
                ? $"↑{PerSecond(Up)}　↓{PerSecond(Down)}"
                : $"↑—　↓—（{WhyUnknown}）";

            /// <summary>字节/秒 → 人话（与 <see cref="Bytes"/> 同一张单位表，后缀多个 <c>/s</c>）。</summary>
            internal static string PerSecond(double bytesPerSecond)
            {
                if (bytesPerSecond < 0) bytesPerSecond = 0;
                if (bytesPerSecond < 1024) return $"{bytesPerSecond:F0}B/s";
                double v = bytesPerSecond / 1024;
                string[] units = { "KB/s", "MB/s", "GB/s", "TB/s", "PB/s" };
                int i = 0;
                while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
                return $"{v:F1}{units[i]}";
            }

            internal static Rate Unknown(string why) => new(0, 0, false, why);
        }

        /// <summary>
        /// 差分。入参是"上一次采到的累计值 + 时刻"与"这一次累计值 + 时刻"。
        /// ★ 累计值用 <c>long</c> 原样传（内核侧是 <c>int64</c>），
        ///   只在除以秒时落到 <c>double</c>，不改变量级。
        /// </summary>
        internal static Rate Between(long prevUp, long prevDown, DateTime prevAt,
                                    long curUp, long curDown, DateTime curAt)
        {
            if (prevAt == default)
                return Rate.Unknown("首次采样，还没有可比的上一条");

            double seconds = (curAt - prevAt).TotalSeconds;
            if (seconds <= 0)
                return Rate.Unknown("采样间隔非正（时钟回拨或同一轮重复采）");

            long dUp = curUp - prevUp;
            long dDown = curDown - prevDown;
            // ★ 计数器变小 = ResetStatistic 或内核重启。绝不取绝对值 —— 见类注释判据②。
            if (dUp < 0 || dDown < 0)
                return Rate.Unknown("累计值变小＝内核重启或计数器被清零");

            return new Rate(dUp / seconds, dDown / seconds, true, null);
        }

        /// <summary>
        /// 累计量与内存的一行文本。内核响应里 <c>uploadTotal</c>/<c>downloadTotal</c>/
        /// <c>memory</c> 三个键<b>全缺</b>时 <c>KernelView.ParseTraffic</c> 给的就是三个 0，
        /// "三个 0 就是没读到"这个约定在这里落地。
        /// ★ 三条全零才判"读不到"：真连上但一条流量都没走时，<c>memory</c> 不可能是 0
        ///   （内核进程 RSS 实测量级见 Docs/60，同一份内核在 Verge 里基线 54 MB），
        ///   所以<b>内存这一项正是把"确实没流量"和"根本没读到"分开的判据</b>。
        ///   不这么判的话，"隧道空载"会被界面读成"内核没在跑"，而后者是另一件事。
        /// </summary>
        internal static string DescribeTotals(long up, long down, long memory)
        {
            if (up == 0 && down == 0 && memory == 0)
                return "累计 读不到　内存 读不到";
            string s = $"累计 ↑{Bytes(up)} ↓{Bytes(down)}";
            // 内存单独判"读不到"：老内核没有 memory 字段，但流量照样有。
            s += memory > 0 ? $"　内核内存 {Bytes(memory)}" : "　内核内存 读不到";
            return s;
        }
    }
}
