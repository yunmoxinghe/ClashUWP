using System;

namespace ClashUWP.Backend
{
    /// <summary>
    /// ★【1.0.119 · 订阅定时自动更新】"这一次到底该不该去拉订阅"的判据 —— 纯函数。
    ///
    /// 【为什么默认必须是「关」】这一条动的是<b>生效配置</b>：拉下来走的是
    ///   <c>FetchAsync(activate:true)</c>，它会写 <c>config.yaml</c> 并热重载内核。
    ///   改的是用户正在用的那份出口配置，所以默认关闭、开启后仍要过下面几道判据。
    ///
    /// ★★【1.0.122 · 口径换了：连着 VPN 也照样更新，对齐 Clash Verge】
    ///   原先这里有一条「会话进行中一律 Skip」，Docs/62 §八 那条装机判据从未跑过
    ///   （要隧道），而这条口径本身也站不住脚：现读 clash-verge-rev 的实现，
    ///   自动更新与手动更新是<b>同一个函数</b> ——
    ///     · <c>src-tauri/src/core/timer.rs</c> 的 <c>async_task(uid)</c> 到点只调
    ///       <c>feat::update_profile(uid, None, false)</c>；
    ///     · <c>src-tauri/src/feat/profile.rs:203</c> 的 <c>update_profile</c> 判据只有四条：
    ///       是远程订阅、有 URL、<c>option.allow_auto_update</c>、到点（间隔由 timer 的
    ///       DelayQueue 管）—— <b>整段没有任何"内核/隧道在不在跑"的判断</b>；
    ///     · 更新完如果是当前那份（<c>is_current</c>），就走
    ///       <c>CoreManager::global().update_config_with_force(…)</c>（<c>:219</c>），
    ///       即 <c>PUT /configs?force=true</c> <b>原地热重载</b>，不重启内核、不断流；
    ///       自动触发时如果这次刷新被判 Skipped/Busy，它只记一行
    ///       「本次配置刷新已跳过」（<c>:224-226</c>），<b>不算失败</b>。
    ///   我们这一侧的形状本来就等价：拉取走 <c>FetchAsync(activate:true)</c>
    ///   = 写正文 + <c>ReloadActiveConfigAsync()</c>（内核不在跑就只保证文件就位、下次连接生效，
    ///   见该方法 <c>:516</c> 那支），写系统 VPN profile 那一步照旧过 1.0.101 的全局写入闸
    ///   （Docs/52：会话挂着就跳过并记欠账，断开后由边沿对账补）。
    ///   ⇒ 所以"动手会把用户的连接当场换掉"这个前提不成立：真正会换掉隧道的是
    ///     <b>切到另一份订阅</b>（Docs/55 那条三段式），那一条由 <c>switchInProgress</c>
    ///     和 <c>ActivateAsync</c> 自己的三段式管着，不在本判据里；本判据管的是
    ///     <b>同一份订阅的正文更新</b>，它与手动点「更新当前订阅」完全同路。
    ///   ★ 留着的判据（缺一个就变成无声骚扰或凭空造行为）：档位=关 → Skip、
    ///     没有生效订阅 → Skip、切订阅进行中 → Skip（两条流程叠在一起写 config.yaml 就是交叉写）、
    ///     读不到上次拉取时间 → Skip 不猜、没到点/退避中 → Skip。
    ///     每个 Skip 都带人话理由进日志与界面那行小字，不许静默。
    ///
    /// 【为什么间隔是"档位"而不是自由数字】界面是一排按钮，写进
    ///   <c>settings.txt</c> 的必须是白名单里的那个值；认不出来的一律当「关」
    ///   （默认必须是"什么都不改"那一档，绝不能是"最激进"那一档，见 <see cref="NormalizeHours"/>）。
    ///
    /// 【失败退避为什么必须有】订阅 URL 会过期、机场会挂。没有退避时，
    ///   一个 6 小时档的自动任务在机场 403 之后会变成<b>每 5 分钟一次的稳定骚扰</b>，
    ///   而用户完全看不出它在敲。所以连续失败按档加宽，成功一次立刻清零。
    /// </summary>
    internal static class SubscriptionAutoPolicy
    {
        /// <summary>可选间隔（小时）。<c>0</c> = 关闭，永远排第一。</summary>
        internal static readonly int[] IntervalHours = { 0, 6, 12, 24, 72 };

        /// <summary>检查节拍：判据每这么久问一次"到点了吗"（真正动手仍由到点决定）。</summary>
        internal static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(5);

        /// <summary>连续失败后的退避基数与上限（小时）。</summary>
        internal static readonly int FailBackoffBaseHours = 6;
        internal static readonly int FailBackoffMaxHours = 48;

