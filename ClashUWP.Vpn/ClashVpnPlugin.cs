using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Networking.Vpn;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// S4~S7 · <c>IVpnPlugIn</c> 实现。
    ///
    /// 【官方】<c>IVpnPlugIn</c>：<i>"Provides an interface to implement specific third party
    /// VPN client operations."</i> 五个方法：Connect / Disconnect / Encapsulate /
    /// Decapsulate / GetKeepAlivePayload。
    ///
    /// 骨架期这里是个「假内核」：只把 ICMP echo request 原样应答回去，
    /// 用来证明 VpnChannel 链路通（详见 <see cref="IpPacket"/> 的说明）。
    /// 真内核（mihomo dll）接进来时，只需要替换 <see cref="Encapsulate"/> 里
    /// 那句 <c>TryBuildEchoReply</c> 与它的注入路径。
    ///
    /// ★ 与 sample 最大的结构差异：
    ///   sample 是「Encapsulate 封装 → 走 outer tunnel transport 发给服务器 →
    ///   Decapsulate 从 transport 收 → 解出 IP 包」。
    ///   ClashUWP 的数据面<b>不走 outer transport</b> —— 真内核在进程内，
    ///   拿到包就地处理，回包用 <c>AppendVpnReceivePacketBuffer</c> 主动注入。
    ///   所以 Decapsulate 在正常运行时不会被调用（这里只留接口要求的空实现）。
    ///
    /// ★ 为什么是 <c>internal</c> 而不是 public：
    ///   本工程是 CsWinRT 组件工程，<b>所有 public 类型都会被投影进 winmd</b>，
    ///   因此必须全部可 WinRT 投影。ClashVpnPlugin 只是被 VpnBackgroundTask 内部
    ///   new 出来、再原样交给 VpnChannel.ProcessEventAsync 的 —— 平台不需要激活它，
    ///   设成 internal 就不会被投影，也就不受投影规则约束。
    ///   整个工程里唯一需要 public 的只有 VpnBackgroundTask。
    /// </summary>
    internal sealed partial class ClashVpnPlugin : IVpnPlugIn
    {
        /// <summary>日志节流：Encapsulate 是按包回调的，逐包写日志会淹掉系统日志。</summary>
        private const uint LogEveryNPackets = 200;

        private VpnPluginConfig _config = VpnPluginConfig.Parse(null);
        private DatagramSocket? _transport;
        private bool _started;

        // ── 路线 A：mihomo 进程内数据面 ──────────────────────────────────
        // ★ 这里<b>不留</b>任何"本实例的桥在不在跑"的字段：桥与回程泵都是<b>进程级</b>的
        //   （Go 侧 Default 是单例），实例字段必然与真相脱钩。旧的 <c>_bridgeRunning</c>
        //   只被赋值、从没被读过（CS0414），留在代码里就是一个"看起来像状态、其实没人信"
        //   的诱饵 —— 1.0.34 起删掉，状态一律以 <see cref="s_bridgeGlobal"/> 为准。
        /// <summary>bridge 启动诊断（成功/失败原因），Disconnect 时随快照落盘。</summary>
        private string _bridgeDiag = "未启动";

        // ★ 回程泵与 mihomo 桥都是<b>进程级</b>的（Go 侧 Default 是单例），而
        //   VpnBackgroundTask 每次连接事件可能 new 一个新的 ClashVpnPlugin 实例 ——
        //   泵的状态如果放实例字段，就会出现「旧实例的泵还挂在已 Stop 的 channel 上、
        //   新实例以为桥没跑却撞上 rc=-2 already started」的鬼状态
        //   （2026-10-14 23:44 日志实测：自愈式复现，随后每次连接都 rc=-2）。
        /// <summary>进程级回程泵的取消源；Attach 时替换旧泵，StopBridge 时置空。</summary>
        private static CancellationTokenSource? s_pumpCts;
        /// <summary>进程级桥是否处于可收发状态。Encapsulate 以它为准决定是否喂包。</summary>
        private static volatile bool s_bridgeGlobal;
        /// <summary>串行化 start/stop/pump 切换，防 Connect 与 Disconnect 交错。</summary>
        private static readonly object s_bridgeGate = new();
        /// <summary>最近一次桥绑定的物理网卡快照（桥停机后 bound_iface 会清零，快照供诊断）。</summary>
        private static string s_boundIfaceSnapshot = "";
        /// <summary>Start 成功后由静态字段持住的活动 channel：给 outerTunnelTransport
        /// 一个 GC 根，防止桥启动阻塞期间唯一托管 socket 的它被回收终结（0x800703E3 的成因）。</summary>
        private static VpnChannel? s_activeChannel;
        /// <summary>进程级桥是否刻意留在跑动状态（内核常驻策略，见 Disconnect 的注释）。</summary>
        private static bool s_bridgeResident;
        /// <summary>上一次连接用的内核配置，用于判断订阅是否变了（变了才热重载）。</summary>
        private static string s_lastConfigFingerprint = "";

        // ── 数据面取证采样（黑洞定位用）────────────────────────────────────
        // 每个方向只留前 MaxTunSamples 个包的头部摘要，写进 LocalState\tun-sample.txt。
        // 存在理由：22:12 那次实测「共处理 7188 包 / 注入 8705」但桌面 curl 全 000，
        // 光有计数分不清「包没进内核」「内核没回」「回了但注入不被协议栈接受」这三种病，
        // 而这三种病的修法完全不同。计数只能证明「有东西在动」，不能证明「动对了」。
        private const int MaxTunSamples = 12;
        private static readonly object s_sampleGate = new();
        private static readonly List<string> s_outSamples = new();
        private static readonly List<string> s_inSamples = new();
        private static int s_sampleWritten;

        private static void AddSample(List<string> list, string line)
        {
            lock (s_sampleGate)
            {
                if (list.Count < MaxTunSamples) list.Add(line);
            }
        }

        /// <summary>把两侧采样落盘（fire-and-forget；写不了就不写）。</summary>
        private static void WriteTunSamples(string tag)
        {
            string outText, inText;
            lock (s_sampleGate)
            {
                outText = string.Join("\r\n", s_outSamples);
                inText = string.Join("\r\n", s_inSamples);
            }
            string content =
                $"Time={DateTime.Now:yyyy-MM-dd HH:mm:ss} tag={tag}\r\n" +
                $"--- 本机 → 内核（Encapsulate 收到的包）---\r\n{outText}\r\n" +
                $"--- 内核 → 本机（回程泵注入的包）---\r\n{inText}\r\n";
            _ = Task.Run(async () =>
            {
                try
                {
                    var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                    var file = await folder.CreateFileAsync(
                        "tun-sample.txt", Windows.Storage.CreationCollisionOption.ReplaceExisting);
                    await Windows.Storage.FileIO.WriteTextAsync(file, content);
                }
                catch { /* 诊断文件写不了就算了 */ }
            });
        }

        /// <summary>本次连接下发的节点服务器排除路由（<c>/32</c>），对齐安卓 protect()。</summary>
        private List<string> _nodeExclusions = new();

        /// <summary>本次 <c>BuildSelfSplitFilter()</c> 的实际形态，写进诊断快照用
        /// （臂号 / PFN / AllowOutbound / 构造失败原因）。没开就是 arm=0。</summary>
        private string _selfSplitFilterInfo = "arm=0 未调用";

        /// <summary>本次实际使用的 transport 外层目标（auto 解析后的结果），写快照与日志用。</summary>
        private string _outerTargetUsed = "";

        private uint _encapsulatedPackets;
        private uint _echoReplies;
        private uint _unknownPackets;
        private uint _injectedPackets;
        private uint _nonUnicastDropped;
        /// <summary><c>Encapsulate</c> 里归还给平台 <b>send 池</b>的 buffer 条数。
        /// ★ 数据面生死判据：它必须与 <c>encap</c> 同步增长。两者一旦分叉，
        ///   就是"包进来了但 buffer 没还"，池子借空之后平台不再交付出站包（run 32 定性）。
        ///   旧实现这个数恒为 0。</summary>
        private uint _sendBuffersReturned;
        /// <summary><b>档 3</b> 的口径：buffer 从未离开 <c>packets</c> 列表，
        /// <c>Encapsulate</c> 返回时由平台按官方 remarks "<i>will clean up the remaining
        /// IVpnPacketBuffer objects in packets</i>" 自己回收。这个数记的是"交给平台回收"的条数，
        /// 与 <c>encap</c> 同步即说明每个包都被处理过且没有一条被我们扣在手里。</summary>
        private int _sendBuffersLeft;
        /// <summary>归还失败（抛异常）的次数。<c>0x8007045B</c> 这类会在这里现形。</summary>
        private uint _sendReturnFailures;
        /// <summary><c>FlushVpnSendPacketBuffers</c> 调用次数（仅档 2 会涨；用来核对"送去 transport"的代价）。</summary>
        private uint _sendBuffersFlushed;
        /// <summary>首个归还失败的原文，只记一次，写诊断快照用。</summary>
        private string _sendReturnFirstError = "";
        /// <summary>本次生效的归还档位（<c>settings.txt: encapReturn</c>），写快照用。</summary>
        private string _encapReturnMode = "";
        /// <summary>★ run 34 的盲区：<c>encap=0</c> 有两种完全不同的死法 ——
        ///   平台<b>压根没回调 Encapsulate</b>，还是<b>回调了但一条包都没从列表里取出来</b>
        ///   （档 3 只能 <c>foreach</c>，而投影出来的 <c>VpnPacketBufferList</c> 没有 GetAt/索引器）。
        ///   这个调用次数把两者分开，是"归因"而不是"判据"，所以放在最前面、不受分支影响。</summary>
        private uint _encapsulateCalls;
        /// <summary>每次进 <c>Encapsulate</c> 时看到的 <c>packets.Size</c> 累计值。
        /// 与 <c>encapsulateCalls</c> 一起回答"平台到底交给我们多少个包"。</summary>
        private uint _encapListItemsSeen;
        /// <summary>单次回调里见过的最大 <c>packets.Size</c>（批量形态）。</summary>
        private uint _encapListSizeMax;
        /// <summary>档 3 里 <c>foreach</c> 真正枚举出来的条数。
        /// ★ 若 <c>seen&gt;0</c> 而这一列为 0 ⇒ 枚举器是空的，档 3 的实现方式作废。</summary>
        private uint _encapEnumerated;
        /// <summary>档 3 枚举抛出的首个异常原文，只记一次。
        /// <c>E_NOINTERFACE</c>/<c>InvalidCastException</c> 这类投影问题会在这里现形，
        /// 而不是把一个异常从平台同步回调里抛出去拆掉整个会话。</summary>
        private string _encapEnumFirstError = "";
        /// <summary>★ <b>档 4</b> 的口径：buffer 被 <c>RemoveAtBegin</c> 摘走、就地处理完，
        ///   又用 <c>packets.Append()</c> <b>塞回原列表</b>，返回时由平台按 remarks
        ///   "<i>will clean up the remaining IVpnPacketBuffer objects in packets</i>" 回收。
        ///   这一列记"塞回去"的条数：与 <c>encap</c> 同步 ⇒ 档 4 的写法成立；
        ///   平台真回收的证据是<b>长跑越过 7188 不停摆</b>（send 池不再见底）。</summary>
        private uint _sendBuffersReappended;
        /// <summary><c>Decapsulate</c> 被平台回调的次数（Docs/09 O1）。
        /// ★ 本架构不走 outer transport，推理上恒为 0 —— 但"推理不会被调用"不是证据，
        ///   空实现要是真被调了就是漏收数据。记下来才能证伪。</summary>
        private uint _decapsulateCalls;
        /// <summary>首个 <c>Decapsulate</c> 回调的形状（包长），只记一次，进日志与快照。</summary>
        private string _decapFirstInfo = "";
        /// <summary>已 <c>AppendVpnReceivePacketBuffer</c>、<b>还没 Flush</b> 的 receive buffer 条数
        ///   —— receive 侧"在借未还"的直接读数（Docs/09 O2，与 send 侧 <c>_sendBuffersLeft</c> 对称）。
        ///   ★ <b>静态</b>口径：注入路径（<see cref="InjectAppend"/> / <see cref="FlushReceive"/>）是
        ///     static 方法，泵线程与 Encapsulate 回落的 echo 注入<b>两个线程</b>都会写 ⇒
        ///     一律走 <c>Interlocked</c>。
        ///     这一列要是单调上涨不回落，就是 run 32 那个借空池子的病在 receive 侧重演。</summary>
        private static int s_receiveBuffersPending;
        /// <summary>经 <c>FlushVpnReceivePacketBuffers</c> 真正提交给协议栈的 receive buffer 条数。
        /// 与 <c>inj</c> + <c>big</c>（超限丢弃）对齐才对得上账。</summary>
        private static int s_receiveBuffersFlushed;
        /// <summary><c>GetKeepAlivePayload</c> 里借了 Send 池 buffer 却<b>没能</b>经出参交回平台的次数
        ///   （异常路径）。这类 buffer 必须主动归还，否则就是逐次泄漏 —— 与 7188 同一条病根（Docs/09 A2）。</summary>
        private uint _keepAliveOrphanBuffers;
        /// <summary>★ <c>Encapsulate</c> 内部被外层守卫<b>兜住</b>的异常次数（1.0.34）。
        ///   这一列是"平台不再交包"这类静默死的<b>前置</b>信号：以前异常一漏出去就再也看不到数，
        ///   现在它被记在这里 + <c>err</c> 原文，会话还能继续跑。</summary>
        private int _encapsulateErrors;
        /// <summary>首个被兜住的 <c>Encapsulate</c> 异常原文，只记一次。</summary>
        private string _encapsulateFirstError = "";
        /// <summary>IP 头校验和被本机留空/算错、由插件补齐的包数。
        /// ★ 黑洞主因的量化观测点：这个数非 0 就说明「内核在验校验和、包验不过」这条路成立。</summary>
        private uint _ipChecksumFixed;
        /// <summary>传输层（TCP/UDP）校验和补齐的包数。</summary>
        private uint _transportChecksumFixed;
        /// <summary>首个补算事件是否已写进系统日志（只写一次，避免逐包刷屏）。</summary>
        private int _checksumFixLogged;
        /// <summary>回程包大于 Receive 池容量（=mtuSize）而被丢的条数。
        /// 黑洞排查必读：内核在回，但包太大投不进去，和「内核没回」是两种病。</summary>
        private static int s_oversizedPackets;

        /// <summary>回程注入失败（借不到 receive buffer / 归还抛异常）的次数。
        /// ★ 静态口径：跨会话累计，与 <c>s_oversizedPackets</c> 一致。
        ///   这个数非 0 而泵还在跑，就是"receive 池也在被借空"的信号 —— run 32 定性的
        ///   send 侧 7188 泄漏若在 receive 侧重演，先在这里现形，而不是等整机黑洞。</summary>
        private static int s_injectFailures;

        /// <summary>首个注入失败的原文（只记一次，避免逐包刷日志）。</summary>
        private static string s_injectFirstError = "";

        /// <summary>注入失败后的背压休眠。不睡眠会在池空时逐包抛异常热转；
        /// 直接退出泵又会把回程永久断掉 —— 那个形状和黑洞无法区分。</summary>
        private const int InjectBackoffMs = 20;

        /// <summary>平台向本插件索取 keep-alive 包的次数。
        /// ★ 这是<b>唯一</b>能测出"平台自己多久判一次隧道活着"的观测点：
        ///   run 26 之后发现的"连上约 96 s 必被拆（RasClient reason 828）+ 系统无限自动重拨"
        ///   要么由 keep-alive 周期/失败触发，要么与它无关，必须用计数与间隔区分，不许猜。</summary>
        private uint _keepAlives;

        /// <summary>上一次 keep-alive 回调的时刻（Environment.TickCount64），用来算平台节奏。</summary>
        private long _lastKeepAliveTick;

        /// <summary>本次会话 Start 成功的时刻，Disconnect 时换算成"活了多久"。</summary>
        private long _sessionStartTick;

        /// <summary>
        /// 本次 <c>Connect</c> 被叫起来的墙上时间。看门狗用它筛
        /// <c>dataface-sessions.txt</c> 里的 <c>evt=connect-begin</c>：
        /// 这个时刻之后数到的次数减 1 就是"非本次发起"的重拨次数（判据二）。
        /// 必须用墙上时间而不是 <c>TickCount64</c> —— 日志行里记的就是墙上时间，
        /// 而且跨进程比较只有绝对时间说得通。
        /// </summary>
        private DateTime _connectBeganAt;

        /// <summary>
        /// 进程级 <c>Connect</c> 调用次数（静态：跨会话累加，因为常驻内核让同一个
        /// 后台任务进程可以被平台重拨复用）。
        /// </summary>
        private static uint s_connectCalls;

        /// <summary>给日志用的只读视图（<see cref="RollingLog"/> 在后台任务入口就要写行，拿不到实例）。</summary>
        internal static uint ConnectCalls => s_connectCalls;

        /// <summary>
        /// 会话生命周期日志：追加进 <c>LocalState\dataface-sessions.txt</c>，一行一个事件。
        ///
        /// 【为什么单独一个文件】<c>dataface-plugin.txt</c> 是"最后一次 Disconnect 的快照"，
        ///   每次覆盖。而 run 29 剩下的那个现象需要的恰恰是<b>序列</b>：
        ///   黑洞 transport（192.0.2.1:9）下 29-B/29-C 都在 <b>~70 s</b> 出现一次
        ///   RasClient 828，紧接着 SYSTEM 重拨、第二段会话反而稳定活到哨兵上限
        ///   （29-B：23:44:11 连 → 23:45:25 828 → 23:45:27 再连 → 23:48:24 631；
        ///    29-C：23:49:04 连 → 23:50:13 828 → 23:50:15 再连 → 23:53:07 631）。
        ///   要区分"平台拆通道后有没有回调 Disconnect""重拨落在同进程还是新 pid"
        ///   "第二次 Connect 是否复用了常驻内核"，只有追加日志答得了。
        ///
        /// 【★ 行格式是契约，不是风格】插件 <c>SessionWatchdog</c> 与前台
        ///   <c>HealthMonitor</c> 都按<b>定长 19 字符</b>时间戳解析这个文件，
        ///   再数 <c>evt=connect-begin</c> 当"平台自动重拨"保险丝。
        ///   所以写行必须走 <see cref="RollingLog.Sessions"/>（它保证无毫秒、
        ///   带 pid 与 connectCalls 前缀），而不是自由文本日志那条汇。
        ///
        /// 【1.0.23 改的是什么】旧实现每行都 <c>_ = Task.Run(AppendTextAsync)</c>：
        ///   并发追加 ⇒ 行序不保证，文件不封顶 ⇒ 无上限地长。
        ///   现在交给 <see cref="RollingLog"/>：入队即返回（平台回调不阻塞）、
        ///   单线程按序落盘、按 512 KB×3 滚动。Disconnect 是平台回调，
        ///   "fire-and-forget + 失败静默"这条写法则原样保留在 RollingLog 里。
        /// </summary>
        internal static void LogSession(string evt) => RollingLog.Sessions(s_connectCalls, evt);

        // ────────────────────────────────────────────────────────────────────
        // S4 · Connect
        // 【官方】"instructs the VPN plug-in to connect to the VPN server and
        //          establish the VPN channel."
        // ────────────────────────────────────────────────────────────────────
        public void Connect(VpnChannel channel)
        {
            // 步 0 · ★ <b>AssociateTransport 是第一个平台 API 调用</b>（1.0.34 前置，对齐官方条文）。
            //   【官方】AssociateTransport(Object, Object)："Prepares and marks the transport
            //     for use by the VPN framework ... <b>This call must be performed before any other
            //     operations related to the VPN framework APIs.</b>"
            //   旧代码把它排在 <c>LogDiagnosticMessage</c> / <c>channel.Id</c> /
            //   <c>channel.Configuration.CustomField</c> 之后（步 4）—— 那三个都算"与 VPN
            //   framework API 相关的操作"，条文上是<b>越序</b>的。它跟 socket 连没连、连到哪里
            //   无关（关联的只是对象），所以完全能提前；外层目标照旧在 ConnectAsync 之前算完。
            //   ⚠️ 这里一直是嫌疑位：transport 过去被硬编码"连"到 127.0.0.1（run 14/16/20/21 的靶子），
            //      但那是"连到哪里"的问题，与"何时关联"无关，两件事分开修。
            //   ★ 绝不把这里的异常漏出去：漏出 Connect ⇒ 平台既不回调 Disconnect、
            //     也不清它自己下发的策略（run 34 实证），所以兜住后走官方失败出口。
            try
            {
                // 为什么还建 socket：Start 形参要求一个 DatagramSocket / StreamSocket
                // 【官方】："can be a DatagramSocket or a StreamSocket"。
                // 但 ClashUWP 不用它传数据（数据面走进程内内核 + 主动注入），
                // 它只是满足平台「transport 已连接」契约的形式占位：用 UDP ConnectAsync
                // （不握手、不发包），见下方步 4 的实现。
                _transport = new DatagramSocket();
                channel.AssociateTransport(_transport, null);
            }
            catch (Exception ex)
            {
                LogSession($"evt=connect-abort stage=associate-transport err={ex.GetType().Name}: {ex.Message}");
                RollingLog.Error("connect-abort", "AssociateTransport 失败，本次连接已终止", ex);
                try { channel.LogDiagnosticMessage("ClashUWP: AssociateTransport 失败，终止本次连接: " + ex); }
                catch { /* 日志出口偶尔不可用 */ }
                AbortConnectionAttempt(channel, "AssociateTransport failed");
                return;
            }

            // 步 1 · 日志。【官方】LogDiagnosticMessage(String)："Writes a diagnostic message
            //        to the system log." —— 这是插件<b>唯一</b>的官方日志出口。
            channel.LogDiagnosticMessage("ClashUWP: 进入 Connect.");

            // ★ 会话日志第一行：谁在第几次被叫起来。connectCalls>1 且 pid 相同 ⇒ 平台在复用本进程重拨。
            Interlocked.Increment(ref s_connectCalls);
            _connectBeganAt = DateTime.Now;
            LogSession($"evt=connect-begin channel={channel.Id} " +
                       $"resident={(s_bridgeResident ? "复用常驻内核" : "全新内核")}");
            RollingLog.Info("connect", $"平台发起连接 #{s_connectCalls} " +
                       $"resident={(s_bridgeResident ? "复用常驻内核" : "全新内核")} pid={Environment.ProcessId}");

            // 每次会话重新采样（进程级列表，不清就会拿上一轮的包糊这一轮的结论）。
            lock (s_sampleGate) { s_outSamples.Clear(); s_inSamples.Clear(); }
            Interlocked.Exchange(ref s_sampleWritten, 0);


            // 步 2 · 通道 Id。【官方】："unique instance ID of a VPN channel ... can be used
            //        to simplify demuxing operations between two VPN connection instances."
            channel.LogDiagnosticMessage("ClashUWP: Channel Id = " + channel.Id);

            // 步 3 · 读自己定义那段 XML。
            //
            // ⚠️ 名字不对称，这里是最大的一个坑：
            //    前台<b>写</b>的时候属性叫 <c>VpnPlugInProfile.CustomConfiguration</c>；
            //    插件<b>读</b>的时候却叫 <c>VpnChannelConfiguration.CustomField</c>。
            //    官方 sample 是这么取的（CustomConfiguration.cs:
            //    "if (!String.IsNullOrWhiteSpace(cfg.CustomField)) doc.LoadXml(cfg.CustomField);"），
            //    照 profile 侧的名字读会直接编译不过（CS1061）。
            //   ★ 1.0.34：这一步包进 try。<c>channel.Configuration</c> 是平台对象，
            //     取它/解析它一旦抛异常，原来是<b>直接从 Connect 漏出去</b>的 ——
            //     那时隧道还没建（Start 都还没调），平台却不一定会替我们收尾；
            //     漏异常的真实代价在 run 34 量过：平台此后不再交包，数据面静默死掉。
            try
            {
                _config = VpnPluginConfig.Parse(channel.Configuration.CustomField);
            }
            catch (Exception ex)
            {
                LogSession($"evt=connect-abort stage=read-config err={ex.GetType().Name}: {ex.Message}");
                try { channel.LogDiagnosticMessage("ClashUWP: 读 profile 配置失败，终止本次连接: " + ex); }
                catch { /* 日志出口偶尔不可用 */ }
                AbortConnectionAttempt(channel, "read CustomField failed");
                return;
            }

            channel.LogDiagnosticMessage(
                $"ClashUWP: mtu={_config.Mtu} maxFrame={_config.MaxFrame} ip={_config.ClientIpV4}");

            // 步 4 · 读插件开关 + 解析外层目标，然后把 transport <b>连</b>上。
            //   ★ AssociateTransport 已经在步 0 抢先做了（官方条文要求它排在所有平台 API 操作之前），
            //     这里只剩"连到哪里"：外层目标由 settings + 订阅共同决定，必须在这一步才知道。
            //   顺序仍是 AssociateTransport → ConnectAsync → Start，与官方 sample 一致
            //     （sample 也是先 ConnectAsync(server) 再 Start；V2 条文那句
            //      "The socket must be unconnected at the point of the call" 的矛盾见 Docs/09 §3.1）。
            LocalSettings.Load();
            (string rawConfig, string? rawConfigPath) = LoadMihomoConfig();

            // ★ 1.0.23 步 4.1：把订阅/前台 YAML 改成本插件能跑通数据面的形态
            //   （dns.enhanced-mode: fake-ip + 兜底 tun），并落成 config.kernel.yaml。
            //   热重载走的是<b>磁盘上那份文件</b>（PUT /configs 的 JSON body path），所以必须让它指向
            //   改写后的文件，否则常驻热重载会按订阅原文把 fake-ip 覆盖回 redir-host
            //   —— run 24 是靠 hotReload=0 才压住这个覆盖的，产品形态不能依赖它。
            string mihomoConfig = LocalSettings.FakeIp ? KernelConfig.Transform(rawConfig) : rawConfig;
            string? mihomoConfigPath = LocalSettings.FakeIp
                ? (KernelConfig.WriteEffectiveFile(mihomoConfig) ?? rawConfigPath)
                : rawConfigPath;

            // 步 4.2 · ★ 先算节点排除路由，再定外层目标。
            //   顺序是 run 21 三臂的直接产物：平台按<b>外层 transport 会走哪条路</b>
            //   安装 Ipv4ExclusionRoutes 的接口（127.0.0.1 ⇒ Loopback ifIdx=1 黑洞；
            //   网关 / 节点 IP ⇒ WLAN ifIdx=6），所以"外层连到节点 IP"既是最省事的
            //   自动选法，也要求节点列表在这之前就算好。
            //   NodeRoutes 的域名解析本来也必须在隧道起来之前做（缓存注释同义），提前无副作用。
            _nodeExclusions = NodeRoutes.BuildExclusionCidrs(mihomoConfig);
            string outerTarget = ResolveOuterTarget(LocalSettings.OuterTarget);
            _outerTargetUsed = outerTarget;

            // （transport 对象与 AssociateTransport 都在步 0 做完了，这里只负责"连到哪里"。）
            try
            {
                // ★ transport 必须在 AssociateTransport（官方要求此时 unconnected）之后、
                //   Start 之前进入「已连接」状态 —— 官方 sample 正是 ConnectAsync(server)
                //   之后才 Start。之前这里只 BindServiceNameAsync（bind ≠ connect），
                //   传给 Start 的仍是 unconnected socket，导致连接建立失败（状态机 Abort，
                //   日志停在 mtu 之后、缺「Start 完成」）。
                //   UDP 的 ConnectAsync 不握手、不发包，只设默认远端，所以骨架期即使对端
                //   不存在也立即成功 —— 数据面在进程内，transport 纯属满足平台契约。
                // Connect 是同步回调（IVpnPlugIn.Connect 返回 void），只能用同步等待；
                // 后台任务线程上没有 SynchronizationContext，不会死锁。
                _transport.ConnectAsync(new HostName(outerTarget), LocalSettings.OuterPort)
                    .AsTask().GetAwaiter().GetResult();
                channel.LogDiagnosticMessage(
                    $"ClashUWP: transport 已连接（outerTarget={outerTarget}:{LocalSettings.OuterPort}，" +
                    $"配置来源={(rawConfigPath == null ? "内置兜底" : "config.yaml")}，" +
                    $"改写={mihomoConfig.Length}B fakeIp={(LocalSettings.FakeIp ? "on" : "off")}，" +
                    $"内核配置路径={(mihomoConfigPath ?? "无")}）.");
            }
            catch (Exception ex)
            {
                // 连不上也要继续 —— 旧代码把异常吞掉是为了「带着警告跑起来」，
                // 但代价是 Start 失败时看不到真正 hresult。这里先落日志，Start 那边另有兜底。
                channel.LogDiagnosticMessage("ClashUWP: transport ConnectAsync 失败（继续）: " + ex.Message);
            }

            // 步 5 · 路由与 DNS 下发
            //   ★ 节点 /32 排除路由已在步 4.2 算好（外层目标要用它，必须在那之前）。
            //     DNS 走 VpnDomainNameAssignment（官方 sample 的 v2 路径）。
            VpnRouteAssignment routes = BuildRoutes();
            VpnDomainNameAssignment? domains = LocalSettings.DnsIntoTunnel ? BuildDomainNames() : null;
            // V1 <c>Start()</c> 路径已退役（docs/76：它不装 inclusion 路由），所以
            // <c>VpnNamespaceAssignment</c> 这一路彻底不再用 —— 何况它的元素构造器
            // <c>VpnNamespaceInfo</c> 官方标记「Not supported」，本项目 AOT 形态下实测
            // 直接抛 NullReferenceException。DNS/NRPT 一律走 V2 的
            // <c>VpnDomainNameAssignment</c> + <c>VpnDomainNameInfo</c>（见 BuildDomainNames）。

            channel.LogDiagnosticMessage(
                $"ClashUWP: 排除路由 候选 {_nodeExclusions.Count} 条 /32（节点服务器，" +
                $"下发={(LocalSettings.NodeExclusions ? "是" : "否 nodeExclusions=0")}），" +
                $"DNS {(domains == null ? "留物理网卡(dnsInTunnel=0)" : "进隧道 " + string.Join(",", _config.DnsServers))}，" +
                $"内核={LocalSettings.Source} 开关[resident={LocalSettings.ResidentBridge} hotReload={LocalSettings.HotReload} selfSplitTunnel={LocalSettings.SelfSplitTunnel} outer={_outerTargetUsed}:{LocalSettings.OuterPort}(要求={LocalSettings.OuterTarget}) fakeIp={(LocalSettings.FakeIp ? "on" : "off")}]");

            // 步 6 · ★ 建隧道 = 建 L3 网卡。
            // 【官方】Start(...)："Creates the VPN channel to be used by the VPN plug-in.
            //   **It also creates an L3 network interface** for applications in the client
            //   machine to be able to see the corporate network."
            //
            // 参数官方语义（★ = 硬约束）：
            //   assignedClientIPv4list : "the IPv4 address assigned by the VPN server ... If null, DHCPv4 is used."
            //   assignedClientIPv6list : "If null, DHCPv6 or RD is used."
            //   vpnInterfaceId         : 传 null（sample 同）
            //   routeScope             : "the routes which should be associated / not associated with the VPN interface"
            //   namespaceScope         : "the list of name prefixes ... including its DNS and proxy servers"（NRPT）
            //   mtuSize        ★       : "should be configured to be at most 1400"；也是 Receive 池 buffer 大小
            //   maxFrameSize   ★       : "should be <= 1500"；也是 Send 池 buffer 大小
            //   第 8 位（V2 官方名 reserved）: 条文只写 "Reserved." ⇒ 传 false。
            //     注意 <c>optimizeForLowCostNetwork</c> 是 <b>V1 Start 的同位参数名</b>，
            //     语义（旧网络收费、新低成本网络可用时再触发 connect()）在 V2 已随重命名废弃，
            //     不要把它当 V2 的第 8 参语义读。
            //   main/optionalOuterTunnelTransport: "can be a DatagramSocket or a StreamSocket"
            //     ⚠️ V2 条文另有一句 "The socket must be unconnected at the point of the call."，
            //     而我们传的是<b>已连接</b>的 socket —— 见 Docs/09 §3.1（档 2 的 Flush 需要一个已
            //     连接目的地，官方 sample 同样先 ConnectAsync 再 Start；V1 Start 的条文没这句话）。
            //
            // Remarks 四条硬约束（原文）：
            //   · "Any call to this API outside of a connected VPN plug-in will fail ..."
            //   · "There can only be one VPN profile per process ..."
            //   · "On success the ownership of outerTunnelTransport is transferred by the VPN
            //      framework. Calling this method multiple times with the same parameters will
            //      have no effect."
            //   · "There can only be one VPN channel per process ... The mtuSize must always be
            //      smaller than the maxFrameSize."
            //
            // ⚠️ sample 这里写的是 mtuSize=1500 / maxFrameSize=1501，<b>两个都违规</b>。
            //    本实现按官方上限取 1400 / 1500。
            try
            {
                // Start 之前先挂上静态 GC 根：平台在 Start 内部持有并使用 transport，
                // channel 被静态引用持住 ⇒ transport  transitively 有根，
                // 桥启动阻塞数秒期间也不会被终结（0x800703E3 的成因）。
                s_activeChannel = channel;

                var ipv4 = new List<HostName> { new(_config.ClientIpV4) };

                // ★★ 先遣项目实测出的两条硬约束，两条都压在旧实现身上（docs/76）：
                //
                // ① V1 <c>Start()</c> **静默丢弃 Ipv4InclusionRoutes**（docs/76 发现一，
                //    83-T0 vs 84-R1 同一路由对象、只差 overload）：
                //      T0(V1) if=45 路由只有 255.255.255.255/32、224.0.0.0/4、10.0.1.2/32 —— 没有 include；
                //      R1(V2) +2.6s 出现 192.168.21.0/24 nh=0.0.0.0 metric=256，流量才命中 if=45。
                //    旧代码在 dnsInTunnel=0 时走的就是 V1 —— 那条路上 0.0.0.0/0 根本没装上。
                //
                // ② <c>StartWithTrafficFilter</c> 传**默认构造的空 VpnTrafficFilterAssignment**
                //    ⇒ 数据面被**默认 deny**（docs/76 发现三，tfmode 三态单变量 A/B）：
                //      tfmode=0 空对象      → GenFail=23、reqSeen=1（只有装 filter 前瞬间溜过 1 个包）
                //      tfmode=1 Allow*=true → GenFail=0 但 reqSeen=0（语义是"允许绕过 VPN 直连"，不是放行）
                //      tfmode=2 **nullptr**  → GenFail=0、reqSeen=24 ✅ 全部进隧道
                //    被 block 的表现形式：TCP → WSAEACCES(10013)，ICMP → "General failure"。
                //    都是**本地立刻失败**，不是超时 —— 与我们实测到的
                //    "curl baidu 退出码 000 / 0.069 秒"完全对得上，而黑洞应该是等超时。
                //    旧代码正是 tfmode=0 这条路（注释里"传空对象即可，sample 也这样"是误读：
                //    sample 的 XML 不给 <trafficfilter> 时压根不调 V2）。
                //
                // 结论：不分应用分流时**恒走 V2、trafficFilterAssignment 传 null**。
                //   dnsInTunnel 只决定 namespaceScope，不再决定 overload。
                //
                // ★ 1.0.21 新增第三个变量 <c>selfSplitTunnel</c>（run 13/15/16/17 之后
                //   唯一还没试过的平台级出口，理由见 LocalSettings.SelfSplitTunnel 注释）：
                //   0 → 仍传 null（基线，行为与 1.0.20 逐字一致）；
                //   1/2 → 传一个只含「本包 + SplitRouting」的 assignment。
                VpnTrafficFilterAssignment? selfFilter = BuildSelfSplitFilter();

                channel.StartWithTrafficFilter(
                    ipv4,                                  // assignedClientIPv4list
                    null,                                  // assignedClientIPv6list（v6 暂未接管）
                    null,                                  // vpnInterfaceId
                    routes,                                // routeScope（含节点 /32 排除）
                    domains ?? new VpnDomainNameAssignment(), // namespaceScope：★ 这里传的是<b>空 assignment 对象</b>，不是 null（"null 对象=不发 NRPT"是旧注释的误述，见 Docs/09 C3；空对象与 null 的语义差别在 trafficFilter 那一路已实测过 ⇒ 本项属未验证）
                    _config.Mtu,                           // mtuSize      ★ ≤ 1400
                    _config.MaxFrame,                      // maxFrameSize ★ ≤ 1500
                    false,                                 // reserved 位（V2 官方名就是 reserved，条文只写 "Reserved."；勿按 V1 的 optimizeForLowCostNetwork 读）
                    _transport,                            // mainOuterTunnelTransport
                    null,                                  // optionalOuterTunnelTransport
                    selfFilter);                           // trafficFilterAssignment ★ 0=**null**，1/2=本包分流

                _started = true;
                _encapsulatedPackets = 0;
                _echoReplies = 0;
                _unknownPackets = 0;
                _injectedPackets = 0;
                _nonUnicastDropped = 0;
                _ipChecksumFixed = 0;
                _transportChecksumFixed = 0;
                _checksumFixLogged = 0;
                _keepAlives = 0;
                // ★ 归还侧计数器每次 start 归零 —— 判据是"ret 与 encap 同步涨"，
                //   留下上一会话的数会让同步性没法逐轮核对。档位同时固化进快照。
                _encapReturnMode = LocalSettings.EncapReturn.ToString();
                _sendBuffersReturned = 0;
                _sendBuffersLeft = 0;
                _sendReturnFailures = 0;
                _sendBuffersFlushed = 0;
                _sendReturnFirstError = "";
                _encapsulateCalls = 0;
                _encapListItemsSeen = 0;
                _encapListSizeMax = 0;
                _encapEnumerated = 0;
                _encapEnumFirstError = "";
                _sendBuffersReappended = 0;
                _keepAliveOrphanBuffers = 0;
                _encapsulateErrors = 0;
                _encapsulateFirstError = "";
                _decapsulateCalls = 0;
                _decapFirstInfo = "";
                // receive 侧两列也每次会话归零（静态跨会话留着就没法判"在借未还"是不是本轮的）。
                Interlocked.Exchange(ref s_receiveBuffersPending, 0);
                Interlocked.Exchange(ref s_receiveBuffersFlushed, 0);
                _lastKeepAliveTick = 0;
                _sessionStartTick = Environment.TickCount64;

                // 注意：这里不能 .ToArray() —— 本工程里 Windows.Storage.Streams 的
                //   WindowsRuntimeBufferExtensions 把 ToArray 抢成了 IBuffer 专属扩展，
                //   对 IEnumerable<string> 直接编译失败（CS1929）。string.Join 本身吃 IEnumerable 就够。
                System.Collections.Generic.IEnumerable<string> includeUsed =
                    LocalSettings.IncludeMode.Equals("narrow", StringComparison.OrdinalIgnoreCase)
                        ? NarrowInclusionRoutes()
                        : _config.IncludeRoutes;

                channel.LogDiagnosticMessage(
                    "ClashUWP: StartWithTrafficFilter 完成（trafficFilter=" +
                    (selfFilter == null ? "null" : $"本包 SplitRouting, AllowOutbound={selfFilter.AllowOutbound}") +
                    $"，includeMode={LocalSettings.IncludeMode} include=[{string.Join(",", includeUsed)}]），L3 接口已建立.");

                // 会话日志：Start 成功即"这一段隧道真的存在过"，带上当时的关键变量，
                //   事后哪怕 Disconnect 没被回调（run 20 观察过：OS 拆通道时直接换新进程
                //   重新 Connect，从不回调 Disconnect），这一行也已经把变量固化下来了。
                LogSession($"evt=start includeMode={LocalSettings.IncludeMode} " +
                           $"include=[{string.Join(",", includeUsed)}] " +
                           $"outer={_outerTargetUsed}:{LocalSettings.OuterPort} " +
                           $"dnsInTunnel={(LocalSettings.DnsIntoTunnel ? 1 : 0)} " +
                           $"nodeExcl={_nodeExclusions.Count} mtu={_config.Mtu}");
                RollingLog.Info("session", "L3 隧道接口已建立：" +
                           $"includeMode={LocalSettings.IncludeMode} " +
                           $"include=[{string.Join(",", includeUsed)}] " +
                           $"outer={_outerTargetUsed}:{LocalSettings.OuterPort} mtu={_config.Mtu}");

                // 步 7 · ★ 启动 mihomo 进程内数据面（路线 A）。
                //   必须在 Start 之后：bridge 一旦跑起来，回程泵就会往这个 channel 注包。
                //   失败不阻断连接 —— Encapsulate 会回落假内核 echo，方便单独定位问题。
                StartMihomoBridge(channel, mihomoConfig, mihomoConfigPath);

                // 步 8 · ★ 武装插件进程内的看门狗（判黑洞 / 数重拨 / 硬上限）。
                //   必须在 Start 成功之后：之前隧道还不存在，探什么都是误判。
                //   放在桥之后：桥的 failFast 若已经决定放弃这次连接，看门狗不该再插一脚
                //   —— 它第一件事就是检查开关，开关关着（或桥失败路径已断开）就不会动手。
                SessionWatchdog.Start(channel, _config, _connectBeganAt, this);
            }
            catch (Exception ex)
            {
                // ★ 之前这里没包 try-catch：Start 在 fire-and-forget 的
                //   ProcessEventAsync 里抛出的异常会被平台吞掉，日志只剩「停在 mtu 之后」，
                //   看不到具体 hresult。这里显式落日志（LogDiagnosticMessage 是插件唯一
                //   可控的日志出口）后再 rethrow，让失败原因可定位。
                //   实测过 0x800703E3（ERROR_OPERATION_ABORTED）：transport socket 在
                //   Start 阻塞期间被 GC 回收、句柄终结导致 I/O 中止 —— catch 里
                //   GC.KeepAlive 只是事后保命，真正的保命在 Start 成功后置静态引用。
                GC.KeepAlive(_transport);
                // ★ 阶段必须分开报：try 块里串着 <c>StartWithTrafficFilter</c>、
                //   <c>StartMihomoBridge</c>、<c>SessionWatchdog.Start</c> 三处，旧日志统一写
                //   "VpnChannel.Start 异常"，于是<b>桥的 FailFast 会伪装成 Start 失败</b>
                //   （Docs/09 C4）。<c>_started</c> 只在 Start 成功后置真，拿它切阶段最省。
                //   ★ 日志出口本身也要兜住：这一行要是抛（平台日志偶尔不可用），
                //     下面的泵/桥收尾就整个跳过、异常还变成"从回调漏出去"的第二种死法。
                try
                {
                    channel.LogDiagnosticMessage(
                        (_started ? "ClashUWP: Start 已成功，异常出在后续步骤（桥 FailFast / 看门狗）: "
                                  : "ClashUWP: VpnChannel.StartWithTrafficFilter 异常: ") + ex);
                }
                catch { /* 日志出口不可用，不能因此跳过收尾 */ }
                LogSession($"evt=start-failed stage={(_started ? "post-start" : "start")} " +
                           $"err={ex.GetType().Name} {ex.Message}");
                // ★ 人读的那一行把阶段说清楚：_started=false 是平台 Start 本身失败，
                //   true 则是桥 FailFast / 看门狗 —— 两者修法完全不同（Docs/09 C4）。
                RollingLog.Error("start-failed",
                    (_started ? "Start 已成功，异常出在后续步骤（桥 FailFast / 看门狗）"
                              : "StartWithTrafficFilter 失败"), ex);
                // Start 失败 ⇒ 不会有 Disconnect 来收尾。摘掉本连接的泵是必须的
                // （它还挂在刚失败的 channel 上）。桥本身是否停，取决于常驻开关：
                //   · 常驻开 → 留着。"同进程第二次 Start 之后 TLS 回不来"就是销毁/重建
                //     这条路走出来的病（见 Docs/04 与项目记忆），能不复用销毁路径就别走它。
                //   · 常驻关 → 按旧行为无条件 stop（Go 侧对未启动桥是 no-op）。
                lock (s_bridgeGate)
                {
                    try { s_pumpCts?.Cancel(); } catch { }
                    s_pumpCts = null;
                    s_bridgeGlobal = false;
                    if (!LocalSettings.ResidentBridge)
                    {
                        try { s_boundIfaceSnapshot = MihomoBridge.BoundInterface(); } catch { }
                        MihomoBridge.clash_bridge_stop();
                        s_bridgeResident = false;
                    }
                    else
                    {
                        DrainBridgeQueue();
                        s_bridgeResident = true;
                    }
                }
                if (ReferenceEquals(s_activeChannel, channel))
                    s_activeChannel = null;

                // ★ <b>Start 还没成功</b> ⇒ 这次 Connect 仍是一次"建连尝试"，走官方失败出口
                //   <c>TerminateConnection</c>（条文："Terminates the connection attempt performed
                //   inside IVpnPlugIn.Connect."），再 rethrow 让平台把原因上报给宿主应用。
                //   为什么不只 rethrow：只抛出去时"平台到底怎么理解这次失败"是<b>推断</b>，
                //   而 00:21 那类"半装状态没人收尾"（孤儿 NRPT + 幽灵路由 = 整机断网）
                //   正是靠推断过活的路径长出来的。显式终止 = 把意图说给平台听。
                // ★ <c>_started==true</c>（桥 FailFast）这一支<b>不调</b> TerminateConnection：
                //   那时 L3 接口与 NRPT 已经装上，拆已跑通的会话超出该方法条文
                //   （口径与取舍见 <see cref="LocalSettings.SessionWatchdog"/> 与 Docs/09 §4），
                //   仍然只 rethrow，由平台拆通道后回调 Disconnect → <c>channel.Stop()</c> 收尾。
                if (!_started)
                    AbortConnectionAttempt(channel, "start failed: " + ex.GetType().Name);

                throw;
            }
        }

        /// <summary>
        /// Connect 内、<b>Start 成功之前</b>的官方失败出口。
        ///
        /// 【官方】TerminateConnection(String)：
        ///   "Terminates the connection attempt performed inside IVpnPlugIn.Connect.
        ///    <b>Should not be called on reconnect cases.</b>"
        ///   —— 也就是说它是给"插件自己判定这次建连做不下去"准备的显式出口，
        ///   不是用来拆已经跑起来的会话（那种形态走平台级断开 +
        ///   <see cref="GracefulTeardown"/> 那道闸，见 <see cref="SessionWatchdog"/>）。
        ///
        /// ★ 本方法<b>绝不抛出</b>：它在失败路径上被调用，从这里再抛一次异常
        ///   就等于把"漏异常出平台回调"这个病自己犯一遍（run 34 的实证后果）。
        /// ★ 重连（reconnect）场景在 26100 的插件侧<b>读不到</b>（没有 DisconnectReason、
        ///   也没有公开的"这是重连"标志，见 <see cref="VpnChannel"/> 的方法表核对结果），
        ///   所以这里的调用条件只能是"<c>_started==false</c> 且我们仍在自己的 Connect 里"——
        ///   这正是条文描述的场景；平台自动重拨时新起的 Connect 同样是这个形状。
        /// </summary>
        private void AbortConnectionAttempt(VpnChannel channel, string reason)
        {
            try
            {
                channel.TerminateConnection(reason);
                LogSession($"evt=terminate-connection reason={reason}");
            }
            catch (Exception ex)
            {
                LogSession($"evt=terminate-connection-failed reason={reason} " +
                           $"err={ex.GetType().Name}: {ex.Message}");
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // S5.1 · Encapsulate —— 出站（本机 → 外）
        // 【官方】"instructs the VPN plug-in to **prepare the indicated L3 IP packet for
        //          transmission to the VPN server**. The process might include any
        //          encapsulation specific to the VPN protocol."
        // ────────────────────────────────────────────────────────────────────
        public void Encapsulate(VpnChannel channel,
                                VpnPacketBufferList packets,
                                VpnPacketBufferList encapulatedPackets)
        {
            // ★★ 外层守卫（1.0.34）：<c>Encapsulate</c> 是平台<b>同步</b>回调 —— 从这里漏出去的
            //   异常不是"丢一个包"，实测后果是"<b>平台此后不再交包</b>"：
            //   run 34 就是档 3 的 <c>foreach</c> 没被 try 包住，<c>InvalidCastException</c>
            //   直接漏进平台，整条数据面从第 11 秒起静默死掉（run 36 才把这条例程钉死）。
            //   所以这里兜住一切：只计数、只记首个原文、写一条日志，然后<b>正常返回</b>。
            //   没处理完的 buffer 仍留在 <c>packets</c> 里，平台按 remarks 自己 cleanup，
            //   不会因此变成泄漏（比"因为抛异常而再也不被调用"好得多）。
            try
            {
                EncapsulateCore(channel, packets, encapulatedPackets);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _encapsulateErrors);
                if (_encapsulateFirstError.Length == 0)
                {
                    _encapsulateFirstError = ex.GetType().Name + ":" +
                        ex.Message.Replace('\r', ' ').Replace('\n', ' ');
                    try
                    {
                        channel.LogDiagnosticMessage(
                            "ClashUWP: Encapsulate 内部异常（已兜住，首个）" + _encapsulateFirstError);
                    }
                    catch { /* 日志出口偶尔不可用 */ }
                }
            }
        }

        /// <summary>
        /// <c>Encapsulate</c> 的真实形态（外层保证不抛，见 <see cref="Encapsulate"/>）。
        /// </summary>
        private void EncapsulateCore(VpnChannel channel,
                                     VpnPacketBufferList packets,
                                     VpnPacketBufferList encapulatedPackets)
        {
            // Start 之前平台不该回调这里；兜一下，免得往一个还没建起来的 channel 里注入。
            if (!_started)
                return;

            // ★ 归因三列，进函数第一件事就记，任何分支都跑不到漏：
            //   calls（平台回调了几次）/ seen（列表里一共几个包）/ max（单批最大）。
            //   run 34 就是缺这三列，"encap=0" 到底是"平台没交包"还是"枚举器是空的"分不开。
            _encapsulateCalls++;
            uint listSize = 0;
            try { listSize = packets.Size; } catch { /* 极端情况下取不到 Size，下面按 0 处理 */ }
            _encapListItemsSeen += listSize;
            if (listSize > _encapListSizeMax)
                _encapListSizeMax = listSize;

            // ★ 档 3（run 33 之后新增，官方 remarks 原文见 TryReturnSendBuffer 的注释）：
            //   <b>不摘列表</b> —— 只枚举读包，Encapsulate 返回时把列表里的 buffer 交给平台
            //   "clean up the remaining IVpnPacketBuffer objects in packets" 自己回收。
            //   这条既不需要 Append 也不需要 Flush ⇒ 零外层传输代价，又脱离不了生命周期。
            // ★ 档位只读一次：同一个 Encapsulate 回调里绝不能一半按档 2 走、一半按档 5 走
            //   （Flush 的批边界会跟着读值时机漂，实测数就对不上账）。
            int mode = LocalSettings.EncapReturn;

            if (mode == 3)
            {
                // ★ 只能枚举，不能按下标取。反射实测（.local/ifc35.txt，TFM 10.0.26100 的
                //   Microsoft.Windows.SDK.NET.dll）投影出来的 <c>VpnPacketBufferList</c>：
                //   实例方法 = Append / AddAtBegin / RemoveAtEnd / RemoveAtBegin / Clear /
                //   GetEnumerator(+非泛型显式实现) / Equals / GetHashCode，属性只有 Size；
                //   接口 = IEnumerable<VpnPacketBuffer> + IEnumerable + ICustomQueryInterface +
                //   IWinRTObject + IDynamicInterfaceCastable + IEquatable<VpnPacketBufferList>。
                //   <b>没有 GetAt、没有索引器、没有 Status</b>（它不是 IVector&lt;T&gt;，
                //   而是 IVpnPacketBufferList），所以 foreach 是唯一的取包路径 ——
                //   而这条路径在运行时到底能不能出包，就是上面那三个归因列要回答的问题。
                //
                // ★ run 34 实测这一档 <c>Encapsulated=0</c> 且整机数据面从第 11 秒就是死的，
                //   所以现在把整个 foreach 包进 try：如果 <c>GetEnumerator</c> 在运行时抛
                //   （投影出来实现了 IEnumerable&lt;T&gt;，但底层要靠 QI 拿 IIterable），
                //   异常绝不能从平台同步回调里漏出去 —— 那比少处理一个包严重得多。
                //
                // ★★ run 36（组件 1.0.20，encapReturn=3）把机制钉死了，读数是：
                //     EncapsulateCalls=219  EncapListItemsSeen=243  EncapMaxBatch=4
                //     EncapEnumerated=0     EncapEnumFirstError=InvalidCastException:Specified cast is not valid.
                //   ⇒ <b>平台一直在回调、一直在交包，是 CsWinRT 给这个列表生成的枚举器一取就抛</b>
                //     （它实现了 IEnumerable&lt;VpnPacketBuffer&gt;，但没有 GetAt/索引器可绕）。
                //     也解释了 run 34 为什么"一个包都没处理还不报错"：那时 foreach 没被 try 包住，
                //     异常直接漏进平台，平台此后不再交包 —— 同步回调里抛异常的真实后果。
                //   这一档因此<b>作废</b>，保留仅为归因列的证据链；真要试"列表留给平台 cleanup"
                //   这个想法，得用投影里存在的 mutator：RemoveAtBegin 摘出来处理完再 Append 塞回去。
                try
                {
                    foreach (VpnPacketBuffer buffer in packets)
                    {
                        byte[] packet = buffer.Buffer.ToArray();
                        int length = (int)buffer.Buffer.Length;
                        _encapsulatedPackets++;
                        _encapEnumerated++;
                        Interlocked.Increment(ref _sendBuffersLeft);
                        HandleOutboundPacket(channel, packet, length);
                    }
                }
                catch (Exception ex)
                {
                    if (_encapEnumFirstError.Length == 0)
                    {
                        _encapEnumFirstError = ex.GetType().Name + ":" + ex.Message;
                        try
                        {
                            channel.LogDiagnosticMessage(
                                "ClashUWP: 档 3 枚举 packets 失败（首个）" + _encapEnumFirstError);
                        }
                        catch { /* 日志出口偶尔不可用 */ }
                    }
                }
                return;
            }

            // ★★ <b>档 4</b> 的循环上界必须用<b>进来时</b>的条数，不能写 <c>while (packets.Size > 0)</c>：
            //   档 4 每处理一条就 <c>packets.Append()</c> 塞回队尾，Size 永远不为 0 ——
            //   照抄档 1/2 的循环条件就是死循环（而且是<b>平台同步回调里</b>的死循环，
            //   等于把整个会话钉死，比丢包严重得多）。
            //   顺序不受影响：RemoveAtBegin 取队首、Append 落队尾，塞回去的相对次序与交来时一致。
            //   <c>listSize</c> 取不到（上面 try 过）时预算为 0 ⇒ 一条都不处理，
            //   数上会留下 <c>seen&gt;0 / encap=0</c> 的形状，不会假装处理过。
            int arm4Budget = mode == 4 ? (int)listSize : 0;
            int handled = 0;

            while (packets.Size > 0 && (mode != 4 || handled < arm4Budget))
            {
                // sample 注释强调：用 RemoveAtBegin 保序。
                // ★ 但 RemoveAtBegin <b>只是摘出来</b>，不是归还 —— 见 TryReturnSendBuffer。
                VpnPacketBuffer buffer = packets.RemoveAtBegin();
                handled++;

                // IBuffer.Length 是本包有效长度；Capacity 是池化 buffer 的容量（= mtuSize）。
                byte[] packet = buffer.Buffer.ToArray();
                int length = (int)buffer.Buffer.Length;

                _encapsulatedPackets++;

                // ★★ 【run 27/28/31/32 停在 7188 包的那条漏】拷完就得把 buffer 交回平台，
                //   且必须对<b>每一个</b>包都交（包括下面刻意丢掉的组播/广播包）。
                //   注意 run 33 已经把档 1（只 Append 不 Flush）实测否掉了：
                //   <c>SendBuffersReturned=7188 == Encapsulated=7188, SendReturnFailures=0</c>
                //   却<b>照旧停在 7188</b> ⇒ Append 只是"入队"，入队的 buffer 仍被通道占着，
                //   没回池子。口径与出处见 LocalSettings.EncapReturn 与 TryReturnSendBuffer。
                //   ★ 顺序也是刻意的：<b>先交 buffer 再处理包</b>。处理路径（喂内核 / 补校验和）
                //     万一抛异常，归还已经做完了 —— 反过来就会欠平台一条，正是借空池子的形状。
                TryReturnSendBuffer(channel, packets, buffer);

                HandleOutboundPacket(channel, packet, length);
            }

            // ★ <b>档 5</b>：整批 Append 完只提交一次 Flush（官方 sample 的攒批形态）。
            //   放在循环外 ⇒ 哪怕这一批里一个包都没摘到（Size=0 直接开跑），也只是多调一次
            //   Flush，不会漏 Flush；漏 Flush 才是致命的（档 1 的死法）。
            if (mode == 5)
                TryFlushSendBuffers(channel);
        }

        /// <summary>
        /// 出站包的逐包处置（档 1/2 与档 3 共用，抽出来只为让"buffer 怎么交回平台"这件事
        /// 单独可切换 —— 本臂的唯一变量就是它，包的处理逻辑必须逐字不变）。
        /// </summary>
        private void HandleOutboundPacket(VpnChannel channel, byte[] packet, int length)
        {
            if (_encapsulatedPackets <= MaxTunSamples)
                AddSample(s_outSamples,
                    $"#{_encapsulatedPackets} {IpPacket.Describe(packet, length)}");

            // ★ 组播 / 受限广播 / 0/8 不喂内核（对齐安卓：VpnService 压根不捕获广播类流量）。
            //   喂进去的后果是 sing-tun 的 LinkEndpointFilter 把它们原样回写 tun，
            //   等于把本机自己发的 LLMNR/SSDP 注回本机 —— 实测 Injected(8705) 反超
            //   Encapsulated(7188)，这份虚胖主要就是它贡献的，也会把采样刷满垃圾。
            //   ★ buffer 的交回动作在调用方（摘列表档在 TryReturnSendBuffer，
            //     档 3 靠平台 cleanup），这里 return 不再欠平台的。
            if (IpPacket.IsNonUnicastDestination(packet, length))
            {
                _nonUnicastDropped++;
                return;
            }

            if (s_bridgeGlobal)
            {
                // ★ 路线 A：原始 L3 包直接交给进程内 mihomo（gvisor 栈终结 TCP/UDP，
                //   代理出网），回程包由回程泵（PumpLoop）取出并 Inject 注回本机。
                //
                //   喂包前先把校验和补算齐（IpPacket.FixChecksums 有三层出处的注释）：
                //   我们的 gvisor 端点没声明 RXChecksumOffload，内核会逐包验校验和，
                //   而 Windows 交给 L3 接口的包很可能带没补算的校验和 —— 验不过就整包丢弃，
                //   这就是「包进得去、内核不回话」的隧道黑洞。
                IpPacket.ChecksumFix fixedFields = IpPacket.FixChecksums(packet, length);
                if (fixedFields != IpPacket.ChecksumFix.None)
                {
                    if ((fixedFields & IpPacket.ChecksumFix.IpHeader) != 0)
                        _ipChecksumFixed++;
                    if ((fixedFields & IpPacket.ChecksumFix.Transport) != 0)
                        _transportChecksumFixed++;

                    if (_checksumFixLogged == 0 &&
                        Interlocked.Exchange(ref _checksumFixLogged, 1) == 0)
                        channel.LogDiagnosticMessage(
                            "ClashUWP: 交来的包校验和未填完（IP/传输层），已就地补齐再喂内核 —— " +
                            "黑洞主因验证点，后续同类包只计数不再写日志.");
                }

                int rc = MihomoBridge.clash_bridge_put_packet(packet, length);
                if (rc != 0)
                {
                    _unknownPackets++;
                    if (_unknownPackets == 1)
                        channel.LogDiagnosticMessage("ClashUWP: put_packet 失败 rc=" + rc);
                }
            }
            else if (IpPacket.TryBuildEchoReply(packet, length, out byte[] reply))
            {
                // 回落路径（bridge 未启动）：echo request → echo reply，直接注回本机协议栈。
                // ★ 只在真注入成功时才计 echoReplies —— 否则"应答数"会大于"内核收下的包数"，
                //   把池子借空伪造成"回程正常"。失败原因由 NoteInjectFailure 统一记录。
                if (Inject(channel, reply))
                    _echoReplies++;
            }
            else
            {
                _unknownPackets++;
            }

            if (_encapsulatedPackets % LogEveryNPackets == 0)
            {
                channel.LogDiagnosticMessage(
                    $"ClashUWP: 已处理 {_encapsulatedPackets} 包（注入 {_injectedPackets}，echo 应答 {_echoReplies}，异常 {_unknownPackets}）.");
            }
        }

        /// <summary>
        /// 把 <c>Encapsulate</c> 借到的 send 池 buffer 原样归还平台。
        ///
        /// 【官方 remarks 原文（先遣项目 docs/65:129 逐字引的 MSDN 口径，最关键的一句）】
        ///   "The VPN framework will then send the IVpnPacketBuffer objects in
        ///    <i>encapsulatedPackets</i> via one of the transports specified during Connect
        ///    <b>and will clean up the remaining IVpnPacketBuffer objects in <i>packets</i></b>."
        ///   ⇒ 平台在 <c>Encapsulate</c> 返回时回收的是<b>还留在 packets 里</b>的 buffer。
        ///   先遣项目 docs/65 对官方 sample 的审计结论（A1）也正是这一条：
        ///   "Sample 把 packets 全部 RemoveAtBegin() 取走…既不填 encapsulatedPackets，
        ///    也不让平台回收 packets … 脱离平台对缓冲池的生命周期管理。"
        ///   ⇒ 所以本方法的档 1/2 是"<b>已经摘走</b>之后怎么补救"，而档 3（不摘列表）
        ///     才是那句 remarks 直接支持的形态。
        ///
        /// 【★ run 33 的实测否证：档 1 不够】
        ///   终值 <c>Encapsulated=7188</c>、<c>SendBuffersReturned=7188</c>、
        ///   <c>SendReturnFailures=0</c>、<c>SendBuffersFlushed=0</c> —— 每个包都 Append 了，
        ///   却<b>照旧停在 7188</b>（四臂同数第五次复现，且这臂加了外部请求压力、
        ///   包速 60 pps，仍然死在同一包数）。与先遣项目 docs/105 §4.4 那句对上：
        ///   "<c>AppendVpn*PacketBuffer</c> 只是<b>入队</b>，必须调用 <c>FlushVpn*PacketBuffers()</c>
        ///   才真正发出" —— 入队的 buffer 仍被通道占着，没有回池子。
        ///
        /// 【官方口径】"...must eventually be returned to the VPN platform
        ///   (i.e. via IVpnPlugIn.Encapsulate or IVpnPlugIn.Decapsulate)"
        ///   —— 出处：先遣项目 docs/105 §4.1（sample 注释引用的原文），
        ///   并给出实测后果："不归还 = buffer 泄漏"，之后
        ///   <c>RequestVpnPacketBuffer</c> 永久返回 <c>0x8007045B</c>
        ///   （<c>ERROR_NO_SYSTEM_RESOURCES</c>，docs/77 §… 也记了同一条）。

        ///
        /// 【Append 与 Flush 的分工（先遣项目 mode 80 实测口径，逐字）】
        ///   · <c>AppendVpnSendPacketBuffer</c> = <b>归还 buffer 给平台池</b>；
        ///   · <c>FlushVpnSendPacketBuffers</c> = <b>提交并送去 transport</b>；
        ///   · "不归还 → 平台 send 池被借空 → 之后一切 RequestVpnPacketBuffer 永久返回 0x8007045B"；
        ///   · "不 Flush → 包不会被送去 transport，达到「在插件内终止」的效果"。
        ///   ★ 但 run 33 把"只 Append 不 Flush = 归还"这条<b>否证</b>了（见上节的终值），
        ///     "在插件内终止"和"让 buffer 回池子"这两件事<b>靠 Append 做不到同时</b>。
        ///
        /// 【★★ 现行口径（1.0.33，Docs/09 §3.1 复核过 —— 本注释此前撒谎）】
        ///   产品默认 = <b>档 2（Append+Flush）</b>，见 <c>LocalSettings.EncapReturn</c> 的默认值 2。
        ///   它由 run 35（18202 包）与 run 37（头条判据 PASS）实测立住，<b>不是"判别臂"</b>。
        ///   旧注释里"⇒ 默认档改成 3"那句是 run 36 当时的意图，而档 3 已经<b>死在投影层</b>：
        ///   CsWinRT 在 TFM 10.0.26100 抛 <c>InvalidCastException</c>（run 36 终值），
        ///   文档面的 <c>VpnPacketBufferList</c> 只有 Append/AddAtBegin/RemoveAtBegin/
        ///   RemoveAtEnd/Clear/First + Size/Status，<b>没有 GetAt、没有索引器</b>。
        ///   ⇒ <b>档 4 已实现（1.0.34，本方法 <c>mode==4</c> 分支）</b>：RemoveAtBegin → 就地处理 →
        ///     <c>packets.Append()</c> 塞回原列表，让平台按 remarks "will clean up the remaining
        ///     IVpnPacketBuffer objects in *packets*" 自己回收（一次 Flush 都不调）。
        ///     ★ 循环上界因此<b>不能</b>再用 <c>while (packets.Size > 0)</c> —— 塞回去 Size 恒非 0，
        ///       那是<b>平台同步回调里的死循环</b>；改成进来时记条数，见 <c>Encapsulate</c> 的 arm4Budget。
        ///     能不能调通<b>必须实测</b> ⇒ 默认仍留档 2。
        ///     另有<b>档 5</b>（逐包 Append + 每批一次 Flush，官方 sample 的攒批形态）：
        ///     与档 2 只差 Flush 的调用次数，外层重复流量不变，同样未实测、不作默认。
        ///
        /// 【为什么不改成"塞进 Encapsulate 的第三个参数 encapulatedPackets"】
        ///   那个列表的语义是"交给平台经 outerTunnelTransport <b>发出去</b>的封装结果"，
        ///   我们没有任何封装结果可交 —— 硬塞等于把每个出站包 UDP 送到外层目标
        ///   （黑洞地址，路由器直接丢，白占上行；先遣项目 docs/93 还记过更坏的一面：
        ///   外层 socket 一旦被 ICMP 拒绝，OS 直接拆通道重拨）。
        ///   ★ 但这句话现在<b>对本品也成立</b>，别再把它只当"硬塞 encapulatedPackets 的代价"读：
        ///     官方给 <c>AppendVpnSendPacketBuffer</c> 的形参名是 <c>encapsulatedPacketBuffer</c>，
        ///     <c>FlushVpnSendPacketBuffers</c> 的条文是 "Flushes any appended send packet buffers"
        ///     （Append 的 See also 就是它）⇒ 档 2 的每包 Flush 语义上等于<b>把裸 L3 包当封装结果
        ///     发往外层 transport</b>，也就是每一份出站流量在本机被重复 UDP 发一次（明文负载上
        ///     局域网 + 等量上行占用）。它不连通性无罪，但属已记在册的产品质量债（Docs/09 §3.1），
        ///     解法是上面那条<b>档 4</b>，不是回到档 0/1。
        ///
        /// ★ 失败只计数、绝不抛出：<c>Encapsulate</c> 是平台同步回调，从这里抛出去
        ///   会让整个会话被平台拆掉（那是比"少还一个 buffer"严重得多的后果）。
        /// </summary>
        private void TryReturnSendBuffer(VpnChannel channel, VpnPacketBufferList packets, VpnPacketBuffer buffer)
        {
            int mode = LocalSettings.EncapReturn;

            // 0 = 旧行为（摘出来什么都不做，只为反向复现 7188 留档）；
            // 3 = 列表压根没被摘过，由平台 cleanup，不会走到这里。
            if (mode == 0 || mode == 3)
                return;

            try
            {
                // ★ 档 4：塞回<b>原列表</b>，让 Encapsulate 返回时平台自己回收 ——
                //   这是官方 remarks 那句 "clean up the remaining IVpnPacketBuffer objects in packets"
                //   的直接实现，一次 Append/Flush 都不调。
                if (mode == 4)
                {
                    packets.Append(buffer);
                    _sendBuffersReappended++;
                    Interlocked.Increment(ref _sendBuffersLeft);
                    return;
                }

                channel.AppendVpnSendPacketBuffer(buffer);
                _sendBuffersReturned++;

                // 档 2 = 每包一次 Flush（run 35/37 实测成立、产品默认）；
                // 档 5 = 逐包 Append、每批一次 Flush（Flush 由调用方在循环外做）。
                if (mode == 2)
                {
                    channel.FlushVpnSendPacketBuffers();
                    _sendBuffersFlushed++;
                }
            }
            catch (Exception ex)
            {
                NoteSendReturnFailure("append/flush", ex, channel);
            }
        }

        /// <summary>
        /// 一次 <c>FlushVpnSendPacketBuffers</c>（<b>档 5</b> 用：整批 Append 完只提交一次）。
        /// 与档 2 的差别<b>只在调用次数</b>（每包 1 次 → 每批 1 次）：归还的 buffer 总量、
        /// 外层 UDP 的重复量都不变，省下的是每包一次 Flush 的开销。
        /// ★ 绝不抛出 —— 同 <see cref="TryReturnSendBuffer"/>，这是平台同步回调。
        /// </summary>
        private void TryFlushSendBuffers(VpnChannel channel)
        {
            try
            {
                channel.FlushVpnSendPacketBuffers();
                _sendBuffersFlushed++;
            }
            catch (Exception ex)
            {
                NoteSendReturnFailure("flush-batch", ex, channel);
            }
        }

        /// <summary>
        /// send buffer 归还失败的统一记账：计数 + 只写一次原文 + 一条系统日志。
        /// ★ 无论哪一步失败都<b>不往外抛</b>：<c>Encapsulate</c>/<c>GetKeepAlivePayload</c>
        ///   是平台同步回调，异常漏出去平台就不再交包（run 34 的实证，Docs/09 §3.2）。
        /// </summary>
        private void NoteSendReturnFailure(string stage, Exception ex, VpnChannel channel)
        {
            _sendReturnFailures++;
            if (_sendReturnFirstError.Length == 0)
            {
                _sendReturnFirstError = stage + " " + ex.GetType().Name + ":" +
                    ex.Message.Replace('\r', ' ').Replace('\n', ' ');
                try
                {
                    channel.LogDiagnosticMessage(
                        "ClashUWP: send buffer 归还失败（首个）" + _sendReturnFirstError);
                }
                catch { /* 日志出口偶尔不可用 */ }
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // S5.2 · Decapsulate —— 入站（外 → 本机）
        // 【官方】"instructs the VPN plug-in to prepare receive a buffer from the VPN server
        //          outerTunnelTransport, **extract any number of IP packets encapsulated in
        //          the buffer**, and return the IP packets together with any other reply
        //          buffers ..."
        //
        // ★ ClashUWP 不走 outer transport，所以这条回调正常运行时<b>不会</b>被平台触发。
        //   真内核的回包走 Inject() 那条主动注入路径（见下），它不受 Decapsulate 时序约束，
        //   可以在任意线程调用 —— 这是本架构的关键自由度。
        // ────────────────────────────────────────────────────────────────────
        public void Decapsulate(VpnChannel channel,
                                VpnPacketBuffer encapBuffer,
                                VpnPacketBufferList decapsulatedPackets,
                                VpnPacketBufferList controlPacketsToSend)
        {
            // 有意留空：数据面不经过 outer transport。
            //
            // ★ 但"留空"这件事本身要能被<b>证伪</b>（Docs/09 O1）：这条回调一旦真被平台触发，
            //   说明外层传输确实进了数据，而我们把它整个扔了 —— 那是漏收，不是"设计如此"。
            //   推理说它不会被调用不算证据，计数才算。所以这里只记数、不碰 buffer。
            //   ★ 只记数也不许抛：Decapsulate 同样是平台同步回调。
            try
            {
                Interlocked.Increment(ref _decapsulateCalls);
                if (_decapFirstInfo.Length == 0)
                {
                    uint len = 0;
                    try { len = encapBuffer?.Buffer?.Length ?? 0; } catch { }
                    _decapFirstInfo = $"#{_decapsulateCalls} len={len} " +
                        $"out={decapsulatedPackets?.Size ?? 0} ctl={controlPacketsToSend?.Size ?? 0}";
                    channel.LogDiagnosticMessage(
                        "ClashUWP: ★ Decapsulate 被回调了（本架构预期不会）：" + _decapFirstInfo);
                }
            }
            catch { /* 观测点绝不影响到平台回调 */ }
        }

        // ────────────────────────────────────────────────────────────────────
        // S5.3 · GetKeepAlivePayload
        // 【官方】"instructs the VPN plug-in to compute and return a Keep alive payload ready
        //          to be sent to the VPN server."
        // ────────────────────────────────────────────────────────────────────
        public void GetKeepAlivePayload(VpnChannel channel, out VpnPacketBuffer keepAlivePacket)
        {
            // 【官方 sample】VpnPlugin.cs:871~903 的逐字契约：
            //   RequestVpnPacketBuffer(Send) → Buffer.Length=1 → 写一个 VPN_CONTROL_KEEP_ALIVE 字节。
            //   （sample 的 VPN_CONTROL 枚举：PACKET=1、DISCONNECT=2、KEEP_ALIVE=3。）
            // 为什么要照抄而不是留空：这个回调是<b>平台用来判断外层隧道是否还活着</b>的入口。
            //   旧实现取了 buffer 只改 Length、不写内容 ⇒ 交回去的是内存池里的陈旧字节；
            //   更要命的是它整个方法没有 try-catch，`RequestVpnPacketBuffer` 一旦抛异常就从
            //   平台回调里逃出去 —— 这条路径足以让平台判插件死亡。
            // ★ 观测点：计数 + 相邻间隔是"平台多久判一次隧道"的唯一直接证据。
            //   run 26 之后发现的"连上约 96 s 必被拆（RasClient reason 828）+ 系统无限自动重拨"
            //   到底是 keep-alive 节奏/失败造成，还是另有其因，用这里的数字区分，不许猜。
            // 投影出来的形参类型是 <c>out VpnPacketBuffer</c>（非空标注），但"这一次交不出
            // keep-alive"是合法结果 —— 平台对 null 的处理就是"本轮没有 keep-alive"，
            // 比硬交一个内容没写完的 buffer 安全。null! 只是压掉投影层的标注，不是压掉约束。
            keepAlivePacket = null!;
            VpnPacketBuffer? borrowed = null;
            try
            {
                long now = Environment.TickCount64;
                long gapMs = _lastKeepAliveTick == 0 ? 0 : now - _lastKeepAliveTick;
                _lastKeepAliveTick = now;
                _keepAlives++;

                channel.RequestVpnPacketBuffer(VpnDataPathType.Send, out VpnPacketBuffer buf);
                borrowed = buf;
                if (buf != null)
                {
                    buf.Buffer.Length = 1;
                    using (Stream s = buf.Buffer.AsStream())
                    {
                        s.Seek(0, SeekOrigin.Begin);
                        s.SetLength(1);
                        s.WriteByte(KeepAliveControlByte);
                    }
                    keepAlivePacket = buf;
                    borrowed = null;   // 交回平台了（出参就是官方的交回路径），不再需要我们归还
                }

                // 只在前 3 次和每 20 次写系统日志，避免按 keep-alive 周期刷屏；
                // 完整节奏（次数/首末间隔）在 Disconnect 的快照里给。
                if (_keepAlives <= 3 || _keepAlives % 20 == 0)
                {
                    channel.LogDiagnosticMessage(
                        $"ClashUWP: keep-alive #{_keepAlives}，距上次 {gapMs / 1000.0:F1} s，" +
                        $"交回包={(keepAlivePacket == null ? "null" : "1B")}，会话已 {ElapsedSeconds():F0} s");
                }
            }
            catch (Exception ex)
            {
                // 绝不把异常抛出平台回调：交回 null 让平台自己按无 keep-alive 处理
                // （借出去的 buffer 由方法末尾的 orphan 归还分支收掉，不会因此漏在池外）。
                keepAlivePacket = null!;
                RollingLog.Error("keepalive", ex);
                try { channel.LogDiagnosticMessage("ClashUWP: keep-alive 构造失败: " + ex.Message); } catch { }
            }

            // ★★ 借了却没交回的 buffer 必须<b>主动归还</b>（Docs/09 A2）。
            //   这条路径原来是真漏：RequestVpnPacketBuffer(Send) 一旦成功、后面任何一步抛
            //   （写 IBuffer 越界、AsStream 失败…），<c>keepAlivePacket</c> 就停在 null，
            //   借出的 buffer 再没人管 —— 和"send 池借空停在 7188 包"是同一条病根，
            //   只是 keep-alive 节奏慢（几十秒一次），撞不满配额所以一直没现形。
            //   归还走 Append+Flush（run 33/35 证明"Append 只是入队、必须 Flush 才回池子"），
            //   失败只计数不抛 —— 这个 catch 之外再抛就等于从平台回调里漏出去。
            if (borrowed != null)
            {
                _keepAliveOrphanBuffers++;
                try
                {
                    borrowed.Buffer!.Length = 0;              // 空包：不把半截 keep-alive 发出去
                    channel.AppendVpnSendPacketBuffer(borrowed);
                    channel.FlushVpnSendPacketBuffers();
                }
                catch (Exception ex2)
                {
                    NoteSendReturnFailure("keepalive-orphan", ex2, channel);
                }
            }
        }

        /// <summary>sample 的 <c>VPN_CONTROL.VPN_CONTROL_KEEP_ALIVE</c> 取值（1=PACKET、2=DISCONNECT、3=KEEP_ALIVE）。</summary>
        private const byte KeepAliveControlByte = 3;

        /// <summary>本次会话已存活秒数（Start 成功起算；没 Start 成功过则 0）。</summary>
        private double ElapsedSeconds() =>
            _sessionStartTick == 0 ? 0 : (Environment.TickCount64 - _sessionStartTick) / 1000.0;

        /// <summary>给看门狗用的存活秒数（同一口径，别让两个看门的人各算各的）。</summary>
        internal double SessionSeconds() => ElapsedSeconds();

        /// <summary>
        /// 给看门狗心跳用的数据面计数器。心跳里带上它是为了答一个以前答不了的问题：
        ///   run 30 那一轮计数停在什么形状上死的（encap 还在涨、inj 不涨 = 内核不回；
        ///   两个都不涨 = 平台没把包交给我们）。没有逐轮心跳，事后只看得到会话终值。
        ///
        /// ★ <c>ret</c>（归还的 send buffer 数）是 run 32 之后最关键的一列：
        ///   <c>ret</c> 与 <c>encap</c> 必须<b>同步涨</b>。一旦 ret 落后，就是"包进来了、
        ///   buffer 没还"，平台 send 池被逐包借空 —— 那正是四臂同停在 7188 包的机制。
        /// </summary>
        internal string DatafaceCounters() =>
            $"encap={_encapsulatedPackets} inj={_injectedPackets} ka={_keepAlives} " +
            $"calls={_encapsulateCalls} seen={_encapListItemsSeen} max={_encapListSizeMax} enum={_encapEnumerated} " +
            $"ret={_sendBuffersReturned}/{LocalSettings.EncapReturn} retFail={_sendReturnFailures} " +
            $"flush={_sendBuffersFlushed} reapp={_sendBuffersReappended} " +
            $"err={System.Threading.Volatile.Read(ref _encapsulateErrors)} " +
            $"left={_sendBuffersLeft} injFail={s_injectFailures} " +
            $"rxpend={System.Threading.Volatile.Read(ref s_receiveBuffersPending)} " +
            $"rxflush={System.Threading.Volatile.Read(ref s_receiveBuffersFlushed)} " +
            $"decap={_decapsulateCalls} kaOrphan={_keepAliveOrphanBuffers} " +
            $"big={s_oversizedPackets} drop={_nonUnicastDropped}";


        // ────────────────────────────────────────────────────────────────────
        // S7 · Disconnect
        // 【官方】"instructs the VPN plug-in to disconnect from the VPN server and destroy
        //          the VPN channel."
        // ────────────────────────────────────────────────────────────────────
        public void Disconnect(VpnChannel channel)
        {
            // ★★ Disconnect 是平台同步回调：<b>绝不抛出</b>，而且 <c>channel.Stop()</c> 必须
            //   <b>一定</b>被执行。旧写法是"一长串收尾语句 + 最后一行 Stop()"——
            //   中间任何一步抛（LogDiagnosticMessage 偶发不可用、P/Invoke <c>clash_bridge_stop</c>、
            //   采样落盘…）都会同时造成两件坏事：Stop 没调到（通道与它下发的 NRPT/路由没人关，
            //   正是 00:21 那类"拆不干净 = 整机断网"的入口形状），异常又漏给平台
            //   （run 34 实证：漏异常之后平台不再理这个插件）。
            //   ⇒ 收尾主体包 try；<c>Stop()</c> 与诊断落盘放 finally，各自再兜一层。
            //
            // ★ <c>_started</c> 抢在第一行置假：泵的三个门闩里它就是"别再往这条通道注包"的信号，
            //   放在收尾中间意味着异常一抛、泵还在对一条正在拆的通道注入。
            _started = false;

            try
            {
                DisconnectCore(channel);
            }
            catch (Exception ex)
            {
                LogSession($"evt=disconnect-inner-error err={ex.GetType().Name}: {ex.Message}");
                // ★ 这一行是"拆不干净"的现场：收尾主体抛了 ⇒ 后面的 Stop/快照可能没走完。
                //   平台回调里不能抛，所以只能靠日志把这次半途收尾留下证据。
                RollingLog.Error("disconnect-inner", "Disconnect 收尾主体抛异常（通道可能未收干净）", ex);
                try { channel.LogDiagnosticMessage("ClashUWP: Disconnect 内部异常（已兜住）: " + ex.Message); }
                catch { /* 日志出口偶尔不可用 */ }
            }
            finally
            {
                // 【官方】Stop()："Destroys a previously established VPN channel object.
                //   **Disassociates and closes the outerTunnelTransport connection to the VPN server.**"
                // ⚠️ socket 所有权在 Start 成功时已转移给框架，Stop 之后<b>不要</b>自己 Dispose。
                try
                {
                    channel.Stop();
                }
                catch (Exception exStop)
                {
                    // Stop 失败 = 平台侧通道还开着。插件已经没有别的原语可用
                    // （26100 上没有 Disconnect()，见 Docs/09 §4），只剩如实记录这一件事。
                    LogSession($"evt=channel-stop-failed err={exStop.GetType().Name}: {exStop.Message}");
                }

                // 诊断文件也放进 finally：内部异常那一路同样要留下数据面终值。
                try { WriteDatafaceSnapshot(); } catch { /* 写不了就算了 */ }
            }
        }

        /// <summary>
        /// <c>Disconnect</c> 的收尾主体（不抛、不含 <c>Stop()</c> 与快照落盘 —— 那两件事
        /// 由 <see cref="Disconnect"/> 的 finally 保证，见那里的注释）。
        /// </summary>
        private void DisconnectCore(VpnChannel channel)
        {
            // ★ 平台已发起收尾 ⇒ 看门狗立刻停，避免它和平台同时拆（它会看到"探针不通"，
            //   而那时拆隧道是正常收尾，不是黑洞）。
            SessionWatchdog.Stop("平台回调 Disconnect");

            // ★ 会话日志：活多久、收了多少包、平台索取过几次 keep-alive。
            //   828 到底是"平台拆通道不回调"还是"回调了我们自己收尾"，就看这一行
            //   与下一条 connect-begin 之间的间隔（不回调 ⇒ 两行 connect-begin 相邻、无 disconnect）。
            LogSession($"evt=disconnect alive={ElapsedSeconds():F0}s " +
                       $"encap={_encapsulatedPackets} inj={_injectedPackets} keepAlives={_keepAlives} " +
                       $"calls={_encapsulateCalls} seen={_encapListItemsSeen} max={_encapListSizeMax} enum={_encapEnumerated} " +
                       $"ret={_sendBuffersReturned}/{LocalSettings.EncapReturn} retFail={_sendReturnFailures} " +
                       $"flush={_sendBuffersFlushed} reapp={_sendBuffersReappended} " +
                       $"left={_sendBuffersLeft} retErr={_sendReturnFirstError} enumErr={_encapEnumFirstError} " +
                       $"encapErr={_encapsulateErrors} encapErrFirst={_encapsulateFirstError} " +
                       $"injFail={s_injectFailures} injErr={s_injectFirstError} " +
                       $"rxpend={System.Threading.Volatile.Read(ref s_receiveBuffersPending)} " +
                       $"rxflush={System.Threading.Volatile.Read(ref s_receiveBuffersFlushed)} " +
                       $"decap={_decapsulateCalls} decapFirst={_decapFirstInfo} " +
                       $"kaOrphan={_keepAliveOrphanBuffers} batch={LocalSettings.ReceiveBatch} " +
                       $"getBuf={(LocalSettings.InjectGetBuffer ? 1 : 0)}");
            // 人读的那一行只给存活与包量，其余计数器留在 sessions 行里 ——
            // plugin.log 是时间轴，不是第二份账本；要细账就看 dataface-plugin.txt 的快照。
            RollingLog.Info("session", $"会话结束，存活 {ElapsedSeconds():F0}s " +
                       $"encap={_encapsulatedPackets} inj={_injectedPackets} keepAlives={_keepAlives}");

            // 归因（任务 #20）想过用 <c>channel.DisconnectReason</c>，实测**取不到**：
            //   本工程的 TFM 是 10.0.26100，编译直接 CS1061「VpnChannel 未包含 DisconnectReason」
            //   —— 文档方法表里那一行属于更高 contract（winrt-28000/Insider），
            //   也就是说"平台为什么拆这条通道"在 26100 上对插件根本不暴露。
            //   所以归因只能靠两样我们已经有的东西：这条 disconnect 与下一条
            //   connect-begin 之间的间隔（有没有回调），以及 RasClient 事件日志里的原因码。
            //   再加一条逐轮心跳（看门狗的 evt=hb），至少能定位"计数停在什么形状上死的"。

            channel.LogDiagnosticMessage(
                $"ClashUWP: 断开。会话存活 {ElapsedSeconds():F0} s，keep-alive 索取 {_keepAlives} 次。" +
                $"共处理 {_encapsulatedPackets} 包（注入 {_injectedPackets}，echo 应答 {_echoReplies}，" +
                $"组播丢弃 {_nonUnicastDropped}，补校验和 IP={_ipChecksumFixed}/L4={_transportChecksumFixed}，" +
                $"超 MTU 未注入 {s_oversizedPackets}，异常 {_unknownPackets}）.");

            // 收尾把两侧采样写全：会话结束时的这份是定位黑洞的主证据。
            WriteTunSamples("会话结束");

            // 进程级收尾：退泵 +（按开关）停桥。
            //
            // ★ 对齐安卓版的生命周期：ClashMetaForAndroid 的内核只在 TunService 起来时
            //   装一次（ClashRuntime 进入时 reset、退出时才 reset），会话之间靠
            //   proxy-group 的健康检查换节点，而不是把内核销毁重建。
            //   我们实测到的"同进程第二次 Start 后 TLS 回不来"正好落在销毁重建这条路上
            //   （官方文档也明说 VpnChannel/插件进程不支持跨会话反复起通道）。
            //   所以默认走常驻：只摘泵、只排空回程队列，桥留着给下一次连接复用。
            //   关闭常驻（settings.txt: residentBridge=0）时退回旧行为，便于 A/B 定位。
            lock (s_bridgeGate)
            {
                var cts = s_pumpCts;
                s_pumpCts = null;
                s_bridgeGlobal = false;
                try { cts?.Cancel(); } catch { /* 尽力收尾 */ }

                // 快照先取：Go 侧 Stop 会把 boundIface 清零。
                try { s_boundIfaceSnapshot = MihomoBridge.BoundInterface(); } catch { }

                int drained = DrainBridgeQueue();

                if (LocalSettings.ResidentBridge)
                {
                    s_bridgeResident = true;
                    _bridgeDiag += $"｜会话结束，内核常驻（排空回程 {drained} 包）";
                    channel.LogDiagnosticMessage(
                        $"ClashUWP: 回程泵已退出、排空 {drained} 包，mihomo 内核保持常驻.");
                }
                else
                {
                    MihomoBridge.clash_bridge_stop();
                    s_bridgeResident = false;
                    channel.LogDiagnosticMessage("ClashUWP: mihomo 内核随会话停止（residentBridge=0）.");
                }
            }

            _transport = null;
            if (ReferenceEquals(s_activeChannel, channel))
                s_activeChannel = null;

            // （<c>channel.Stop()</c> 与 <c>WriteDatafaceSnapshot()</c> 都在
            //  <see cref="Disconnect"/> 的 finally 里，收尾主体走到一半抛异常也照样执行。）
        }

        /// <summary>
        /// 把 bridge 诊断与包计数写到 <c>LocalState/dataface-plugin.txt</c>。
        /// fire-and-forget：Disconnect 是平台回调，不能阻塞；写失败不影响收尾。
        /// </summary>
        private void WriteDatafaceSnapshot()
        {
            // BoundIface 用 Disconnect 里抢在停桥前取的快照 —— Go 侧 Stop 会清零，
            // 现场再查只会得到空串，把「绑定生效过」误报成「从未绑定」。
            string content =
                $"Time={DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n" +
                $"Bridge={_bridgeDiag}\r\n" +
                $"BoundIface={s_boundIfaceSnapshot}\r\n" +
                $"ResidentBridge={LocalSettings.ResidentBridge}\r\n" +
                $"DnsIntoTunnel={LocalSettings.DnsIntoTunnel} ({string.Join(",", _config.DnsServers)})\r\n" +
                $"NodeExclusions={(_nodeExclusions.Count)} switch={(LocalSettings.NodeExclusions ? "on" : "off")} /32\r\n" +
                $"SelfSplitTunnel={_selfSplitFilterInfo}\r\n" +
                $"OuterTarget={_outerTargetUsed}:{LocalSettings.OuterPort} (要求={LocalSettings.OuterTarget}) fakeIp={(LocalSettings.FakeIp ? "on" : "off")}\r\n" +
                $"IncludeMode={LocalSettings.IncludeMode} inclusion=[{string.Join(",", (LocalSettings.IncludeMode.Equals("narrow", StringComparison.OrdinalIgnoreCase) ? NarrowInclusionRoutes() : _config.IncludeRoutes))}]\r\n" +
                $"SessionSeconds={ElapsedSeconds():F0}\r\n" +
                $"KeepAlives={_keepAlives}\r\n" +
                $"Encapsulated={_encapsulatedPackets}\r\n" +
                $"Injected={_injectedPackets}\r\n" +
                // ★ run 34 只留下 "Encapsulated=0" 这一个数，分不清"平台没回调 Encapsulate"
                //   和"回调了但档 3 的 foreach 一条没枚举出来"。这三列就是为区分它们而加的：
                //   EncapsulateCalls = 平台回调次数，EncapListItemsSeen = 列表累计交包数，
                //   EncapMaxBatch = 单批最大，EncapEnumerated = 档 3 真正枚举到的条数。
                $"EncapsulateCalls={_encapsulateCalls} EncapListItemsSeen={_encapListItemsSeen} " +
                $"EncapMaxBatch={_encapListSizeMax} EncapEnumerated={_encapEnumerated} " +
                $"EncapEnumFirstError={_encapEnumFirstError}\r\n" +
                // ★ 外层守卫兜住的异常：这一列非 0 = 内部真的抛过、只是没让平台看见（会话因此得以继续）。
                $"EncapsulateErrors={_encapsulateErrors} EncapsulateFirstError={_encapsulateFirstError}\r\n" +
                // ★ run 32 定性的泄漏：这两列必须与 Encapsulated 同步，落后就是"包进来了 buffer 没还"。
                $"EncapReturn={_encapReturnMode} SendBuffersReturned={_sendBuffersReturned} " +
                $"SendBuffersFlushed={_sendBuffersFlushed} SendBuffersReappended={_sendBuffersReappended} " +
                $"SendBuffersLeftForCleanup={_sendBuffersLeft} " +
                $"SendReturnFailures={_sendReturnFailures}\r\n" +
                $"SendReturnFirstError={_sendReturnFirstError}\r\n" +
                // ★ receive 侧的对称三列（Docs/09 O2）：rxpend 是"已 Append 未 Flush"的在借数，
                //   正常应当贴着 0 抖；rxflush 与 Injected+OversizedDropped 对得上账才算闭环。
                $"ReceiveBatch={LocalSettings.ReceiveBatch} InjectGetBuffer={(LocalSettings.InjectGetBuffer ? 1 : 0)} " +
                $"ReceiveBuffersPending={System.Threading.Volatile.Read(ref s_receiveBuffersPending)} " +
                $"ReceiveBuffersFlushed={System.Threading.Volatile.Read(ref s_receiveBuffersFlushed)}\r\n" +
                // ★ Decapsulate 计数：预期恒为 0（不走 outer transport）。非 0 就是"外层确实进了数据
                //   却被我们扔了"的直接证据 —— 这条推理以前只是推理，现在可证伪（Docs/09 O1）。
                $"DecapsulateCalls={_decapsulateCalls} DecapFirst={_decapFirstInfo} " +
                $"KeepAliveOrphanBuffers={_keepAliveOrphanBuffers}\r\n" +
                $"InjectFailures={s_injectFailures} InjectFirstError={s_injectFirstError}\r\n" +
                $"NonUnicastDropped={_nonUnicastDropped}\r\n" +
                $"IpChecksumFixed={_ipChecksumFixed}\r\n" +
                $"TransportChecksumFixed={_transportChecksumFixed}\r\n" +
                $"OversizedDropped={s_oversizedPackets}\r\n" +
                $"EchoReplies={_echoReplies}\r\n" +
                $"Unknown={_unknownPackets}\r\n" +
                // ★ 日志自身的健康度也进快照。滚动日志在队列满时是<b>故意丢行</b>的
                //   （宁可少几行也绝不在平台回调里等磁盘），没有这一行，
                //   "plugin.log 里查不到某件事"就分不清「没发生」还是「被丢了」。
                $"Log={RollingLog.Diag()}\r\n";

            _ = Task.Run(async () =>
            {
                try
                {
                    var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                    var file = await folder.CreateFileAsync(
                        "dataface-plugin.txt", Windows.Storage.CreationCollisionOption.ReplaceExisting);
                    await Windows.Storage.FileIO.WriteTextAsync(file, content);
                }
                catch (Exception ex)
                {
                    // 后台任务里 ApplicationData 偶发不可用；诊断文件写不了就不写。
                    // ★ 但要在滚动日志里留一行 —— 否则前台读到的快照是<b>上一次会话</b>的，
                    //   而它看起来和本次一模一样（文件名、字段齐全），最容易把人骗进去。
                    RollingLog.Error("snapshot", "dataface-plugin.txt 写入失败，前台读到的可能是旧快照", ex);
                }
            });
        }

        // ────────────────────────────────────────────────────────────────────
        // 路线 A · mihomo 数据面接入
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 启动/复用进程内 mihomo 数据面（clash_bridge.dll）并拉起回程泵。
        /// 失败只记日志不抛出 —— 连接本身已建立，Encapsulate 会回落假内核 echo。
        ///
        /// ★ 三条进入路径：
        ///   1) 桥没在跑（本进程首次）→ 全量 Start，Go 侧会 Parse 配置 + 建 gvisor 栈 +
        ///      ApplyConfig + 绑物理网卡；
        ///   2) 常驻复用（Disconnect 没停桥，或上一次 Start 与 Disconnect 交错留下活桥，
        ///      rc=-2 "already started"）→ 不重建，只把新泵挂到当前 channel；配置变了
        ///      就走 mihomo REST 控制器热重载
        ///      （<c>PUT /configs?force=true</c> + body <c>{"path":"…"}</c>，
        ///      换订阅的做法），没变就原样继续；形态的取舍见 <see cref="TryHotReload"/>
        ///   3) 其它 rc≠0 → 记诊断、不阻断连接。
        /// </summary>
        private void StartMihomoBridge(VpnChannel channel, string config, string? configPath)
        {
            // ★ G10（Docs/12 §5 第 2 项）：内核 home 从「包安装目录」换成「LocalState」，
            //   并把随包的 Country.mmdb 复制进去 —— 两件事必须一起做，因为 mihomo 的
            //   MMDB 解析就是 ReadDir(home) 里找 Country.mmdb。
            //   home 之前是只读目录，后果是三件静默失败：<cache.db 选节点持久化>、
            //   <provider 落盘>、<热重载的 path 安全阀（SafePaths 只有 home 之内）>。
            //   为什么必须"复制"而不是"直接指过去"，以及退回包目录的兜底口径，
            //   全写在 <see cref="KernelHome"/> 的类注释里。
            string mmdbDir = KernelHome.Ensure();

            lock (s_bridgeGate)
            {
                // 上一任的泵先摘除（它可能还挂在已 Stop 的旧 channel 上）。
                var old = s_pumpCts;
                s_pumpCts = null;
                try { old?.Cancel(); } catch { }

                // ★ 复用/首次都要先排空回程队列：常驻时队列里可能留着上一会话的包，
                //   注进新建的 L3 接口就是凭空给 TCP 塞陈旧段（TLS 首包失败的现成成因）。
                int drained = DrainBridgeQueue();

                bool configChanged = Fingerprint(config) != s_lastConfigFingerprint;

                int rc = 0;
                bool calledStart = false;

                if (!s_bridgeResident)
                {
                    // 路径 1：本进程首次（或上一次以 residentBridge=0 停过桥）→ 全量启动。
                    // 路径 2：常驻复用 —— 根本不再调用 clash_bridge_start，
                    //   这样"同进程第二次 Start"这条病态路径在本版本里不存在。
                    try
                    {
                        rc = MihomoBridge.Start(config, (int)_config.Mtu, mmdbDir);
                        calledStart = true;
                    }
                    catch (Exception ex)
                    {
                        // 常见于 DLL 缺失（DllNotFoundException）或入口缺失（EntryPointNotFoundException）。
                        channel.LogDiagnosticMessage("ClashUWP: mihomo bridge 启动异常: " + ex.Message);
                        _bridgeDiag = "启动异常: " + ex.Message;
                        s_bridgeGlobal = false;
                        return;
                    }
                }

                if (s_bridgeResident ||
                    (calledStart && rc == -2 && MihomoBridge.LastError().Contains("already started")))
                {
                    // 桥已在跑（常驻或残留）：泵改挂到当前 channel，数据面继续可用。
                    s_bridgeResident = true;
                    s_bridgeGlobal = true;
                    _bridgeDiag = $"复用常驻内核（排空 {drained} 包" +
                                  (configChanged ? "，配置已变 → 尝试热重载" : "，配置未变") + "）";
                    channel.LogDiagnosticMessage("ClashUWP: 内核常驻，回程泵改挂当前 channel.");

                    if (configChanged)
                        TryHotReload(channel, configPath);
                }
                else if (rc != 0)
                {
                    channel.LogDiagnosticMessage($"ClashUWP: mihomo bridge start 失败 rc={rc}: {MihomoBridge.LastError()}");
                    _bridgeDiag = $"start 失败 rc={rc}: {MihomoBridge.LastError()}";
                    s_bridgeGlobal = false;

                    // ★ failFast（默认开）：内核没起来就<b>不要</b>留着这条隧道。
                    //   run 25 实测的形态正是"配置解析失败 → 只记日志 → 隧道照装"：
                    //   快照 Encapsulated=883 / Injected=0，整机断网 60~90 s 才由哨兵兜住。
                    //   抛出去会让 Connect 走 catch（平台据此终止本次连接），
                    //   物理网卡立刻恢复 —— "连不上就退回直连"比"连上了但全黑"有用得多。
                    if (LocalSettings.FailFast)
                        throw new InvalidOperationException(
                            $"mihomo 内核启动失败，已按 failFast 放弃本次连接：rc={rc} {MihomoBridge.LastError()}");

                    return;
                }
                else
                {
                    s_bridgeGlobal = true;
                    s_bridgeResident = true;
                    _bridgeDiag = $"本进程首次启动（排空 {drained} 包）";
                    channel.LogDiagnosticMessage("ClashUWP: mihomo bridge 已启动，回程泵运行中.");
                }

                s_lastConfigFingerprint = Fingerprint(config);

                // 回程泵：mihomo 的回包是异步产生的，VpnChannel 没有「有包了」的事件，
                // 只能轮询 get_packet → Inject。5ms 一拍，无包时休眠不空转。
                var cts = new CancellationTokenSource();
                s_pumpCts = cts;
                _ = Task.Run(() => PumpLoop(channel, cts.Token));
            }
        }

        /// <summary>
        /// 排空回程队列里的残留包（不注入）。返回排掉的包数。
        /// 常驻桥的上一会话可能留包，必须丢干净再接新 L3 接口。
        /// </summary>
        private static int DrainBridgeQueue()
        {
            int n = 0;
            try
            {
                byte[] buf = new byte[4096];
                for (int i = 0; i < 4096; i++)
                {
                    int rc = MihomoBridge.clash_bridge_get_packet(buf, buf.Length, out int len);
                    if (rc != 1 || len <= 0)
                        break;
                    n++;
                }
            }
            catch
            {
                // 桥没起来时这里会抛/返回 -1，无所谓
            }
            return n;
        }

        /// <summary>
        /// 常驻时热重载订阅：让 mihomo 自己的 REST 控制器换配置，
        /// 避免为了换配置去走"销毁 + 第二次 Start"这条已知有病的路径。
        /// 失败不致命 —— 只记诊断，沿用当前配置继续跑。
        ///
        /// 【★★ 形态必须按 v1.19.32 的源码写，不能按直觉】
        ///   <c>hub/route/configs.go updateConfigs</c>（我们 <c>go.mod</c> pin 的就是 v1.19.32，
        ///   与 Docs/12 §7 实测用的官方二进制同版本，所以这份源码就是权威口径）：
        ///   <code>
        ///   req := struct{ Path string `json:"path"`; Payload string `json:"payload"` }{}
        ///   render.DecodeJSON(r.Body, &amp;req)          // ⇒ path 只能从 <b>JSON body</b> 来
        ///   force := r.URL.Query().Get("force") == "true"  // ⇒ force 只能从 <b>query</b> 来
        ///   </code>
        ///   所以正确的一条是 <c>PUT /configs?force=true</c> + body <c>{"path":"&lt;绝对路径&gt;"}</c>
        ///   —— <b>两个参数分属两个位置</b>。Docs/12 §7 那行"body {path,force} → 204"
        ///   其实是在 <c>force=false</c> 下重载成功的（body 里的 force 根本没人读），
        ///   这条已经按源码更正。
        /// 【★ 为什么以前"从来没生效过"，其实有两把锁】
        ///   ① 老代码把 path 拼在 query 上、body 为空 ⇒ <c>DecodeJSON</c> 读不到 path，
        ///     于是走 <c>req.Path == ""</c> 分支去读 <c>C.Path.Config()</c>（home 里的
        ///     <c>config.yaml</c>，我们根本没有那份文件）⇒ 400。实测复现见 §7。
        ///   ② 就算改对 body 也还是 400：<c>IsSafePath</c> 要求 path 在 <b>home 之内</b>
        ///     （<c>SafePaths() = [homeDir] + SAFE_PATHS</c>；只有 CMFA 构建标记或
        ///     <c>allowUnsafePath</c> 才放行，我们两者都没有）。而改写后的配置写在
        ///     <c>LocalState</c>，home 原先却在只读的包目录 ⇒ 不在 safePaths 里。
        ///     ⇒ 这一把锁是 <see cref="KernelHome"/>（G10）解的：<b>G10 不做，
        ///     热重载就是修不好</b>，两条得一起上。
        /// 【path 用哪个】<c>configPath</c> 传进来的是
        ///   <c>KernelConfig.WriteEffectiveFile</c> 的返回值（<c>config.kernel.yaml</c> 的
        ///   绝对路径），与喂给 <c>MihomoBridge.Start</c> 的是<b>同一份内容</b> ——
        ///   热重载必须读磁盘上这份，否则常驻时会把 fake-ip 覆盖回订阅原文（1.0.23 的注释同义）。
        /// </summary>
        private void TryHotReload(VpnChannel channel, string? configPath)
        {
            if (!LocalSettings.HotReload || string.IsNullOrEmpty(configPath))
            {
                _bridgeDiag += "｜热重载跳过（无配置文件或开关关闭）";
                return;
            }

            // fire-and-forget：Connect 是平台同步回调，不能在这儿等 HTTP。
            _ = Task.Run(async () =>
            {
                try
                {
                    using var http = new System.Net.Http.HttpClient(
                        new System.Net.Http.HttpClientHandler { UseProxy = false });
                    http.Timeout = TimeSpan.FromSeconds(5);

                    // ★ 手写 JSON：本组件最终随前台一起 Native AOT，反射版
                    //   JsonSerializer.Serialize 会直接抛（前台 MihomoController 同样的坑，
                    //   实测 measure-12.log）。Windows 路径全是反斜杠，<b>必须转义</b> ——
                    //   不转义的话 {"path":"C:\Users\…"} 连 JSON 都不成立。
                    var sb = new StringBuilder();
                    sb.Append("{\"path\":\"");
                    foreach (char ch in configPath!)
                    {
                        if (ch is '"' or '\\') sb.Append('\\').Append(ch);
                        else if (ch == '\n') sb.Append("\\n");
                        else if (ch == '\r') sb.Append("\\r");
                        else if (ch == '\t') sb.Append("\\t");
                        else if (ch < 0x20) sb.Append($"\\u{(int)ch:x4}");
                        else sb.Append(ch);
                    }
                    sb.Append("\"}");

                    using var req = new System.Net.Http.HttpRequestMessage(
                        System.Net.Http.HttpMethod.Put,
                        "http://127.0.0.1:9090/configs?force=true")
                    {
                        Content = new System.Net.Http.StringContent(
                            sb.ToString(), Encoding.UTF8, "application/json")
                    };
                    using System.Net.Http.HttpResponseMessage resp = await http.SendAsync(req);
                    int code = (int)resp.StatusCode;
                    string body = "";
                    if (!resp.IsSuccessStatusCode)
                    {
                        // 400 的原文就写着"为什么"（path is not a absolute path /
                        // not safe path / parse error），必须带回来 —— 只记状态码
                        // 等于把两把锁重新变成猜谜。
                        try { body = await resp.Content.ReadAsStringAsync(); }
                        catch { /* 读不到原文也别把热重载带崩 */ }
                        if (body.Length > 200) body = body[..200] + "…";
                    }
                    _bridgeDiag += $"｜热重载={code}" + (body.Length == 0 ? "" : $" {body}");
                    // ★ 同步进滚动日志：_bridgeDiag 只在会话快照里，而快照是"下一次断开"
                    //   才落盘的文件；热重载是不是真的生效，得能在 plugin.log 里当场查到。
                    if (resp.IsSuccessStatusCode)
                        RollingLog.Info("hot-reload", $"PUT /configs?force=true -> {code}");
                    else
                        RollingLog.Warn("hot-reload", $"PUT /configs?force=true -> {code} {body}");
                    try { channel.LogDiagnosticMessage($"ClashUWP: 配置热重载 HTTP {code}"); }
                    catch { /* 日志出口偶尔不可用 */ }
                }
                catch (Exception ex)
                {
                    _bridgeDiag += $"｜热重载失败={ex.GetType().Name}";
                    RollingLog.Error("hot-reload", configPath ?? "", ex);
                }
            });
        }

        /// <summary>配置指纹（长度 + 简单散列），够判断"订阅换没换"。</summary>
        private static string Fingerprint(string s) => $"{s.Length}:{s.GetHashCode():X8}";

        /// <summary>
        /// 回程泵：把 gvisor 栈发出的原始 L3 包逐个注回本机协议栈。
        /// 在后台线程运行（Inject 不受 Decapsulate 时序约束 —— 本架构的关键自由度）。
        /// 三处门闩任何一个关闭即退出：token（被摘泵/停桥）、_started（本连接断开）、
        /// s_bridgeGlobal（进程级桥已停 —— 修掉旧泵「channel 活着就空转到永远」的漏）。
        /// </summary>
        private void PumpLoop(VpnChannel channel, CancellationToken token)
        {
            byte[] buf = new byte[4096];

            // ★ 攒批档只在泵入口读一次：中途换档会让同一条泵的 Flush 边界前后不一致，
            //   <c>rxpend</c> 就当不了"在借未还"的读数了（这个开关本来也只在该进程重连时才该变）。
            //   <c>batch<=0</c>（默认）= 每包一次 Append+Flush，与 run 35/37 实测形态逐字一致；
            //   <c>batch=N</c>       = 攒到 N 包（或取空/休眠/退出前）才提交一次。
            int batch = LocalSettings.ReceiveBatch;
            int pending = 0;

            while (!token.IsCancellationRequested && _started && s_bridgeGlobal)
            {
                try
                {
                    int rc = MihomoBridge.clash_bridge_get_packet(buf, buf.Length, out int n);
                    if (rc == 1 && n > 0)
                    {
                        // 取包与注入之间再查一次：Disconnect 恰好落在中间时，
                        // 不能把包注进已经 channel.Stop() 的通道。
                        if (token.IsCancellationRequested || !_started || !s_bridgeGlobal)
                            break;
                        byte[] packet = new byte[n];
                        Array.Copy(buf, packet, n);
                        // ★ 注入失败 = receive 池借不动了（run 32 定性的同类泄漏只会更早在这里现形）。
                        //   三条处置里只有一条对：
                        //     · 退出泵 ⇒ 回程永久断，形状与整机黑洞无法区分（绝不做）；
                        //     · 热转重试 ⇒ 池空时逐包抛异常，把 CPU 烧光（绝不做）；
                        //     · 丢这一个包 + 背压休眠 ⇒ 包已经被 bridge 消费、还不回去，只能丢；
                        //       TCP 自己会重传，UDP 少一个包也只是少一个包，比"隧道再也不注入"好得多。
                        bool ok;
                        if (batch > 0)
                        {
                            ok = InjectAppend(channel, packet);
                            if (ok) pending++;
                        }
                        else
                        {
                            ok = Inject(channel, packet);
                        }

                        if (!ok)
                        {
                            // 失败也要先把<b>攒着的</b>提交掉：pending 里的 buffer 已经 Append 过，
                            // 只是没 Flush —— 带着它们休眠就是"在借未还"，正是 send 侧 7188 的形状。
                            if (pending > 0) { FlushReceive(channel); pending = 0; }
                            token.WaitHandle.WaitOne(InjectBackoffMs);
                            continue;
                        }
                        Interlocked.Increment(ref _injectedPackets);

                        {
                            // 回程侧优先记「单播回包」（那才是给本机的真回复）。
                            // 前 3 个不看类型，保证会话最初的形状也留得下来。
                            // 条数上限由 AddSample 兜（满 12 条即不再收）。
                            bool unicast = !IpPacket.IsNonUnicastDestination(packet, n);
                            if (unicast || _injectedPackets <= 3)
                                AddSample(s_inSamples,
                                    $"#{_injectedPackets}{(unicast ? "" : "(组播回写)")} {IpPacket.Describe(packet, n)}");
                        }

                        // 出向采满、回程也见到几个包就先落一次盘：万一会话中途崩掉，证据还在。
                        bool enough;
                        lock (s_sampleGate)
                            enough = s_outSamples.Count >= MaxTunSamples && s_inSamples.Count >= 3;
                        if (enough && Interlocked.Exchange(ref s_sampleWritten, 1) == 0)
                            WriteTunSamples("采样满额");

                        // 攒够一批就提交（batch<=0 时上面已经逐包提交过，pending 恒为 0）。
                        if (batch > 0 && pending >= batch) { FlushReceive(channel); pending = 0; }

                        continue; // 有包时立刻取下一个，不休眠
                    }
                }
                catch (Exception ex)
                {
                    // channel 已 Stop 或 bridge 已卸载：结束泵。
                    // ★ 退出前先把攒着的 buffer 提交掉 —— 泄漏一个未 Flush 的 receive buffer
                    //   比少注一个包严重（它永远回不了池子）。
                    if (pending > 0) { FlushReceive(channel); pending = 0; }
                    try { channel.LogDiagnosticMessage("ClashUWP: 回程泵退出: " + ex.Message); } catch { }
                    return;
                }

                // 本轮没取到包 ⇒ <b>先提交再休眠</b>：绝不让已 Append 的 buffer 跟着休眠漂着，
                // 那既让回程在内核队列里滞留，又让 rxpend 这一列失去意义。
                if (pending > 0) { FlushReceive(channel); pending = 0; }
                token.WaitHandle.WaitOne(5);
            }

            // 门闩关闭 / break 退出：收尾同上一条 —— 有 pending 一定 Flush。
            if (pending > 0)
                FlushReceive(channel);
        }

        /// <summary>
        /// 取 mihomo 配置：优先应用 LocalFolder\config.yaml（前台应用写入），
        /// 缺失时用内置最小配置（rule 模式 + DIRECT，tun.enable=false —— 路由归 VpnChannel 管）。
        /// 返回值第二项是配置文件绝对路径，只在真的读了文件时非空 —— 常驻热重载要用它。
        /// </summary>
        private static (string Text, string? Path) LoadMihomoConfig()
        {
            try
            {
                var file = ApplicationData.Current.LocalFolder
                    .GetFileAsync("config.yaml").AsTask().GetAwaiter().GetResult();
                string path = file.Path;
                using (var stream = file.OpenStreamForReadAsync().GetAwaiter().GetResult())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string text = reader.ReadToEnd();
                    if (!string.IsNullOrWhiteSpace(text))
                        return (text, path);
                }
            }
            catch
            {
                // 没有外部配置 → 用内置最小配置。
            }

            // 注意：tun.enable 必须为 false —— executor 会在 enable=true 时走平台 tunNew
            // （wintun 设备），UWP 沙箱内不可能成功；数据面由本插件的 VirtualTun 承接。
            return ("""
                {
                  "mode": "rule",
                  "log-level": "info",
                  "ipv6": false,
                  "tun": { "enable": false, "stack": "gvisor" }
                }
                """, null);
        }

        // ────────────────────────────────────────────────────────────────────
        // 内部实现
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 把处理完的 IP 包注回本机协议栈 —— 这是 ClashUWP 数据面的出口。
        /// ★ 返回是否成功；<b>本方法不抛异常</b>（旧实现会把异常直接抛给回程泵，
        ///   泵一遇 <c>0x8007045B</c> 就整条退出 —— 一次背压就把回程永久关掉，
        ///   那是比丢一个包严重得多的病，见 PumpLoop 的注释）。
        ///
        /// 【官方】RequestVpnPacketBuffer(VpnDataPathType, out VpnPacketBuffer)：
        ///   "Requests an IVpnPacketBuffer from the specified pool. There is one IVpnPacketBuffer
        ///    pool for the send path and another IVpnPacketBuffer pool for the receive path."
        /// 【官方】AppendVpnReceivePacketBuffer(VpnPacketBuffer)：
        ///   "Appends a receive VpnPacketBuffer object to the channel."
        /// 【官方】FlushVpnReceivePacketBuffers()：
        ///   "Flushes any appended receive packet buffers."
        ///
        /// 另有一条官方明确支持 injection 的取包方式 GetVpnReceivePacketBuffer()：
        ///   "Requests a VpnPacketBuffer object from the receive packet buffer pool to be used
        ///    in the decapsulation or **injection** of a received packet from the VPN server
        ///    into the local network stack."
        ///   ⇒ 已实现为<b>可切换</b>的取包入口，见 <see cref="TakeReceiveBuffer"/>
        ///     （<c>settings.txt: injectGetBuffer</c>，默认仍走 <c>RequestVpnPacketBuffer</c>）。
        ///
        /// ★★ 与 <see cref="TryReturnSendBuffer"/> 对称的另一半：<b>借来的 receive buffer
        ///   也必须归还</b>。旧实现在"回程包超 MTU"那一支直接 <c>return</c>，buffer 就此丢失 ——
        ///   和 send 侧借空 7188 包是同一个病，只是 receive 侧的配额还没被我们撞上。
        ///   现在这条分支把 <c>Length</c> 置 0 原样归还（不注入内容，但把 buffer 还回池子）。
        ///
        /// 【1.0.34 的拆法】本方法 = <see cref="InjectAppend"/>（取包→写→Append）
        ///   + <see cref="FlushReceive"/>（提交），逐包 Flush ⇒ 与 run 35/37 的形态逐字一致。
        ///   攒批提交（<c>settings.txt: receiveBatch=N</c>）由回程泵直接调那两个方法，
        ///   Flush 边界与销账口径见 <see cref="PumpLoop"/>。
        ///   echo 回落路径（桥没起来）永远走本方法，不留未提交的 buffer。
        /// </summary>
        private static bool Inject(VpnChannel channel, byte[] packet)
        {
            bool ok = InjectAppend(channel, packet);
            FlushReceive(channel);
            return ok;
        }

        /// <summary>
        /// 取 receive 池 buffer → 写入 → <c>AppendVpnReceivePacketBuffer</c>，<b>不 Flush</b>。
        /// ★ 返回值只说明"这个包有没有被注入"，<b>不说明 buffer 的下落</b>：
        ///   只要取到了 buffer，三条出口（正常 / 超 MTU / Append 前抛异常）里前两条都把它
        ///   Append 回去了（超 MTU 那条 Append 一个 <c>Length=0</c> 的空包 —— 不灌内容但还池子），
        ///   差的就是一次 <c>Flush</c> 提交。
        /// ★ 与 <see cref="TryReturnSendBuffer"/> 对称的另一半：<b>借来的 receive buffer
        ///   也必须归还</b>。旧实现在"回程包超 MTU"那一支直接 <c>return</c>，buffer 就此丢失 ——
        ///   和 send 侧借空 7188 包是同一个病，只是 receive 侧的配额还没被我们撞上。
        /// </summary>
        private static bool InjectAppend(VpnChannel channel, byte[] packet)
        {
            VpnPacketBuffer outBuffer;
            try
            {
                outBuffer = TakeReceiveBuffer(channel);
            }
            catch (Exception ex)
            {
                // 池子空了（0x8007045B 一类）：这轮丢包、记下原因，让泵背压重试，绝不退出。
                NoteInjectFailure("request", ex);
                return false;
            }

            try
            {
                // IBuffer.Length 必须 ≤ Capacity（Receive 池的 Capacity = mtuSize = 1400）。
                // 真内核进来后这里要分片：超过 MTU 的包不能直接丢进去。
                if (packet.Length > outBuffer.Buffer.Capacity)
                {
                    // 不计数就等于「内核回了、但本机没收到」被误报成「内核没回」。
                    Interlocked.Increment(ref s_oversizedPackets);
                    outBuffer.Buffer.Length = 0;          // 空包：不往协议栈灌内容
                    channel.AppendVpnReceivePacketBuffer(outBuffer);
                    Interlocked.Increment(ref s_receiveBuffersPending);
                    return false;
                }

                outBuffer.Buffer.Length = (uint)packet.Length;
                using (var stream = outBuffer.Buffer.AsStream())
                {
                    stream.Write(packet, 0, packet.Length);
                    stream.Flush();
                }

                channel.AppendVpnReceivePacketBuffer(outBuffer);
                Interlocked.Increment(ref s_receiveBuffersPending);
                return true;
            }
            catch (Exception ex)
            {
                NoteInjectFailure("append", ex);
                return false;
            }
        }

        /// <summary>
        /// 提交所有已 Append 的 receive buffer（<c>FlushVpnReceivePacketBuffers</c>）。
        /// ★ 即使 <c>pending==0</c> 也照调：默认路径（<c>receiveBatch=0</c>）下
        ///   每次 Inject 都调它，行为与 run 35/37 逐字一致；空提交按官方口径是无副作用的。
        /// ★ 绝不抛出（泵线程与 Encapsulate 回落线程都会走到这里）。
        /// </summary>
        private static void FlushReceive(VpnChannel channel)
        {
            int pending = System.Threading.Volatile.Read(ref s_receiveBuffersPending);
            try
            {
                channel.FlushVpnReceivePacketBuffers();
            }
            catch (Exception ex)
            {
                NoteInjectFailure("flush", ex);
                return;
            }

            if (pending > 0)
            {
                // 只在 Flush 成功后销账：失败时 buffer 仍算"在借未还"，
                // 让 rxpend 这一列诚实反映借没还的量，而不是被一次失败的 Flush 抹平。
                Interlocked.Add(ref s_receiveBuffersPending, -pending);
                Interlocked.Add(ref s_receiveBuffersFlushed, pending);
            }
        }

        /// <summary>
        /// 从 receive 池取一个 buffer，两条官方入口按开关二选一。
        ///
        /// 【默认】<c>RequestVpnPacketBuffer(VpnDataPathType.Receive, out buf)</c>：
        ///   "Requests an IVpnPacketBuffer from the specified pool. There is one pool for the
        ///    send path and another pool for the receive path." —— run 35/37 就是这条。
        /// 【<c>injectGetBuffer=1</c>】<c>GetVpnReceivePacketBuffer()</c>：
        ///   "...to be used in the decapsulation or <b>injection</b> of a received packet from
        ///    the VPN server into the local network stack." —— 官方<b>点名</b>给 injection 的入口，
        ///   本品在 <c>Decapsulate</c> 之外（泵线程）投包，条文上更贴这一条。
        /// ★ 两条借的是同一个池、还的路径（Append+Flush）也一样，差别只在取包层 ⇒
        ///   没有实测证据前不改默认（口径同 <c>EncapReturn</c>）。
        /// </summary>
        private static VpnPacketBuffer TakeReceiveBuffer(VpnChannel channel)
        {
            if (LocalSettings.InjectGetBuffer)
            {
                VpnPacketBuffer? got = channel.GetVpnReceivePacketBuffer();
                if (got == null)
                    throw new InvalidOperationException("GetVpnReceivePacketBuffer() 返回 null");
                return got;
            }

            channel.RequestVpnPacketBuffer(VpnDataPathType.Receive, out VpnPacketBuffer buf);
            if (buf == null)
                throw new InvalidOperationException("RequestVpnPacketBuffer(Receive) 返回 null");
            return buf;
        }

        /// <summary>注入失败计数 + 首个原因（只写一次日志）。静态口径：跨会话累计。</summary>
        private static void NoteInjectFailure(string stage, Exception ex)
        {
            Interlocked.Increment(ref s_injectFailures);
            if (s_injectFirstError.Length == 0)
                s_injectFirstError = stage + " " + ex.GetType().Name + ":" +
                    ex.Message.Replace('\r', ' ').Replace('\n', ' ');
        }

        /// <summary>
        /// 把 <c>settings.txt: outerTarget</c> 解析成可用的主机名。
        ///
        /// ★【病根：外层 socket 一旦被对端拒绝，平台就"自动重启"这条 VPN】
        ///   微软官方答复原话（VPN Plugin framework and detection of closed peer connection）：
        ///   <i>"The OS will detect that the outer socket has been closed and will
        ///   automatically restart (you'll get a new connect call)."</i>
        ///   先遣项目 docs/93 用单变量把它坐实：把包灌进外层传输 ⇒ 外层 socket 报错 ⇒
        ///   OS 拆通道 ⇒ 全新进程重新 Connect，实测一臂内 4 个不同 pid、从不回调 Disconnect。
        ///   本项目 run 25 之后从事件日志查到同一形状的风暴：RasClient 单日
        ///   dial=413 / terminated=414，终止原因码 828 占 411 次，相邻间隔稳定 97±2 s。
        ///
        /// 【所以外层目标必须是"静默黑洞"，不能是活服务器】
        ///   平台的 keep-alive 是要<b>真的发出去</b>的（GetKeepAlivePayload 给的 Send 池包）。
        ///   旧默认 <c>auto</c> 解析成"订阅里第一个节点 IP"，节点机器的 UDP/53 多半没人监听
        ///   ⇒ 回 ICMP port unreachable ⇒ 外层 socket 被判关闭 ⇒ 平台拆通道重拨。
        ///   改用 <c>192.0.2.1</c>（RFC 5737 TEST-NET-1 文档保留段）：包到网关即被丢弃，
        ///   <b>既没人应答也没有 ICMP 回程</b>，外层 socket 永远不报错 ——
        ///   这是先遣项目 docs/79 实测过的取值（udp transport + 黑洞 ⇒ 45/90/60/60/60 s 稳定，
        ///   Connect=1 / Disconnect=0）。
        ///   同时它仍是公网地址，路由照旧走物理网卡，run 21 的"排除路由归属"不受影响。
        ///
        /// 【取值口径】
        ///   <c>auto</c>（默认）/ 空 → 黑洞 <c>192.0.2.1</c>；
        ///   <c>node</c> → 本次订阅里第一个 IPv4 节点服务器 IP（<b>旧默认行为，留作反向 A/B</b>）；
        ///   其它 → 原样使用，留作单变量实验的把手。
        ///
        /// ★ 这里<b>绝不</b>回落 <c>127.0.0.1</c>：run 21-A 实测那个值会让节点 <c>/32</c>
        ///   排除路由整体落到 <c>Loopback Pseudo-Interface 1 ifIdx=1</c>，
        ///   拨号包进黑洞（21-B 网关 / 21-C 节点 IP 都落到 <c>WLAN ifIdx=6</c>）。
        /// </summary>
        private string ResolveOuterTarget(string requested)
        {
            if (!string.IsNullOrWhiteSpace(requested) &&
                !requested.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                string v = requested.Trim();
                if (v.Equals("node", StringComparison.OrdinalIgnoreCase))
                    return FirstNodeOuterTarget();
                return v;
            }

            return BlackHoleOuterHost;
        }

        /// <summary>RFC 5737 TEST-NET-1：到网关即丢弃、无应答、无 ICMP 回程的静默黑洞。</summary>
        private const string BlackHoleOuterHost = "192.0.2.1";

        /// <summary>订阅里第一个可用的 IPv4 节点服务器 IP；一个都没有则退回公网目的地 8.8.8.8。</summary>
        private string FirstNodeOuterTarget()
        {
            foreach (string cidr in _nodeExclusions)
            {
                string ip = cidr.Split('/')[0];
                if (ip.Length == 0 || ip.Contains(':') || ip.StartsWith("127."))
                    continue;   // v6 节点与环回都不适合做 v4 socket 的外层目标
                return ip;
            }
            return "8.8.8.8";
        }

        private VpnRouteAssignment BuildRoutes()
        {
            var assignment = new VpnRouteAssignment();

            // ★ includeMode=narrow 时不用 profile 里的 0.0.0.0/0，改成"只把假 IP 段
            //   和隧道内 DNS 拉进隧道"：
            //   【为什么】28-A 实测隧道期间 <c>www.baidu.com</c> 恒为 <c>000</c>、
            //   断开立刻 <c>200/0.097 s</c> —— 域名解析出的<b>真</b>国内 IP 撞上
            //   <c>0.0.0.0/0</c> 进隧道，内核按 GEOIP,CN,DIRECT 想直连出去，出站又被
            //   同一条默认路由抓回来（自环），TCP 直接超时。节点 IP 有 /32 排除，
            //   而"所有国内 IP"是不可能穷举排除的集合 —— 只能反过来收窄 include。
            //   fake-ip 形态下要进隧道的本来就只有两类目的地：
            //     ① 假 IP 池 <see cref="KernelConfig.FakeIpRoute"/>（域名流量的唯一形态）
            //     ② 下发给隧道的 DNS 服务器 /32（不拉进来，解析就回直连 DNS，fake-ip 白做）
            //   其余（国内真 IP、局域网、联网探测）留在物理网卡上，DIRECT 才是真的 DIRECT。
            //   默认仍是 wide（0.0.0.0/0）：28-A 的头条判据是在 wide 下量到的，
            //   narrow 必须先做单变量 A/B，双向可切才允许换成默认。
            IEnumerable<string> inclusion = LocalSettings.IncludeMode.Equals(
                "narrow", StringComparison.OrdinalIgnoreCase)
                ? NarrowInclusionRoutes()
                : _config.IncludeRoutes;

            foreach (string cidr in inclusion)
            {
                if (TryParseCidr(cidr, out string? address, out byte prefix))
                    assignment.Ipv4InclusionRoutes.Add(new VpnRoute(new HostName(address), prefix));
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string cidr in _config.ExcludeRoutes)
            {
                if (TryParseCidr(cidr, out string? address, out byte prefix) && seen.Add(cidr))
                    assignment.Ipv4ExclusionRoutes.Add(new VpnRoute(new HostName(address), prefix));
            }

            // ★ 节点服务器 IP 的 /32 排除 —— 安卓版 VpnService.protect() 在本平台的等价物。
            //   安卓靠 OS 原语把内核每条出站 socket 顶到物理网卡；Windows VPN 插件没有
            //   per-socket protect，只剩路由表这一层：更特定的路由优先，于是拨号包在路由
            //   层面就不可能被自己的隧道抓回去。这条防线不依赖 BoundIface，换网卡也不失效。
            //
            //   ⚠️ 但 2026-10-16 run 14 实测：这批 /32 被平台装到了
            //   **Loopback Pseudo-Interface 1（ifIdx=1）**，而不是物理网卡 ——
            //   节点 IP 的路由比隧道的 0.0.0.0/0 更特定，于是拨号包进环回，
            //   run 13 内核原文就是 <c>connectex: unreachable host</c>。
            //   <c>VpnRoute</c> 只有 Address/PrefixSize，没有 NextHop/Metric，
            //   所以「给排除路由指网关」这条路在 API 层面不存在。
            //   这里留 settings.txt 开关做单变量：nodeExclusions=0 时一条都不下发，
            //   把「排除路由有害」和「ServerUris=127.0.0.1 导致装错接口」两个假设分开。
            if (LocalSettings.NodeExclusions)
            {
                foreach (string cidr in _nodeExclusions)
                {
                    if (TryParseCidr(cidr, out string? address, out byte prefix) &&
                        address!.IndexOf(':') < 0 && seen.Add(cidr))
                        assignment.Ipv4ExclusionRoutes.Add(new VpnRoute(new HostName(address), prefix));
                }
            }

            // 防自环的第二道防线仍在桥侧（dialer.DefaultInterface → Windows IP_UNICAST_IF
            //   强制绑定物理网卡，等价于用户方案的「StreamSocket 绑网卡」）。
            //   两道叠着：路由层保证"即便绑错网卡也不会被回环的节点 IP"，绑定层兜住
            //   "缓存还没覆盖到的新节点 IP"。
            //   C# 侧无需再加网关排除：NetworkAdapter 没有网关属性（已查证，
            //   只有 NetworkAdapterId/带宽/IanaInterfaceType），拿不到网关就别猜。

            return assignment;
        }

        /// <summary>
        /// 窄 include 的目的地清单（配合 <see cref="BuildRoutes"/> 里的说明）：
        /// fake-ip 池 + 下发进隧道的 DNS 服务器 /32。
        /// DNS 那几条只在 <c>dnsInTunnel=1</c> 时才加 —— 不然隧道里没有 DNS 可去，
        /// 加了反而平白多抓两段直连 DNS 流量。
        /// </summary>
        private IEnumerable<string> NarrowInclusionRoutes()
        {
            var list = new List<string> { KernelConfig.FakeIpRoute };
            if (LocalSettings.DnsIntoTunnel)
                foreach (string dns in _config.DnsServers)
                    if (dns.IndexOf(':') < 0)          // v6 DNS 先不掺进来，v4 路由表只管 v4
                        list.Add($"{dns}/32");
            return list;
        }

        /// <summary>
        /// 按 <c>settings.txt: selfSplitTunnel</c> 建「只把本包拆出强制隧道」的
        /// traffic filter；0（默认）返回 null，行为与上一版逐字一致。
        ///
        /// 【官方语义】
        ///   <c>VpnTrafficFilterAssignment</c>："Represents a set of traffic filters
        ///     to use in per-app VPN scenarios."
        ///   <c>VpnTrafficFilter(VpnAppId)</c> + <c>RoutingPolicyType</c>：
        ///     <c>SplitRouting(0)</c> = "Apps are split tunneled and allowed to talk
        ///     through other interfaces"；<c>ForceAllTrafficOverVpn(1)</c> = 全部走隧道。
        ///   <c>AllowOutbound</c>（docs/76 §5.4 实测）："不匹配任何 filter 的流量
        ///     允许绕过 VPN 直连" —— 不是"允许进隧道"，这个方向不测一定搞反。
        ///
        /// 【为什么 filter 里只放本包】
        ///   run 13/15/16/17 证明：mihomo 所在的 vpnClient 后台任务进程被自家
        ///   0.0.0.0/0 抓回隧道，而路由层无法表达 per-socket protect（VpnRoute 无
        ///   NextHop），绑定物理网卡又只会立刻 <c>connectex: unreachable host</c>。
        ///   按应用分流是本平台剩下的唯一原语：本包（前台 + 同 family 的后台任务，
        ///   内核就在里面）允许走物理网卡，其余应用照 include 0.0.0.0/0 进隧道。
        ///   —— 这就是安卓版「VPN 自身进程不入隧道」的等价实现。
        ///
        /// 【臂】1 = AllowOutbound=false（其余应用仍进隧道，期望臂）
        ///       2 = AllowOutbound=true（其余应用旁路直连，用来分离
        ///           「filter 有没有生效」与「AllowOutbound 语义」两件事，
        ///           兼作隧道彻底失败时的不黑洞兜底形态）。
        /// 【失败也要能读】构造异常（AOT/投影问题）落日志后返回 null，退到基线，
        ///   绝不让一个实验开关把连接流程炸掉。
        /// </summary>
        private VpnTrafficFilterAssignment? BuildSelfSplitFilter()
        {
            int arm = LocalSettings.SelfSplitTunnel;
            if (arm == 0)
                return null;

            try
            {
                string pfn = Windows.ApplicationModel.Package.Current.Id.FamilyName;
                var assignment = new VpnTrafficFilterAssignment
                {
                    AllowInbound = false,
                    // ★ 只有 2 号臂开 AllowOutbound；1 号保持"其余应用进隧道"
                    AllowOutbound = arm >= 2
                };
                var filter = new VpnTrafficFilter(
                    new VpnAppId(VpnAppIdType.PackageFamilyName, pfn))
                {
                    RoutingPolicyType = VpnRoutingPolicyType.SplitRouting
                };
                assignment.TrafficFilterList.Add(filter);

                _selfSplitFilterInfo = $"arm={arm} pfn={pfn} AllowOutbound={assignment.AllowOutbound}";
                return assignment;
            }
            catch (Exception ex)
            {
                _selfSplitFilterInfo = "构造失败: " + ex.GetType().Name + ": " + ex.Message;
                RollingLog.Error("self-split-filter", ex);
                return null;   // 退到 null = 上一版行为
            }
        }

        /// <summary>
        /// 建 DNS/NRPT 下发对象 —— 对齐安卓版"DNS 收进内核"那一层。
        ///
        /// 【安卓侧】TunService 用 <c>addDnsServer(172.19.0.2)</c> 把系统解析器指向 TUN 里的
        ///   虚拟 DNS，<c>dnsHijacking</c> 默认 true → 内核劫持 <c>0.0.0.0:53</c> 自己作答。
        ///   于是"节点全死"只是代理失败，直连流量的解析照常 —— 这是官方客户端不会整机断网
        ///   的关键一层。
        /// 【Windows 插件模型】<c>VpnPlugInProfile</c> 压根没有 DNS 服务器属性；官方 sample 的
        ///   v2 路径是 <c>VpnDomainNameInfo(space, Suffix, dnsServers, proxies)</c> 加进
        ///   <c>VpnDomainNameAssignment.DomainNameList</c>，交给 <c>StartWithTrafficFilter</c>
        ///   的 namespaceScope，即 NRPT 规则。这里用根后缀 "."：全部解析走这批服务器。
        /// 【配套要求】config.yaml 必须写 <c>tun.dns-hijack: [any:53]</c>，否则 tun.enable=false
        ///   时 sing-tun 不采用默认劫持列表，查询进隧道没人应答（Docs/04 §5 的教训）。
        /// </summary>
        private VpnDomainNameAssignment? BuildDomainNames()
        {
            try
            {
                var dnsServers = new List<HostName>();
                foreach (string dns in _config.DnsServers)
                {
                    if (!string.IsNullOrWhiteSpace(dns))
                        dnsServers.Add(new HostName(dns.Trim()));
                }
                if (dnsServers.Count == 0)
                    return null;

                // 后缀 "." + Suffix 型 = 全局命名空间规则；代理列表为空按官方 sample 传 null。
                var info = new VpnDomainNameInfo(".", VpnDomainNameType.Suffix, dnsServers, null);
                var assignment = new VpnDomainNameAssignment();
                assignment.DomainNameList.Add(info);
                return assignment;
            }
            catch (Exception)
            {
                // 拿不到 NRPT 对象就退回"DNS 留在物理网卡"的旧行为：
                // 少一个优化远好过整条连接失败。
                return null;
            }
        }

        private static bool TryParseCidr(string cidr, out string? address, out byte prefix)
        {
            address = null;
            prefix = 0;

            string[] parts = cidr.Split('/');
            if (parts.Length != 2)
                return false;
            if (!byte.TryParse(parts[1], out prefix))
                return false;

            address = parts[0];
            return true;
        }
    }
}
