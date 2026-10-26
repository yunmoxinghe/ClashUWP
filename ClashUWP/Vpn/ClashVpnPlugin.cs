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
        /// <summary>
        /// ★【1.0.35 · G5】给 <see cref="SessionReset"/> 的只读门闩：内核此刻在不在跑。
        /// 会话结束那一臂必须在 <see cref="DisconnectCore"/> 把 <c>s_bridgeGlobal</c> 置假<b>之前</c>
        /// 调用，否则它永远读到"内核没跑"、重置就静默失效。
        /// </summary>
        internal static bool BridgeRunningForReset => s_bridgeGlobal;
        /// <summary>串行化 start/stop/pump 切换，防 Connect 与 Disconnect 交错。</summary>
        private static readonly object s_bridgeGate = new();
        /// <summary>最近一次桥绑定的物理网卡快照（桥停机后 bound_iface 会清零，快照供诊断）。</summary>
        private static string s_boundIfaceSnapshot = "";
        /// <summary>Start 成功后由静态字段持住的活动 channel：给 outerTunnelTransport
        /// 一个 GC 根，防止桥启动阻塞期间唯一托管 socket 的它被回收终结（0x800703E3 的成因）。</summary>
        private static VpnChannel? s_activeChannel;
        /// <summary>进程级桥是否刻意留在跑动状态（内核常驻策略，见 Disconnect 的注释）。</summary>
        private static bool s_bridgeResident;
        /// <summary>
        /// ★【1.0.79 · 实测根因】本进程<b>真的</b>把 mihomo 内核起来过（<c>clash_bridge_start</c>
        /// 返回 0，或 Go 侧回 <c>already started</c>）且尚未成功 <c>stop</c>。
        ///
        /// 【为什么必须和 <see cref="s_bridgeResident"/> 分开】
        /// <c>s_bridgeResident</c> 的语义是"策略上把内核留给下一次连接复用"，可它在两条
        /// <b>从没起过内核</b>的路径上也会被置真：① 会话收尾（<c>TearDownAsync</c>）只看
        /// <c>residentBridge</c> 开关，不看这次会话到底走到没走到步 7；② <c>Start</c> 失败的
        /// catch 同理。于是"空壳不拨号"这类在建桥之前就 abort 的会话，会把一个<b>不存在的
        /// 常驻内核</b>留给下一次连接。
        /// 2026-10-20 06:50:54→06:52:44（pid=19060）实测的形状就是这条：
        /// 第一次 abort（<c>会话结束，存活 0s encap=0 inj=0</c>）→ 第二次连接
        /// <c>复用常驻内核（排空 0 包）</c> 起了泵却没起内核 ⇒ 整段会话
        /// <c>inj=0</c>、<c>fake-ip=NO:解析超时</c>、<c>内核=HttpRequestException</c>，
        /// 58 s 后被插件看门狗判死删 profile —— 看门狗是对的，被它杀掉的是一条<b>必然死</b>的隧道。
        /// 自此：<c>s_bridgeResident = s_kernelStarted && 常驻开关</c>，
        /// "复用"只能以"确实起过"为前提；万一状态仍与实情不符，Go 侧的
        /// <c>already started</c>（rc=-2）仍是兜底。
        /// </summary>
        private static bool s_kernelStarted;
        /// <summary>
        /// ★【1.0.70 · G7】当前常驻内核的 gvisor 栈是否<b>以 v6 形态</b>建起来的
        /// （= 最近一次全量 Start 时 <c>ipv6</c> 档为 2）。
        ///
        /// 【为什么必须单独记】栈的地址族集合在 <c>tun.NewStack</c> 时定死一次，
        /// 之后热重载 / 已启动分支的 rebind 都改不了它（Go 侧 Start 命中 stack != nil
        /// 只重绑网卡就返回）。若 0/1 → 2 的切换只做热重载，DNS 会开始发 fake v6，
        /// 而 gvisor 栈不认识 v6 —— 应用连假 v6 地址就是黑洞，且日志里看不出是配置问题。
        /// 反向（2 → 0/1）不需要重建：没有 v6 路由、没有 AAAA，栈里的 v6 能力闲置无害。
        /// </summary>
        private static bool s_bridgeKernelV6;
        /// <summary>上一次连接用的内核配置，用于判断订阅是否变了（变了才热重载）。</summary>
        private static string s_lastConfigFingerprint = "";
        // ★【1.0.38 · G6】重绑要"重放同一次 start"，所以 start 的三个入参必须留档。
        //   为什么留参数而不是留别的什么：Go 侧的 rebindInterface() 只存在于
        //   <c>Bridge.Start</c> 的两条路径里（首次 <c>core.go:153</c>、已启动分支
        //   <c>core.go:135</c>），而"已启动"分支<b>不解析配置</b>（直接 return
        //   <c>ErrAlreadyStarted</c>）⇒ 传什么配置都不影响内核状态，只需要<b>不是空串</b>
        //   （<c>clash_bridge_start</c> 对 len<=0 先返回 -1 参数非法，走不到 rebindInterface）。
        /// <summary>最近一次成功喂给 <c>clash_bridge_start</c> 的配置原文（G6 重绑重放用，见上）。</summary>
        private static string s_lastBridgeConfig = "";
        /// <summary>同上：那次 start 用的 mihomo home（<c>KernelHome.Ensure()</c> 的返回值）。</summary>
        private static string s_lastBridgeMmdbDir = "";
        /// <summary>同上：那次 start 用的 MTU。</summary>
        private static int s_lastBridgeMtu = 1400;

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

        /// <summary>
        /// ★【1.0.70 · G7】本次 IPv6 下发的实际形态（臂号 / 地址 / 路由 / 内核侧 env），写快照用。
        /// arm=0 时逐字说明"未下发"，与 1.0.69 的行为对齐 —— 这一列存在本身就是
        /// "这次连接到底动没动 v6"的唯一档案。
        /// ★【1.0.119】arm=0 还要带 <see cref="RouteInclusion.LeakNote"/>：
        ///   "没动 v6"在本机有全局 v6 的网络里等于"v6 全部绕过隧道"，
        ///   这句话以前要靠读代码才知道，现在直接写在档案里。
        /// </summary>
        private string _ipv6Diag = "arm=0 未下发";

        /// <summary>
        /// ★【1.0.119 · G7】本次 <see cref="BuildRoutes"/> 算出的路由表摘要（四桶计数 + 剔除条数）。
        /// 判据本体在 <see cref="RouteInclusion"/>；这一列进快照，是为了让"当时到底装了哪些路由"
        /// 和 <c>_ipv6Diag</c> 一样能被事后读出来 —— 装错族别这类问题只在日志里有痕迹才查得动。
        /// </summary>
        private string _routePlanNote = "未构建";

        /// <summary>
        /// ★【1.0.35 · G9】本次连接的应用分流注入结果（<see cref="KernelConfig.Transform"/> 回来的那句）。
        /// 放实例字段而不是只进日志：快照是"这一条会话"的档案，
        /// 而"名单写了但没注进内核配置"必须能和当时的其它字段一起被读出来。
        /// </summary>
        private string _splitNote = "未处理";

        /// <summary>
        /// ★【1.0.40 · DNS 可配】本次连接读到的 <c>dns-config.txt</c>（总闸关/文件空时是空对象）。
        /// 它有三个读者，必须共用这<b>一个实例</b>：
        ///   ① <see cref="KernelConfig.Transform"/> 的 dns 块覆盖；
        ///   ② <see cref="NarrowInclusionRoutes"/> 的假 IP 池路由（改了池子，路由必须跟着改）；
        ///   ③ <see cref="SessionWatchdog"/> 的"答案在不在假 IP 池里"判据。
        /// 三处各读一次文件就会各拿到一份可能不同的结果（用户在会话中途保存），
        /// 那会造出"路由改了、看门狗还按旧池判死"的假故障。
        /// </summary>
        private DnsConfig _dnsConfig = new DnsConfig();

        /// <summary>本次 DNS 覆盖的实际结果（内核侧），写快照 <c>Dns=</c> 行与日志。</summary>
        private string _dnsNote = "未处理";

        /// <summary>
        /// ★【1.0.40】本次生效的 v6 假 IP 池（<c>dns-config.txt: fake-ip-range6</c> 优先，
        /// 否则 <see cref="LocalSettings.Ipv6FakeRange"/>）。路由、<c>tun.inet6-address</c>、
        /// <c>dns.fake-ip-range6</c> 三处必须用同一个值 —— 与 v4 池同一个级联纪律。
        /// </summary>
        private string _ipv6PoolEffective = LocalSettings.DefaultIpv6FakeRange;

        private uint _encapsulatedPackets;
        private uint _echoReplies;
        private uint _unknownPackets;
        private uint _injectedPackets;
        private uint _nonUnicastDropped;
        /// <summary>★【1.0.66】<b>字节</b>计数（供状态通知算实时上下行，同时进会话日志）。
        /// 口径必须写清：量的是<b>隧道 L3</b> —— <c>_outBytes</c> = 平台交来的每个出站 IP 包的
        /// <c>Buffer.Length</c>（不含外层 UDP 封装），<c>_inBytes</c> = 注回本机的每个回程 IP 包长度。
        /// 所以它<b>不等于</b>内核 REST <c>GET /traffic</c> 的代理好速度，也不含封装开销
        /// （差异说明见 <see cref="StatusToast"/> 类注释）。
        /// ★ 只在<b>已有</b>的两个热点位置各加一次 <c>Interlocked.Add</c>，不新增任何 IO。</summary>
        private long _outBytes;
        private long _inBytes;
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
        /// <summary>★ 1.0.26（T3）：<c>encapBuffer.Status != Ok</c> 的次数。
        /// 平台用它"报告操作 IVpnPacketBufferList 时的错误"（见 VpnPacketBuffer.Status 条文），
        /// 我们<b>只读不判死</b>：这一列非 0 = 平台交来的 buffer 自己就带着问题，
        /// 是"入站侧确实有东西、但形状不对"的直接证据，而不是我们的处理问题。</summary>
        private uint _decapNonOkStatus;
        /// <summary>首个非 Ok 的 Status 原文，只记一次（进日志与快照，避免按回调刷屏）。</summary>
        private string _decapStatusFirst = "";
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

        /// <summary>★ 1.0.26（审计项 T2）：从 <b>send 池</b>取包失败的次数。
        ///   ★ 1.0.52 起取包只有一条入口 <c>GetVpnSendPacketBuffer()</c>（旧臂
        ///     <c>RequestVpnPacketBuffer(VpnDataPathType.Send)</c> 已删：那个枚举被官方标
        ///     "not supported and may be altered or unavailable in the future"）。
        ///   这条 API 在<b>本仓库从未实测</b>（先遣项目也只有 C# 兜底分支的静态存在，从未触发），
        ///   所以它自带这一列判据：<c>ka</c> 在涨而 <c>takeFail</c> 同步涨 = 取包这条路走不通；
        ///   两列都不涨 = 平台压根没来要 keep-alive，另说。</summary>
        private static int s_takeSendFailures;

        /// <summary>首个 send 取包失败原文（只记一次；keep-alive 几十秒一次，也不该刷屏）。</summary>
        private static string s_takeSendFirstError = "";

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
        /// ★【1.0.89 · Docs/38 §11.7 / §十四】本进程内"上一次 <c>Connect</c> 走完了、且之后平台
        ///   再没回调过 <c>Disconnect</c>"——满足它就是<b>重连（reconnect）场景</b>。
        ///
        /// 【官方条文】<c>VpnChannel.TerminateConnection</c>："Terminates the connection attempt
        ///   performed inside IVpnPlugIn.Connect. <b>Should not be called on reconnect cases.</b>"
        ///   <c>VpnChannel.SetErrorMessage</c> 同被官方 sample 门控（原文注释
        ///   "we do not set the error message for reconnection attempts"）——
        ///   因为重连时错误消息会<b>反复糊在系统 VPN 面板/快速面板上</b>，而用户根本没在等一次新的建连。
        ///
        /// 【依据是现读的官方 sample，不是我的推断】
        ///   <c>D:\fluentapps\repos\UwpVpnPluginSample\CSharp\TestVpnPluginAppBg\VpnPlugin.cs</c>：
        ///   <c>:87</c> 声明 <c>IsReconnect</c>（注释 "For demo purposes, only support one instance"），
        ///   <c>:572</c> 在 <c>Connect</c> 末尾置真，<c>:640</c> 只在 <c>Disconnect</c> 里置假；
        ///   它门控 <c>SetErrorMessage</c>（<c>:595-597</c>）与前后自定义 UI 提示（<c>:207</c>、<c>:434</c>），
        ///   而 <b>sample 全文一次都没调 <c>TerminateConnection</c></b>——失败就是 <c>throw</c>（<c>:599</c>）。
        ///   ⇒ 官方口径是"平台<b>不给</b>"这是重连"的标志（26100 确实没有 <c>DisconnectReason</c>，
        ///     见 <see cref="VpnChannel"/> 方法表核对），但<b>调用方可以自己推</b>"。
        ///     本篇旧注释 <c>:1037-1040</c> 里"插件侧读不到 ⇒ 所以只能无条件调"那半句推理不成立，已订正。
        ///
        /// 【为什么这里是 <c>static</c> 而 sample 用实例字段】
        ///   sample 明写"只支持一个实例"，靠的就是实例生命周期；我们不是——1.0.77 单清单入口合并之后
        ///   app 与插件同进程（<c>Docs/38</c> §12.3 一手：<c>session 用户发起…</c> 与
        ///   <c>connect 平台发起连接</c> 两行同 pid），且装机日志里每个会话都有多条
        ///   <c>bgtask 插件进程启动</c> ⇒ 平台会<b>反复新建后台任务实例</b>复用同一个进程。
        ///   实例字段会把标志位丢在新实例上，判据必须挂在进程级。
        ///   <c>volatile</c>：写方是平台回调线程，读方可能是 catch 里的同一条线程，
        ///   但同一个进程里还有泵线程在读别的静态标志——按本文件既有惯例一律 <c>volatile</c>。
        /// </summary>
        private static volatile bool s_lastConnectFinishedWithoutDisconnect;

        /// <summary>只读视图：<see cref="s_lastConnectFinishedWithoutDisconnect"/>（判"这次是不是重连"）。</summary>
        internal static bool IsReconnectAttempt => s_lastConnectFinishedWithoutDisconnect;

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
                AbortOrThrow(channel, "AssociateTransport failed");
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

            // 步 2.5 · ★【1.0.115】插件侧执行 "There can only be one VPN profile per process"。
            //   平台不保证"先断旧再连新"的顺序（Docs/43 官方 sample clean-room 实测：
            //   A 处于 Connecting/Connected 时拨 B，官方代码压根没有闸，B 的 await
            //   ≥5 分 56 秒不返回且无出口）。快速设置面板点另一条订阅时，前台三段式
            //   （Docs/55 先断开→后修改→再重连）完全被绕过，Connect(B) 直接到这里，
            //   而 A 的 channel 还活着。旧代码直接覆盖 s_activeChannel，A 的 Stop()
            //   永远没人调 ⇒ 平台侧 A 一直挂着，Windows UI 停在"验证你的登录信息"。
            //   现在：插件自己收尾旧会话，再处理新连接。
            //   ★ 后续平台若再回调 Disconnect(old)，现有守卫（:2097 ReferenceEquals）
            //     会跳过清 null，DisconnectCore 里各 Stop 均幂等，old.Stop() 二次调用
            //     在 try/catch 里 ⇒ 不会炸。
            VpnChannel? oldChannel = s_activeChannel;
            if (oldChannel != null && !ReferenceEquals(oldChannel, channel))
            {
                RollingLog.Info("connect",
                    $"旧会话仍在（channel={oldChannel.Id}），插件侧先收尾再接纳新连接（one-profile-per-process）");
                try
                {
                    _started = false;
                    DisconnectCore(oldChannel);
                    oldChannel.Stop();
                }
                catch (Exception exOld)
                {
                    RollingLog.Warn("connect",
                        $"收尾旧会话异常（继续处理新连接）: {exOld.GetType().Name}: {exOld.Message}");
                }
                s_activeChannel = null;
            }

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
                AbortOrThrow(channel, "read CustomField failed");
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

            // ★【1.0.40 · DNS 可配】每次连接读一次 dns-config.txt（总闸关 = 不读）。
            //   ★ 必须在步 4.1 的改写之前、步 5 的 BuildRoutes 之前：
            //     前者要把覆盖叠进内核配置，后者要用生效的假 IP 池算窄 include 路由。
            //   ★ 被拒绝的行必须原话进日志 —— "存下了但没生效"的静默失败
            //     在 DNS 上比应用分流更贵：一条写错就可能让整份配置解析失败。
            _dnsConfig = LocalSettings.DnsConfigEnabled ? DnsConfig.Load() : new DnsConfig();
            _ipv6PoolEffective = _dnsConfig.EffectiveFakeIpRange6(LocalSettings.Ipv6FakeRange);
            RollingLog.Info("dns",
                (LocalSettings.DnsConfigEnabled ? "读 dns-config.txt：" : "总闸关闭(dnsConfig=0)，不读 dns-config.txt；")
                + (LocalSettings.DnsConfigEnabled ? _dnsConfig.Describe() : "无覆盖")
                + (LocalSettings.Ipv6 >= 2 ? $" v6pool={_ipv6PoolEffective}" : ""));
            if (LocalSettings.DnsConfigEnabled && _dnsConfig.Rejected.Count > 0)
            {
                var reasons = new System.Text.StringBuilder();
                foreach (string r in _dnsConfig.Rejected)
                    reasons.Append(reasons.Length == 0 ? "" : " / ").Append(r);
                RollingLog.Warn("dns", "dns-config.txt 有行被拒：" + reasons);
            }

            // ★【1.0.62】按 profile 携带的订阅 id 选配置源：
            //   有 subId（每条订阅一个系统 profile ClashUWP-{id}）→ 读 subscriptions/{id}.yaml；
            //   无 subId（基础 profile ClashUWP）→ 读 config.yaml（生效副本，向后兼容）。
            (string rawConfig, string? rawConfigPath) = LoadMihomoConfig(_config.SubId);

            // ★【1.0.62】配置源必须留一条<b>自己日志里</b>可查的证据行：
            //   快速设置点的是哪条订阅 profile，内核就该吃哪份 subscriptions\{id}.yaml。
            //   上面 LogDiagnosticMessage 那行只进系统日志（该通道可能被清空，
            //   见 memory「平台日志丢失」），而"这次连接到底读了哪份配置"
            //   恰恰是核验"快速面板切订阅"成败的第一判据 ⇒ 必须同时进 RollingLog，
            //   由 <c>logtail plugin</c> 直接读回。
            RollingLog.Info("config",
                $"配置源={(string.IsNullOrWhiteSpace(_config.SubId) ? "config.yaml(基础profile)" : $"subscriptions/{_config.SubId}.yaml(订阅profile)")}" +
                $" path={rawConfigPath ?? "内置兜底"}");

            // ★ 1.0.23 步 4.1：把订阅/前台 YAML 改成本插件能跑通数据面的形态
            //   （dns.enhanced-mode: fake-ip + 兜底 tun），并落成 config.kernel.yaml。
            //   热重载走的是<b>磁盘上那份文件</b>（PUT /configs 的 JSON body path），所以必须让它指向
            //   改写后的文件，否则常驻热重载会按订阅原文把 fake-ip 覆盖回 redir-host
            //   —— run 24 是靠 hotReload=0 才压住这个覆盖的，产品形态不能依赖它。
            // ★【1.0.35 · G9】同一次改写里还会把 LocalState\split-apps.txt 的应用分流名单
            //   注到 <c>rules:</c> 开头。名单是<b>每次连接重读</b>的（AppSplit.Load），
            //   所以"前台改完 → 下次连接生效"，不需要重装、也不需要重建 VPN profile。
            //   splitNote 必须进 RollingLog：这条功能最容易失败的形态是
            //   "用户写了名单、界面也存下了，但内核那份配置里根本没有这几行" ——
            //   静默不算失败，可查的静默才算把问题交给了用户（口径同上面 1.0.62 那行配置源）。
            string mihomoConfig;
            string splitNote;
            string dnsNote;
            if (LocalSettings.FakeIp)
            {
                // ★【1.0.70 · G7】ipv6 档位与<b>生效的</b> fake-ip v6 池一起进改写器：
                //   档 0 时几个参数都不改变输出（逐字同 1.0.69），档 1/2 才动 dns/tun 块。
                // ★【1.0.40 · DNS 可配】dns 覆盖是同一份改写的输入之一；总闸关时传 null，
                //   改写器会把它写成"总闸关闭"而不是"无覆盖"，快照里分得清。
                mihomoConfig = KernelConfig.Transform(rawConfig, AppSplit.Load(),
                    LocalSettings.DnsConfigEnabled ? _dnsConfig : null,
                    LocalSettings.Ipv6, _ipv6PoolEffective,
                    out splitNote, out dnsNote);
            }
            else
            {
                // fakeIp=0 是整份改写器的总闸（KernelConfig.Transform 不进），
                // 于是应用分流与 DNS 覆盖也一起关 —— 这一句把耦合写明，别让它成为"未知原因失效"。
                mihomoConfig = rawConfig;
                splitNote = "fakeIp=0 → 整份改写关闭，名单未注入";
                dnsNote = "fakeIp=0 → 整份改写关闭，DNS 覆盖未应用";
            }
            _splitNote = splitNote;
            _dnsNote = dnsNote;
            RollingLog.Info("config", $"应用分流={splitNote}；DNS 覆盖={dnsNote}");
            // ★【1.0.75 · 空壳不拨号】改写完、落盘前，先问一句"这份配置有没有可用出站"。
            //   【为什么卡在 WriteEffectiveFile 之前】再往下一步就是写 config.kernel.yaml ——
            //   空配置一旦落盘，下一次热重载会把用户那份能用的正文覆盖掉（口径同 1.0.62
            //   "桥 FailFast 不该没收用户的配置"）。判据只看将要交给内核的那份，不看磁盘上的旧那份。
            //   【为什么门控在 failFast】关它（failFast=0）就是保留实验臂："先建隧道、再看诊断"
            //   —— run 25 那种读法需要隧道在，才读得到 Encapsulated/Injected。
            //   【对齐安卓版】CMFA 是 <c>catch → reason → finally stopSelf()</c>：配置不可用就
            //   带着原因把服务停掉，绝不"先连着，回头再说"。我们的对等物就是
            //   SetErrorMessage + TerminateConnection（AbortConnectionAttempt），
            //   系统 VPN 面板因此能看到原因，而不是一台状态 Connected、流量全进黑洞的"空回环连接"。
            if (LocalSettings.FailFast)
            {
                string emptyReason = KernelConfig.ReasonIfNoOutbound(mihomoConfig);
                if (emptyReason.Length > 0)
                {
                    RollingLog.Warn("config",
                        $"空壳不拨号：{emptyReason}；配置源={(string.IsNullOrWhiteSpace(_config.SubId) ? "config.yaml" : $"subscriptions/{_config.SubId}.yaml")}");
                    try
                    {
                        channel.LogDiagnosticMessage("ClashUWP: 配置无任何可用出站，终止本次连接: " + emptyReason);
                    }
                    catch { /* 日志出口偶尔不可用 */ }
                    LogSession($"evt=connect-abort stage=no-outbound reason={emptyReason}");
                    AbortOrThrow(channel, "配置无任何可用出站：" + emptyReason);
                    return;
                }
            }

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
                // ★【1.0.88 · Docs/38 §十一 · D9】这个等待从<b>无上限</b>改成上限 3 s。
                //   理由不是"猜它会卡"，而是官方承认平台在<b>等这次 Connect 返回</b>
                //   （ActivateForeground 条文原话："While the app is in the foreground,
                //    the regular <b>Connect timeout</b> is paused."）—— 既然平台在等，
                //    我们就不能把自己的任何一步挂成无上限。官方从没给过这个超时的数值，
                //    所以 3 s 是我们自己定的；实测这一步 UDP 立即返回（UDP ConnectAsync
                //    不握手、不发包，只设默认远端），14 条会话里 Connect→Start 全程 1–2 s。
                //   超时后照旧"继续"（与下面 catch 同一语义：Start 才是平台契约点），
                //   但一定留一行，不让它变成又一个无声的悬挂。
                var connectTask = _transport.ConnectAsync(new HostName(outerTarget), LocalSettings.OuterPort).AsTask();
                if (!connectTask.Wait(System.TimeSpan.FromSeconds(3)))
                {
                    channel.LogDiagnosticMessage(
                        "ClashUWP: ★ transport ConnectAsync 3 s 未返回（继续走 Start，这一跳不再等）.");
                    RollingLog.Warn("connect", $"transport ConnectAsync 3s 未返回 outer={outerTarget}:{LocalSettings.OuterPort}（继续）");
                    // 这一条我们不再等它 ⇒ 它将来抛的话就没人接了。挂个只看失败的续块，
                    // 让"晚到的失败"也出声（未观察异常在 .NET 里不会崩进程，但会静静消失）。
                    _ = connectTask.ContinueWith(t => RollingLog.Warn("connect",
                            $"那条超时的 transport ConnectAsync 后来抛了：{t.Exception?.GetBaseException().GetType().Name}: " +
                            $"{t.Exception?.GetBaseException().Message}"),
                        System.Threading.CancellationToken.None,
                        System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted,
                        System.Threading.Tasks.TaskScheduler.Default);
                }
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
                $"DNS {(domains == null ? "留物理网卡(dnsInTunnel=0)" : "进隧道 " + string.Join(",", EffectiveNrptDnsServers()))}，" +
                $"DNS覆盖={_dnsNote}，" +
                $"内核={LocalSettings.Source} 开关[resident={LocalSettings.ResidentBridge} hotReload={LocalSettings.HotReload} selfSplitTunnel={LocalSettings.SelfSplitTunnel} ipv6={LocalSettings.Ipv6} outer={_outerTargetUsed}:{LocalSettings.OuterPort}(要求={LocalSettings.OuterTarget}) fakeIp={(LocalSettings.FakeIp ? "on" : "off")}]");

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

                // ★【1.0.70 · G7】IPv6 三档（默认 0 = 与 1.0.69 逐字一致）。
                //   0 = 不下发 v6 地址、不下发 v6 路由、内核配置一个字节都不改；
                //   1 = 防泄漏：给 L3 接口一个 ULA 地址 + 下发 2000::/3（全局单播的
                //       "IPv6 版默认路由"），同时内核 dns.ipv6=false —— 应用拿不到 AAAA，
                //       硬编码 v6 字面量的流量也会进隧道而不是从物理网卡旁路；
                //   2 = 全量：在 1 之上让内核真正跑 v6（tun.inet6-address + fake-ip v6 池 +
                //       dns.ipv6=true），并额外把 fake-ip v6 池前缀（默认 ULA，不在
                //       2000::/3 里）拉进隧道。
                //   ★ 地址必须配路由才有意义：先遣 V6D/E vs V6F/H 实测过
                //     "只给 fd00::2 不给路由"和"只给路由不给地址"两种情况接口都不承载 v6。
                //   ★ 绝不用 ::/0：先遣 docs/96 四臂实测它会把 IPv6 变成真正的默认路由、
                //     连 IPv4 的出站都被抢走；2000::/3 四臂三通过，是这条路上验证过的形态。
                List<HostName>? ipv6 = null;
                if (LocalSettings.Ipv6 >= 1)
                {
                    string v6addr = LocalSettings.Ipv6ClientAddress.Length > 0
                        ? LocalSettings.Ipv6ClientAddress
                        : _config.ClientIpV6;
                    ipv6 = new List<HostName> { new(v6addr) };
                    _ipv6Diag = $"arm={LocalSettings.Ipv6} addr={v6addr} " +
                                $"routes=[{string.Join(",", Ipv6InclusionRoutes())}] " +
                                $"kernelIpv6={(LocalSettings.Ipv6 >= 2 ? "on" : "off（AAAA 不解析）")} " +
                                $"pool={_ipv6PoolEffective}" +
                                (string.Equals(_ipv6PoolEffective, LocalSettings.Ipv6FakeRange,
                                    StringComparison.OrdinalIgnoreCase) ? "" : "(dns-config 覆盖)");
                }
                else
                {
                    // ★【1.0.119 · G7】arm=0 不再只写"未下发"：这一档在本机有全局 v6 时
                    //   等价于"v6 全部绕过隧道"，这条缺口以前是藏着的。
                    //   只如实播报，不偷偷抬档（抬档要拨号验证，见 LocalSettings.Ipv6 的口径）。
                    RouteInclusion.V6Face face = NetworkFollow.InspectLocalV6Face(out string v6detail);
                    _ipv6Diag = $"arm=0 未下发｜{RouteInclusion.LeakNote(0, face)}｜{v6detail}";
                    // ★★【1.0.128 订正】以前只有 Global 一档出声，于是"None 还是 Unknown"在
                    //   日志里读不出来：这条读数只进快照 Ipv6= 一列，而快照只有一个槽、
                    //   每次会话结束整个覆盖 ⇒ 装机至今无法回溯"AppContainer 里到底枚举得到地址吗"。
                    //   三档一律落一行（Global 用 Warn，其余 Info），热路径不碰这里。
                    string faceLine = $"泄漏面读数 face={face} arm=0｜{v6detail}";
                    if (face == RouteInclusion.V6Face.Global)
                    {
                        RollingLog.Warn("ipv6", faceLine);
                        LogSession($"evt=v6leak face=global detail={v6detail}");
                    }
                    else
                        RollingLog.Info("ipv6", faceLine);
                }

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
                    ipv6,                                  // assignedClientIPv6list ★ G7：arm=0 → null（同 1.0.69）
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
                // ★ 1.0.26（T4）：以下计数全部改成 Interlocked.Exchange 归零，与写侧的
                //   Interlocked.Increment/Add 同一口径。为什么值得改：这些字段在
                //   <c>_started = true</c> 之后<b>立刻</b>可能被平台线程写（Encapsulate 只认
                //   <c>_started</c>），而这里还有看门狗/Disconnect 两个读者 ——
                //   裸赋值在别的线程眼里可以长期停在旧值上，"上一会话的数"就会被当成本轮的。
                Interlocked.Exchange(ref _encapsulatedPackets, 0);
                Interlocked.Exchange(ref _echoReplies, 0);
                Interlocked.Exchange(ref _unknownPackets, 0);
                Interlocked.Exchange(ref _injectedPackets, 0);
                Interlocked.Exchange(ref _nonUnicastDropped, 0);
                // ★ 1.0.66：字节计数同样每次会话归零 —— 状态通知的"累计"就是这一列，
                //   留着上一会话的数会把"这次连上才走的量"报成历史总量（速率不受影响，
                //   因为它取的是两次读数的差，但累计行必须干净）。
                Interlocked.Exchange(ref _outBytes, 0);
                Interlocked.Exchange(ref _inBytes, 0);
                Interlocked.Exchange(ref _ipChecksumFixed, 0);
                Interlocked.Exchange(ref _transportChecksumFixed, 0);
                Interlocked.Exchange(ref _checksumFixLogged, 0);
                Interlocked.Exchange(ref _keepAlives, 0);
                // ★ 归还侧计数器每次 start 归零 —— 判据是"ret 与 encap 同步涨"，
                //   留下上一会话的数会让同步性没法逐轮核对。档位同时固化进快照。
                _encapReturnMode = LocalSettings.EncapReturn.ToString();
                Interlocked.Exchange(ref _sendBuffersReturned, 0);
                Interlocked.Exchange(ref _sendBuffersLeft, 0);
                Interlocked.Exchange(ref _sendReturnFailures, 0);
                Interlocked.Exchange(ref _sendBuffersFlushed, 0);
                Interlocked.Exchange(ref _sendReturnFirstError, "");
                Interlocked.Exchange(ref _encapsulateCalls, 0);
                Interlocked.Exchange(ref _encapListItemsSeen, 0);
                Interlocked.Exchange(ref _encapListSizeMax, 0);
                Interlocked.Exchange(ref _encapEnumerated, 0);
                Interlocked.Exchange(ref _encapEnumFirstError, "");
                Interlocked.Exchange(ref _sendBuffersReappended, 0);
                Interlocked.Exchange(ref _keepAliveOrphanBuffers, 0);
                Interlocked.Exchange(ref _encapsulateErrors, 0);
                Interlocked.Exchange(ref _encapsulateFirstError, "");
                Interlocked.Exchange(ref _decapsulateCalls, 0);
                _decapFirstInfo = "";
                Interlocked.Exchange(ref _decapNonOkStatus, 0);
                _decapStatusFirst = "";
                // receive 侧两列也每次会话归零（静态跨会话留着就没法判"在借未还"是不是本轮的）。
                Interlocked.Exchange(ref s_receiveBuffersPending, 0);
                Interlocked.Exchange(ref s_receiveBuffersFlushed, 0);
                Interlocked.Exchange(ref _lastKeepAliveTick, 0);
                Interlocked.Exchange(ref _sessionStartTick, Environment.TickCount64);

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
                           $"v6include=[{string.Join(",", Ipv6InclusionRoutes())}] " +
                           $"outer={_outerTargetUsed}:{LocalSettings.OuterPort} " +
                           $"dnsInTunnel={(LocalSettings.DnsIntoTunnel ? 1 : 0)} ipv6={LocalSettings.Ipv6} " +
                           $"dnsCfg={(_dnsConfig.IsEmpty ? "none" : _dnsConfig.Entries.Count + "项")} " +
                           $"fakePool={_dnsConfig.FakeIpRoute} " +
                           $"nodeExcl={_nodeExclusions.Count} mtu={_config.Mtu}");
                RollingLog.Info("session", "L3 隧道接口已建立：" +
                           $"includeMode={LocalSettings.IncludeMode} " +
                           $"include=[{string.Join(",", includeUsed)}] " +
                           $"ipv6={LocalSettings.Ipv6} v6include=[{string.Join(",", Ipv6InclusionRoutes())}] " +
                           $"outer={_outerTargetUsed}:{LocalSettings.OuterPort} mtu={_config.Mtu}");

                // 步 7 · ★ 启动 mihomo 进程内数据面（路线 A）。
                //   必须在 Start 之后：bridge 一旦跑起来，回程泵就会往这个 channel 注包。
                //   失败不阻断连接 —— Encapsulate 会回落假内核 echo，方便单独定位问题。
                StartMihomoBridge(channel, mihomoConfig, mihomoConfigPath);

                // 步 8 · ★ 武装插件进程内的看门狗（判黑洞 / 数重拨；无固定时长上限）。
                //   必须在 Start 成功之后：之前隧道还不存在，探什么都是误判。
                //   放在桥之后：桥的 failFast 若已经决定放弃这次连接，看门狗不该再插一脚
                //   —— 它第一件事就是检查开关，开关关着（或桥失败路径已断开）就不会动手。
                // ★【1.0.40】假 IP 池与 NRPT 服务器必须传本次生效的那一份：
                //   看门狗判据②按池子判答案、判据①探的正是 NRPT 实际下发的 DNS。
                SessionWatchdog.Start(channel, _connectBeganAt, this,
                    _dnsConfig.FakeIpRoute, EffectiveNrptDnsServers());

                // 步 8.5 · ★【1.0.49】武装插件进程内的<b>整机物理网络兜底看门狗</b>。
                //   ★ 为什么放在插件进程而不是只靠前台的 HealthMonitor：
                //     前台 UWP 进程会被系统挂起/节流/关闭 ⇒ 那个 while 循环直接停摆，
                //     而"物理网卡断了、隧道还装着"恰恰是最需要兜底的场景（用户看到整机断网）。
                //     插件进程在会话期间是活跃的（数据面就在里面跑，平台保活），且不随前台生命周期走。
                //   ★ 与 SessionWatchdog 的分工：那条判"隧道黑洞"（探隧道内 DNS），
                //     这条判"物理网络是否还活着"（探 114.114.114.114，绕开隧道）。
                //     两条都指向同一个拆隧道动作，用 Interlocked 门闩保证只有一个动手。
                PhysicalNetworkWatchdog.Start();

                // 步 8.6 · ★【1.0.38 · G6】跟随网络变化（换 Wi-Fi / 有线 / 热点）。
                //   ★ 为什么放在桥与两条看门狗之后：它读的基线里有 <c>bound_iface</c>
                //     （桥此刻绑到哪张物理网卡），桥没起来时读到空串，基线就没意义。
                //   ★ 为什么默认只观测（netFollow=1）：订阅事件这件事在 vpnClient 后台任务
                //     进程里到底收不收得到<b>没有实测</b>，而动作臂要再调一次 clash_bridge_start
                //     —— 两条都是 Docs/21 的待验项，代码全写完、默认不动桥。
                //   ★ 它是<b>尽力而为</b>：Start 内部订阅失败只进 channel=net-follow 日志，
                //     绝不让"跟随网络"这个附加能力把整条连接拖垮（见 NetworkFollow.Start 的 catch）。
                NetworkFollow.Start(BoundIfaceNow());

                // 步 9 · ★【1.0.66】通知中心那条常驻的"实时上下行"（对齐安卓 DynamicNotificationModule）。
                //   放在最后：隧道、桥、两条看门狗都装好之后再发 —— 通知讲的是"隧道正在跑"这件事，
                //   早发一秒钟就是一条谎。开关 <c>statusToast=0</c> 时它自己不动作。
                //   发不出通知（比如插件进程没有包身份）只影响通知，绝不影响数据面：
                //   StatusToast 内部全程 try/catch，失败只进 channel=toast 的日志。
                StatusToast.Start(_config, this);

                // ★【1.0.88 · Docs/38 §十一 · D9】Connect 回调累计阻塞时长 —— 给装机验收用的判据行。
                //   依据两条官方原文：
                //   ① <c>IVpnPlugIn.Connect</c> 是平台<b>同步</b>等待的回调（26100 投影里它只有签名、
                //      没有 Remarks，官方也从未承诺时限）⇒ 这一段是我们占住平台的时间；
                //   ② <c>VpnChannel.ActivateForeground</c> 的 Remarks 原文
                //      "While the app is in the foreground, the regular <b>Connect timeout</b> is paused" ——
                //      官方承认<b>存在</b>一个 Connect 超时，但从没给数值。
                //   所以这一行是唯一能和那句官方超时放在同一把尺上比的量；数值口径（多少算慢）是我们自己定的。
                //   本机已有对照：Connect → <c>evt=start</c> 实测 1–2 s，桥启动 157–577 ms（1.0.85 / 1.0.87）。
                RollingLog.Info("connect-window",
                    $"Connect 回调内累计阻塞={(DateTime.Now - _connectBeganAt).TotalMilliseconds:F0}ms" +
                    "（平台同步等这一段；官方承认有 Connect 超时但未给数值）");

                // ★【1.0.89 · Docs/38 §十四】建连成功走完 ⇒ 记下"这条会话活着、且还没被 Disconnect 回调过"。
                //   位置照官方 sample：<c>VpnPlugin.cs:572</c> 就是放在 Connect 末尾。
                //   从这一行起，任何再进来的 Connect 都是<b>重连</b>，<see cref="AbortConnectionAttempt"/>
                //   据此跳过 TerminateConnection 与 SetErrorMessage（条文"Should not be called on reconnect cases."）。
                s_lastConnectFinishedWithoutDisconnect = true;
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
                // ★【1.0.88 · Docs/38 §十一 · D9】失败也要量：占住平台多久之后才抛出去，
                //   和成功路径是同一个判据（上面那条 connect-window 走不到这里）。
                RollingLog.Info("connect-window",
                    $"Connect 回调失败前累计阻塞={(DateTime.Now - _connectBeganAt).TotalMilliseconds:F0}ms" +
                    $" stage={(_started ? "post-start" : "start")}");
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
                        s_kernelStarted = false;
                        s_bridgeKernelV6 = false;
                    }
                    else
                    {
                        DrainBridgeQueue();
                        // ★【1.0.79】这里曾经是<b>无条件</b> s_bridgeResident = true，
                        //   而这条 catch 有一半情况是"步 7 之前"就失败了（空壳不拨号 / Start 抛），
                        //   内核根本没起过 ⇒ 下一次连接会去复用一个不存在的常驻内核。
                        //   现在以"确实起过"为前提（见 <see cref="s_kernelStarted"/>）。
                        s_bridgeResident = s_kernelStarted;
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
                //   ★【1.0.89 · Docs/38 §十四】<b>但重连场景除外</b>：条文原话 "Should not be called on
                //     reconnect cases."，所以 <see cref="AbortConnectionAttempt"/> 在重连时两个 channel
                //     调用都不发（<c>evt=skip-terminate-reconnect</c> 留痕）。这一支的出口本来就是紧跟在
                //     后面的 <c>throw;</c>（<b>原异常</b>，与官方 sample 的 catch 完全同形：
                //     <c>VpnPlugin.cs:595-599</c> 先按 <c>IsReconnect</c> 决定要不要 SetErrorMessage，
                //     然后一律 <c>throw;</c>），所以这一处<b>不需要</b>像那三处早期失败点一样改用
                //     <see cref="AbortOrThrow"/> —— 出口已经有了，只是从"显式终止 + 抛出"变成"只抛出"。
                // ★ <c>_started==true</c>（桥 FailFast / 步 8 之后任何一处抛）这一支<b>不调</b>
                //   TerminateConnection，改为<b>显式</b>发起平台级收尾：1.0.26 之前这里只 rethrow，
                //   注释写着"由平台拆通道后回调 Disconnect 收尾"，而那个假设没有证据（同一个
                //   catch 上面那段注释就记着实测反例"Start 失败 ⇒ 不会有 Disconnect 来收尾"，
                //   run 20 还观察过 OS 拆通道时直接换新进程重新 Connect、从不回调 Disconnect）。
                //   "拆不拆得干净"这件事 00:21 已经量过一次代价（孤儿 NRPT + 幽灵路由 = 整机断网，
                //   Docs/08 §10.10），所以不再靠推断。理由与取舍全在 <see cref="RequestPostStartTeardown"/>。
                if (!_started)
                    AbortConnectionAttempt(channel, "start failed: " + ex.GetType().Name);
                else
                {
                    // ★★ 1.0.27（复核轮）：把这条<b>已失败</b>的 channel 上的数据面闸亲手关上。
                    //   旧写法在这一支什么都不复位：`_started` 只在 :549 置真、只在
                    //   <c>Disconnect</c> 的第一行置假 ⇒ 异常抛出后它仍是 <c>true</c>，
                    //   而下游两道闸都读它：
                    //     · <c>EncapsulateCore</c> 的 <c>if (!_started) return;</c> —— 平台在
                    //       拆这条会话的途中若还往本实例交包，我们会<b>对一个正在销毁的通道</b>
                    //       跑完整数据面（桥还在、泵已被摘，包直接进 dial）；1.0.34 的外层守卫
                    //       只保证"不抛出去"，不保证"不干活"。
                    //     · <c>PumpLoop</c> 的 <c>while (... && _started ...)</c> —— 本 catch 已经
                    //       Cancel 了 s_pumpCts，这里是第二道保险。
                    //   为什么写在这一行而不是 catch 开头：上面 :647-656 的<b>阶段归因</b>
                    //   （"Start 已成功，异常出在后续步骤" vs "StartWithTrafficFilter 异常"）
                    //   和这里的分支判定读的都是同一个 <c>_started</c>，提前置假会把
                    //   桥 FailFast 伪装成 Start 失败 —— 那正是 Docs/09 C4 记过的坑。
                    //   幂等：<c>Disconnect</c> 真来了再置一次同样的值，无副作用。
                    _started = false;
                    RequestPostStartTeardown("post-start 异常 " + ex.GetType().Name);
                }

                throw;
            }
        }

        /// <summary>
        /// Connect 内、<b>Start 成功之前</b>的失败出口。返回值 = <b>"官方那条显式出口（TerminateConnection）
        /// 这次到底用没用"</b>：<c>true</c> 表示已按非重连路径终止，调用方可以 <c>return</c>；
        /// <c>false</c> 表示这是<b>重连场景</b>、按官方条文两次都没调 ⇒ 调用方<b>必须改成抛出</b>
        /// （见下方【重连这一支为什么要有出口】），否则这次 Connect 既没建连也没给平台任何答复。
        ///
        /// 【官方原文一】<c>VpnChannel.TerminateConnection(String)</c>：
        ///   "Terminates the connection attempt performed inside IVpnPlugIn.Connect.
        ///    <b>Should not be called on reconnect cases.</b>"
        ///   —— 它是给"插件自己判定这次<b>新建连</b>做不下去"准备的显式出口，
        ///   不是用来拆已经跑起来的会话（那种形态走平台级断开 +
        ///   <see cref="GracefulTeardown"/> 那道闸，见 <see cref="SessionWatchdog"/>）。
        /// 【官方原文二】<c>SetErrorMessage</c>：
        ///   "These error messages will be displayed to the user in both the VPN flyout and
        ///    settings page when we fail to connect."
        ///
        /// ★★【1.0.89 · Docs/38 §十四 · 订正】旧注释在这里写过一句错的推论：
        ///   "重连场景在 26100 的插件侧读不到 ⇒ 所以这里的调用条件只能是 <c>_started==false</c>
        ///    且我们仍在自己的 Connect 里"。前半句事实成立（平台确实不给标志），
        ///   但<b>由它推出的结论不成立</b>：官方 sample 就是<b>自己推</b>这个标志来门控的。现读为证
        ///   （<c>D:\fluentapps\repos\UwpVpnPluginSample\CSharp\TestVpnPluginAppBg\VpnPlugin.cs</c>）：
        ///   <c>:87</c> <c>bool IsReconnect</c>（注释 "Set this bool to true so that next time
        ///   we are called to connect we will know it is really a reconnection"），
        ///   <c>:572</c> 在 <c>channel.Start(...)</c> 之后立刻置真，<c>:640</c> 只在 <c>Disconnect</c> 置假；
        ///   catch 里 <c>:595-597</c> 原文 <c>if (IsReconnect == false) { channel.SetErrorMessage(ex.Message); }</c>
        ///   配注释 "Note that we do not set the error message for reconnection attempts"，紧跟 <c>throw;</c>；
        ///   而 <b>sample 全文一次都没有调 <c>TerminateConnection</c></b>。
        ///   ⇒ 官方的失败出口是<b>把异常抛回平台</b>，连重连测试异常（<c>exConnect_3</c>，<c>:577</c>）
        ///     都是"从 Connect 里抛出去"这一种形状，条文里那句 "Should not" 于是有了落点。
        ///
        /// 【重连这一支为什么要有出口】跳过两个 channel 调用之后如果直接 <c>return</c>，
        ///   这次 Connect 就<b>什么信号都不给</b>：既不建连、不终止、也不抛 ⇒ 平台只能等它自己那个
        ///   "存在但从不给数值"的 Connect 超时（<c>ActivateForeground</c> 条文），
        ///   正是本篇在修的那一族"把系统吊在那儿"的形态。所以按 sample 换成抛出。
        ///   ★ 这三处抛出点（步 0 AssociateTransport / 步 3 读 CustomField / 步 4 无可用出站）
        ///     全都在<b>路由与 DNS 下发（步 5）之前</b>，此时我们<b>还没</b>往平台交过任何东西，
        ///     所以"异常漏出 Connect 会不会没人收尾"在这里不成立 —— 没有东西要收。
        ///
        /// ★ 本方法自身<b>绝不抛出</b>：它在失败路径上被调用，从这里再抛一次异常
        ///   就等于把"漏异常出平台回调"这个病自己犯一遍（run 34 的实证后果）。
        ///   （返回值给调用方决定抛不抛，抛的是调用方那一句，不是这里。）
        /// </summary>
        private bool AbortConnectionAttempt(VpnChannel channel, string reason)
        {
            // ★【1.0.89】重连场景：TerminateConnection 有条文禁令、SetErrorMessage 被官方 sample 门控，
            //   两个都不调。只留一行证据，并把"这次没走官方出口"回给调用方。
            if (IsReconnectAttempt)
            {
                LogSession($"evt=skip-terminate-reconnect reason={reason} " +
                           "判据=上次 Connect 走完且平台未回调 Disconnect ⇒ 本次是重连（官方 sample 同口径）");
                try
                {
                    channel.LogDiagnosticMessage(
                        "ClashUWP: 重连场景不使用 TerminateConnection/SetErrorMessage（官方条文），" +
                        "本次失败以异常上报: " + reason);
                }
                catch { /* 日志出口偶尔不可用 */ }
                return false;
            }

            try
            {
                // 先设错误消息，再终止连接。
                //   【官方】SetErrorMessage："These error messages will be displayed to the user
                //     in both the VPN flyout and settings page when we fail to connect."
                //   不调它 = 用户在系统 VPN 面板看不到任何失败原因，只看到"连接失败"。
                //   必须在 TerminateConnection 之前调用（顺序不影响功能，但语义上先设消息再终止更清晰）。
                try
                {
                    channel.SetErrorMessage(reason);
                    LogSession($"evt=set-error-message msg={reason}");
                }
                catch (Exception exSet)
                {
                    // SetErrorMessage 失败不致命：至少还能终止连接
                    LogSession($"evt=set-error-message-failed err={exSet.GetType().Name}: {exSet.Message}");
                }

                channel.TerminateConnection(reason);
                LogSession($"evt=terminate-connection reason={reason}");
            }
            catch (Exception ex)
            {
                LogSession($"evt=terminate-connection-failed reason={reason} " +
                           $"err={ex.GetType().Name}: {ex.Message}");
            }
            return true;
        }

        /// <summary>
        /// ★【1.0.89 · Docs/38 §十四】<see cref="AbortConnectionAttempt"/> 的统一写法，给
        ///   Connect 里那三处"<b>步 5 路由/DNS 下发之前</b>"的失败点用（AssociateTransport、
        ///   读 CustomField、无可用出站）：非重连 ⇒ 官方显式出口 + 调用方 <c>return</c>；
        ///   重连 ⇒ 条文禁令生效后<b>必须有别的出口</b>，按官方 sample 换成抛出
        ///   （<c>VpnPlugin.cs:599</c> 就是 <c>throw;</c>，且它连"重连时抛"都不例外）。
        /// 这三处抛出去时我们还没往平台交过任何路由/DNS/NRPT ⇒ 不存在"没人收尾"的敞口。
        /// </summary>
        private void AbortOrThrow(VpnChannel channel, string reason)
        {
            if (!AbortConnectionAttempt(channel, reason))
                throw new InvalidOperationException(
                    "重连场景按官方条文不使用 TerminateConnection/SetErrorMessage，本次失败以异常上报: " + reason);
        }

        /// <summary>
        /// 发起收尾之前先让出的秒数：给平台把这次 Connect 的失败上报处理完的时间。
        /// ★ 这是保险，不是实测结论 —— "断开请求与 Connect 失败同时到达"的形状没量过。
        /// </summary>
        private const int PostStartTeardownDelaySeconds = 3;

        /// <summary>
        /// Connect 内、<b>Start 成功之后</b>才失败的收尾出口（1.0.26，审计项 T1）。
        ///
        /// 【要修的是什么】旧实现在这一支什么都不做，只 <c>throw;</c>，注释写着
        ///   "由平台拆通道后回调 Disconnect → channel.Stop() 收尾"。这个假设没有证据支撑：
        ///   ① 同一个 catch 上面就记着实测反例 —— "Start 失败 ⇒ 不会有 Disconnect 来收尾"；
        ///   ② run 20 观察过 OS 拆通道时直接换新进程重新 Connect，<b>从不</b>回调 Disconnect；
        ///   ③ 00:21 那次整机断网（Docs/08 §10.10）正是"没人收尾"长出来的形状：接口被扯掉时
        ///      平台不清它自己下发的 NRPT，路由还挂在已消失的 ifIndex 上。
        ///   ⇒ "异常抛出去，平台自然会拆干净"是推断，不能再当依赖。
        ///
        /// 【这一支为什么不能用平台对象上的出口】
        ///   · <c>channel.TerminateConnection</c>：条文只管<b>建连尝试</b>
        ///     "Terminates the connection attempt performed inside IVpnPlugIn.Connect.
        ///      Should not be called on reconnect cases." —— 此时 L3 接口与 NRPT 已经装上，
        ///     拿它拆一条已 Start 的会话超出条文。
        ///   · <c>channel.Stop()</c>：条文 "Destroys a previously established VPN channel object.
        ///     Disassociates and closes the outerTunnelTransport connection to the VPN server."，
        ///     官方 sample 只在 <c>IVpnPlugIn.Disconnect</c> 里调它（先遣项目
        ///     VpnPlugInImpl.cpp:775 同），语义是<b>回应平台的断开请求</b>，不是插件发起断开。
        ///   · 插件侧<b>没有</b> <c>Disconnect()</c>（26100 的 VpnChannel 方法表逐条核对过，
        ///     见 <see cref="GracefulTeardown"/> 类注释）。
        ///   ⇒ 站得住的只有用户级那一条：<c>VpnManagementAgent.DisconnectProfileAsync</c>，
        ///     也就是 <see cref="GracefulTeardown"/> 那道<b>唯一</b>的闸。本方法是它的第三个
        ///     触发点（前台、看门狗、这里），不新写第二种拆法。
        ///
        /// 【用闸里的哪一个入口】<see cref="GracefulTeardown.EnsureSessionTornDownAsync"/>
        ///   —— 发起平台级断开并<b>等</b>会话收干净，但<b>不删 profile</b>；不用
        ///   <see cref="GracefulTeardown.DisconnectThenDeleteAsync"/>，因为它额外删 profile。
        ///   删 profile 是"拆掉 RasMan 自动重拨的燃料"这一目的下的动作（看门狗判死时才需要），
        ///   代价是用户必须回前台重建才能再拨 —— 桥 FailFast 不该没收用户的配置。
        ///   重拨风暴那条病因（828 + 系统无限重拨）已由外层目标换成静默黑洞 192.0.2.1 解决
        ///   （1.0.25），不靠删 profile 压。
        ///
        /// 【为什么必须排在 SessionWatchdog.Start 之前也可触发】看门狗是步 8 才武装的，
        ///   而"桥 FailFast"这类异常出在步 7 —— 那时看门狗还没跑。若收尾只有看门狗一个触发点，
        ///   这一支仍然是"没人收尾"。所以它必须直接在 Connect 的 catch 里可达；同时先
        ///   <c>SessionWatchdog.Stop</c> 把另一个闸关掉，保证同一轮只有一个发起者
        ///   （否则日志里分不清隧道是谁拆的 —— 看门狗动手会写 evt=watchdog-trip）。
        ///
        /// 【为什么丢后台线程】<c>IVpnPlugIn.Connect</c> 是<b>同步</b>回调（返回 void），
        ///   而这道闸最长要等 <see cref="GracefulTeardown.WaitSeconds"/> = 20 s 确认会话收干净。
        ///   在平台回调里同步等 20 s 等于把平台的建连状态机钉住；run 34 的实证是同步回调里
        ///   出问题会让平台此后不再交包。所以这里只负责"把收尾这件事发出去"，不在原地等。
        ///
        /// 【rethrow 为什么保留】这是判断，不是顺手：
        ///   ① 这一支已经是"连接失败"，异常是把失败原因送上去的<b>唯一</b>通道
        ///      （TerminateConnection 不能用，见上）；吞掉它 = 平台以为这条隧道建成了，
        ///      而我们正在把它拆掉，状态比原来更不一致。
        ///   ② 前台"连不上自动退回直连"依赖连接失败被上报（1.0.x 的既有产品行为）。
        ///   ③ 保留 rethrow 就是保留发布版已经跑过的形态；改成吞异常是一次<b>行为变更</b>，
        ///     本轮的规矩是没有实测不做行为变更。
        ///   ★ 代价必须写明：throw 出 Connect 之后平台会不会回调 Disconnect，仍然没有实测数据。
        ///     所以收尾<b>不依赖</b>那个回调 —— 本方法自己把会话拆干净；Disconnect 真来了
        ///     也只是再走一遍已有的 <c>DisconnectCore</c>。
        /// </summary>
        private void RequestPostStartTeardown(string why)
        {
            // 唯一闸：先停看门狗，避免两个发起者同时断开。
            try { SessionWatchdog.Stop("post-start 失败，收尾由 Connect 的 catch 发起"); }
            catch { /* Stop 内部已兜住，这里只防进程级意外 */ }

            LogSession($"evt=post-start-teardown reason={why} delay={PostStartTeardownDelaySeconds}s " +
                       "入口=EnsureSessionTornDownAsync（只等不删 profile）");
            RollingLog.Warn("post-start-teardown",
                why + " → Start 已成功但 Connect 抛出，发起平台级断开（不删 profile）");

            // ★ 后台任务里任何异常都必须自己吃掉：这条 Task 一旦抛出，就是"从插件进程
            //   漏出去一个未观察异常"，而它跑在会话收尾的正中间。
            _ = Task.Run(async () =>
            {
                string report;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(PostStartTeardownDelaySeconds))
                        .ConfigureAwait(false);
                    (bool ok, string torn) = await GracefulTeardown.EnsureSessionTornDownAsync()
                        .ConfigureAwait(false);
                    report = (ok ? "会话已确认收干净 " : "★未确认收干净，profile 保留，需手工 rasdial /disconnect ") + torn;
                }
                catch (Exception ex)
                {
                    report = $"收尾异常={ex.GetType().Name}: {ex.Message}";
                }
                LogSession($"evt=post-start-teardown-done report={report}");
                RollingLog.Warn("post-start-teardown", report);
            });
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
            // ★ 1.0.26（T4）：这三列全部走 Interlocked。旧写法是裸 <c>++</c> / <c>+=</c>，
            //   而读它们的人不止一个线程：<c>DatafaceCounters()</c> 由<b>看门狗线程</b>每 10 s 读、
            //   <c>WriteDatafaceSnapshot()</c> 由 Disconnect 线程读 ⇒ 写侧必须原子，
            //   否则"计数在涨但心跳读到旧值"会被读成"数据面停了"（run 30 那类误判的形状）。
            Interlocked.Increment(ref _encapsulateCalls);
            uint listSize = 0;
            try { listSize = packets.Size; } catch { /* 极端情况下取不到 Size，下面按 0 处理 */ }
            Interlocked.Add(ref _encapListItemsSeen, listSize);
            // max 只有一个写者（就是本线程这一处），且比较-赋值不构成跨线程账目 ⇒ 保持普通赋值，
            // 用 Max 的循环写法反而会引入一次多余的 Interlocked 读。
            if (listSize > System.Threading.Volatile.Read(ref _encapListSizeMax))
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
                        Interlocked.Increment(ref _encapsulatedPackets);
                        Interlocked.Add(ref _outBytes, length);
                        Interlocked.Increment(ref _encapEnumerated);
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

                Interlocked.Increment(ref _encapsulatedPackets);
                // ★ 1.0.66：状态通知的"上行"就取这里（隧道 L3 原始包长，不含外层封装）。
                Interlocked.Add(ref _outBytes, length);

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
            // ★ 1.0.26（T4）：本方法里的计数一律 Interlocked，读一律 Volatile.Read 取一次进局部量。
            //   原因不是"Encapsulate 会并发"（平台串行调用它），而是<b>读的人并发</b>：
            //   看门狗线程每 10 s 读 DatafaceCounters()、Disconnect 线程读快照，
            //   裸 uint 的写在另一线程眼里可以长期停在旧值上 —— "数在涨但心跳读到不动"
            //   会被读成"数据面停了"，这正是 run 30 那类误判的形状。
            //   判据（前 N 包采样 / 每 N 包一行日志）用的是同一次读到的值，语义与原实现一致。
            uint encapSeen = System.Threading.Volatile.Read(ref _encapsulatedPackets);
            if (encapSeen <= MaxTunSamples)
                AddSample(s_outSamples,
                    $"#{encapSeen} {IpPacket.Describe(packet, length)}");

            // ★ 组播 / 受限广播 / 0/8 不喂内核（对齐安卓：VpnService 压根不捕获广播类流量）。
            //   喂进去的后果是 sing-tun 的 LinkEndpointFilter 把它们原样回写 tun，
            //   等于把本机自己发的 LLMNR/SSDP 注回本机 —— 实测 Injected(8705) 反超
            //   Encapsulated(7188)，这份虚胖主要就是它贡献的，也会把采样刷满垃圾。
            //   ★ buffer 的交回动作在调用方（摘列表档在 TryReturnSendBuffer，
            //     档 3 靠平台 cleanup），这里 return 不再欠平台的。
            if (IpPacket.IsNonUnicastDestination(packet, length))
            {
                Interlocked.Increment(ref _nonUnicastDropped);
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
                        Interlocked.Increment(ref _ipChecksumFixed);
                    if ((fixedFields & IpPacket.ChecksumFix.Transport) != 0)
                        Interlocked.Increment(ref _transportChecksumFixed);

                    if (_checksumFixLogged == 0 &&
                        Interlocked.Exchange(ref _checksumFixLogged, 1) == 0)
                        channel.LogDiagnosticMessage(
                            "ClashUWP: 交来的包校验和未填完（IP/传输层），已就地补齐再喂内核 —— " +
                            "黑洞主因验证点，后续同类包只计数不再写日志.");
                }

                int rc = MihomoBridge.clash_bridge_put_packet(packet, length);
                if (rc != 0)
                {
                    // ★ "== 1" 这种"只报第一次"的门闩，必须用 Interlocked 的<b>返回值</b>判，
                    //   不能"先 ++ 再读"：读到的可能是别的线程已经加过的值，首报会漏或多报。
                    if (Interlocked.Increment(ref _unknownPackets) == 1)
                        channel.LogDiagnosticMessage("ClashUWP: put_packet 失败 rc=" + rc);
                }
            }
            else if (IpPacket.TryBuildEchoReply(packet, length, out byte[] reply))
            {
                // 回落路径（bridge 未启动）：echo request → echo reply，直接注回本机协议栈。
                // ★ 只在真注入成功时才计 echoReplies —— 否则"应答数"会大于"内核收下的包数"，
                //   把池子借空伪造成"回程正常"。失败原因由 NoteInjectFailure 统一记录。
                if (Inject(channel, reply))
                {
                    Interlocked.Increment(ref _echoReplies);
                    // 1.0.66：回落路径的注包同样算"下行字节"，口径与回程泵一致
                    //（否则"假内核 echo"模式下的状态通知会显示 0 ↓，明明在应答却看不见）。
                    Interlocked.Add(ref _inBytes, reply.Length);
                }
            }
            else
            {
                Interlocked.Increment(ref _unknownPackets);
            }

            if (encapSeen % LogEveryNPackets == 0)
            {
                channel.LogDiagnosticMessage(
                    $"ClashUWP: 已处理 {encapSeen} 包（注入 {System.Threading.Volatile.Read(ref _injectedPackets)}，" +
                    $"echo 应答 {System.Threading.Volatile.Read(ref _echoReplies)}，" +
                    $"异常 {System.Threading.Volatile.Read(ref _unknownPackets)}）.");
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
                    Interlocked.Increment(ref _sendBuffersReappended);
                    Interlocked.Increment(ref _sendBuffersLeft);
                    return;
                }

                channel.AppendVpnSendPacketBuffer(buffer);
                Interlocked.Increment(ref _sendBuffersReturned);

                // 档 2 = 每包一次 Flush（run 35/37 实测成立、产品默认）；
                // 档 5 = 逐包 Append、每批一次 Flush（Flush 由调用方在循环外做）。
                if (mode == 2)
                {
                    channel.FlushVpnSendPacketBuffers();
                    Interlocked.Increment(ref _sendBuffersFlushed);
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
                Interlocked.Increment(ref _sendBuffersFlushed);
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
            Interlocked.Increment(ref _sendReturnFailures);
            // ★ 首因原文用 CompareExchange 抢占，而不是"判空再写"：Encapsulate 与
            //   GetKeepAlivePayload 两条归还路径可能在不同线程失败，判空再写会让两条路各写一次、
            //   把"首个"变成"任一个"。CompareExchange 只有在"原本仍是空串"时才写成功。
            string reason = stage + " " + ex.GetType().Name + ":" +
                ex.Message.Replace('\r', ' ').Replace('\n', ' ');
            if (Interlocked.CompareExchange(ref _sendReturnFirstError, reason, "") == "")
            {
                try
                {
                    channel.LogDiagnosticMessage(
                        "ClashUWP: send buffer 归还失败（首个）" + reason);
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
            //
            // ★★ 【本方法不得请求 buffer —— 1.0.26 审计项 T3 写死的不变式】
            //   【官方 remarks 原文】（先遣项目 docs/65 逐字引的 MSDN
            //   <c>ivpnplugin_decapsulate_1667754039</c>）：
            //     "It is required that any **IVpnPacketBuffer** objects requested by the plugin
            //      be returned to the VPN framework **via either the *decapsulatedPackets* chain**,
            //      to inject into the client TCP/IP stack, **or the *controlPacketsToSend* chain**,
            //      to reply back to the VPN server."
            //   以及 <c>RequestVpnPacketBuffer</c> 自己的 remarks（docs/65 同篇引
            //   <c>vpnchannel_requestvpnpacketbuffer_7633600</c>，★ 这条正是 7188 停摆的机制原文）：
            //     "Any **IVpnPacketBuffer** object requested by the plugin must eventually be
            //      returned to the VPN platform (**i.e. via IVpnPlugIn.Encapsulate or
            //      IVpnPlugin.Decapsulate**). Otherwise the plugin may be unable to request new
            //      buffers until the outstanding buffers have been returned."
            //   ⇒ 在本方法里借 buffer 只有两条合法出路（塞进那两个出参链），而本架构<b>不在这儿投包</b>
            //     （回程由 <see cref="Inject"/> 在泵线程主动注入，见下面的"注入不在这里"），
            //     所以在这里借= 白借，还只能靠 Append+Flush 那套脱离回调上下文的归还 ——
            //     那就是 docs/65 判给官方 sample 的 A2 违规，也正是我们量过的池子耗尽死。
            //   ★ 将来要在这里处理入站包，唯一合法形态是：<b>取包 → 填进 <c>decapsulatedPackets</c>
            //     或 <c>controlPacketsToSend</c> 出参链</b>；任何"先借了回头再还"的写法一律不许。
            //
            // 【注入不在这里】本方法<b>不调</b> <c>AppendVpnReceivePacketBuffer</c>：
            //   出参链是平台在回调返回时读的，泵线程用的是另一条已量通的形态（run 35/37）。
            //   把两条路混在一起会让 <c>rxpend</c> 这一列同时被两个上下文改，事后对不上账。
            try
            {
                Interlocked.Increment(ref _decapsulateCalls);

                // ★ T3 的 Status 读取：<c>VpnPacketBuffer.Status</c> 在 26100 的投影里<b>存在</b>
                //   （本地元数据 <c>P:Windows.Networking.Vpn.VpnPacketBuffer.Status</c>，
                //    返回 <c>VpnPacketBufferStatus</c>：只有 <c>Ok</c> / <c>InvalidBufferSize</c> 两值），
                //   所以读它是安全的 —— 与 <c>VpnPacketBufferList</c> 的坑不同（那个列表没有
                //   GetAt/索引器，foreach 在运行时抛 InvalidCastException，档 3 就死在这儿，
                //   Docs/09 §9）。<b>只读、只记，不做任何处置。</b>
                string status = "读不到";
                try { status = encapBuffer?.Status.ToString() ?? "null"; }
                catch (Exception exSt) { status = exSt.GetType().Name; }

                if (!status.Equals("Ok", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _decapNonOkStatus);
                    if (_decapStatusFirst.Length == 0)
                        _decapStatusFirst = status;
                }

                if (_decapFirstInfo.Length == 0)
                {
                    uint len = 0;
                    try { len = encapBuffer?.Buffer?.Length ?? 0; } catch { }
                    _decapFirstInfo = $"#{_decapsulateCalls} len={len} status={status} " +
                        $"out={decapsulatedPackets?.Size ?? 0} ctl={controlPacketsToSend?.Size ?? 0}";
                    channel.LogDiagnosticMessage(
                        "ClashUWP: ★ Decapsulate 被回调了（本架构预期不会）：" + _decapFirstInfo);
                }
                // 非 Ok 且已经记过首行 ⇒ 单独再报一次这一条，别把"平台一直在交坏 buffer"糊成一列数字。
                else if (!status.Equals("Ok", StringComparison.Ordinal))
                {
                    channel.LogDiagnosticMessage(
                        $"ClashUWP: Decapsulate 第 {_decapsulateCalls} 次回调，encapBuffer.Status={status}" +
                        $"（首次={_decapStatusFirst}，只记录不处置）.");
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
                long gapMs = System.Threading.Interlocked.Read(ref _lastKeepAliveTick) == 0
                    ? 0 : now - System.Threading.Interlocked.Read(ref _lastKeepAliveTick);
                Interlocked.Exchange(ref _lastKeepAliveTick, now);
                // ★ T4：用返回值当"第几次"，别再"先 ++ 再读" —— 心跳线程同时在读这一列。
                uint kaSeq = (uint)Interlocked.Increment(ref _keepAlives);

                // ★ 1.0.26（T2）：取包入口收进 <see cref="TakeSendBuffer"/>。
                //   ★ 1.0.52：只留具名入口 <c>GetVpnSendPacketBuffer()</c> ——
                //     旧臂 <c>RequestVpnPacketBuffer(VpnDataPathType.Send)</c> 吃的枚举被官方标
                //     "not supported and may be altered or unavailable in the future"，已删。
                //   借的是同一个 send 池，写入与"交回出参/orphan 归还"的形状完全不变。
                VpnPacketBuffer? buf = TakeSendBuffer(channel);
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
                if (kaSeq <= 3 || kaSeq % 20 == 0)
                {
                    channel.LogDiagnosticMessage(
                        $"ClashUWP: keep-alive #{kaSeq}，距上次 {gapMs / 1000.0:F1} s，" +
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
            //   这条路径原来是真漏：取包（<c>TakeSendBuffer</c> → GetVpnSendPacketBuffer）一旦成功、后面任何一步抛
            //   （写 IBuffer 越界、AsStream 失败…），<c>keepAlivePacket</c> 就停在 null，
            //   借出的 buffer 再没人管 —— 和"send 池借空停在 7188 包"是同一条病根，
            //   只是 keep-alive 节奏慢（几十秒一次），撞不满配额所以一直没现形。
            //   ★ T2 换取包入口时<b>没有动</b>这段：借到就必须还的义务与用哪条 API 借的无关，
            //     而这条 orphan 归还正是"取包臂换了也不能漏还"的保证（TakeSendBuffer 的注释同口径）。
            //   归还走 Append+Flush（run 33/35 证明"Append 只是入队、必须 Flush 才回池子"），
            //   失败只计数不抛 —— 这个 catch 之外再抛就等于从平台回调里漏出去。
            if (borrowed != null)
            {
                Interlocked.Increment(ref _keepAliveOrphanBuffers);
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
        /// ★【1.0.66】读本次会话的隧道 L3 字节数（上/下行），给 <see cref="StatusToast"/> 用。
        /// 用 <c>Volatile.Read</c>：写侧在平台回调线程与回程泵线程上做 <c>Interlocked.Add</c>，
        /// 读侧是通知循环 —— 与 <see cref="ElapsedSeconds"/> 同一口径，不需要一把锁。
        /// </summary>
        internal void ReadTrafficBytes(out long upBytes, out long downBytes)
        {
            upBytes = System.Threading.Volatile.Read(ref _outBytes);
            downBytes = System.Threading.Volatile.Read(ref _inBytes);
        }

        /// <summary>
        /// 给看门狗心跳用的数据面计数器。心跳里带上它是为了答一个以前答不了的问题：
        ///   run 30 那一轮计数停在什么形状上死的（encap 还在涨、inj 不涨 = 内核不回；
        ///   两个都不涨 = 平台没把包交给我们）。没有逐轮心跳，事后只看得到会话终值。
        ///
        /// ★ <c>ret</c>（归还的 send buffer 数）是 run 32 之后最关键的一列：
        ///   <c>ret</c> 与 <c>encap</c> 必须<b>同步涨</b>。一旦 ret 落后，就是"包进来了、
        ///   buffer 没还"，平台 send 池被逐包借空 —— 那正是四臂同停在 7188 包的机制。
        ///
        /// ★ 1.0.26（T2）新增 <c>takeFail</c>：send 池<b>取包</b>失败次数（待测臂
        ///   <c>GetVpnSendPacketBuffer()</c> 在本仓库从未实测，这一列就是它的判据；默认臂
        ///   <c>GetVpnSendPacketBuffer()</c> 失败也记同一列，见 <see cref="TakeSendBuffer"/>；
        ///   原文原因随 <c>disconnect</c> 行的 <c>takeErr</c> 出来）。
        ///   ★ 本方法只<b>追加</b>字段、不改已有字段名，也不加毫秒 —— 心跳行是
        ///     <c>dataface-sessions.txt</c> 的机器契约（<c>CountConnectsSinceAsync</c> 要按
        ///     前 19 字符取时间戳、按 <c>evt=connect-begin</c> 数重拨）。
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
            $"takeFail={System.Threading.Volatile.Read(ref s_takeSendFailures)} " +
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

            // ★【1.0.89 · Docs/38 §十四】平台既然回调了 Disconnect，这条会话就"死过一次"了：
            //   下一次 Connect 是<b>全新建连</b>、不再算重连 ⇒ <see cref="AbortConnectionAttempt"/>
            //   重新允许 TerminateConnection / SetErrorMessage。
            //   位置照官方 sample（<c>VpnPlugin.cs:640</c> 只在这一处把 IsReconnect 置假），
            //   并跟着 <c>_started</c> 一起<b>抢在第一行</b>改：收尾主体一抛就进 catch，
            //   标志位不能留到 finally 才动（留在 finally 也一样能走到，但这里要的是"绝不落空"）。
            s_lastConnectFinishedWithoutDisconnect = false;

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

            // ★【1.0.49】物理网络兜底看门狗同样要停：平台已在收尾，
            //   它继续探只会读到"探针不通"（那正是正常收尾的形态，不是黑洞），
            //   判死之后会做一次多余的拆隧道动作。
            PhysicalNetworkWatchdog.Stop("平台回调 Disconnect");

            // ★【1.0.38 · G6】跟随网络的事件订阅同样要退：会话结束后还挂着，
            //   下一次换网的事件会落在一个已经没有隧道的进程状态上（日志里看起来像"幽灵跟随"）。
            NetworkFollow.Stop("平台回调 Disconnect");

            // ★【1.0.66】状态通知同样要撤：它讲的就是"隧道正在跑"，
            //   会话都结束了还留着，就是一条永久谎报（通知中心里显示已连接、其实接口早没了）。
            StatusToast.Stop("平台回调 Disconnect");

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
                       $"takeFail={System.Threading.Volatile.Read(ref s_takeSendFailures)} takeErr={s_takeSendFirstError} " +
                       $"rxpend={System.Threading.Volatile.Read(ref s_receiveBuffersPending)} " +
                       $"rxflush={System.Threading.Volatile.Read(ref s_receiveBuffersFlushed)} " +
                       $"decap={_decapsulateCalls} decapFirst={_decapFirstInfo} " +
                       $"kaOrphan={_keepAliveOrphanBuffers} batch={LocalSettings.ReceiveBatch} " +
                       // ★ 1.0.66：字节两列放在最后（sessions 行是 key=value 的机器契约，
                       //   只在尾部追加，不动既有列的顺序与名字）。
                       $"upBytes={_outBytes} downBytes={_inBytes} toast={(LocalSettings.StatusToast ? 1 : 0)}");
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

            // ★【1.0.35 · G5】会话结束这一臂必须在下面的锁块把 s_bridgeGlobal 置假<b>之前</b>
            //   发起（它读的就是那个门闩），且是 fire-and-forget：Disconnect 是平台同步回调，
            //   收尾后面还有 Stop() 要走。这一臂是尽力而为 —— 进程可能随即被挂起，
            //   真正的"干净起点"由会话开始那一臂保证（见 StartMihomoBridge）。
            SessionReset.AtSessionEnd();

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
                    // ★【1.0.79 · 实测根因】曾经这里是<b>无条件</b> <c>true</c>：这条收尾对
                    //   "会话到底走到没走到步 7"一无所知。空壳不拨号 / Start 之前任何 abort
                    //   都会走到这里，于是把"没起过的内核"标成常驻留给下一次连接 ——
                    //   下一次连接跳过 clash_bridge_start，隧道照装、泵照挂，但<b>没有内核</b>：
                    //   实测 <c>encap=281 inj=0</c> + <c>fake-ip=NO:解析超时</c> +
                    //   <c>内核=HttpRequestException</c>，58 s 后被插件看门狗判死删 profile。
                    //   口径同 <see cref="s_kernelStarted"/>：策略只能建立在事实之上。
                    s_bridgeResident = s_kernelStarted;
                    _bridgeDiag += $"｜会话结束，内核{(s_bridgeResident ? "常驻" : "（本进程没起过内核）")}" +
                                   $"（排空回程 {drained} 包）";
                    channel.LogDiagnosticMessage(
                        $"ClashUWP: 回程泵已退出、排空 {drained} 包，" +
                        (s_bridgeResident ? "mihomo 内核保持常驻." : "本进程未曾起内核，下次连接将全量启动."));
                }
                else
                {
                    MihomoBridge.clash_bridge_stop();
                    s_bridgeResident = false;
                    s_kernelStarted = false;
                    s_bridgeKernelV6 = false;
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
                $"DnsIntoTunnel={LocalSettings.DnsIntoTunnel} ({string.Join(",", EffectiveNrptDnsServers())}) " +
                $"nrpt={(_dnsConfig.Has("nrpt-dns") ? "dns-config 覆盖" : "profile")}\r\n" +
                // ★【1.0.40 · DNS 可配】内核侧覆盖的结果：应用几项、跳过什么、拒绝了什么。
                //   最坏的失败形态是"界面存下了、内核配置里没有"——这一列就是那条证据。
                $"Dns={_dnsNote}\r\n" +
                // ★【1.0.70 · G7】IPv6 三档的现场：臂号 / 接口地址 / v6 路由 / fake-ip v6 池。
                //   arm=0 的"未下发"必须逐字可见 —— 它是"这次连接与 1.0.69 完全一致"的凭据。
                $"Ipv6={_ipv6Diag}\r\n" +
                // ★【1.0.119 · G7】路由表四桶的实际形状（判据本体 RouteInclusion，离线单测钉住）。
                //   v6 排除路由以前根本不存在，装错族别也只体现在"某个节点连不上"，
                //   这一行把四桶计数 + 被剔除条数固化成会话档案，取证时不用猜。
                $"Routes={_routePlanNote}\r\n" +
                $"NodeExclusions={(_nodeExclusions.Count)} switch={(LocalSettings.NodeExclusions ? "on" : "off")} 宿主路由(v4/32·v6/128)\r\n" +
                $"SelfSplitTunnel={_selfSplitFilterInfo}\r\n" +
                // ★【1.0.35 · G9/G5】两行新证据。G9 最容易失败的形态是"名单存下了、
                //   内核配置里却没有那几行"（rules 块缺失/行内写法/fakeIp 总闸关闭三种静默路径），
                //   G5 最容易失败的形态是"控制器在包内根本不可达"（Docs/12 §8 那条未验前提）——
                //   两条都必须能在这一份档案里当场分辨，不能只活在滚动日志的时间轴里。
                $"AppSplit={_splitNote}\r\n" +
                $"SessionReset={SessionReset.LastNote}\r\n" +
                // ★【1.0.38 · G6】跟随网络的两行证据：
                //   NetFollow = 最近一次评估（事件名 / 指纹变没变 / bound_iface 前后值 / 动作），
                //   NodeCache = 排除路由缓存的落盘时间与"是否已作废待重解析"
                //   （换网后若这里一直是"已作废"，说明下次连接的强制重解析一直没成功 ——
                //    那正是"节点域名解析到了旧网络的 IP"的证据）。
                $"NetFollow={NetworkFollow.LastNote}\r\n" +
                $"NodeCache={NodeRoutes.DescribeCacheAge()}\r\n" +
                $"OuterTarget={_outerTargetUsed}:{LocalSettings.OuterPort} (要求={LocalSettings.OuterTarget}) fakeIp={(LocalSettings.FakeIp ? "on" : "off")}\r\n" +
                $"IncludeMode={LocalSettings.IncludeMode} inclusion=[{string.Join(",", (LocalSettings.IncludeMode.Equals("narrow", StringComparison.OrdinalIgnoreCase) ? NarrowInclusionRoutes() : _config.IncludeRoutes))}]\r\n" +
                $"SessionSeconds={ElapsedSeconds():F0}\r\n" +
                $"KeepAlives={_keepAlives}\r\n" +
                $"Encapsulated={_encapsulatedPackets}\r\n" +
                $"Injected={_injectedPackets}\r\n" +
                // ★ 1.0.66：隧道 L3 字节量（状态通知用的就是这两个数）。
                //   写进快照是为了事后能核对"通知里显示的速率是不是读错了"：
                //   包数与字节数同向才说明计数点没漏（例如回程泵丢包时字节也不该加）。
                $"UpBytes={_outBytes} DownBytes={_inBytes}\r\n" +
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
                $"ReceiveBatch={LocalSettings.ReceiveBatch} " +
                $"ReceiveBuffersPending={System.Threading.Volatile.Read(ref s_receiveBuffersPending)} " +
                $"ReceiveBuffersFlushed={System.Threading.Volatile.Read(ref s_receiveBuffersFlushed)}\r\n" +
                // ★ 1.0.26（T2）：send 池取包失败计数。1.0.52 起取包只有一条入口
                //   GetVpnSendPacketBuffer()（旧臂 RequestVpnPacketBuffer(VpnDataPathType) 已删）。
                //   KeepAlives 涨而 TakeSendFailures 同步涨 = 这条取包路走不通。
                $"TakeSendFailures={System.Threading.Volatile.Read(ref s_takeSendFailures)} " +
                $"TakeSendFirstError={s_takeSendFirstError}\r\n" +
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
            // ★ 观测性修复（1.0.41）：桥的生命周期过去只走 channel.LogDiagnosticMessage
            //   （发给平台，<b>不进 RollingLog</b>），于是 plugin.log 里 kernel-home 之后一片
            //   安静，把"其实跑得好好的"误读成"内核没起来"。这里每个分支都补一条 RollingLog。
            // ★ 健壮性修复：给"取 s_bridgeGate 锁"和"clash_bridge_start 的 P/Invoke"各加超时。
            //   实测形态（1.0.38，pid=8964 会话无 disconnect、pid=3196 新进程 evt=start 后彻底
            //   静默）最像两种卡死：① 上一会话的 Disconnect 在锁内做 clash_bridge_stop/BoundInterface
            //   的 native 调用没返回 → 新进程 Start 卡在取锁；② 新进程 clash_bridge_start 撞 9090
            //   端口（上一进程残留桥还占着）卡死在 hub.ApplyConfig。两种都会把 Connect 线程永久
            //   wedge 且日志无痕。加超时后从"静默永久卡死"变成"有记录的失败 + failFast 拆连接"。
            //   代价：超时的 native 调用线程会被弃用（它不持锁，弃用不影响锁），换 Connect 不被拖死。
            string mmdbDir;
            try
            {
                mmdbDir = KernelHome.Ensure();
            }
            catch (Exception ex)
            {
                RollingLog.Error("bridge-start", $"KernelHome.Ensure 异常: {ex.GetType().Name}: {ex.Message}", ex);
                _bridgeDiag = "KernelHome.Ensure 异常: " + ex.Message;
                s_bridgeGlobal = false;
                if (LocalSettings.FailFast) throw;
                return;
            }

            bool lockTaken = false;
            try
            {
                // 取锁也要限时：上一任收尾若卡在 native Stop，这里不能无限等。
                if (!Monitor.TryEnter(s_bridgeGate, TimeSpan.FromSeconds(10)))
                {
                    string msg = "取 s_bridgeGate 锁超时 10s（上一会话的桥收尾/Stop 仍卡在 native P/Invoke）";
                    RollingLog.Error("bridge-start", msg);
                    channel.LogDiagnosticMessage("ClashUWP: mihomo " + msg);
                    _bridgeDiag = msg;
                    s_bridgeGlobal = false;
                    if (LocalSettings.FailFast)
                        throw new InvalidOperationException("mihomo 内核桥取锁超时，已按 failFast 放弃本次连接：" + msg);
                    return;
                }
                lockTaken = true;

                // 上一任的泵先摘除（它可能还挂在已 Stop 的旧 channel 上）。
                // ★ 放在重建判断之前：停桥重建时绝不能让旧泵还对着旧 vtun 取包。
                var old = s_pumpCts;
                s_pumpCts = null;
                try { old?.Cancel(); } catch { }

                // ★【1.0.70 · G7】常驻栈的地址族是 Start 时定死的，热重载改不了
                //   （理由见 <see cref="s_bridgeKernelV6"/>）。目标档 2 而常驻栈没有 v6
                //   时先 Stop 再全量 Start —— 这是 <c>residentBridge=0</c> 每次断开都在走
                //   的销毁/重建路径，不是"同进程第二次 Start"那条病态路径。
                //   Stop 失败不阻断：后续 Start 会命中 already started 并按复用处理，
                //   快照里 s_bridgeKernelV6 与实际不符会体现在下一轮的判断上。
                if (s_bridgeResident && LocalSettings.Ipv6 >= 2 && !s_bridgeKernelV6)
                {
                    RollingLog.Info("bridge-start",
                        "ipv6 档 0/1 → 2：常驻内核的 gvisor 栈没有 v6，停桥重建（Stop → Start）");
                    try
                    {
                        int stopRc = MihomoBridge.clash_bridge_stop();
                        RollingLog.Info("bridge-start", $"为重建 v6 栈调用 clash_bridge_stop rc={stopRc}");
                    }
                    catch (Exception ex)
                    {
                        RollingLog.Error("bridge-start", "为重建 v6 栈停桥抛异常: " + ex.Message, ex);
                    }
                    s_bridgeResident = false;
                    s_kernelStarted = false;
                    s_bridgeGlobal = false;
                }

                // ★ 复用/首次都要先排空回程队列：常驻时队列里可能留着上一会话的包，
                //   注进新建的 L3 接口就是凭空给 TCP 塞陈旧段（TLS 首包失败的现成成因）。
                int drained = DrainBridgeQueue();

                bool configChanged = Fingerprint(config) != s_lastConfigFingerprint;

                int rc = 0;
                bool calledStart = false;
                bool startTimedOut = false;

                if (!s_bridgeResident)
                {
                    // 路径 1：本进程首次（或上一次以 residentBridge=0 停过桥）→ 全量启动。
                    // 路径 2：常驻复用 —— 根本不再调用 clash_bridge_start，
                    //   这样"同进程第二次 Start"这条病态路径在本版本里不存在。
                    RollingLog.Info("bridge-start",
                        $"本进程首次启动桥 mtu={_config.Mtu} drained={drained} " +
                        $"mmdbDir={(mmdbDir.Length == 0 ? "(空!)" : mmdbDir)}");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    // ★【1.0.70 · G7 · arm 2】必须在 P/Invoke 之前设：mihomo 的
                    //   <c>config.Parse</c> 里 <c>parseIPV6(rawCfg)</c>（config.go:716）会调
                    //   <c>verifyIP6()</c>（config/utils.go:205）—— 本机没有全局 IPv6 地址，
                    //   校验失败就会把 <c>tun.inet6-address</c> 与 <c>dns.fake-ip-range6</c>
                    //   静默清空（config.go:1679-1682），arm 2 的内核半边等于没写。
                    //   官方留了环境变量 <c>SKIP_SYSTEM_IPV6_CHECK=1</c> 作为这个校验的
                    //   逃生口（utils.go 先读它、命中即直接返回 true），口径与 Clash Verge
                    //   在无 v6 机器上开 v6 一致。只影响本次进程解析，不改注册表/系统设置。
                    if (LocalSettings.Ipv6 >= 2)
                    {
                        try
                        {
                            Environment.SetEnvironmentVariable("SKIP_SYSTEM_IPV6_CHECK", "1");
                            RollingLog.Info("bridge-start",
                                "arm=2 已设 SKIP_SYSTEM_IPV6_CHECK=1（本机无全局 v6，跳过 mihomo 的系统校验）");
                        }
                        catch (Exception ex)
                        {
                            // 设不上不致命：没有 inet6-address 时内核只是不跑 v6，
                            // 隧道侧的地址与路由仍在 —— 会体现在快照的 _ipv6Diag 里。
                            RollingLog.Warn("bridge-start", "设置 SKIP_SYSTEM_IPV6_CHECK 失败: " + ex.Message);
                        }
                    }
                    var startTask = Task.Run(() => MihomoBridge.Start(config, (int)_config.Mtu, mmdbDir));
                    calledStart = true;
                    if (!startTask.Wait(TimeSpan.FromSeconds(20)))
                    {
                        startTimedOut = true;
                        rc = -99;
                    }
                    else
                    {
                        rc = startTask.Result;
                        RollingLog.Info("bridge-start", $"clash_bridge_start 返回 rc={rc} 用时={sw.ElapsedMilliseconds}ms");
                    }
                }

                if (startTimedOut)
                {
                    string msg = "clash_bridge_start 超时 20s（疑似卡死在 P/Invoke / hub.ApplyConfig / 9090 被上一进程占用）";
                    RollingLog.Error("bridge-start", msg);
                    channel.LogDiagnosticMessage("ClashUWP: mihomo " + msg);
                    _bridgeDiag = msg;
                    s_bridgeGlobal = false;
                    if (LocalSettings.FailFast)
                        throw new InvalidOperationException("mihomo 内核启动超时，已按 failFast 放弃本次连接：" + msg);
                    return;
                }

                string lastErr = calledStart ? MihomoBridge.LastError() : "";

                if (s_bridgeResident ||
                    (calledStart && rc == -2 && lastErr != null && lastErr.Contains("already started")))
                {
                    // 桥已在跑（常驻或残留）：泵改挂到当前 channel，数据面继续可用。
                    s_bridgeResident = true;
                    // ★【1.0.79】走到这里只有两种可能：上一会话确实起过内核并留着，
                    //   或 Go 侧刚回过 already started —— 两者都等于"内核确实活着"，
                    //   所以事实位跟策略位一起置真（见 <see cref="s_kernelStarted"/>）。
                    s_kernelStarted = true;
                    s_bridgeGlobal = true;
                    _bridgeDiag = $"复用常驻内核（排空 {drained} 包" +
                                  (configChanged ? "，配置已变 → 尝试热重载" : "，配置未变") + "）";
                    RollingLog.Info("bridge-start", _bridgeDiag);
                    channel.LogDiagnosticMessage("ClashUWP: 内核常驻，回程泵改挂当前 channel.");

                    if (configChanged)
                        TryHotReload(channel, configPath);
                }
                else if (rc != 0)
                {
                    RollingLog.Error("bridge-start", $"start 失败 rc={rc}: {lastErr}");
                    channel.LogDiagnosticMessage($"ClashUWP: mihomo bridge start 失败 rc={rc}: {lastErr}");
                    _bridgeDiag = $"start 失败 rc={rc}: {lastErr}";
                    s_bridgeGlobal = false;

                    // ★ failFast（默认开）：内核没起来就<b>不要</b>留着这条隧道。
                    //   run 25 实测的形态正是"配置解析失败 → 只记日志 → 隧道照装"：
                    //   快照 Encapsulated=883 / Injected=0，整机断网 60~90 s 才由哨兵兜住。
                    //   抛出去会让 Connect 走 catch（平台据此终止本次连接），
                    //   物理网卡立刻恢复 —— "连不上就退回直连"比"连上了但全黑"有用得多。
                    if (LocalSettings.FailFast)
                        throw new InvalidOperationException(
                            $"mihomo 内核启动失败，已按 failFast 放弃本次连接：rc={rc} {lastErr}");

                    return;
                }
                else
                {
                    s_bridgeGlobal = true;
                    s_bridgeResident = true;
                    s_kernelStarted = true;   // ★【1.0.79】clash_bridge_start 返回 0：内核事实存在
                    // ★【1.0.70 · G7】栈的地址族在这一刻定死，供下一轮判断要不要重建。
                    s_bridgeKernelV6 = LocalSettings.Ipv6 >= 2;
                    _bridgeDiag = $"本进程首次启动（排空 {drained} 包，v6栈={(s_bridgeKernelV6 ? "on" : "off")}）";
                    RollingLog.Info("bridge-start", "mihomo bridge 已启动，回程泵运行中.");
                    channel.LogDiagnosticMessage("ClashUWP: mihomo bridge 已启动，回程泵运行中.");

                    // ★【1.0.75 · 撤销 1.0.73 的 NotifyStatus】那个"盾标修复"的一半是<b>不存在的 API</b>：
                    //   1.0.73 写的 <c>channel.NotifyStatus(VpnManagementErrorStatus.Ok)</c> 从未编译过 ——
                    //   当时组件没重新 pack（本地 feed 里最高只有 1.0.40，主工程还原报
                    //   "找不到版本为 (>= 1.0.42)"，见 Docs/30 §2.1），错误被那次失败的构建一起吞掉了。
                    //   现已按官方条文核实：<c>VpnChannel</c> 在 26100 的投影里
                    //   （Microsoft.Windows.SDK.NET.Ref 10.0.26100.57，ilspycmd 全量导出）
                    //   <b>没有任何 Notify* 成员</b>，也只有 IVpnChannel/2/4/5/6 五层接口。
                    //   平台侧"隧道已就绪"这件事本来就是 <c>Start(...)</c> 成功 + 首个包被
                    //   取走时由框架自己推进的，插件没有"上报状态"的原语可拿。
                    //   ⇒ 盾标那条线索只剩 <b>ServerUri 不再指向 127.0.0.1</b>（1.0.73 的另一半，
                    //     现默认 192.0.2.1，见 Docs/27 已更正的结论）。这里不再放任何"调了但没用"的调用。

                    // ★【1.0.74 · P1】检查系统健康状态（Statement of Health）。
                    //   【官方】SystemHealth："Gets the statement of health for the VPN connection."
                    //   企业/MDM 场景下，平台会要求设备符合某些合规策略（如防火墙开启、杀毒软件运行），
                    //   不符合时返回 Statement of Health blob，插件应把它发给服务器做二次校验。
                    //   ClashUWP 是个人用户场景，不发也没关系，但读一下留日志有助于诊断：
                    //   如果健康检查失败，可能是 MDM 策略阻止了连接。
                    try
                    {
                        var health = channel.SystemHealth;
                        if (health != null && health.StatementOfHealth != null)
                        {
                            uint blobLen = health.StatementOfHealth.Length;
                            RollingLog.Info("system-health", $"系统健康检查返回 StatementOfHealth blob（{blobLen} 字节）");
                            channel.LogDiagnosticMessage($"ClashUWP: SystemHealth 检查通过，blob 长度={blobLen}");
                        }
                        else
                        {
                            RollingLog.Info("system-health", "系统健康检查通过（无 StatementOfHealth，非 MDM 管理设备）");
                        }
                    }
                    catch (Exception healthEx)
                    {
                        // SystemHealth 失败不致命：只是诊断信息，不影响连接本身
                        RollingLog.Warn("system-health", $"SystemHealth 检查异常: {healthEx.GetType().Name}: {healthEx.Message}");
                    }
                }

                s_lastConfigFingerprint = Fingerprint(config);
                // ★【1.0.38 · G6】留档三个入参：网络变化后的"重绑"就是拿它们再调一次
                //   <c>clash_bridge_start</c>，命中 Go 侧"已启动 → rebindInterface()"分支。
                //   只存不解析：已启动分支根本不读这份配置（见字段注释）。
                s_lastBridgeConfig = config;
                s_lastBridgeMmdbDir = mmdbDir;
                s_lastBridgeMtu = (int)_config.Mtu;

                // ★【1.0.35 · G5】会话开始这一臂：桥已经就绪（冷启动成功或复用常驻都算），
                //   此时清缓存的语义是"这次连接从干净状态开始"（对齐安卓进入时那次 reset）。
                //   sessionReset=0（默认）时本调用只做一次判断就返回，不产生任何 HTTP。
                //   放在热重载<b>之后</b>发起、并自带 1.5 s 延迟：PUT /configs 会重建配置，
                //   先清再重载等于白清。
                SessionReset.AtSessionStart();

                // 回程泵：mihomo 的回包是异步产生的，VpnChannel 没有「有包了」的事件，
                // 只能轮询 get_packet → Inject。5ms 一拍，无包时休眠不空转。
                var cts = new CancellationTokenSource();
                s_pumpCts = cts;
                _ = Task.Run(() => PumpLoop(channel, cts.Token));
            }
            catch (Exception ex)
            {
                // ★ StartMihomoBridge 内部的任何异常都必须进 RollingLog，否则会被外层 catch
                //   （只走 channel 日志）吞掉，plugin.log 里什么都不留（这正是最初的观测盲区）。
                RollingLog.Error("bridge-start", $"StartMihomoBridge 异常: {ex.GetType().Name}: {ex.Message}", ex);
                channel.LogDiagnosticMessage($"ClashUWP: StartMihomoBridge 异常: {ex.GetType().Name}: {ex.Message}");
                _bridgeDiag = $"桥启动失败: {ex.Message}";
                s_bridgeGlobal = false;

                if (LocalSettings.FailFast)
                    throw;
            }
            finally
            {
                if (lockTaken) Monitor.Exit(s_bridgeGate);
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
                        SessionReset.ControllerBase + "/configs?force=true")
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
                    {
                        RollingLog.Info("hot-reload", $"PUT /configs?force=true -> {code}");
                        // ★【1.0.38 · G6】"热重载会把防自环绑定清空"这件事必须在这里补回来：
                        //   ApplyConfig 无条件执行 dialer.DefaultInterface.Store(general.Interface)
                        //   （executor.go:412），而我们生成的配置没有 interface-name（空串）。
                        //   补法走 PATCH /configs 的同一个 Store（configs.go:366-368），
                        //   用的是桥侧记录的原名字，不重探测、不改 Go。
                        await RestoreInterfaceBindingAsync("hot-reload");
                    }
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

        // ══ G6 跟随网络（1.0.38）════════════════════════════════════════════
        // 这一块的三个成员只被 NetworkFollow 与 TryHotReload 调用，为什么不放进 NetworkFollow：
        //   重绑要碰 s_bridgeGate / s_lastBridge* / s_bridgeGlobal —— 它们都是本类的状态，
        //   跨类搬过去只会多一层"谁负责拿锁"的模糊。

        /// <summary>桥侧记录的绑定网卡名（空 = 未绑定/桥没起）。绝不抛。</summary>
        internal static string BoundIfaceNow()
        {
            try { return MihomoBridge.BoundInterface() ?? ""; }
            catch { return ""; }
        }

        /// <summary>
        /// 请求桥重新探测并绑定物理网卡（G6 档 2 的动作臂）。
        /// ★ 立即返回、动作在后台线程：调用点是 <c>NetworkInformation</c> 的平台事件线程，
        ///   那里最多允许读属性 + 写日志，不允许等锁/等 native。
        /// </summary>
        /// <returns>提交结果的人读句子（写进 <see cref="NetworkFollow.LastNote"/> 与快照）。</returns>
        internal static string RequestBridgeRebind(string reason)
        {
            if (!s_bridgeGlobal)
            {
                RollingLog.Warn("net-follow", $"重绑跳过（{reason}）：桥未运行");
                return "跳过：桥未运行";
            }
            if (s_lastBridgeConfig.Length == 0)
            {
                RollingLog.Warn("net-follow", $"重绑跳过（{reason}）：无 start 参数留档");
                return "跳过：无 start 参数";
            }
            _ = Task.Run(() => RebindBridgeCore(reason));
            return "已提交重绑";
        }

        /// <summary>
        /// 重绑的执行体：拿 <c>s_bridgeGate</c> → 再调一次 <c>clash_bridge_start</c>。
        ///
        /// 【为什么"再 start 一次"就是重绑】Go 侧 <c>bridge/core.go:132-137</c>：
        ///   栈已在 ⇒ <c>b.rebindInterface()</c> 后返回 <c>ErrAlreadyStarted</c>（rc=-2）。
        ///   <c>rebindInterface()</c> 会重新枚举 UP 网卡、排除名为 <c>ClashUWP</c> 的
        ///   自家 L3 接口与虚拟网卡，把新名字 <c>dialer.DefaultInterface.Store(...)</c>。
        ///   而"已启动"分支<b>不 Parse 配置、不 ApplyConfig</b> ⇒ 不会动规则/节点/TUN，
        ///   也不会再撞 <c>hub.ApplyConfig</c> 的 9090 端口（那条是本项目记录过的 wedging 形态）。
        ///
        /// 【失败只记日志】这是网络事件触发的最佳努力动作：重绑失败不等于数据面坏了
        ///   （旧绑定可能仍然可用），绝不能因为"跟随网络"这个附加能力去拆隧道。
        /// </summary>
        private static void RebindBridgeCore(string reason)
        {
            bool lockTaken = false;
            try
            {
                if (!s_bridgeGlobal)
                {
                    RollingLog.Info("net-follow", $"重绑放弃（{reason}）：桥已停止");
                    return;
                }
                if (!Monitor.TryEnter(s_bridgeGate, TimeSpan.FromSeconds(10)))
                {
                    RollingLog.Warn("net-follow", $"重绑放弃（{reason}）：取 s_bridgeGate 超时 10s（上一任收尾仍卡在 native）");
                    return;
                }
                lockTaken = true;

                string before = BoundIfaceNow();
                string cfg = s_lastBridgeConfig;
                int mtu = s_lastBridgeMtu;
                string mmdb = s_lastBridgeMmdbDir;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var task = Task.Run(() => MihomoBridge.Start(cfg, mtu, mmdb));
                bool timedOut = !task.Wait(TimeSpan.FromSeconds(20));
                int rc = timedOut ? -99 : task.Result;
                string err = MihomoBridge.LastError();
                string after = BoundIfaceNow();

                // rc=0 只可能出现在"桥其实已经没了"的竞态里（我们的 start 重新把栈建起来了）。
                // 不把它当成功、也不拆会话：泵还在轮询，put_packet 的 -1 计数会把真相留在快照里。
                string verdict = timedOut ? "超时 20s"
                    : rc == 0 ? "意外冷启动 rc=0"
                    : (rc == -2 && err.IndexOf("already started", StringComparison.OrdinalIgnoreCase) >= 0)
                        ? "已启动→已重绑"
                        : $"rc={rc} {err}";

                string line = $"重绑（{reason}）结果={verdict} rc={rc} " +
                              $"bound={(before == after ? after : $"{before}→{after}")} " +
                              $"用时={sw.ElapsedMilliseconds}ms";
                if (verdict == "已启动→已重绑")
                    RollingLog.Info("net-follow", line);
                else
                    RollingLog.Warn("net-follow", line);
                LogSession($"evt=net-rebind reason={reason} rc={rc} " +
                           $"bound={before}->{after} ms={sw.ElapsedMilliseconds}");
            }
            catch (Exception ex)
            {
                RollingLog.Error("net-follow", $"重绑异常（{reason}）: {ex.GetType().Name}: {ex.Message}", ex);
            }
            finally
            {
                if (lockTaken)
                {
                    try { Monitor.Exit(s_bridgeGate); } catch { /* 理论上不会发生 */ }
                }
            }
        }

        /// <summary>
        /// 热重载成功后把防自环绑定写回去（<c>PATCH /configs</c>）。
        /// ★ 为什么用桥侧记录的名字而不是重新探测：那个名字是 Go 侧
        ///   <c>detectDefaultInterface</c> 的输出，和 <c>iface.ResolveInterface</c> 的口径<b>同一套</b>；
        ///   在 C# 里另写一份探测等于制造两套命名，出分歧时反而更难查。
        /// ★ 调用点只有 TryHotReload 成功分支：PATCH 的 tun/端口等字段全都是"未提供 =
        ///   沿用上次值"（configs.go:145-285 的 pointerOrDefault*），
        ///   且 <c>listener.ReCreateTun</c> 对相同配置走 <c>Equal(LastTunConf)</c> 提前返回
        ///   （listener/listener.go:509-514），所以这次 PATCH 只改 interface-name 一项。
        /// </summary>
        internal static async Task RestoreInterfaceBindingAsync(string via)
        {
            string iface = BoundIfaceNow();
            if (iface.Length == 0)
                iface = s_boundIfaceSnapshot;
            if (iface.Length == 0)
            {
                // 桥没绑上（探测失败）时无值可恢复：这本身就是要留档的证据，
                // 下一次连接时 Start 会再试一次，不能凭空编一个网卡名写进内核。
                RollingLog.Warn("iface-bind", $"{via}: 无 bound_iface 可恢复，防自环绑定仍为空");
                return;
            }

            try
            {
                using var http = new System.Net.Http.HttpClient(
                    new System.Net.Http.HttpClientHandler { UseProxy = false });
                http.Timeout = TimeSpan.FromSeconds(3);

                string json = "{\"interface-name\":\"" + EscapeJson(iface) + "\"}";
                using var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Patch,
                    SessionReset.ControllerBase + "/configs")
                {
                    Content = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json")
                };
                using System.Net.Http.HttpResponseMessage resp = await http.SendAsync(req);
                int code = (int)resp.StatusCode;
                if (code >= 200 && code < 300)
                    RollingLog.Info("iface-bind", $"{via}: PATCH interface-name={iface} -> {code}");
                else
                    RollingLog.Warn("iface-bind", $"{via}: PATCH interface-name={iface} -> {code}");
                LogSession($"evt=iface-bind via={via} iface={iface} code={code}");
            }
            catch (Exception ex)
            {
                // 绑定没恢复 ≠ 隧道立刻坏：现有连接已经建立，受影响的是<b>下一次</b>拨号。
                RollingLog.Warn("iface-bind", $"{via}: 恢复绑定失败={ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>手写 JSON 字符串转义（本组件 Native AOT，反射版序列化会抛）。</summary>
        private static string EscapeJson(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char ch in s)
            {
                if (ch is '"' or '\\') sb.Append('\\').Append(ch);
                else if (ch == '\n') sb.Append("\\n");
                else if (ch == '\r') sb.Append("\\r");
                else if (ch == '\t') sb.Append("\\t");
                else if (ch < 0x20) sb.Append($"\\u{(int)ch:x4}");
                else sb.Append(ch);
            }
            return sb.ToString();
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
                        // ★ 1.0.66：状态通知的"下行"取这里（gvisor 栈发回来、注回本机协议栈的
                        //   原始 IP 包长度，n 就是这条包的净长）。
                        Interlocked.Add(ref _inBytes, n);

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
        /// 取 mihomo 配置：
        /// ★【1.0.62】<paramref name="subId"/> 非空时优先读 LocalFolder\subscriptions\{subId}.yaml
        ///   （该订阅专属正文，对应系统 profile ClashUWP-{subId}）；
        /// 否则读 LocalFolder\config.yaml（前台写入的生效副本，对应基础 profile ClashUWP）。
        /// 两者都缺失时用内置最小配置（rule 模式 + DIRECT，tun.enable=false —— 路由归 VpnChannel 管）。
        /// 返回值第二项是配置文件绝对路径，只在真的读了文件时非空 —— 常驻热重载要用它。
        /// </summary>
        private static (string Text, string? Path) LoadMihomoConfig(string? subId = null)
        {
            // ★ 每条订阅一个 profile：读该订阅专属正文。
            if (!string.IsNullOrWhiteSpace(subId))
            {
                try
                {
                    // subId 已在 VpnPluginConfig.Parse 里消毒（仅 [A-Za-z0-9._-]），
                    // 这里再夹一道：拼出来的路径一定落在 subscriptions\ 下，杜绝穿越。
                    var subFile = ApplicationData.Current.LocalFolder
                        .GetFolderAsync("subscriptions").AsTask().GetAwaiter().GetResult()
                        .GetFileAsync(subId + ".yaml").AsTask().GetAwaiter().GetResult();
                    string path = subFile.Path;
                    using (var stream = subFile.OpenStreamForReadAsync().GetAwaiter().GetResult())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string text = reader.ReadToEnd();
                        if (!string.IsNullOrWhiteSpace(text))
                            return (text, path);
                    }
                }
                catch (Exception ex)
                {
                    // 该订阅正文缺失（如文件被清但 profile 还在）→ 回落到内置最小配置，
                    // 隧道仍要能起来，不能因一份缺失的订阅把连接整体拖垮。
                    RollingLog.Warn("config",
                        $"订阅正文 subscriptions/{subId}.yaml 读不到，回落内置兜底: {ex.GetType().Name}: {ex.Message}");
                }
            }

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
        /// 【官方】RequestVpnPacketBuffer(VpnDataPathType, out VpnPacketBuffer)
        ///   （本地 26100 元数据逐字，★ 整句以 "<b>Not supported.</b>" 开头）：
        ///   "Not supported. Requests a IVpnPacketBuffer from the specified pool. There is one
        ///    IVpnPacketBuffer for the send path and another IVpnPacketBuffer for the receive path"
        ///   枚举 <c>VpnDataPathType</c> 同样被标："VpnDataPathType is not supported and may be
        ///   altered or unavailable in the future."
        /// 【官方】AppendVpnReceivePacketBuffer(VpnPacketBuffer)：
        ///   "Appends a receive VpnPacketBuffer object to the channel."
        /// 【官方】FlushVpnReceivePacketBuffers()：
        ///   "Flushes any appended receive packet buffers."
        ///
        /// 另有一条官方明确支持 injection 的取包方式 GetVpnReceivePacketBuffer()：
        ///   "Requests a VpnPacketBuffer object from the receive packet buffer pool to be used
        ///    in the decapsulation or **injection** of a received packet from the VPN server
        ///    into the local network stack."
        ///   ⇒ 取包入口已收进 <see cref="TakeReceiveBuffer"/>，1.0.52 起<b>只有这一条</b>
        ///     （旧臂 <c>RequestVpnPacketBuffer(VpnDataPathType)</c> 已删，见那里的注释）。
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
            // ★★ 1.0.26（T4）：旧写法是 <c>Volatile.Read(pending)</c> → Flush → <c>Add(-pending)</c>，
            //   这三步<b>不是原子的</b>。读与销账之间 <c>InjectAppend</c> 还能往里 Increment
            //   （泵线程与 <c>Encapsulate</c> 的 echo 回落线程都会走到这两个方法），
            //   于是 <c>Add(-pending)</c> 会把"这期间刚借出去、还没提交"的 buffer 一起销掉 ——
            //   <c>rxpend</c> 长期偏低。而 <c>rxpend</c> 是我们判断"receive 池有没有被借空"的
            //   <b>唯一</b>一列（run 32 的 send 侧泄漏就是靠它的对称列先现形的）：
            //   数错了比没数更糟，它会把"正在借空"读成"干净"。
            //   ⇒ 现在用 <c>Interlocked.Exchange</c> 一次<b>取走</b>整段 pending（取走后新借的从 0 记），
            //     Flush 失败再把<b>取走的量原样加回去</b>，"失败不算已提交"的口径不变。
            //   ★ 残余误差必须写明：Exchange 之后、Flush 之前新 Append 的 buffer 会被这次 Flush
            //     一起提交掉，但它们记在"在借"里，要等下一次 Flush 才销账 ⇒
            //     <c>rxpend</c> 只会<b>偏高</b>（保守方向），<c>rxflush</c> 可能重复计一次。
            //     这两列都是<b>纯诊断</b>（没有任何分支读它们做决策），所以取保守侧；
            //     判"有没有借空"看的是趋势不绝对值 —— 单调上涨才是病。
            int taken = Interlocked.Exchange(ref s_receiveBuffersPending, 0);
            try
            {
                channel.FlushVpnReceivePacketBuffers();
            }
            catch (Exception ex)
            {
                // 一次失败的 Flush 什么都没提交：把取走的量放回"在借"，
                // 让 rxpend 继续诚实反映借没还的量，而不是被一次失败的 Flush 抹平。
                if (taken > 0)
                    Interlocked.Add(ref s_receiveBuffersPending, taken);
                NoteInjectFailure("flush", ex);
                return;
            }

            if (taken > 0)
                Interlocked.Add(ref s_receiveBuffersFlushed, taken);
        }

        /// <summary>
        /// 从 receive 池取一个 buffer，两条官方入口按开关二选一。
        ///
        /// 【默认（1.0.26 起）】<c>GetVpnReceivePacketBuffer()</c>：
        ///   "Requests a VpnPacketBuffer object from the receive packet buffer pool to be used
        ///    in the decapsulation or <b>injection</b> of a received packet from the VPN server
        ///    into the local network stack" —— 官方<b>点名</b>给 injection 的入口，本品在
        ///   <c>Decapsulate</c> 之外（泵线程）投包，正是这条。先遣项目 docs/76 的
        /// <summary>
        /// 从 <b>receive 池</b>取一个 buffer（回程注入 <c>InjectAppend</c> 用）。
        /// ★ 1.0.52：<b>删掉废弃臂</b>，只留 <c>GetVpnReceivePacketBuffer()</c>。
        ///
        /// 【为什么删 <c>RequestVpnPacketBuffer(VpnDataPathType.Receive, out buf)</c>】
        ///   本地 26100 元数据里 <c>VpnDataPathType</c> 整句是
        ///   "is <b>not supported and may be altered or unavailable in the future</b>"，
        ///   两个字段（Send / Receive）在各自页面上都标着 "<b>Not supported.</b>"。
        ///   ⇒ 这条臂吃的是一个官方声明随时会撤的枚举，删掉它<b>不损失任何能力</b> ——
        ///     <c>GetVpnReceivePacketBuffer()</c> 是同一个 receive 池的<b>具名</b>入口，
        ///     文档在 <c>RequestVpnPacketBuffer</c> 的 see-also 里主动指向它作为等价物。
        ///
        /// 【1.0.26~1.0.51 为什么留着它】那时新臂在本仓库零实测，留旧臂当对照。
        ///   但旧臂本身也是"跑过 18202 包"的那条（run 35/37），两边都验证过 ⇒
        ///   单变量已经跑完，没有理由再为一个官方标了 Not supported 的入口保留代码路径。
        ///
        /// 【失败口径不变】抛异常 ⇒ 原样继续抛，由 <c>InjectAppend</c> 的 catch 计进
        ///   <c>injFail</c>/<c>injErr</c>（stage=<c>request</c>）；返回 null ⇒ 同样抛，
        ///   让计数统一走异常那条。判据仍是 <c>rxpend</c> 不单调上涨、
        ///   <c>rxflush</c> 与 <c>inj</c> 同步、长跑不停摆。
        /// </summary>
        private static VpnPacketBuffer TakeReceiveBuffer(VpnChannel channel)
        {
            VpnPacketBuffer? got = channel.GetVpnReceivePacketBuffer();
            if (got == null)
                throw new InvalidOperationException("GetVpnReceivePacketBuffer() 返回 null");
            return got;
        }

        /// <summary>
        /// 从 <b>send 池</b>取一个 buffer（目前只有 <c>GetKeepAlivePayload</c> 用）。
        /// ★ 1.0.52：<b>删掉废弃臂</b>，只留 <c>GetVpnSendPacketBuffer()</c>。
        ///
        /// 【为什么删 <c>RequestVpnPacketBuffer(VpnDataPathType.Send, out buf)</c>】
        ///   本地 26100 元数据里 <c>VpnDataPathType</c> 整句是
        ///   "is <b>not supported and may be altered or unavailable in the future</b>"，
        ///   两个字段（Send / Receive）都标着 "<b>Not supported.</b>"。
        ///   ⇒ 与 receive 侧同一条理由：这条臂吃的是一个官方声明随时会撤的枚举，
        ///     而 <c>GetVpnSendPacketBuffer()</c> 是同一个 send 池的<b>具名</b>入口，
        ///     文档在 <c>RequestVpnPacketBuffer</c> 的 see-also 里主动指向它作为等价物。
        ///
        /// 【1.0.27~1.0.51 为什么默认留在旧臂】那时新臂<b>两个仓库都零实测</b>
        ///   （先遣项目只有 C# 兜底分支的静态存在，从未触发），投影层会不会抛也无记录。
        ///   单变量（<c>sendGetBuffer</c>）跑了四轮没有采集到样本：keep-alive 几十秒
        ///   才一次，且 <c>ka</c> 列长期不动 ⇒ <b>无法证明新臂可用</b>。
        ///   ★ 但"无法证明可用"不构成"继续用一个官方标了 Not supported 的入口"的理由：
        ///     两条入口的失败形态完全相同（抛 / 返回 null），都由同一个 catch 兜住，
        ///     切换不引入新的失败路径。所以 1.0.52 直接删旧臂、改用具名入口。
        ///
        /// 【失败口径】两种失败都走同一条出口：
        ///   · 抛异常 ⇒ <b>原样继续抛</b>，由 <c>GetKeepAlivePayload</c> 既有的 catch 兜住
        ///     （它已经会把 <c>keepAlivePacket</c> 置 null 并记日志）；
        ///   · 返回 null ⇒ 计数后返回 null，调用方照常交 null。
        ///   两种都进 <c>takeFail</c>/<c>takeErr</c> ⇒ 判"具名入口行不行"看这两列，不是猜。
        /// ★ 借到的 buffer 的<b>归还义务在调用方</b>：交给出参 = 交回平台；
        ///   没交出去（null / 异常）就必须走 orphan 归还包括 Append+Flush，见
        ///   <c>GetKeepAlivePayload</c> 末尾那段（Docs/09 A2）。
        /// </summary>
        private static VpnPacketBuffer? TakeSendBuffer(VpnChannel channel)
        {
            try
            {
                VpnPacketBuffer? got = channel.GetVpnSendPacketBuffer();
                if (got == null)
                {
                    NoteTakeSendFailure("GetVpnSendPacketBuffer() 返回 null");
                    return null;
                }
                return got;
            }
            catch (Exception ex)
            {
                NoteTakeSendFailure($"{ex.GetType().Name}:{ex.Message}");
                throw;
            }
        }

        /// <summary>send 池取包失败计数 + 首个原因（不写日志：keep-alive 节奏慢，
        /// 原因随 <c>Disconnect</c> 快照与心跳的 <c>takeFail/takeErr</c> 两列出来）。
        /// 静态口径：跨会话累计，与 <see cref="NoteInjectFailure"/> 一致。</summary>
        private static void NoteTakeSendFailure(string reason)
        {
            Interlocked.Increment(ref s_takeSendFailures);
            if (s_takeSendFirstError.Length == 0)
                s_takeSendFirstError = reason.Replace('\r', ' ').Replace('\n', ' ');
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
            //   同一条默认路由抓回来（自环），TCP 直接超时。节点 IP 有宿主路由排除，
            //   而"所有国内 IP"是不可能穷举排除的集合 —— 只能反过来收窄 include。
            //   fake-ip 形态下要进隧道的本来就只有两类目的地：
            //     ① 假 IP 池 <see cref="KernelConfig.FakeIpRoute"/>（域名流量的唯一形态）
            //     ② 下发给隧道的 DNS 服务器 /32（不拉进来，解析就回直连 DNS，fake-ip 白做）
            //   其余（国内真 IP、局域网、联网探测）留在物理网卡上，DIRECT 才是真的 DIRECT。
            //   默认仍是 wide（0.0.0.0/0）：28-A 的头条判据是在 wide 下量到的，
            //   narrow 必须先做单变量 A/B，双向可切才允许换成默认。
            //
            // ★【1.0.119 · G7】四个桶（v4/v6 × include/exclude）怎么分配、什么被剔掉，
            //   整段判断搬到 <see cref="RouteInclusion"/> 并进了离线单测。搬走的直接原因是
            //   两条族别错配：<b>v6 节点服务器的排除路由此前整条缺失</b>（旧循环只喂
            //   <c>Ipv4ExclusionRoutes</c>，并把带冒号的地址一律跳过；而
            //   <c>NodeRoutes</c> 又给 v6 拼了个 v4 的 <c>/32</c>）——
            //   <c>ipv6=1</c> 下发 <c>2000::/3</c> 之后，拨向 v6 节点的外层包正好落进
            //   自家 include，没有任何排除路由兜它，形态就是自环黑洞。
            //   另一条：profile 里若带 v6 排除段，旧循环会把它塞进 <b>v4</b> 桶。
            //
            //   ★ 节点排除路由这条防线本身仍带着 2026-10-16 run 14 的旧伤：那批 /32 被平台
            //     装到了 **Loopback Pseudo-Interface 1（ifIdx=1）**而不是物理网卡，于是拨号包
            //     进环回（Docs/08 的定论：平台按"这个 transport socket 会走哪条路"决定
            //     排除路由装在哪个接口）。所以 <c>nodeExclusions=0</c> 这个单变量开关保留，
            //     摘掉时 v6 桶也一起空 —— 本次改动只纠正<b>族别与前缀长度</b>，不改这条开关语义。
            RouteInclusion.Plan plan = RouteInclusion.Build(new RouteInclusion.Input(
                _config.IncludeRoutes,
                NarrowInclusionRoutes(),
                LocalSettings.IncludeMode.Equals("narrow", StringComparison.OrdinalIgnoreCase),
                LocalSettings.Ipv6,
                _ipv6PoolEffective,
                _config.ExcludeRoutes,
                _nodeExclusions,
                LocalSettings.NodeExclusions));
            _routePlanNote = plan.Describe();

            foreach (string cidr in plan.Ipv4Includes)
                if (RouteInclusion.SplitCidr(cidr, out string? a, out int p, out _))
                    assignment.Ipv4InclusionRoutes.Add(new VpnRoute(new HostName(a!), (byte)p));
            foreach (string cidr in plan.Ipv6Includes)
                if (RouteInclusion.SplitCidr(cidr, out string? a, out int p, out _))
                    assignment.Ipv6InclusionRoutes.Add(new VpnRoute(new HostName(a!), (byte)p));
            foreach (string cidr in plan.Ipv4Excludes)
                if (RouteInclusion.SplitCidr(cidr, out string? a, out int p, out _))
                    assignment.Ipv4ExclusionRoutes.Add(new VpnRoute(new HostName(a!), (byte)p));
            foreach (string cidr in plan.Ipv6Excludes)
                if (RouteInclusion.SplitCidr(cidr, out string? a, out int p, out _))
                    assignment.Ipv6ExclusionRoutes.Add(new VpnRoute(new HostName(a!), (byte)p));

            // 被剔掉的项必须出声：这些是"我看了但没照做"的决定，装完机只靠快照反推最费时间。
            foreach (string d in plan.Dropped)
                RollingLog.Warn("routes", d);
            RollingLog.Info("routes",
                $"arm={LocalSettings.Ipv6} nodeExcl开关={(LocalSettings.NodeExclusions ? 1 : 0)} {_routePlanNote} " +
                $"剔除={plan.Dropped.Count}");

            // 防自环的第二道防线仍在桥侧（dialer.DefaultInterface → Windows IP_UNICAST_IF
            //   强制绑定物理网卡，等价于用户方案的「StreamSocket 绑网卡」）。
            //   两道叠着：路由层保证"即便绑错网卡也不会被回环的节点 IP"，绑定层兜住
            //   "缓存还没覆盖到的新节点 IP"。
            //   C# 侧无需再加网关排除：NetworkAdapter 没有网关属性（已查证，
            //   只有 NetworkAdapterId/带宽/IanaInterfaceType），拿不到网关就别猜。

            return assignment;
        }

        /// <summary>
        /// ★【1.0.70 · G7】v6 进隧道路由清单 —— 判据本体在 <see cref="RouteInclusion.Ipv6IncludeRoutes"/>
        /// （离线单测钉住三档形状与"绝不出现 <c>::/0</c>"）。这里只是把快照/诊断那几行接回同一份口径，
        /// 保证<b>日志说的</b>和 <c>BuildRoutes</c><b>装出去的</b>是同一张表。
        ///
        /// 【为什么是 2000::/3 而不是 ::/0】先遣项目 docs/96 的四臂实测：
        ///   <c>::/0</c> 一旦下发，IPv6 就成为<b>真正的默认路由</b>，连 IPv4 的出站选择
        ///   都被它抢走（4/4 臂把 VPN 变成 v4 默认路由）；<c>2000::/3</c> 是全局单播的
        ///   覆盖段，能兜住全部公网 v6 地址而不碰 ::/0 的语义，3/3 臂通过。
        /// </summary>
        private List<string> Ipv6InclusionRoutes() =>
            RouteInclusion.Ipv6IncludeRoutes(LocalSettings.Ipv6, _ipv6PoolEffective);

        /// <summary>
        /// 窄 include 的目的地清单（配合 <see cref="BuildRoutes"/> 里的说明）：
        /// fake-ip 池 + 下发进隧道的 DNS 服务器 /32。
        /// DNS 那几条只在 <c>dnsInTunnel=1</c> 时才加 —— 不然隧道里没有 DNS 可去，
        /// 加了反而平白多抓两段直连 DNS 流量。
        /// </summary>
        private List<string> NarrowInclusionRoutes()
        {
            // ★【1.0.40】池子可被 dns-config.txt: fake-ip-range 改，路由必须跟着走。
            //   只改 YAML 不改这里 = 新池的域名流量根本不进隧道（比黑洞更糟的"以为在代理其实直连"）。
            //   空配置时 FakeIpRoute 就是原来的 198.18.0.0/16，逐字不变。
            var list = new List<string> { _dnsConfig.FakeIpRoute };
            if (LocalSettings.DnsIntoTunnel)
                foreach (string dns in EffectiveNrptDnsServers())
                    if (dns.IndexOf(':') < 0)          // v6 DNS 先不掺进来，v4 路由表只管 v4
                        list.Add($"{dns}/32");
            return list;
        }

        /// <summary>
        /// ★【1.0.40 · DNS 可配】本次要交给 Windows NRPT 的 DNS 服务器：<br/>
        ///   <c>dns-config.txt: nrpt-dns</c> 优先（逗号分隔、已逐条校验为纯 IP），
        ///   否则用 profile 的 <c>&lt;dns&gt;</c> 清单（<see cref="_config"/>.DnsServers）。
        /// ★ 这三个读者必须是同一份结果：<see cref="BuildDomainNames"/>（真正下发）、
        ///   步 5 的诊断行、<see cref="SessionWatchdog"/> 的隧道内探针目标。
        ///   分开算会出现"看门狗探旧 DNS、系统解析走新 DNS"的假故障。
        /// </summary>
        private IList<string> EffectiveNrptDnsServers()
        {
            if (_dnsConfig.Has("nrpt-dns"))
                return DnsConfig.SplitList(_dnsConfig.Get("nrpt-dns"));
            return _config.DnsServers;
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
                // ★【1.0.40】nrpt-dns 覆盖（没有就还是 profile 的 <dns> 清单）。
                foreach (string dns in EffectiveNrptDnsServers())
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
    }
}
