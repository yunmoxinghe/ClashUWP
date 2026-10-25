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
        /// 这次连接<b>实际</b>会用到哪个订阅 profile：互斥形状（<c>panelMultiEntry=0</c>）下
        /// 面板里只有基础条目，连隐藏的订阅 profile 会让隧道在跑而面板永远 Disconnected，
        /// 所以订阅 id 一律归零 —— 基础 profile 读的就是当前生效那份 <c>config.yaml</c>，效果等价。
        /// ★ 这条规则只写这一份：<see cref="ProfileGate"/> 的闸门标签也来读它，
        ///   两处各写一遍迟早一处改了另一处没改（标签与真实形状不一致就等于判据说谎）。
        /// </summary>
        internal static string? EffectiveSubId(string? subId) => AppSettings.PanelMultiEntry ? subId : null;

        /// <summary>
        /// 连接主 profile（全路由）。
        /// ★ 先武装健康哨兵再连接：拨号悬挂会由非终态出口断开；已 Connected 的健康会话
        ///   不再因固定时长被强拆。
        /// ★【1.0.62】<paramref name="subId"/> 非空时连<b>该订阅专属</b>的系统 profile ——
        ///   与用户在快速设置里点那条订阅走的是<b>完全同一条</b>平台路径
        ///   （ConnectProfileAsync → 平台 → IVpnPlugIn.Connect），差别只是发起者是我们。
        ///   快速面板本身没法自动化点（系统 UI），这条入口就是它的自动化等价物：
        ///   同一 profile、同一 CustomConfiguration、同一插件读配置逻辑。
        ///   哨兵照常武装 —— 订阅 profile 同样是 0.0.0.0/0 全路由。
        /// </summary>
        internal static async Task<string> ConnectAsync(string? subId = null)
        {
            // ★【1.0.117】panelMultiEntry=0 时一律连基础 profile（面板可见的那条）。
            //   切换三步式的"再重连"也走这里，必须遵循同一条规则，
            //   否则隧道连在隐藏条目上、面板永远 Disconnected。
            subId = EffectiveSubId(subId);

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
        /// ★【1.0.107 · Docs/54】3 秒轮询读到 Connected 而前台还没有哨兵 ⇒ <b>认领</b>这个会话。
        ///
        /// 【为什么要有这条】Docs/53 §五.⑥.2 的暴露面：哨兵原先只由"本前台发起连接"的两条路
        ///   （按钮 / debug-cmd）武装，快速面板点连接、平台自发重拨、臂 C 无头会话之后重开 app ——
        ///   这三种形状的活会话上前台<b>没有任何黑洞兜底</b>。轮询本来就是唯一能看到
        ///   这些会话的观察者（<see cref="StatusAsync"/> 的注释），让它顺手把保护补上。
        ///
        /// 【为什么安全】判据是只读的状态文本；<see cref="HealthMonitor.AdoptArm"/> 认领模式
        ///   认领模式不拆健康隧道，收尾全靠判据（探针 / 重拨保险丝 / 物理兜底 /
        ///   会话消失出口）。已有哨兵时是空操作 —— Arm 语义是"顶掉再武装"，
        ///   重复认领不应重置既有保护的观测基线。
        /// </summary>
        internal static void AdoptLiveSessionIfUnprotected()
        {
            if (HealthMonitor.IsActive) return;
            HealthMonitor.AdoptArm();
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
        /// ★★【1.0.109 · 用户指令「更换订阅遵循先断开、后修改、再重连」】切订阅这条路的<b>顺序本身</b>。
        ///
        /// 【三段各是什么】
        ///   ① <b>先断开</b> —— <see cref="VpnProfileSetup.TearDownSessionForSwitchAsync"/>：
        ///      只拆会话、不删条目，等官方那把尺子确认收干净；
        ///   ② <b>后修改</b> —— 调用方交进来的 <paramref name="modifyStep"/>
        ///      （<c>SubscriptionManager.ActivateAsync</c>：写 config.yaml + 记生效 + 热重载 + 对账）。
        ///      它跑的这一刻<b>台上没有会话</b>，所以 profile 存储的 Add/Update 不再被写入闸欠下来
        ///      （Docs/52 那套"欠账等边沿"仍然留着，只是切订阅这条路基本用不上它了）；
        ///      ★ 配平的一处：<c>ProveStoreWriteSafeAsync</c> 原先那条判据 ③「切换在飞即不写」
        ///      问的是"旧会话未必收尾"，而①已经把这件事证实了 —— 留着它等于让②自己排队等边沿，
        ///      本批删掉，细则与理由见 Docs/55 §三.⑤。
        ///   ③ <b>再重连</b> —— 只有①真的拆掉过会话才拨，走 <see cref="ConnectAsync"/>：
        ///      与点「连接 VPN」完全同一条路径（同一个哨兵纪律、同一套中毒复位档位）。
        ///      ① 回 <c>NoSession</c>/<c>AlreadyTarget</c> 时<b>不拨</b> ——
        ///      "用户根本没连接，切个订阅不该凭空拨出一条隧道"是已验收口径（空壳不拨号）。
        ///      ★【1.0.110 · Docs/55 §八】① 还会回 <c>SessionStateUnknown</c>（有条目读数不可信、
        ///      又无法从网络栈指认出会话）⇒ 同样<b>不拨</b>：这一刻"有没有会话"是不知道的，
        ///      凭空拨一条等于在可能的活会话上再叠一次连接（官方 <c>AlreadyConnected</c> 那个坑），
        ///      而用户要的隧道由他自己在面板/按钮上发（那条路有中毒复位档 <c>ConnectWithPoisonRepairAsync</c> 兜）。
        ///      回话里 ① 那一行会写清"没拆隧道、也没重拨"，不许读成"切换后已重连"。
        ///      ★【1.0.113 · Docs/58 · 用户指令"断开系统侧连接再重建"】<c>SessionStateUnknown</c> 这一档
        ///      现在<b>先逐条发断开并等读数恢复</b>才决定（<see cref="VpnProfileSetup.TearDownSessionForSwitchAsync"/>）：
        ///      任一条恢复就升格成 <c>TornDown</c> 走③重拨，全不恢复才留在"不拨"。
        ///      ★【1.0.114 · Docs/59 · 用户指令"读不出来就都删了重新创建"】再往下还有一手：
        ///      <b>整组删旧建新</b>（同「建/重建 profile」按钮那条路），建完复核<b>新</b>清单 ——
        ///      全干净 ⇒ 升格 <c>TornDown</c>（走③重拨）；显形出会话态 ⇒ 改判 <c>Refused</c>（整段停在未修改）；
        ///      仍读不出 ⇒ 才真的留在 <c>SessionStateUnknown</c>，回话里带"删建已试过"与出口=重启。
        ///      ★ 返回值第三个 <c>Redialed</c> 是给调用方判"新配置到底靠什么生效"用的：
        ///      隧道已经重建过一次 ⇒ 内核加载的就是目标订阅（插件侧 <c>配置源=subscriptions/&lt;subId&gt;.yaml</c>），
        ///      ②里那次原地热重载降格成补充信息，不再能单独把整次切换判成失败。
        ///
        /// 【① 拆不干净就整段停手】<see cref="VpnProfileSetup.SwitchSeat.Refused"/> 时账本、正文、
        ///   profile 存储<b>一个字都不改</b>，也不进③：那一刻写存储正是本批要消灭的形状，
        ///   而先改账本会造出 Docs/36 §3.1 那个"生效订阅是新的一条、隧道还在旧的一条"的谎报。
        ///   回话必须人话在前、把平台侧出口（系统设置→VPN / <c>ncpa.cpl</c>）带出来，
        ///   口径同 Docs/51 §七.9（1.0.99 那次把判据顶在最前，界面截断后用户看不见出口）。
        ///
        /// 【为什么整个动作包在一个作用域里】轮询的回填与"边沿/补账"对账都读
        ///   <see cref="SwitchInProgress"/> 让路；这个窗口从前的三行代码变成十几秒
        ///   （等收尾最多 <c>GracefulTeardown.WaitSeconds</c>=20 s），让路纪律反而更要紧。
        ///   ★【1.0.114】① 里那一支"断开没治好就整组删建"会把窗口再拉长一档（坏条目数×15 s +
        ///   一轮删+Add，平台不答时各占 <see cref="ProfileStore"/> 一次写预算）⇒ 它是<b>只在异常支上</b>
        ///   才走的路，正常切换的耗时不受影响；装机要盯的就是 <c>profile-switch</c> 与 <c>profile-store</c> 这两类行的时间差。
        ///   计数必须由 <c>finally</c> 归零 —— ②③ 里任何一处抛穿都不能把标记留成"永远在切"。
        /// </summary>
        internal static async Task<(bool Ok, string Message, bool Redialed)> SwitchSubscriptionAsync(
            string targetSubId, Func<Task<(bool Ok, string Message)>> modifyStep)
        {
            System.Threading.Interlocked.Increment(ref _switchInFlight);
            try
            {
                RollingLog.Info("session",
                    $"用户发起订阅切换（先断开→后修改→再重连）targetSubId={targetSubId}");

                (VpnProfileSetup.SwitchSeat seat, string tearLine) =
                    await VpnProfileSetup.TearDownSessionForSwitchAsync(targetSubId);
                if (seat == VpnProfileSetup.SwitchSeat.Refused)
                {
                    string stop =
                        $"这次切不过去：{tearLine} ⇒ 旧会话还在，本应用不在会话期间改配置或写 VPN 条目，" +
                        "隧道与账本都保持原样。请等这条落地，或打开 系统设置→VPN（或 ncpa.cpl）把它断开后再点一次。" +
                        $"内部判据：{tearLine}";
                    RollingLog.Warn("session", $"订阅切换结果 ok=False：{stop}");
                    return (false, stop, false);
                }

                (bool modOk, string modMsg) = await modifyStep();
                if (!modOk)
                {
                    string stop = $"后修改未完成：{modMsg}";
                    RollingLog.Warn("session", $"订阅切换结果 ok=False：{tearLine}；{stop}");
                    return (false, $"{tearLine}；{stop}", false);
                }

                if (seat != VpnProfileSetup.SwitchSeat.TornDown)
                {
                    string idle = $"{tearLine}；{modMsg}";
                    RollingLog.Info("session", $"订阅切换结果 ok=True：{idle}");
                    return (true, idle, false);
                }

                string dial = await ConnectAsync(targetSubId);
                bool dialed = !VpnProfileSetup.IsNoDialResult(dial);
                string msg = $"{tearLine}；{modMsg}；再重连={dial}";
                RollingLog.Info("session", $"订阅切换结果 ok={dialed}：{msg}");
                return (dialed, msg, dialed);
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
