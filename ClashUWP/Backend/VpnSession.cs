using System;
using System.Threading.Tasks;

using ClashUWP.Vpn;

namespace ClashUWP.Backend
{
    /// <summary>
    /// 【后端 · 会话动作】前端唯一被允许调用的 VPN 动作入口。
    ///
    /// 这层存在的理由只有一条：<b>把「谁该被保护」这件事固定在动作里</b>。
    /// 健康哨兵原先是 UI 按钮和 debug 回路各自记得去武装一次，这种"靠人记"的
    /// 写法迟早漏一条路 —— 而漏掉的那条路一旦黑洞就是整机断网。
    /// 现在 connect 必武装、disconnect 必解除，前端只管把返回的状态字符串显示出来。
    ///
    /// 分工：<see cref="VpnProfileSetup"/> 负责平台 API（建/连/断/删 profile），
    /// 本类负责<b>顺序与配套保护</b>，前端负责<b>显示与异常文案</b>。
    /// 异常一律原样向上抛，由前端翻译成状态文本 —— 后端不猜 UI 想显示什么。
    /// </summary>
    internal static class VpnSession
    {
        /// <summary>
        /// 重建主 profile。按钮分工照官方 sample：<b>建/重建</b>与<b>连接</b>是两个
        /// 独立动作 —— 合成一个「连接 VPN」等于自己制造
        /// 「刚建出来还没被平台索引」和「旧 profile 里的无效配置永远修不掉」两个坑。
        /// </summary>
        internal static Task<string> RecreateAsync() => VpnProfileSetup.RecreateProfileAsync();

        /// <summary>
        /// 连接主 profile（全路由）。
        /// ★ 先武装健康哨兵再连接：即使连接卡死/异常，哨兵的硬上限到点也必然断开，
        ///   网络绝不会被长期占死。
        /// ★【1.0.62】<paramref name="subId"/> 非空时连<b>该订阅专属</b>的系统 profile ——
        ///   与用户在快速设置里点那条订阅走的是<b>完全同一条</b>平台路径
        ///   （ConnectProfileAsync → 平台 → IVpnPlugIn.Connect），差别只是发起者是我们。
        ///   快速面板本身没法自动化点（系统 UI），这条入口就是它的自动化等价物：
        ///   同一 profile、同一 CustomConfiguration、同一插件读配置逻辑。
        ///   哨兵照常武装 —— 订阅 profile 同样是 0.0.0.0/0 全路由。
        /// </summary>
        internal static async Task<string> ConnectAsync(string? subId = null)
        {
            // ★【1.0.88】先记"本来有没有哨兵"：Arm() 是"顶掉上一个再武装"，
            //   所以后面那句"没发起拨号就解除"如果不分情况地执行，
            //   会把<b>上一次连接留下的黑洞保护</b>一起摘掉 —— 黑洞保护不能因为多点一下按钮而消失。
            bool hadSentinel = HealthMonitor.IsActive;
            HealthMonitor.Arm();
            RollingLog.Info("session", subId == null
                ? "用户发起连接（已先武装健康哨兵）"
                : $"用户发起订阅连接 subId={subId}（已先武装健康哨兵）");
            string r;
            try
            {
                r = await VpnProfileSetup.ConnectAsync(subId);
            }
            catch (ProfileStore.ProfileStoreBusyException)
            {
                // ★【1.0.88 · Docs/38 §十一】串行层拒绝发这一条（上一条 profile 存储调用未返回）
                //   ⇒ 这也属于"这一句根本没拨"，必须先按同一套哨兵纪律处理，再把这条
                //   中文异常原样交回上层显示（<c>MainComponent.RunAsync</c> 的 catch 会贴出 Message）。
                //   顺序不能反：先 throw 就把刚武装的哨兵留在那儿到点了。
                ApplyNoDialSentinelRule(hadSentinel, "本次拨号被 profile 存储串行层拒绝");
                throw;
            }

            // ★【1.0.88 · Docs/38 F1/F3】这一句代表"根本没拨"（官方前置条件拦住，或安全闸拒绝收尾）。
            //   本方法上面那句"先武装再连接"是为了<b>真拨出去且可能悬挂</b>的那次兜底的，
            //   不能改成"先判再武装"（判据要读 profile，那次读自己也可能不返回），
            //   所以拦住之后怎么处理哨兵只能分两种走 —— 见 <see cref="ApplyNoDialSentinelRule"/>。
            if (VpnProfileSetup.IsNoDialResult(r))
                ApplyNoDialSentinelRule(hadSentinel, "本次未发起拨号");
            return r;
        }

        /// <summary>
        /// "这一句根本没拨出去"之后对健康哨兵的处理（1.0.88 从 <see cref="ConnectAsync"/> 里抽出来，
        /// 因为现在有两个入口都会走到：返回值里带 <c>未发起=</c>/<c>新连接=跳过</c>，
        /// 以及串行层直接抛 <see cref="ProfileStore.ProfileStoreBusyException"/>）。
        /// </summary>
        private static void ApplyNoDialSentinelRule(bool hadSentinel, string why)
        {
            if (hadSentinel)
            {
                // ★ 不解除。理由要说清：<see cref="HealthMonitor.Arm"/> 是"顶掉上一个再武装"，
                //   所以本次武装的这一个<b>就是</b>原来那份保护的延续（点击前它已经在跑了，
                //   点击后不解除它 = 状态和点击前一致）。在这里 Cancel 反而等于
                //   "多点一下按钮就把既有黑洞保护摘掉" —— 那是净变坏。
                RollingLog.Info("session",
                    $"{why}，但点击前已有哨兵在跑 ⇒ 不解除，保持与点击前相同的保护");
            }
            else
            {
                // ★ 点击前<b>没有</b>保护 ⇒ 本次这个哨兵是白武装的，留着它到点会把
                //   <b>平台自己正在处理</b>的那条连接当成"隧道没起来"给断掉
                //   （<c>HealthMonitor</c> 的非终态出口正是这么写的，见其 :309-313）。
                HealthMonitor.Cancel();
                RollingLog.Info("session",
                    $"{why} ⇒ 当场解除健康哨兵（没有要保护的连接，不能让哨兵去断平台在途的会话）");
            }
        }

