using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Windows.Networking.Vpn;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// ★【1.0.81 · Docs/33 §三 的后续】<b>profile 存储 RPC 的串行层</b> ——
    ///   <see cref="ClashUWP.Backend.VpnProfileSetup"/> 与 <see cref="GracefulTeardown"/> 从此共用<b>一个</b>
    ///   <see cref="VpnManagementAgent"/> 实例，并且<b>任何时刻只允许一次调用在飞</b>。
    ///
    /// ══ 这条是从实机数据里长出来的，不是先猜再验 ══
    ///   1.0.79 的实测缺陷是"武装后整条静默"：第一次读 VPN 状态就抛
    ///   <c>COMException: 句柄无效。 (0x80070006)</c>，而它逃进了 fire-and-forget 任务、
    ///   零痕迹（Docs/33 §三）。1.0.80 加固后同一位置每次拨号都复现，且<b>第二次读（约 2 s 后）就好了</b>。
    ///
    ///   Docs/33 §三 当时留的候选解释是"`private static readonly VpnManagementAgent`
    ///   这个 COM 代理是正主"。<b>本轮把这条候选自己证伪了</b>，三条证据：
    ///   ① 官方参考页的类声明原文（现读，winrt-26100 moniker）：
    ///      <c>[MarshalingBehavior(MarshalingType.Agile)]</c> + <c>[Threading(ThreadingModel.MTA)]</c>
    ///      —— 这是自由线程对象，"缓存在静态字段上会被公寓/线程绑死"这个前提不成立。
    ///   ② 同一次会话里，UI 那条 3 秒轮询（<c>MainComponent</c> → <c>VpnSession.StatusAsync</c>
    ///      → <c>OurStatusAsync</c>）走的是<b>同一个缓存实例</b>，整整 1350 s <b>零异常</b>
    ///      （1.0.80 刚给这条加了 <c>ui-poll</c> 边沿出声，所以"零"是真零，不是被吞了）。
    ///   ③ 失败那一次的时刻：<c>07:40:55.213</c> 前台发起 <c>ConnectProfileAsync</c>，
    ///      <c>07:40:55.271</c>（58 ms 后）哨兵的第一次 <c>GetProfilesAsync</c> 抛错。
    ///      也就是说<b>只有撞上连接 RPC 的那一读会炸</b>，错峰读全好。
    ///
    ///   ⇒ 剩下的形态是<b>并发</b>：连接在飞的同时枚举 profile 存储，平台侧那条共享 handle
    ///     被并发调用打 invalid。这与"缓存实例"无关，与"同时有几个在飞"有关。
    ///   ⚠️ 诚实边界：这是<b>目前唯一存活的解释</b>，不是已证实的机制 —— 官方没有一句
    ///     文档写着"不得并发调用 VpnManagementAgent"（该页没有 Remarks/Thread safety 段）。
    ///     所以这一层的验收判据是<b>装机看 0x80070006 是否归零</b>；归零才算成立，
    ///     不归零就说明还有一层没读到，届时按 Docs 订正，不许把这层当"已修好"收尾。
    ///
    /// ══ 为什么串行而不是"每次操作新建一个 agent" ══
    ///   新建实例治的是"缓存状态过期"，而 ② 已经说明缓存实例本身是好的；
    ///   真要治并发，得让<b>调用序</b>互斥，新建实例并不阻止两个调用同时在飞。
    ///
    /// ══ 死锁纪律（改这里之前先读）══
    ///   锁是<b>不可重入</b>的 <see cref="SemaphoreSlim"/>(1,1)，所以：
    ///   ★ 只能在"叶子"调用点用 —— 一次 <c>Agent.X</c> 拿到结果就返回，
    ///     它的执行期间<b>绝不</b>再嵌套调用本类其它方法。
    ///   ★ 新增调用点前先确认自己没有跑在另一个 <c>ProfileStore.*</c> 里面。
    ///     目前 <c>VpnProfileSetup</c>/<c>GracefulTeardown</c> 的编排函数
    ///     （Connect/Recreate/Teardown/对账…）都是"先 await 一次取结果，再 await 下一次"，
    ///     没有嵌套，所以安全；<c>ConnectAsync</c> 那 20 轮状态轮询也是各自一次独立调用。
    ///   ★ 等待上一个调用是<b>有意</b>的（3 s 超时是兜底，正常是毫秒级）：
    ///     宁可排一次队，也不要一次"句柄无效"把整条保护链打掉。
    ///
    /// ══【1.0.88 · Docs/38 §十一】把"排队超时 ⇒ 强行执行"这条改掉，并给每次调用加<b>有界等待</b> ══
    ///   旧写法有两个都会拖住平台的出口，各自都错过一次：
    ///   ① <c>await call()</c> <b>不加时限</b> ⇒ 平台不返回时闸被<b>永久</b>占住，
    ///     之后每个调用都要先白等满 3 s（前台 3 秒轮询就是每轮 3 s）。
    ///   ② 排队超时后"强行执行" ⇒ 往<b>同一个 agent</b> 上并发发第二条 RPC，
    ///     而这正是 §开头那条 0x80070006 候选（"撞上连接 RPC 的那一读会炸"）的形状。
    ///   【官方给了什么】现读 26100 参考页：<c>ConnectProfileAsync</c> 全文只有
    ///     "Connects to an existing VPN profile." 一句，带 <c>[RemoteAsync]</c>，
    ///     返回 "An enum value indicating the error status"，<b>没有 Remarks、没有时限、
    ///     没有并发条款、也没有任何中止原语</b>。
    ///     所以"有界等待"这个数值<b>是我们自己定的</b>，官方只支持到原则那一层：
    ///     官方 sample（VpnPlugin.cs:1509 起）找的是"我自己家族名下 <c>ConnectionStatus == Connected</c>
    ///     的<b>那一条</b>"，配合 MainPage.xaml.cs:80 "There should be zero or one of these at all times"
    ///     ⇒ <b>至多一条</b>。因此本类的新纪律是：一次只允许一条 profile 存储调用在飞，
    ///     超时就<b>明说这条没返回并拒绝再发第二条</b>，而不是偷偷并发。
    ///   【悬挂时怎么恢复】超时的那一条<b>不取消</b>（取消是骗自己，平台侧没有中止原语），
    ///     闸由它继续占住；等它真的返回时由续块放行下一位，并记一行"共 X ms 后返回"。
    ///     ⇒ 后果要说白：如果平台永远不返回，本进程内后续 profile 存储调用会一直被拒
    ///       （读侧表现为状态暂不可读、拨侧表现为拒绝发起），直到那条返回或进程退出。
    ///       这是"绝不并发打平台"与"读不到状态"两者里选了前者，装机若不成立再按 Docs 订正。
    /// </summary>
    internal static class ProfileStore
    {
        /// <summary>全进程唯一的 agent 实例（原先两处各有一份静态缓存）。</summary>
        private static readonly VpnManagementAgent Agent = new();

        private static readonly SemaphoreSlim Gate = new(1, 1);

        /// <summary>
        /// 等锁上限（毫秒）。正常前一次调用是毫秒级就返回，这个上限只在平台 RPC 真卡住时兜底。
        /// 口径与 <c>HealthMonitor.ReadStatusBoundedAsync</c> 一致：3 s。
        /// ★【1.0.88】等满之后的动作从"强行执行"改成了"本轮放弃并出声"，见类注释 §十一。
        /// </summary>
        private const int AcquireTimeoutMs = 3000;

        /// <summary>
        /// 单次调用的<b>有界等待</b>上限 —— 读（枚举 profile）。★ 数值是我们自定的，官方无时限。
        ///   口径来源：`Docs/38` §四 F6 定的"读状态臂沿用现有 3 s 口径"（与 <see cref="AcquireTimeoutMs"/>
        ///   和 <c>HealthMonitor.ReadStatusBoundedAsync</c> 同一把尺子），装机第一次跑悬挂臂时
        ///   用一手耗时改它，不许长期挂着猜的值（§五 第 7 行同一条纪律）。
        /// </summary>
        private const int ReadBudgetMs = 3000;

        /// <summary>
        /// 单次调用的<b>有界等待</b>上限 —— 写（连接/断开/增删改）。★ 数值是我们自定的，官方无时限。
        ///   口径来源：`Docs/38` §四 F6 明写的"拨号臂预算 <b>45 s</b>"，那个 45 s 是先遣项目
        ///   `docs/62:65` 的经验线（超过它就再没返回过），不是官方承诺。
        ///   为什么不能收紧到几秒：实测一次正常拨号 <c>Connect→Start</c> 只有 1–2 s
        ///   （2026-10-21 十四条会话全在这个区间，桥启动 157–577 ms），但**切换路径**上
        ///   收尾与轮询各自要占闸，把预算压短会把"慢但会回"误判成"悬挂"，
        ///   而误判的代价（此后全部 profile 存储调用被拒）比多等 25 s 大得多。
        /// </summary>
        private const int WriteBudgetMs = 45000;

        /// <summary>
        /// "有一条调用至今没返回"的显式状态位（值为那条的 op 名，<c>null</c> = 没有）。
        /// ★ 只在<b>超过有界等待</b>之后置位，正常在飞不置位 —— 正常在飞要让后来者排队（3 s 内），
        ///   那是 §开头实测"错峰读全好"依赖的行为；置位后再来的直接拒，不排队、更不并发。
        /// </summary>
        private static volatile string? _abandonedOp;

        /// <summary>
        /// 唯一入口：串行执行一次 <paramref name="call"/>，返回它的结果。
        /// 【1.0.88 的三种出口】① 正常返回；② 超过 <paramref name="budgetMs"/> 未返回 ⇒
        /// 记 Warn、把闸留给这条未返回的调用、向调用方抛 <see cref="ProfileStoreBusyException"/>；
        /// ③ 排队都拿不到闸（上一条还活着）⇒ 同样抛，<b>不再并发</b>。
        /// </summary>
        private static async Task<T> Run<T>(string op, Func<Task<T>> call, int budgetMs)
        {
            // ⓪ 已经有一条没返回 ⇒ 立刻拒，连队都不排（否则前台每轮白等 3 s）。
            string? stuck = _abandonedOp;
            if (stuck != null)
            {
                RollingLog.Warn("profile-store",
                    $"{op}=拒绝（上一条 {stuck} 至今未返回 ⇒ 按官方「至多一条」不在它之上再发第二条 profile 存储调用）");
                throw new ProfileStoreBusyException(stuck, op);
            }

            bool acquired = await Gate.WaitAsync(AcquireTimeoutMs).ConfigureAwait(false);
            if (!acquired)
            {
                // ★ 旧写法在这里"强行执行"——那是往同一个 agent 上并发打第二条 RPC。
                RollingLog.Warn("profile-store",
                    $"等上一次调用超过 {AcquireTimeoutMs}ms 仍未放行 ⇒ {op} 本轮放弃（不并发放行）");
                throw new ProfileStoreBusyException($"前一次调用未在 {AcquireTimeoutMs}ms 内放行", op);
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Task<T> inFlight;
            try
            {
                inFlight = call();
            }
            catch
            {
                Gate.Release();
                throw;
            }

            using var cts = new CancellationTokenSource();
            Task timer = Task.Delay(budgetMs, cts.Token);
            Task finished = await Task.WhenAny(inFlight, timer).ConfigureAwait(false);

            if (finished == inFlight)
            {
                cts.Cancel();
                Gate.Release();
                long ms = sw.ElapsedMilliseconds;
                if (ms > 2000)
                    RollingLog.Warn("profile-store", $"{op} 返回了但很慢：{ms}ms（阈值 2000ms）");
                return await inFlight.ConfigureAwait(false);
            }

            // 超过有界等待还没返回 ⇒ 这条仍在飞。闸<b>不</b>在这里释放，
            // 改由下面这条续块在平台真的给结果时放行 —— 期间所有人走 ⓪ 那条快速拒绝。
            _abandonedOp = op;
            RollingLog.Warn("profile-store",
                $"{op} 超过 {budgetMs / 1000}s 未返回（官方对该 await 无时限承诺）⇒ 本次按失败回话；" +
                "闸由这条未返回的调用继续占住，期间拒绝再发第二条（不取消：平台侧没有中止原语）");
            _ = inFlight.ContinueWith(t =>
            {
                long ms = sw.ElapsedMilliseconds;
                _abandonedOp = null;
                Gate.Release();
                if (t.IsFaulted)
                    RollingLog.Warn("profile-store",
                        $"那条未返回的 {op} 最终抛异常（共 {ms}ms）⇒ 闸已放行：{t.Exception?.GetBaseException().GetType().Name}");
                else
                    RollingLog.Info("profile-store", $"那条未返回的 {op} 最终返回（共 {ms}ms）⇒ 闸已放行");
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

            throw new ProfileStoreBusyException($"{op} 未在 {budgetMs / 1000}s 内返回", op);
        }

        /// <summary>
        /// 【1.0.88 · Docs/38 §十一】profile 存储层"这一条发不出去"的显式失败。
        /// 三条成因（上一条未返回 / 排队超时 / 本条超时）都归它，
        /// <see cref="Message"/> 就是给用户看的那句中文，UI 顶层 catch 直接显示即可。
        /// </summary>
        internal sealed class ProfileStoreBusyException : Exception
        {
            internal ProfileStoreBusyException(string blockingOp, string attemptedOp)
                : base($"VPN 配置存储上有一条调用至今没有结束（{blockingOp}），" +
                       $"所以这次「{attemptedOp}」没有发出 —— 按官方「至多一条活动会话」的建模，" +
                       "绝不在未返回的调用之上再并发一条。稍后重试；若长期如此，请见 系统设置→VPN 的状态。")
            {
            }
        }

        /// <summary>枚举 profile 存储（原 <c>Agent.GetProfilesAsync()</c>）。读，走 <see cref="ReadBudgetMs"/>。</summary>
        internal static Task<IReadOnlyList<IVpnProfile>> GetProfilesAsync() =>
            Run("GetProfilesAsync", () => Agent.GetProfilesAsync().AsTask(), ReadBudgetMs);

        /// <summary>连接一条 profile（原 <c>Agent.ConnectProfileAsync(p)</c>）。写，走 <see cref="WriteBudgetMs"/>。</summary>
        internal static Task<VpnManagementErrorStatus> ConnectProfileAsync(IVpnProfile p) =>
            Run("ConnectProfileAsync", () => Agent.ConnectProfileAsync(p).AsTask(), WriteBudgetMs);

        /// <summary>断开一条 profile（原 <c>Agent.DisconnectProfileAsync(p)</c>）。写，走 <see cref="WriteBudgetMs"/>。</summary>
        internal static Task<VpnManagementErrorStatus> DisconnectProfileAsync(IVpnProfile p) =>
            Run("DisconnectProfileAsync", () => Agent.DisconnectProfileAsync(p).AsTask(), WriteBudgetMs);

        /// <summary>新建 profile（原 <c>Agent.AddProfileFromObjectAsync(p)</c>）。写，走 <see cref="WriteBudgetMs"/>。</summary>
        internal static Task<VpnManagementErrorStatus> AddProfileFromObjectAsync(IVpnProfile p) =>
            Run("AddProfileFromObjectAsync", () => Agent.AddProfileFromObjectAsync(p).AsTask(), WriteBudgetMs);

        /// <summary>更新 profile（原 <c>Agent.UpdateProfileFromObjectAsync(p)</c>）。写，走 <see cref="WriteBudgetMs"/>。</summary>
        internal static Task<VpnManagementErrorStatus> UpdateProfileFromObjectAsync(IVpnProfile p) =>
            Run("UpdateProfileFromObjectAsync", () => Agent.UpdateProfileFromObjectAsync(p).AsTask(), WriteBudgetMs);

        /// <summary>删除 profile（原 <c>Agent.DeleteProfileAsync(p)</c>）。写，走 <see cref="WriteBudgetMs"/>。</summary>
        internal static Task<VpnManagementErrorStatus> DeleteProfileAsync(IVpnProfile p) =>
            Run("DeleteProfileAsync", () => Agent.DeleteProfileAsync(p).AsTask(), WriteBudgetMs);
    }
}
