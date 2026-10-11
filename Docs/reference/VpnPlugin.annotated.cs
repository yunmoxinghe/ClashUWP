// ============================================================================
// 官方 UWP VPN Plugin（C# 版）调用链 —— 逐行官方注释骨架
//
// 来源：microsoft/UwpVpnPluginSample · CSharp/（本地副本 D:/fluentapps/repos/UwpVpnPluginSample）
// 配套文档：../01-官方VPN-Plugin-CSharp版-调用链路与官方文档注释.md
//
// ⚠️ 本文件是「阅读用注释骨架」，不是可编译代码：
//    - 省略了 sample 自带的测试开关（exceptionFlags）、计数器、日志细节
//    - 保留了真实调用顺序与真实 API 签名形状
//    - 每条【官方】注释取自 Microsoft Learn 对应 API 页面原文（中文为本文翻译）
//
// 标注：🚩 = 官方明文 Not supported / 与官方文档冲突，照抄会中毒
// ============================================================================

using System;
using System.Collections.Generic;
using Windows.ApplicationModel.Background;
using Windows.ApplicationModel.Core;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Networking.Vpn;
using Windows.Storage.Streams;

namespace ClashUWP.Vpn
{
    // ==========================================================================
    // S3 · 后台任务入口 —— 平台每次要插件干活，都从这里进
    // 清单声明：<uap:Task Type="vpnClient" />，安装后由 VPN 平台自动注册，无需自注册
    // ==========================================================================
    public sealed class VpnBackgroundTask : IBackgroundTask
    {
        private const string PluginId = "ClashVpnPlugin";

        public void Run(IBackgroundTaskInstance taskInstance)
        {
            // 【官方/UWP 通用】GetDeferral：告知平台本任务为异步，不要在我完成前挂起或终止宿主进程。
            // sample 注释原文："To keep the host process from being suspended or terminated
            //                  while the background task is running"
            BackgroundTaskDeferral deferral = taskInstance.GetDeferral();
            try
            {
                ClashVpnPlugin plugin;

                // ★ 关键契约：每次调度必须复用【同一个】IVpnPlugIn 实例。
                // sample 注释原文："the plug-in may have a partially decapsulated packet stored
                //                  while it awaits more data to be available for decapsulation"
                // 载体是 CoreApplication.Properties —— 注意这是【进程内】字典，宿主进程重建即丢失。
                var props = (IDictionary<string, object>)CoreApplication.Properties;
                if (props.ContainsKey(PluginId))
                {
                    plugin = (ClashVpnPlugin)props[PluginId];
                }
                else
                {
                    plugin = new ClashVpnPlugin();
                    props.Add(PluginId, plugin);
                }

                // 【官方】ProcessEventAsync(Object, Object)："Processes any pending VpnChannel events."
                // 第二参数官方定义："The event object that was passed to the app from a background task
                //                   Run method parameter. App code ... treats this object as an opaque blob."
                // → 别自己解析 TriggerDetails，原样透传。调用返回时平台才认为本次事件处理完。
                VpnChannel.ProcessEventAsync(plugin, taskInstance.TriggerDetails);
            }
            catch (Exception ex)
            {
                // sample 只记日志不 rethrow —— 保证 deferral 一定被 Complete，
                // 否则进程会永远卡在 Running 状态。
                System.Diagnostics.Debug.WriteLine("VpnBackgroundTask.Run: " + ex);
            }
            finally
            {
                deferral.Complete();
            }
        }
    }

    // ==========================================================================
    // S4~S7 · IVpnPlugIn 实现
    // 【官方】IVpnPlugIn："Provides an interface to implement specific third party VPN client operations."
    // 五个方法：Connect / Disconnect / Encapsulate / Decapsulate / GetKeepAlivePayload
    // ==========================================================================
    public sealed class ClashVpnPlugin : IVpnPlugIn
    {
        private StreamSocket tcpSocket;      // 外层传输（可不用）
        private DatagramSocket udpSocket;

        // 跨 Decapsulate 调用保存的「半包」状态 —— 入参 encapBuffer 可能只是一个包的半个
        private byte[] partialPacket = new byte[64 * 1024 + 1];
        private uint partialSize;