        /// <summary>把任意字符串归一成白名单里的小时数；认不出来 = 0（关）。</summary>
        internal static int NormalizeHours(string? raw)
        {
            if (!int.TryParse((raw ?? "").Trim(), out int h)) return 0;
            foreach (int v in IntervalHours)
                if (v == h) return h;
            // 越界/不在档位上（比如手滑写了 1 或 1000）→ 关。
            // ★ 不"就近取一档"：用户写下一个没打算要的数，最坏结果是不跑，
            //   而不是被自动任务替他选了 6 小时。
            return 0;
        }

        internal static string Label(int hours) =>
            hours == 0 ? "关闭" : $"每 {hours} 小时";

        /// <summary>
        /// 下一次到点的时间（含失败退避）。★ 前置条件是 <see cref="IsOn"/> 为真 ——
        ///   关闭档没有"下一次"，那种形状由 <see cref="NextDueAt"/> 表达。
        /// <c>failStreak</c> 把下次推后 <c>min(6×2^(n-1), 48)</c> 小时（n≥1 时生效）。
        /// </summary>
        internal static DateTime DueAt(DateTime fetchedAt, int intervalHours, int failStreak)
        {
            DateTime due = fetchedAt.AddHours(intervalHours);
            if (failStreak <= 0) return due;
            long shift = FailBackoffBaseHours;
            for (int i = 1; i < failStreak && shift < FailBackoffMaxHours; i++) shift *= 2;
            if (shift > FailBackoffMaxHours) shift = FailBackoffMaxHours;
            return due.AddHours(shift);
        }

        /// <summary>同 <see cref="DueAt"/>，但把"关闭 = 永不到点"画成 null —— 界面那行小字要的就是这个形状。</summary>
        internal static DateTime? NextDueAt(DateTime fetchedAt, int intervalHours, int failStreak) =>
            IsOn(intervalHours) ? DueAt(fetchedAt, intervalHours, failStreak) : (DateTime?)null;

        internal static bool IsOn(int intervalHours) => intervalHours > 0;

        /// <summary>一次判据的结论：<see cref="Run"/> 带理由，<see cref="Skip"/> 带原因。</summary>
        internal readonly record struct Decision(bool ShouldRun, string Why)
        {
            internal static Decision Skip(string why) => new(false, why);
        }

        /// <summary>
        /// 该不该在这一拍动手。
        ///
        /// 【参数口径】<c>hasActiveSubscription</c> 为假 = 根本没有生效订阅（没拉过），
        /// 这时"自动更新"没有对象，必须 Skip —— 让它去拉"最近一份"是凭空造行为。
        /// ★ <c>failStreak</c> <b>必须</b>参与到点计算：夹具第一轮就是在这里抓到
        ///   "到点判据只看 fetchedAt+interval"，于是退避只写在界面上、
        ///   动手的那一支照旧每 5 分钟敲一次已经 403 的 URL。
        /// ★★【1.0.122】形参里<b>删掉了</b> <c>sessionActive</c>：连着 VPN 也照样更新，
        ///   与 Verge 同口径（理由与代码证据见类注释那一整段）。判据只剩"对象在不在、
        ///   有没有另一支正在写 config.yaml、到没到点"这三件事。
        /// </summary>
        internal static Decision Decide(int intervalHours, DateTime now,
                                        DateTime? fetchedAt,
                                        bool switchInProgress, bool hasActiveSubscription,
                                        int failStreak = 0)
        {
            if (!IsOn(intervalHours))
                return Decision.Skip("自动更新=关闭");
            if (!hasActiveSubscription)
                return Decision.Skip("没有生效订阅，自动更新没有对象");
            if (switchInProgress)
                return Decision.Skip("切订阅正在进行中");
            if (fetchedAt == null)
                return Decision.Skip("读不到上次拉取时间（订阅文件不在？），不猜");

            // 到这里档位必为"开"（第一行已拦），所以到点时间不需要再判一次形状。
            DateTime due = DueAt(fetchedAt.Value, intervalHours, failStreak);
            if (now < due)
                return Decision.Skip(failStreak > 0
                    ? $"退避中，下次 {due:MM-dd HH:mm}（已连续失败 {failStreak} 次）"
                    : $"还没到点（下次 {due:MM-dd HH:mm}）");
            return new Decision(true,
                $"已过 {Label(intervalHours)} 的间隔（上次 {fetchedAt.Value:MM-dd HH:mm}）");
        }

        /// <summary>
        /// 把"关闭/没到点/退避中/已动手"这几种状态画成一行小字。
        /// ★ <c>nextDue</c> 为 null 时必须写「关闭」，不许写空白 ——
        ///   空白会被读成"坏了"，而这正是本项目反复治的那类无声失效。
        /// </summary>
        internal static string StatusLine(int intervalHours, DateTime? nextDue, int failStreak)
        {
            if (!IsOn(intervalHours)) return "自动更新 关闭";
            string s = $"自动更新 {Label(intervalHours)}　下次 " +
                       (nextDue == null ? "—" : $"{nextDue.Value:MM-dd HH:mm}");
            return failStreak > 0 ? $"{s}（连续失败 {failStreak} 次，已退避）" : s;
        }
    }
}
