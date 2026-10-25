using System;
using System.Threading;
using System.Threading.Tasks;

using ClashUWP.Vpn;

namespace ClashUWP.Backend
{
    /// <summary>
    /// 【后端 · 启动装配】界面第一次渲染时把取证与调试通道拉起来。
    ///
    /// 【为什么放后端】这三件事没有一件需要 UI：dump 是读平台档案，探测是建小路由
    ///   profile 打流量，调试回路是轮询一个文本文件。它们原先长在
    ///   <c>MainComponent.Render</c> 里，只是历史顺手 —— 结果是"UI 组件里藏着会自动
    ///   连 VPN 的代码"，每次审计都得重新解释一遍。现在前端只剩一行调用。
    ///
    /// ★★ 门控（Docs/12 §5 第 1 项 G12）：**两个探测不再无条件跑**。
    ///   它们各自只用小路由 probe profile（203.0.113.0/24 + 8.8.8.8/32），按实测不会
    ///   断网，但"会不会断网"不是这里的判据 —— 判据是<b>它真的建了一个 VPN 会话</b>，
    ///   而本项目出过"拆不干净 → 孤儿 NRPT + 幽灵路由 → 整机断网，只能重置网络恢复"
    ///   的事故。让"第一次开应用就自动拨 VPN"取决于用户（<c>settings.txt: autoProbe=1</c>），
    ///   不再取决于装配顺序。详见 <see cref="AppSettings.AutoProbe"/>。
    ///   <c>dump</c> 与<b>调试回路</b>是无条件装配的：前者纯读，后者纯等命令 ——
    ///   关掉它们等于关掉唯一的取证与唯一的远程断开通道，那是更大的风险。
    ///
    /// ★ 探测自己的两道闸门（selftest.txt / dataface-v4.txt）仍在 <see cref="StartupProbes"/>
    ///   里各认各的，本类只保证<b>本次进程内只装配一次</b>（<see cref="StartOnce"/>），
    ///   且两个探测<b>串行</b>：同一时间只允许一个 VPN 连接，并发会互相顶掉。
    /// </summary>
    internal static class Diagnostics
    {
        private static int _started;

        /// <summary>幂等启动装配：重复调用只有一次生效。</summary>
        internal static void StartOnce()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
                return;

            // ★ 一行"取证通道已装配"：它同时是「界面第一次渲染成功」的证据。
            //   没有这行时，app.log 里只有启动那一行，看不出进程是停在渲染之前还是之后。
            //   探测那句结论由 MaybeRunProbesAsync 另外记一行，因为"授权没给"和
            //   "给了授权但闸门文件已存在"是两件必须能分开的事。
            RollingLog.Info("diag", "装配完成：dump + 调试回路 + 待门控的探测");

            // 启动就把系统里所有 profile dump 到 LocalState/profile-dump.txt。
            //
            // ★ 为什么要这个：排查 CannotFindProfile 时，最关键的一格是
            //   「平台到底存了什么」—— 特别是 ServerUris 的 Port 到底是不是 0。
            //   这一步是**纯读**：只调 GetProfilesAsync，不建不连不删，
            //   不改任何路由，所以绝对不影响网络。
            //   也刻意不点按钮、不写 UI —— 少一次状态更新就少一个变数。
            _ = ProfileDump.DumpAsync();

            // ★ 调试入口：轮询 LocalState/debug-cmd.txt 下发命令，免重装操作 VPN。
            //   必须在门控之外先起来 —— 万一探测被授权打开后把网络搞坏，
            //   这条回路是唯一还能收到 disconnect 的地方。
            _ = DebugCommandLoop.RunAsync();

            // ★ 门控的探测：先读 settings.txt（异步，渲染线程一秒都不许等），再决定跑不跑。
            _ = MaybeRunProbesAsync();

            // ★【1.0.119】订阅自动更新环：判据默认「关」，所以武装它是无害的 ——
            //   真正决定它跑不跑的是 subAutoHours，不是这段装配顺序。
            //   与探测分开的理由：探测撞的是 VPN 会话（要门控），自动更新撞的是 HTTP +
            //   无会话时的写 profile，两者的风险面不是一条。
            _ = ArmAutoUpdaterAsync();
        }

        /// <summary>
        /// 读完设置再武装自动更新环。★ 顺序是必须的：环第一拍就要读
        /// <see cref="AppSettings.SubAutoHours"/>，而那份现值只有 LoadAsync 填得进。
        /// 读设置失败照样武装 —— 那时现值是那对默认（0=关），环只会一直 Skip 并写明理由，
        /// 不会因为一次 IO 失败就把界面承诺的那行小字变成永远不动的黑洞。
        /// </summary>
        private static async Task ArmAutoUpdaterAsync()
        {
            try { await AppSettings.LoadAsync(); }
            catch (Exception ex) { RollingLog.Error("sub-auto", "读设置失败（按默认=关武装）", ex); }
            SubscriptionAutoUpdater.StartOnce();
        }

        /// <summary>
        /// 「读完授权 → 有授权才串行跑两个探测」。整条链全异步，绝不在渲染线程上阻塞。
        ///
        /// 【为什么串行写成两个 await 而不是 <c>ContinueWith</c>】原来那版
        ///   <c>RunProbeOnceAsync().ContinueWith(_ =&gt; RunDatafaceProbeOnceAsync())</c>
        ///   有两个毛病：① 内层任务没人 await，异常掉进未观察任务里只留一个
        ///   <c>TaskScheduler.UnobservedTaskException</c>；② <c>ContinueWith</c> 默认用
        ///   <c>TaskScheduler.Current</c>，从渲染线程点火时它可能落到 UI 调度器上，
        ///   于是"探测在 UI 线程里 await 存储"这种时序问题会随机出现。
        ///   两个 await 的 async 方法把这两件事都关掉了，串行语义一个字没变。
        /// 【为什么这里还要再兜一层异常】两个探测各自内部都有 try/catch 并落盘，
        ///   但 <see cref="AppSettings.LoadAsync"/> 之外的调度失败（比如线程池枯竭）
        ///   不归它们管。这一层只记日志、不改状态：装配线程没有任何用户能看到的东西可写。
        /// </summary>
        private static async Task MaybeRunProbesAsync()
        {
            try
            {
                await AppSettings.LoadAsync();

                if (!AppSettings.AutoProbe)
                {
                    RollingLog.Info("diag",
                        "探测=跳过（settings.txt 无 autoProbe=1）：本次进程不建 profile、不连 VPN。" +
                        "要手动跑一次就往 debug-cmd.txt 写 probe（门控关掉的是自动，不是能）。");
                    return;
                }

                RollingLog.Info("diag", "探测=授权已给，串行开跑（各自仍受闸门文件约束）");
                await StartupProbes.RunProbeOnceAsync();
                await StartupProbes.RunDatafaceProbeOnceAsync();
            }
            catch (Exception ex)
            {
                RollingLog.Error("diag-probes", "装配探测阶段", ex);
            }
        }
    }
}