        // ---------------------------------------------------------------------
        // S4 · Connect —— 【官方】"instructs the VPN plug-in to connect to the VPN server
        //                           and establish the VPN channel."
        // ---------------------------------------------------------------------
        public void Connect(VpnChannel channel)
        {
            // 步 1：日志 —— 【官方】LogDiagnosticMessage(String)："Writes a diagnostic message
            //       to the system log."（插件唯一的官方日志出口）
            channel.LogDiagnosticMessage("Entering Connect.");

            // 步 2：唯一实例 ID —— 【官方】Id："unique instance ID of a VPN channel ... can be used
            //       to simplify demuxing operations between two VPN connection instances."
            channel.LogDiagnosticMessage("Channel Id: " + channel.Id);

            // 步 3：读配置 —— 【官方】Configuration："a VpnChannelConfiguration object corresponding
            //       to the configuration of the VpnChannel ..."
            //       其中 CustomConfiguration 那段 XML 格式【完全自定义】：
            //       【官方】"Refer to the documentation from the VPN plug-in for format and support."
            var config = ReadConfig(channel);   // ← 自己实现：解析 profile 里那段自定义 XML

            // 步 4：建 socket + 关联为外层传输
            //       ★【官方】AssociateTransport(Object, Object)："Prepares and marks the transport
            //         for use by the VPN framework ... **This call must be performed before any other
            //         operations related to the VPN framework APIs.**"
            tcpSocket = new StreamSocket();
            channel.AssociateTransport(tcpSocket, null);

            // 步 5：凭据（可选）—— 【官方】RequestCredentials："Request the VPN platform to collect
            //       credentials from the user." 且 RequestCredentialsAsync 条目下有一条硬约束：
            //       "A VPN plug-in must request credentials before using any type of credential,
            //        even if the intent is not to invoke user interaction for it."
            // var creds = channel.RequestCredentials(VpnCredentialType.UsernamePassword, false, false, null);

            // 步 6：需要前台交互时（订阅链接 / 二次验证 / 节点选择）
            //       官方签名：ActivateForeground(string packageRelativeAppId, ValueSet sharedContext) → ValueSet
            //       packageRelativeAppId = PRAID（清单里的 Application Id，如 "App"）
            //       ★【官方】"Activates the VPN app in the foreground ... **You can call ActivateForeground
            //         only from your implementation of IVpnPlugin.Connect.** While the app is in the
            //         foreground, the regular Connect timeout is paused. The ActivateForeground call will
            //         be cancelled if there's a long pause (about 10 minutes)."
            //       ⚠️ Windows requirements：Windows 11 (10.0.22000.0) 起 —— Win10 上没这个 API
            //
            //       前台侧：以 ActivationKind.VpnForeground 激活，参数是 VpnForegroundActivatedEventArgs，
            //       含 ProfileName / SharedContext / ActivationOperation 三个字段；
            //       填完 UI 后调 ActivationOperation.Complete(valueSet)，valueSet 回流成这里的返回值。
            // var userInput = channel.ActivateForeground("App", new ValueSet());

            // 🚩 步 7（sample 有、但别抄）：channel.RequestCustomPrompt(...)
            //    【官方】RequestCustomPrompt(IVectorView<IVpnCustomPrompt>)： "Not supported."
            //    替代：RequestCustomPromptAsync(IVectorView<IVpnCustomPromptElement>)

            // 步 8：连服务器（纯 socket，非 VPN API）。sample 遍历 ServerHostNameList 逐个试。
            tcpSocket.ConnectAsync(new HostName(config.Server), config.Port).AsTask().Wait();

            // 步 9：系统健康（可选）—— 【官方】SystemHealth："Gets the statement of health of the
            //       client machine."（napagent 关掉会抛异常，务必 try/catch）
            // var health = channel.SystemHealth;

            // 步 10：★ 建隧道 = 建 L3 网卡
            // 【官方】Start(...)："Creates the VPN channel to be used by the VPN plug-in. **It also
            //   creates an L3 network interface** for applications in the client machine to be able to
            //   see the corporate network."
            //
            // 参数官方语义（★ = 硬约束）：
            //   assignedClientIPv4list : "the IPv4 address assigned by the VPN server ... If null, DHCPv4 is used."
            //   assignedClientIPv6list : "If null, DHCPv6 or RD is used."
            //   vpnInterfaceId         : VpnInterfaceId（sample 传 null）
            //   routeScope             : "the routes which should be associated / not associated with the VPN interface"
            //   namespaceScope         : "the list of name prefixes ... including its DNS and proxy servers"（NRPT/DNS）
            //   mtuSize        ★       : "should be configured to be at most **1400**"；也是 Receive 池 buffer 大小
            //   maxFrameSize   ★       : "should be <= **1500**"；也是 Send 池 buffer 大小
            //   optimizeForLowCostNetwork: "If TRUE the VPN framework will invoke the connect() callback ...
            //                              whenever the old network was costed and a new low cost network becomes available."
            //   main/optionalOuterTunnelTransport: "can be a DatagramSocket or a StreamSocket"
            //
            // Remarks 四条硬约束（原文）：
            //   · "Any call to this API outside of a connected VPN plug-in will fail ..."
            //   · "There can only be one VPN profile per process ..."
            //   · "On success the ownership of outerTunnelTransport is transferred by the VPN framework.
            //      Calling this method multiple times with the same parameters will have no effect."
            //   · "There can only be one VPN channel per process ... The mtuSize must always be smaller
            //      than the maxFrameSize."
            channel.Start(
                new List<HostName> { new HostName(config.ClientIpV4) },  // assignedClientIPv4list
                null,                                                    // assignedClientIPv6list
                null,                                                    // vpnInterfaceId
                config.RouteAssignment,                                  // routeScope
                config.NamespaceAssignment,                              // namespaceScope
                1400,                                                    // mtuSize  ★ ≤1400（sample 写 1500，违规）
                1500,                                                    // maxFrameSize ★ ≤1500（sample 写 1501，违规）
                false,                                                   // optimizeForLowCostNetwork
                tcpSocket,                                               // mainOuterTunnelTransport
                null);                                                   // optionalOuterTunnelTransport

            // 需要 per-app 分流时用 StartWithTrafficFilter（1803/17134 引入）：
            // 【官方】"... **It allows the specification of traffic filters to use in per-app VPN scenarios.**"
        }

