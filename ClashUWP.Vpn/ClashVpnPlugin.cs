using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;

using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Networking.Vpn;
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

        private uint _encapsulatedPackets;
        private uint _echoReplies;
        private uint _unknownPackets;

        // ────────────────────────────────────────────────────────────────────
        // S4 · Connect
        // 【官方】"instructs the VPN plug-in to connect to the VPN server and
        //          establish the VPN channel."
        // ────────────────────────────────────────────────────────────────────
        public void Connect(VpnChannel channel)
        {
            // 步 1 · 日志。【官方】LogDiagnosticMessage(String)："Writes a diagnostic message
            //        to the system log." —— 这是插件<b>唯一</b>的官方日志出口。
            channel.LogDiagnosticMessage("ClashUWP: 进入 Connect.");

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
            //    照 profile 侧的名字写会直接编译不过（CS1061）。
            _config = VpnPluginConfig.Parse(channel.Configuration.CustomField);
            channel.LogDiagnosticMessage(
                $"ClashUWP: mtu={_config.Mtu} maxFrame={_config.MaxFrame} ip={_config.ClientIpV4}");

            // 步 4 · 建 transport 并关联。
            //   ★【官方】AssociateTransport(Object, Object)："Prepares and marks the transport
            //     for use by the VPN framework ... **This call must be performed before any other
            //     operations related to the VPN framework APIs.**"
            //
            //   为什么还建 socket：Start 形参要求一个 DatagramSocket / StreamSocket
            //   【官方】："can be a DatagramSocket or a StreamSocket"。
            //   但 ClashUWP 不用它传数据（数据面走进程内内核 + 主动注入），
            //   所以它只是个形式上的占位：绑一个本地 UDP 端口即可，不连任何远端。
            _transport = new DatagramSocket();
            channel.AssociateTransport(_transport, null);

            try
            {
                // 绑本地随机端口（""= 让系统挑）。需要 internetClientServer 能力。
                // Connect 是同步回调（IVpnPlugIn.Connect 返回 void），只能用同步等待；
                // 后台任务线程上没有 SynchronizationContext，不会死锁。
                _transport.BindServiceNameAsync("").AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // 绑不上也要继续 —— Start 本身未必需要它已绑定，
                // 骨架期宁可带着警告跑起来看清后续报错。
                channel.LogDiagnosticMessage("ClashUWP: BindServiceName 失败（继续）: " + ex.Message);
            }

            // 步 5 · 路由与 DNS 下发
            VpnRouteAssignment routes = BuildRoutes();
            VpnNamespaceAssignment namespaces = BuildNamespaces();

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
            //   optimizeForLowCostNetwork: true 时旧网络收费、新低成本网络可用会再触发 connect() 回调
            //   main/optionalOuterTunnelTransport: "can be a DatagramSocket or a StreamSocket"
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
            channel.Start(
                new List<HostName> { new(_config.ClientIpV4) },   // assignedClientIPv4list
                null,                                             // assignedClientIPv6list
                null,                                             // vpnInterfaceId
                routes,                                           // routeScope
                namespaces,                                       // namespaceScope
                _config.Mtu,                                      // mtuSize      ★ ≤ 1400
                _config.MaxFrame,                                 // maxFrameSize ★ ≤ 1500
                false,                                            // optimizeForLowCostNetwork
                _transport,                                       // mainOuterTunnelTransport
                null);                                            // optionalOuterTunnelTransport

            _started = true;
            _encapsulatedPackets = 0;
            _echoReplies = 0;
            _unknownPackets = 0;

            channel.LogDiagnosticMessage("ClashUWP: VpnChannel.Start 完成，L3 接口已建立.");
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
            // Start 之前平台不该回调这里；兜一下，免得往一个还没建起来的 channel 里注入。
            if (!_started)
                return;

            while (packets.Size > 0)
            {
                // sample 注释强调：用 RemoveAtBegin 保序。
                VpnPacketBuffer buffer = packets.RemoveAtBegin();

                // IBuffer.Length 是本包有效长度；Capacity 是池化 buffer 的容量（= mtuSize）。
                byte[] packet = buffer.Buffer.ToArray();
                int length = (int)buffer.Buffer.Length;

                _encapsulatedPackets++;

                if (IpPacket.TryBuildEchoReply(packet, length, out byte[] reply))
                {
                    // 假内核：echo request → echo reply，直接注回本机协议栈。
                    Inject(channel, reply);
                    _echoReplies++;
                }
                else
                {
                    _unknownPackets++;
                }

                if (_encapsulatedPackets % LogEveryNPackets == 0)
                {
                    channel.LogDiagnosticMessage(
                        $"ClashUWP: 已处理 {_encapsulatedPackets} 包（echo 应答 {_echoReplies}，非 ICMP {_unknownPackets}）.");
                }
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
        }

        // ────────────────────────────────────────────────────────────────────
        // S5.3 · GetKeepAlivePayload
        // 【官方】"instructs the VPN plug-in to compute and return a Keep alive payload ready
        //          to be sent to the VPN server."
        // ────────────────────────────────────────────────────────────────────
        public void GetKeepAlivePayload(VpnChannel channel, out VpnPacketBuffer keepAlivePacket)
        {
            // 骨架期没有对端服务器要保活，但 out 参数给 null 有被平台解引用的风险，
            // 所以照 sample 的样子从 Send 池取一个最小包交回去。
            channel.RequestVpnPacketBuffer(VpnDataPathType.Send, out keepAlivePacket);
            keepAlivePacket.Buffer.Length = 1;
        }

        // ────────────────────────────────────────────────────────────────────
        // S7 · Disconnect
        // 【官方】"instructs the VPN plug-in to disconnect from the VPN server and destroy
        //          the VPN channel."
        // ────────────────────────────────────────────────────────────────────
        public void Disconnect(VpnChannel channel)
        {
            channel.LogDiagnosticMessage(
                $"ClashUWP: 断开。共处理 {_encapsulatedPackets} 包（echo 应答 {_echoReplies}）.");

            // 【官方】Stop()："Destroys a previously established VPN channel object.
            //   **Disassociates and closes the outerTunnelTransport connection to the VPN server.**"
            // ⚠️ socket 所有权在 Start 成功时已转移给框架，Stop 之后<b>不要</b>自己 Dispose。
            channel.Stop();

            _started = false;
            _transport = null;
        }

        // ────────────────────────────────────────────────────────────────────
        // 内部实现
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 把处理完的 IP 包注回本机协议栈 —— 这是 ClashUWP 数据面的出口。
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
        /// 需要脱离 Decapsulate 时序投包时可以换那条。
        /// </summary>
        private static void Inject(VpnChannel channel, byte[] packet)
        {
            channel.RequestVpnPacketBuffer(VpnDataPathType.Receive, out VpnPacketBuffer outBuffer);

            // IBuffer.Length 必须 ≤ Capacity（Receive 池的 Capacity = mtuSize = 1400）。
            // 真内核进来后这里要分片：超过 MTU 的包不能直接丢进去。
            if (packet.Length > outBuffer.Buffer.Capacity)
                return;

            outBuffer.Buffer.Length = (uint)packet.Length;
            using (var stream = outBuffer.Buffer.AsStream())
            {
                stream.Write(packet, 0, packet.Length);
                stream.Flush();
            }

            channel.AppendVpnReceivePacketBuffer(outBuffer);
            channel.FlushVpnReceivePacketBuffers();
        }

        private VpnRouteAssignment BuildRoutes()
        {
            var assignment = new VpnRouteAssignment();

            foreach (string cidr in _config.IncludeRoutes)
            {
                if (TryParseCidr(cidr, out string? address, out byte prefix))
                    assignment.Ipv4InclusionRoutes.Add(new VpnRoute(new HostName(address), prefix));
            }

            foreach (string cidr in _config.ExcludeRoutes)
            {
                if (TryParseCidr(cidr, out string? address, out byte prefix))
                    assignment.Ipv4ExclusionRoutes.Add(new VpnRoute(new HostName(address), prefix));
            }

            return assignment;
        }

        private VpnNamespaceAssignment BuildNamespaces()
        {
            var assignment = new VpnNamespaceAssignment();

            var dnsServers = new List<HostName>();
            foreach (string dns in _config.DnsServers)
                dnsServers.Add(new HostName(dns));

            // VpnNamespaceInfo(String name, IVector<HostName> dnsServers, IVector<HostName> webProxyServers)
            // name 是 NRPT 的命名空间；"." 匹配所有域名，等价于"全局 DNS"。
            assignment.NamespaceList.Add(new VpnNamespaceInfo(".", dnsServers, new List<HostName>()));

            return assignment;
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