        /// <summary>断开主 profile（恢复网络的兜底手段），并解除哨兵。</summary>
        internal static async Task<string> DisconnectAsync()
        {
            HealthMonitor.Cancel(); // 手动断开即解除哨兵（连接已经不在了，没必要再探）
            RollingLog.Info("session", "用户发起断开（已解除健康哨兵）");
            return await VpnProfileSetup.DisconnectAsync();
        }

        /// <summary>
        /// ★【1.0.86 · Docs/36】是否有一次「切会话」正在进行。
        ///
        /// 【为什么前台要知道】MainComponent 的 3 秒轮询现在按<b>电平</b>回填
        ///   （隧道连着 A、账本写 B ⇒ 跟隧道改成 B→A）。切换动作本身有几秒钟的窗口：
        ///   账本已经改成 B，而旧会话 A 还没断干净 —— 这一刻轮询闯进来就会把账本改回 A，
        ///   把用户刚点的切换撤销掉（正是 1.0.72 那条"回填撤销用户切换"的坑换了个方向复发）。
        ///   ⇒ 切换期间轮询<b>跳过回填</b>，等动作自己给出结论。
        ///
        /// 用 <c>int</c> + <see cref="System.Threading.Interlocked"/> 而不是 <c>bool</c>：跨线程读写的可见性
        /// 与"重复进入不丢计数"都要靠它，口径同本仓其它跨线程标记。
        /// </summary>
        private static int _switchInFlight;

        /// <summary>切换是否正在进行（轮询读它，非 0 就跳过回填）。</summary>
        internal static bool SwitchInProgress =>
            System.Threading.Volatile.Read(ref _switchInFlight) != 0;

        /// <summary>
        /// ★【1.0.86 · Docs/36 A 案】把隧道会话切到目标订阅的 profile ——
        /// App 内「切订阅」必须走到这里，否则只是改了账本，隧道仍在旧订阅上
        /// （详见 <see cref="VpnProfileSetup.SwitchToSubscriptionAsync"/> 的方法注释）。
        ///
        /// 顺序上<b>不</b>在这里重连武装哨兵：底层切换成功时平台会拉起插件的
        /// <c>Connect</c>，而旧会话断开期间哨兵保持武装 —— 整机不会因为这次切换失去兜底。
        /// 失败路径（旧会话没收尾、新连接没成）由底层原样回话，这里只补一行会话级日志。
        /// </summary>
        internal static async Task<(bool Ok, string Message)> SwitchSubscriptionAsync(string targetSubId)
        {
            System.Threading.Interlocked.Increment(ref _switchInFlight);
            try
            {
                RollingLog.Info("session",
                    $"用户发起订阅切换（隧道换 profile）targetSubId={targetSubId}");
                (bool ok, string r) = await VpnProfileSetup.SwitchToSubscriptionAsync(targetSubId);
                RollingLog.Info("session", $"订阅切换结果 ok={ok}：{r}");
                return (ok, r);
            }
            finally
            {
                System.Threading.Interlocked.Decrement(ref _switchInFlight);
            }
        }

        /// <summary>
        /// 只读当前 profile 的连接状态文本（"Connected"/"Disconnected"/"Connecting"…）。
        ///
        /// ★★★ 这是修复「快速面板（系统 VPN 快速设置）点了连接、app 里没反应 / 没回调」的关键：
        /// 系统快速设置发起的连接走的是 <c>VpnManagementAgent.ConnectProfileAsync</c> → 平台
        /// → <c>IVpnPlugIn.Connect</c>（vpnClient 后台任务），<b>前台 app 完全不参与、也不被激活</b>。
        /// UWP 前台<b>没有</b>任何 VPN 连接状态变更事件（<c>VpnChannel.ActivityChange</c> 只在
        /// 插件进程内有效，前台订阅不到；<c>VpnManagementAgent</c> 也没有 StatusChanged 事件），
        /// 所以前台只能<b>主动轮询</b>这份状态来反映系统驱动的连接/断开。
        /// 本方法就是那个轮询读数入口，由 MainComponent 的 3 秒循环调用。
        /// </summary>
        internal static Task<string> StatusAsync() => VpnProfileSetup.OurStatusAsync();

        /// <summary>
        /// 当前<b>已连接</b>的订阅 id（空串 = 没连着 / 连的是基础 profile）。
        /// 由 MainComponent 的 3 秒轮询调用：快速面板切了订阅，前台要能自己跟上。
        /// </summary>
        internal static Task<string> ConnectedSubAsync() => VpnProfileSetup.ConnectedSubIdAsync();

        /// <summary>
        /// ★【快速面板改造】同时处于 Connected 的我们的 profile 清单（≥2 条才有内容）。
        /// 只读观测：平台对"同插件两条 profile 并行"没有实测背书，先留证据不动手
        /// （详见 <see cref="VpnProfileSetup.ParallelConnectedReportAsync"/>）。
        /// </summary>
        internal static Task<string> ParallelConnectedAsync() =>
            VpnProfileSetup.ParallelConnectedReportAsync();
    }
}