        // ---------------------------------------------------------------------
        // S5.1 · Encapsulate —— 出站（本机 → 服务器）
        // 【官方】"instructs the VPN plug-in to **prepare the indicated L3 IP packet for transmission
        //          to the VPN server**. The process might include any encapsulation specific to the VPN protocol."
        //
        // 契约：从入参 packets 取包 → 封装 → 填进【出参 encapulatedPackets】。
        // 对 ClashUWP：packets 里的每个 Buffer 就是一个原始 IP 包，喂给 mihomo 的 Tun.Read()。
        // ---------------------------------------------------------------------
        public void Encapsulate(VpnChannel channel,
                                VpnPacketBufferList packets,
                                VpnPacketBufferList encapulatedPackets)
        {
            while (packets.Size > 0)
            {
                // sample 注释强调：用 RemoveAtBegin 保序
                VpnPacketBuffer buffer = packets.RemoveAtBegin();

                // 【官方】VpnPacketBufferList.Append(VpnPacketBuffer)：
                //        "Add a new IVpnPacketBuffer object to the end of the IVpnPacketBufferList."
                // 这里做封装（sample 是加 1 字节 VPN_CONTROL 头），然后按官方契约放进出参：
                encapulatedPackets.Append(buffer);

                // 🚩 sample 没走官方契约，而是自己：
                //    channel.AppendVpnSendPacketBuffer(buffer);   ← "Appends a send VpnPacketBuffer object to the channel."
                //    channel.FlushVpnSendPacketBuffers();          ← "Flushes any appended send packet buffers."
                //    且这两句发生在 BackgroundWorker 线程上（65 号 A1 / B3）。
            }
        }

        // ---------------------------------------------------------------------
        // S5.2 · Decapsulate —— 入站（服务器 → 本机）
        // 【官方】"instructs the VPN plug-in to prepare receive a buffer from the VPN server
        //          outerTunnelTransport, **extract any number of IP packets encapsulated in the buffer**,
        //          and return the IP packets together with any other reply buffers ..."
        //
        // ★ 入参 encapBuffer 可能是 1 个包 / 多个包 / 半个包 —— 必须自己跨调用拼包。
        //   半包状态不能放局部变量，要放实例字段（这也是 S3 必须复用同一实例的原因）。
        // ---------------------------------------------------------------------
        public void Decapsulate(VpnChannel channel,
                               VpnPacketBuffer encapBuffer,
                               VpnPacketBufferList decapsulatedPackets,
                               VpnPacketBufferList controlPacketsToSend)
        {
            // 1) 取原始字节（sample 用 VpnPluginGetRawByteBuffer 拿 IBuffer 背后的 byte[]）
            Buffer frame = encapBuffer.Buffer;

            // 2) 拆包：按 IPv4 TotalLength / IPv6 PayloadLength+40 切边界；
            //    不够一个完整包就存进 partialPacket，等下次 Decapsulate 继续。
            //    （sample: VALID_LENGTH = 4+1, VALID_LENGTH_V6 = 6+1, IPV6_HEADER_LENGTH = 40）

            // 3) 投递。
            //    (a) 官方契约：填进【出参 decapsulatedPackets】—— decapsulatedPackets.Append(outBuf);
            //    (b) 主动注入路径（sample 实际走的这条，对 ClashUWP 更有用，
            //        因为可以在 Decapsulate 之外随时投包）：
            //    【官方】RequestVpnPacketBuffer(VpnDataPathType, out VpnPacketBuffer)：
            //           "Requests an IVpnPacketBuffer from the specified pool. There is one IVpnPacketBuffer
            //            pool for the send path and another IVpnPacketBuffer pool for the receive path."
            channel.RequestVpnPacketBuffer(VpnDataPathType.Receive, out VpnPacketBuffer outBuf);

            // ... 把完整 IP 包拷进 outBuf.Buffer，并设置 outBuf.Buffer.Length = 真实长度 ...

            // 【官方】AppendVpnReceivePacketBuffer(VpnPacketBuffer)："Appends a receive VpnPacketBuffer object to the channel."
            channel.AppendVpnReceivePacketBuffer(outBuf);
            // 【官方】FlushVpnReceivePacketBuffers()："Flushes any appended receive packet buffers."
            channel.FlushVpnReceivePacketBuffers();

            // 另一条官方明确支持的注入通道（可在 Decapsulate 之外调用 —— 76 号文档实测过）：
            // 【官方】GetVpnReceivePacketBuffer()："Requests a VpnPacketBuffer object from the receive packet
            //        buffer pool to be used in the decapsulation or **injection** of a received packet from
            //        the VPN server into the local network stack."
        }

