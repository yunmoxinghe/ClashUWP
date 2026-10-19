using System;
using System.Collections.Generic;
using System.Linq;
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
    ///   2 = 在 1 之上，指纹真变了就请求桥重绑（超时 + 门闩 + 每会话上限，见
    ///       <see cref="ClashVpnPlugin.RequestBridgeRebind"/>），并把 <c>bound_iface</c>
    ///       的变化作为证据记下来。
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

            _baseline = Fingerprint();

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

            LastNote = $"arm={arm} 已订阅 events=0 bound={_baselineBoundIface}";
            RollingLog.Info("net-follow",
                $"武装 arm={arm} 事件=NetworkStatusChanged " +
                $"bound_iface={(_baselineBoundIface.Length == 0 ? "(空)" : _baselineBoundIface)} " +
                $"基线={_baseline}");
        }

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
        /// 事件来了：合并窗口内直接跳过，否则重取指纹、与基线比、按需动手。
        /// ★ 本方法跑在<b>平台投递事件的线程</b>上，里面只允许读属性和写日志，
        ///   绝不允许长时间阻塞（重绑动作是 fire-and-forget 到别的线程，见
        ///   <see cref="ClashVpnPlugin.RequestBridgeRebind"/>）。
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

                string now1 = Fingerprint();
                bool changed = now1 != _baseline;
                string bound = ReadBoundIface();

                var note = new StringBuilder();
                note.Append($"evt={eventName}");
                if (detail.Length > 0) note.Append($" detail={detail}");
                note.Append(changed ? " 指纹=变了" : " 指纹=未变");
                note.Append($" bound={(_baselineBoundIface == bound ? bound : $"{_baselineBoundIface}→{bound}")}");

                if (changed)
                {
                    note.Append($" 新指纹={now1}");
                    // ★ 换网 ⇒ 旧的"节点域名→IP"缓存不可信（它是在<b>上一个网络</b>里解析的）。
                    //   这里只作废 TTL，不立刻重解析：重解析必须在隧道外做才可信
                    //   （NodeRoutes 类注释的口径），而会话进行中解析走的是隧道。
                    NodeRoutes.Invalidate("网络变化:" + eventName);
                    note.Append(" nodeCache=作废");
                }

                LastNote = note.ToString();
                RollingLog.Info("net-follow", LastNote);
                ClashVpnPlugin.LogSession($"evt=net-follow {eventName} changed={(changed ? 1 : 0)} " +
                                          $"bound={_baselineBoundIface}→{bound}");

                if (!changed)
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

                long lastAct = System.Threading.Volatile.Read(ref _lastActionTick);
                if (now - lastAct < MinActionIntervalMs)
                {
                    LastNote = note.ToString() + $" 动作=限频（距上次 {now - lastAct}ms）";
                    RollingLog.Info("net-follow", LastNote);
                    return;
                }
                if (Interlocked.Increment(ref _actionCount) > MaxActionsPerSession)
                {
                    LastNote = note.ToString() + $" 动作=超上限 {MaxActionsPerSession} 次/会话，不再动手";
                    RollingLog.Warn("net-follow", LastNote);
                    return;
                }

                Interlocked.Exchange(ref _lastActionTick, now);
                string accepted = ClashVpnPlugin.RequestBridgeRebind($"net-follow:{eventName}");
                LastNote = note.ToString() + $" 动作={accepted}";
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

        /// <summary>读桥侧记录的绑定网卡名；读不到（桥没起/异常）一律空串，不抛。</summary>
        private static string ReadBoundIface()
        {
            try { return ClashVpnPlugin.BoundIfaceNow(); }
            catch { return ""; }
        }

        /// <summary>
        /// 物理网络指纹：把"当前有几条链路、各自的身份/类型/IPv4"压成一行可比较的串。
        /// 每一项单独 try —— 在 AppContainer 里哪个属性读得到是<b>未实测</b>的，
        /// 读不到就在指纹里留 <c>?</c> 而不是整条失败（口径同 Docs/12 §6 的"能力边界要可见"）。
        /// ★ 指纹里<b>没有 DNS</b>：这台机器的 winmd（UniversalApiContract v19）里
        ///   <c>NetworkAdapter</c> 只有 IanaInterfaceType / NetworkAdapterId / NetworkItem /
        ///   Inbound/OutboundMaxBitsPerSecond / GetConnectedProfileAsync 五个成员，
        ///   <b>没有 GetDnsServers、也没有 IsConnected</b>（ilspycmd 反编译实录）。
        ///   所以"换网后 DNS 变没变"在这里<b>观测不到</b>，不写进判据、也不假装有。
        /// </summary>
        internal static string Fingerprint()
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
