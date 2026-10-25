using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

using Windows.Networking.Connectivity;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// ★【G6 · 跟随网络变化】插件进程内的<b>物理网络哨兵</b>：
    /// 订阅 <c>NetworkInformation</c> 的网络事件，比对"物理网络指纹"，
    /// 变了就把内核的<b>出站网卡绑定</b>重新做一次 —— 而不是只让它在连接那一刻绑一次。
    ///
    /// ══ 这条缺口原来是什么形状（Docs/12 §2 G6、§3）══
    ///   安卓 <c>NetworkObserveModule.kt:63-69</c> 监听 <c>onLinkPropertiesChanged</c>，
    ///   把新链路的 DNS 推给内核（<c>:119-127 notifyDnsChanged</c>），退出时清空（<c>:147-152</c>），
    ///   还按 wifi&gt;eth&gt;… 的优先级挑 DNS（<c>:100-117</c>）。
    ///   我们此前 <c>NetworkInformation</c> 出现 <b>0 次</b>：换网络这件事在插件里完全没有感知。
    ///   而"看起来没坏、其实是运气"的原因就在这儿 —— 见下面两节。
    ///
    /// ══ 为什么换网络会真的坏（不是假想的风险）══
    ///   桥的防自环靠把 mihomo 的<b>所有出站拨号绑到物理网卡</b>：
    ///   <c>Tools/mihomo-bridge/bridge/core.go:111-120 rebindInterface()</c> →
    ///   <c>dialer.DefaultInterface.Store(name)</c>（Windows 实现是
    ///   <c>component/dialer/bind_windows.go</c> 的 <c>IP_UNICAST_IF</c>）。
    ///   ★ 关键点：绑的是<b>网卡名</b>，不是索引，也不是"当前默认路由"。
    ///   Wi-Fi 切有线、路由器重连、热点断开 ⇒ 那个名字对应的接口可能已经消失或换了归属，
    ///   而 <c>rebindInterface()</c> 只在 <c>Bridge.Start</c> 里跑
    ///   （<c>core.go:153</c> 首次、<c>core.go:135</c> 走"已启动"分支时刷新一次）。
    ///   ⇒ 会话中途换网络，绑定就是<b>陈旧的</b>：内核拨号包带着一个不存在的接口绑定
    ///   发出去，最坏形态是 <c>connectex: unreachable host</c>（run 13 内核原文同一条），
    ///   节点连不上、隧道照装着 = 黑洞。
    ///   第二条更隐蔽：隧道期间任何一次<b>热重载</b>（<c>PUT /configs</c>）都会走
    ///   <c>hub/executor/executor.go:412</c> 的
    ///   <c>dialer.DefaultInterface.Store(general.Interface)</c> —— 无条件覆盖，
    ///   而我们生成的配置里<b>没有</b> <c>interface-name</c>（值是空串）
    ///   ⇒ 每次热重载都把绑定<b>清空</b>，等于把上面那条防线整条撤掉。
    ///   这条在 <see cref="ClashVpnPlugin.RestoreInterfaceBinding"/> 里补：重载成功后
    ///   用 <c>PATCH /configs {"interface-name": …}</c> 把桥侧记录的名字原样写回去
    ///   （<c>hub/route/configs.go:366-367</c> 确认这个键就是同一个 <c>Store</c>），
    ///   不需要改 Go、也不需要重编 DLL。
    ///
    /// ══ 为什么"动作臂"默认不开 ══
    ///   两件事都没在这台机器上验过，而它们各自决定动作臂的成败：
    ///   ① <c>NetworkInformation.NetworkStatusChanged</c> 这个事件在 <b>vpnClient 后台任务进程</b>里
    ///     到底收不收得到（Docs/12 §6 只证明了 winmd 里有这个事件 = 平台侧 API 存在，
    ///     明确写着"文档级结论，未实测"；AppContainer 里能枚举到什么也是同一类未知）；
    ///   ② 动作臂要靠<b>再调一次</b> <c>clash_bridge_start</c> 触发 Go 侧的
    ///     <c>rebindInterface()</c>（<c>core.go:132-137</c> 的"已启动"分支正是为此存在），
    ///     而"同进程第二次 Start"在本项目是有前科的病态路径
    ///     （Docs/05 §2④：桥常驻就是为了绕开它）。
    ///   ⇒ 口径与 <see cref="LocalSettings.EncapReturn"/> 的档 5、
    ///     <see cref="LocalSettings.ReceiveBatch"/> 的档 0、
    ///     <see cref="LocalSettings.SessionReset"/> 的档 1/2 一致：
    ///     <b>代码全写完，默认停在"只记日志不动手"那一档</b>，
    ///     等实测把①②两条读回来再抬默认。
    ///     档 1 的代价只是订阅事件 + 读几个属性 + 写一行日志，不碰平台状态机、不碰桥。
    ///
    /// ══ 三档 ══
    ///   0 = 完全不订阅（本批改动之前逐字一致）。
    ///   1 = ★ 默认。订阅 + 比对 + 记日志（<c>channel=net-follow</c>），并作废节点路由缓存
    ///       （<see cref="NodeRoutes.Invalidate"/>：换网后旧的"节点域名→IP"解析结果不可信，
    ///       而下一次排除路由必须在<b>隧道外</b>重新解析）；<b>不动桥、不发 HTTP</b>。
    ///   2 = 在 1 之上，判据说"绑定坏了"就请求桥重绑（超时 + 门闩 + 每会话上限，见
    ///       <see cref="ClashVpnPlugin.RequestBridgeRebind"/>），并把 <c>bound_iface</c>
    ///       的变化作为证据记下来。
    ///
    /// ══ ★【1.0.119 · 修 Docs/32 §四 P3】判据原先看不见"链路状态"══
    ///   旧指纹 = WinRT 的 profile 枚举 + 主机名列表。26100 的 winmd 里
    ///   <c>NetworkAdapter</c> 只有 IanaInterfaceType / NetworkAdapterId / NetworkItem /
    ///   收发速率五个成员（Docs/21 §四 反编译实录，本批重读一遍仍然如此）——
    ///   <b>没有名字、也没有连接状态</b>。于是常驻桥复用那一次：
    ///   <c>bound_iface=Meta</c>（别的程序留下的 wintun，已经 Disconnected）而出口早回到 WLAN，
    ///   哨兵却报 <c>指纹=未变</c>（Docs/32 实录；杀进程重建桥后立刻 <c>bound_iface=WLAN</c>）。
    ///   现在补两件事：
    ///   ① 指纹带上系统适配器表（<see cref="AdapterHealth.Format"/>：名字 + 状态 + 有无 IPv4），
    ///     "这张卡掉了/换了张卡"必须改指纹；
    ///   ② 武装与每次评估都对<b>桥实际绑着的那张卡</b>做一次体检
    ///     （<see cref="AdapterHealth.Evaluate"/>），绑定的卡断了/没有 IPv4/根本没绑
    ///     ⇒ 即使其余枚举一模一样也算"该动手"，并且<b>武装那一刻就判</b> ——
    ///     常驻桥复用不吃 <c>NetworkStatusChanged</c> 事件，等事件来等于永远不重绑。
    ///   "名字在表里找不到"（可能只是命名口径不一致）与"绑在隧道设备上"两条
    ///   <b>只报不动手</b>，理由写在 <see cref="AdapterHealth"/> 类头。
    ///
    /// ══ 为什么不做成安卓那样"把系统 DNS 推给内核" ══
    ///   Windows 侧没有 <c>notifyDnsChanged</c> 这种入口（Docs/12 §6 实测 winmd：<c>NotifyDnsChanged=0</c>），
    ///   而且这台机器的 <c>NetworkAdapter</c> 连枚举 DNS 的成员都没有（没有 <c>GetDnsServers</c>，
    ///   见 <see cref="Fingerprint"/> 的注释）⇒ "换网后系统 DNS 变了"在插件进程里<b>读不到</b>，
    ///   本类不把它写进判据、也不假装有。
    ///   我们的形态本来也不依赖物理 DNS：解析权在<b>隧道里</b>（NRPT + <c>tun.dns-hijack: [any:53]</c> +
    ///   fake-ip），物理网络的 DNS 变了不影响 fake-ip 应答。
    ///   用户要改 DNS 走 <see cref="DnsConfig"/>（<c>dns-config.txt</c>），那是"可配"，不是"跟随"。
    /// </summary>
    internal static class NetworkFollow
    {
        /// <summary>事件合并窗口：一次换网会连珠炮地来好几条，5 秒内只评估一次。</summary>
        private const int DebounceMs = 5000;

        /// <summary>两次<b>动作</b>之间的最小间隔（比评估窗口长，防止网络抖动时反复重绑）。</summary>
        private const int MinActionIntervalMs = 30000;

        /// <summary>每次会话最多动几次手。换网不是高频事件，5 次足够，超出说明判据在抖。</summary>
        private const int MaxActionsPerSession = 5;

        /// <summary>本类最近一次结论，进 <c>dataface-plugin.txt</c> 快照（口径同 <see cref="SessionReset"/>）。</summary>
        internal static string LastNote = "未启动";

        /// <summary>连接时取的物理网络指纹（比对基线）。</summary>
        private static string _baseline = "";

        /// <summary>连接时桥侧记录的绑定网卡名（<c>clash_bridge_bound_iface</c>）。</summary>
        private static string _baselineBoundIface = "";

        private static long _lastEvaluatedTick;
        private static long _lastActionTick;
        private static int _actionCount;
        private static int _eventCount;
        private static volatile bool _subscribed;

        // ── 生命周期 ────────────────────────────────────────────────────────

        /// <summary>
        /// 武装（<c>Connect</c> 步 8.6，桥与两条看门狗都装好之后）。
        /// 可重复调用：先退订再订阅，"重新武装"= 基线重取 + 计数归零。
        /// </summary>
        /// <param name="boundIfaceAtConnect">桥此刻绑定的物理网卡名（空 = 没绑上，本身就是要记的证据）。</param>
        internal static void Start(string boundIfaceAtConnect)
        {
            Stop("重新武装");

            int arm = LocalSettings.NetFollow;
            _baselineBoundIface = boundIfaceAtConnect ?? "";
            Interlocked.Exchange(ref _eventCount, 0);
            Interlocked.Exchange(ref _actionCount, 0);
            _lastEvaluatedTick = 0;
            _lastActionTick = 0;

            if (arm <= 0)
            {
                LastNote = "关闭（netFollow=0）";
                return;
            }

            // ★【1.0.119 · P3】绑定体检要在"订阅成功"之前算好：常驻桥复用的会话里，
            //   事件可能一次都不来，等事件等于永远不重绑。
            var boundTable = ReadAdapterTable();
            _baseline = Fingerprint(boundTable);
            var armVerdict = AdapterHealth.Evaluate(_baselineBoundIface, boundTable);

            try
            {
                // ★ 只订阅 <c>NetworkStatusChanged</c> —— 这不是取舍，是这台机器上的 winmd
                //   就只给了这一个：10.0.26100 的 UniversalApiContract v19 里
                //   <c>Windows.Networking.Connectivity.NetworkInformation</c> 的事件表<b>只有</b>
                //   <c>NetworkStatusChanged</c>（用 ilspycmd 反编译 winmd 实录，19.0.0.0）。
                //   上一稿按记忆写的 <c>NetworkAdded/NetworkRemoved/NetworkChanged</c> 与
                //   <c>NetworkChangedEventArgs</c> 在这里<b>不存在</b>，编译期 CS0246 当场否证。
                //   ⇒ 事件粒度就是"网络状态变了"；"哪张卡变了"由 <see cref="Fingerprint"/>
                //     的前后值回答（它带 adapter id/type/addr/dns），不需要另一个事件。
                NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
                _subscribed = true;
            }
            catch (Exception ex)
            {
                _subscribed = false;
                LastNote = $"订阅失败={ex.GetType().Name}";
                RollingLog.Error("net-follow", LastNote + " :: " + ex.Message, ex);
                return;
            }

            string action = "";
            if (AdapterHealth.ShouldRebind(armVerdict))
                action = arm >= 2
                    ? TryAct($"武装时体检:{AdapterHealth.Reason(armVerdict)}")
                    : " 动作=跳过（netFollow=1 只观测）";

            LastNote = $"arm={arm} 已订阅 events=0 bound={DescribeBound(_baselineBoundIface)} " +
                       $"体检={AdapterHealth.Reason(armVerdict)}{action}";
            RollingLog.Info("net-follow",
                $"武装 arm={arm} 事件=NetworkStatusChanged " +
                $"bound_iface={DescribeBound(_baselineBoundIface)} " +
                $"体检={AdapterHealth.Reason(armVerdict)}{action} " +
                $"适配器={AdapterHealth.Format(boundTable)} 基线={_baseline}");
        }

        /// <summary>绑定名的可读形态（空串要说成"(空)"，否则日志里像漏了一格）。</summary>
        private static string DescribeBound(string bound) =>
            bound.Length == 0 ? "(空)" : bound;

        /// <summary>撤除（会话结束时调；退订比订阅更要保证执行，否则事件会挂到下一个会话上）。</summary>
        internal static void Stop(string why)
        {
            if (!_subscribed)
                return;
            _subscribed = false;
            try
            {
                NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
            }
            catch (Exception ex)
            {
                RollingLog.Warn("net-follow", $"退订异常={ex.GetType().Name}（{why}）");
            }
            RollingLog.Info("net-follow",
                $"停止（{why}）events={System.Threading.Volatile.Read(ref _eventCount)} " +
                $"actions={System.Threading.Volatile.Read(ref _actionCount)} :: {LastNote}");
        }

        // ── 事件入口（唯一可用的静态事件；判定汇到 Evaluate）──────────────────

        private static void OnNetworkStatusChanged(object sender) =>
            Evaluate("NetworkStatusChanged", "");

        // ── 判定 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 事件来了：合并窗口内直接跳过，否则重取指纹与适配器表、与基线比、按需动手。
        /// ★ 本方法跑在<b>平台投递事件的线程</b>上，里面只允许读属性和写日志，
        ///   绝不允许长时间阻塞（重绑动作是 fire-and-forget 到别的线程，见
        ///   <see cref="ClashVpnPlugin.RequestBridgeRebind"/>）。
        /// ★【1.0.119 · P3】"算不算变了"现在是<b>两条</b>：指纹变 / 绑定的卡坏了。
        ///   只有前者会漏掉常驻桥那种"profile 一条没动、绑定的卡已经断了"的形态 ——
        ///   那正是 Docs/32 P3 实录的 <c>指纹=未变 bound=Meta</c>。
        /// </summary>
        private static void Evaluate(string eventName, string detail)
        {
            Interlocked.Increment(ref _eventCount);
            try
            {
                long now = Environment.TickCount64;
                long last = System.Threading.Volatile.Read(ref _lastEvaluatedTick);
                if (now - last < DebounceMs)
                {
                    RollingLog.Info("net-follow",
                        $"{eventName}({detail}) 合并跳过（距上次评估 {now - last}ms < {DebounceMs}ms）");
                    return;
                }
                Interlocked.Exchange(ref _lastEvaluatedTick, now);

                var table = ReadAdapterTable();
                string now1 = Fingerprint(table);
                bool changed = now1 != _baseline;
                string bound = ReadBoundIface();
                var verdict = AdapterHealth.Evaluate(bound, table);
                bool stale = AdapterHealth.ShouldRebind(verdict);

                var note = new StringBuilder();
                note.Append($"evt={eventName}");
                if (detail.Length > 0) note.Append($" detail={detail}");
                note.Append(changed ? " 指纹=变了" : " 指纹=未变");
                // ★ 体检单独一条，不参与"未变"的结论：不健康时必须看得见（P3 那次就是
                //   "未变"两个字把假信号盖住了）。
                note.Append($" 体检={AdapterHealth.Reason(verdict)}");
                note.Append($" bound={(_baselineBoundIface == bound ? bound : $"{_baselineBoundIface}→{bound}")}");

                if (changed || stale)
                {
                    if (changed) note.Append($" 新指纹={now1}");
                    // ★ 换网 ⇒ 旧的"节点域名→IP"缓存不可信（它是在<b>上一个网络</b>里解析的）。
                    //   这里只作废 TTL，不立刻重解析：重解析必须在隧道外做才可信
                    //   （NodeRoutes 类注释的口径），而会话进行中解析走的是隧道。
                    //   绑定坏了但指纹没变的那种（常驻桥复用）也一并作废：那张卡已经不在了，
                    //   在它上面解析出来的节点地址同样不可信。
                    NodeRoutes.Invalidate(stale && !changed
                        ? "绑定体检:" + eventName : "网络变化:" + eventName);
                    note.Append(" nodeCache=作废");
                }

                LastNote = note.ToString();
                RollingLog.Info("net-follow", LastNote);
                ClashVpnPlugin.LogSession($"evt=net-follow {eventName} changed={(changed ? 1 : 0)} " +
                                          $"stale={(stale ? 1 : 0)} bound={_baselineBoundIface}→{bound}");

                if (!changed && !stale)
                    return;

                // 基线推进：不推进的话同一次换网剩下的连珠炮事件会反复判成"变了"。
                _baseline = now1;
                _baselineBoundIface = bound;

                if (LocalSettings.NetFollow < 2)
                {
                    note.Append(" 动作=跳过（netFollow=1 只观测）");
                    LastNote = note.ToString();
                    return;
                }

                string act = TryAct($"{eventName}:{(stale ? AdapterHealth.Reason(verdict) : "指纹变化")}");
                LastNote = note.ToString() + act;
                RollingLog.Info("net-follow", LastNote);
            }
            catch (Exception ex)
            {
                // ★ 这条 catch 是本类存在的理由之一：事件处理器里抛出去，
                //   平台侧的行为我们没有任何文档可依，绝不能拿数据面去试。
                LastNote = $"{eventName} 评估异常={ex.GetType().Name}";
                RollingLog.Warn("net-follow", LastNote + " :: " + ex.Message);
            }
        }

        /// <summary>
        /// 动手（档 2）的唯一入口：限频 + 每会话上限 + 提交给桥。
        /// 从 <see cref="Evaluate"/> 里抽出来是为了让"武装那一刻的体检"走<b>同一副闸</b> ——
        /// 两处各写一遍就会有一处漏掉上限（这条动作碰的是有前科的第二次 <c>clash_bridge_start</c>）。
        /// </summary>
        /// <returns>写进 <see cref="LastNote"/> 的短句（含跳过原因）。</returns>
        private static string TryAct(string reason)
        {
            long now = Environment.TickCount64;
            long lastAct = System.Threading.Volatile.Read(ref _lastActionTick);
            if (now - lastAct < MinActionIntervalMs)
                return $" 动作=限频（距上次 {now - lastAct}ms）";
            if (System.Threading.Interlocked.Increment(ref _actionCount) > MaxActionsPerSession)
                return $" 动作=超上限 {MaxActionsPerSession} 次/会话，不再动手";

            System.Threading.Interlocked.Exchange(ref _lastActionTick, now);
            return " 动作=" + ClashVpnPlugin.RequestBridgeRebind($"net-follow:{reason}");
        }

        /// <summary>读桥侧记录的绑定网卡名；读不到（桥没起/异常）一律空串，不抛。</summary>
        private static string ReadBoundIface()
        {
            try { return ClashVpnPlugin.BoundIfaceNow(); }
            catch { return ""; }
        }

        /// <summary>
        /// 采集系统适配器表（<see cref="AdapterHealth"/> 的判据输入）。
        /// 【为什么走 <c>System.Net.NetworkInformation</c> 而不是 WinRT】26100 的
        ///   <c>NetworkAdapter</c> 里没有名字也没有连接状态（类头那条 winmd 实录），
        ///   而"桥绑的那张卡还在不在"必须靠名字对上 —— Go 侧记的就是 Windows 的接口友好名，
        ///   .NET 的 <c>NetworkInterface.Name</c> 同源。<c>GetVpnDeviceInformation</c>
        ///   这条路在本机 winmd 里<b>根本不存在</b>（本批 ilspycmd 试读当场否证），没用它。
        /// 【失败形态】整表采集失败返回空表 ⇒ 体检判 <see cref="AdapterHealth.Bound.Missing"/>
        ///   （只报不动手）；单张卡的地址读不到只让 <c>HasIpv4</c> 为 false。
        /// 【成本】<c>GetIPProperties</c> 每卡一次；调用点是连接时与事件（5 秒合并窗口），
        ///   不在每包热路径上。
        /// </summary>
        private static List<AdapterHealth.Adapter> ReadAdapterTable()
        {
            var res = new List<AdapterHealth.Adapter>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    bool v4 = false;
                    try
                    {
                        foreach (UnicastIPAddressInformation u in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (u.Address.AddressFamily == AddressFamily.InterNetwork &&
                                !IPAddress.IsLoopback(u.Address)) { v4 = true; break; }
                        }
                    }
                    catch { /* 单张卡的地址读不到，不影响它的状态 */ }

                    res.Add(new AdapterHealth.Adapter(
                        ni.Name, ni.Description,
                        ni.OperationalStatus.ToString(), ni.NetworkInterfaceType.ToString(), v4));
                }
            }
            catch (Exception ex)
            {
                RollingLog.Warn("net-follow",
                    $"适配器表采集失败={ex.GetType().Name}（绑定体检按『找不到』处理，不动手）");
            }
            return res;
        }

        /// <summary>
        /// ★【1.0.119 · G7】本机 v6 面貌：<b>有没有</b>会绕过隧道的全局单播 IPv6 地址。
        /// 【为什么单独一条而不并进 <see cref="ReadAdapterTable"/>】那张表喂的是"绑定网卡
        ///   还活着吗"（逐卡一行，给 <see cref="AdapterHealth"/>），这条要的是"全网有没有
        ///   一个能出公网的 v6 地址"（跨卡汇总，给 <see cref="RouteInclusion.LeakNote"/>）。
        ///   两种形状各有各的消费者，硬塞进同一条记录会让两边都难读。
        /// 【失败形态】枚举抛 = <see cref="RouteInclusion.V6Face.Unknown"/>，
        ///   调用方必须把它和"确实没有 v6"分开写：在 AppContainer 里能不能读到地址<b>未实测</b>，
        ///   读不到不等于安全（口径同 Docs/12 §6"能力边界要可见"）。
        /// 【成本】连接时一次（<c>Connect</c> 步 6 前），不在每包热路径、也不在事件线程上反复跑
        ///   —— 事件那条路要的是"绑定坏没坏"，已经由 <see cref="ReadAdapterTable"/> 覆盖。
        /// </summary>
        internal static RouteInclusion.V6Face InspectLocalV6Face(out string detail)
        {
            var v6 = new List<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        foreach (UnicastIPAddressInformation u in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (u.Address.AddressFamily == AddressFamily.InterNetworkV6)
                                v6.Add(u.Address.ToString());
                        }
                    }
                    catch { /* 单张卡读不到不影响其它卡 */ }
                }
            }
            catch (Exception ex)
            {
                detail = $"采集失败={ex.GetType().Name}";
                return RouteInclusion.V6Face.Unknown;
            }

            RouteInclusion.V6Face face = RouteInclusion.ClassifyV6Face(v6, collectFailed: false);
            // collectFailed=false 时 ClassifyV6Face 只可能回 Global/None（采集抛异常在上面
            // 一行就已经返回 Unknown 了），所以这一支不需要为 Unknown 留第三种文案。
            detail = face switch
            {
                RouteInclusion.V6Face.Global => "有全局 v6 " + FirstGlobals(v6),
                _ => $"读到 {v6.Count} 个 v6 地址，全部非全局",
            };
            return face;
        }

        /// <summary>前两个全局单播地址（短串，进日志用；不刷长文本）。</summary>
        private static string FirstGlobals(List<string> v6)
        {
            var picked = new List<string>();
            foreach (string a in v6)
            {
                if (!RouteInclusion.IsGlobalUnicastV6(a))
                    continue;
                picked.Add(a);
                if (picked.Count >= 2)
                    break;
            }
            return "[" + string.Join(",", picked) + "]";
        }

        /// <summary>
        /// 物理网络指纹：把"当前有几条链路、各自的身份/类型/IPv4"加上
        /// <b>系统适配器表的链路状态</b>压成一行可比较的串。
        /// 每一项单独 try —— 在 AppContainer 里哪个属性读得到是<b>未实测</b>的，
        /// 读不到就在指纹里留 <c>?</c> 而不是整条失败（口径同 Docs/12 §6 的"能力边界要可见"）。
        /// ★【1.0.119 · P3】<c>sys=</c> 那一段（<see cref="AdapterHealth.Format"/>）是本批补的：
        ///   WinRT 侧的 profile 枚举不反映"这张卡断了/换成了另一张"，常驻桥复用那次的
        ///   <c>指纹=未变</c> 假信号就是从这儿来的。
        /// ★ 指纹里<b>没有 DNS</b>：这台机器的 winmd（UniversalApiContract v19）里
        ///   <c>NetworkAdapter</c> 只有 IanaInterfaceType / NetworkAdapterId / NetworkItem /
        ///   Inbound/OutboundMaxBitsPerSecond / GetConnectedProfileAsync 五个成员，
        ///   <b>没有 GetDnsServers、也没有 IsConnected</b>（ilspycmd 反编译实录）。
        ///   所以"换网后 DNS 变没变"在这里<b>观测不到</b>，不写进判据、也不假装有。
        /// </summary>
        internal static string Fingerprint(IReadOnlyList<AdapterHealth.Adapter> table)
        {
            var parts = new List<string>();
            string connectivity = "?";
            string internet = "?";

            try
            {
                NetworkConnectivityLevel lvl = NetworkInformation.GetInternetConnectionProfile()?
                    .GetNetworkConnectivityLevel() ?? NetworkConnectivityLevel.None;
                connectivity = lvl.ToString();
            }
            catch (Exception ex) { connectivity = "?" + ex.GetType().Name; }

            try
            {
                ConnectionProfile? p = NetworkInformation.GetInternetConnectionProfile();
                // ★ ConnectionProfile 没有 NetworkItem 属性（同样以 winmd 为准）；
                //   Internet 那条链路的身份 = ProfileName + 它挂的 NetworkAdapterId。
                if (p != null) internet = $"{p.ProfileName}[{SafeAdapterId(p.NetworkAdapter)}]";
            }
            catch (Exception ex) { internet = "?" + ex.GetType().Name; }

            try
            {
                foreach (ConnectionProfile p in NetworkInformation.GetConnectionProfiles())
                {
                    string id = SafeAdapter(p.NetworkAdapter);
                    if (id.Length > 0) parts.Add(id);
                }
            }
            catch (Exception ex) { parts.Add("profiles?" + ex.GetType().Name); }

            parts.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            sb.Append($"lvl={connectivity} internet={internet} n={parts.Count}");
            foreach (string s in parts) sb.Append(' ').Append(s);

            // ★ 链路状态（名字:状态+有无 IPv4）—— P3 那条假信号的解药就在这儿。
            sb.Append(' ').Append(AdapterHealth.Format(table));

            // 地址：AppContainer 里最可能读不全，单独包一层。
            string hosts = SafeHostAddresses();
            if (hosts.Length > 0) sb.Append(" addrs=").Append(hosts);

            // 指纹长度封顶：日志行格式是机器契约（memory「滚动日志」：sessions 汇不能动、
            //   自由文本也别刷几百字），超出就截断，截断本身可见。
            string text = sb.ToString();
            return text.Length <= 900 ? text : text[..900] + "…";
        }

        /// <summary>适配器身份短串（读不到返回空串，不抛）。</summary>
        private static string SafeAdapterId(NetworkAdapter? adapter)
        {
            try { return adapter == null ? "-" : adapter.NetworkAdapterId.ToString("N")[..8]; }
            catch { return "?"; }
        }

        /// <summary>一条链路的指纹项：适配器 id / IANA 类型 / 所连网络的 id。</summary>
        private static string SafeAdapter(NetworkAdapter? adapter)
        {
            try
            {
                if (adapter == null) return "";
                string net = "?";
                try { net = adapter.NetworkItem?.NetworkId.ToString("N")[..8] ?? "-"; }
                catch (Exception ex) { net = "?" + ex.GetType().Name; }
                return $"[{adapter.NetworkAdapterId:N} type={adapter.IanaInterfaceType} net={net}]";
            }
            catch { return ""; }
        }

        /// <summary>本机 IPv4/IPv6 地址清单（含归属适配器），用于让"换了哪张卡"在日志里可读。</summary>
        private static string SafeHostAddresses()
        {
            try
            {
                var seen = new SortedSet<string>(StringComparer.Ordinal);
                foreach (Windows.Networking.HostName h in NetworkInformation.GetHostNames())
                {
                    try
                    {
                        if (h.Type != Windows.Networking.HostNameType.Ipv4 &&
                            h.Type != Windows.Networking.HostNameType.Ipv6)
                            continue;
                        string adapter = "?";
                        try
                        {
                            Windows.Networking.Connectivity.IPInformation? ip = h.IPInformation;
                            if (ip?.NetworkAdapter != null)
                                adapter = ip.NetworkAdapter.NetworkAdapterId.ToString("N")[..8];
                        }
                        catch { /* 归属读不到不影响地址本身 */ }
                        seen.Add($"{h.DisplayName}@{adapter}");
                    }
                    catch { /* 单条跳过 */ }
                    if (seen.Count >= 24) break;
                }
                return string.Join(",", seen.ToArray());
            }
            catch (Exception ex)
            {
                return "?" + ex.GetType().Name;
            }
        }
    }
}