        // ---------------------------------------------------------------------
        // S5.3 · GetKeepAlivePayload
        // 【官方】"instructs the VPN plug-in to compute and return a Keep alive payload ready to be
        //          sent to the VPN server."
        // 注意：通过 out 参数直接交回平台，不需要 Append/Flush。
        // ---------------------------------------------------------------------
        public void GetKeepAlivePayload(VpnChannel channel, out VpnPacketBuffer keepAlivePacket)
        {
            keepAlivePacket = null;
            channel.RequestVpnPacketBuffer(VpnDataPathType.Send, out keepAlivePacket);
            keepAlivePacket.Buffer.Length = 1;
            // keepAlivePacket.Buffer.AsStream().WriteByte(0xXX);
        }

        // ---------------------------------------------------------------------
        // S7 · Disconnect
        // 【官方】"instructs the VPN plug-in to disconnect from the VPN server and destroy the VPN channel."
        // ---------------------------------------------------------------------
        public void Disconnect(VpnChannel channel)
        {
            // 清掉半包状态
            partialSize = 0;

            // 【官方】Stop()："Destroys a previously established VPN channel object. **Disassociates and
            //   closes the outerTunnelTransport connection to the VPN server.**"
            // ⚠️ socket 所有权在 Start 成功时已转移给框架，Stop 之后不要自己 Dispose（65 号 B5）。
            channel.Stop();
        }

        private Config ReadConfig(VpnChannel channel) => throw new NotImplementedException();
        private sealed class Config
        {
            public string Server;
            public string Port;
            public string ClientIpV4;
            public VpnRouteAssignment RouteAssignment = new VpnRouteAssignment();
            public VpnNamespaceAssignment NamespaceAssignment = new VpnNamespaceAssignment();
        }
    }

    // ==========================================================================
    // S1/S2 · 前台侧：建 profile 与发起连接（MainPage.xaml.cs 对应逻辑）
    // ==========================================================================
    internal static class ProfileSetup
    {
        // 【官方】VpnManagementAgent："Allows the creation, deletion, edit, connect, and disconnect
        //   flows for VPN. These can be used to manage the lifecycle of a VPN profile ..."
        private static readonly Windows.Networking.Vpn.VpnManagementAgent Agent = new();

        internal static async void Create()
        {
            var profile = new VpnPlugInProfile
            {
                ProfileName = "ClashUWP",
                // ★【官方】VpnPluginPackageFamilyName："Gets or sets the package family name of the VPN
                //   plug-in to be used for this VPN plug-in profile. **This field is required for
                //   VpnPlugInProfile to be valid.**"
                VpnPluginPackageFamilyName = Windows.ApplicationModel.Package.Current.Id.FamilyName,
                // 【官方】RequireVpnClientAppUI："determines whether this VPN profile appears in the
                //   system's VPN connection UI."（true = 出现在系统 VPN UI，否则只能在自己 app 里连）
                RequireVpnClientAppUI = true,
                // 【官方】CustomConfiguration："an XML string to configure the VPN plug-in. Refer to the
                //   documentation from the VPN plug-in for format and support."
                //   → 格式完全自定义，ClashUWP 可以把 mihomo 配置塞在这里
                CustomConfiguration = "<clash>...</clash>",
            };
            profile.ServerUris.Add(new Uri("http://127.0.0.1:0"));

            // 【官方】AddProfileFromObjectAsync(IVpnProfile)："Creates a new VPN Profile based on the
            //   VpnProfile input object." → 返回 VpnManagementErrorStatus，Ok 才算成功
            var status = await Agent.AddProfileFromObjectAsync(profile);

            // 【官方】ConnectProfileAsync(IVpnProfile)："Connects to an existing VPN profile."
            // 调用后控制权交回平台：它按 PFN 找到插件包，拉起 vpnClient 后台任务（S3）。
            if (status == VpnManagementErrorStatus.Ok)
                await Agent.ConnectProfileAsync(profile);
        }
    }
}
