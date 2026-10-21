using System;
using System.Text;
using System.Threading.Tasks;

using ClashUWP.Vpn;

namespace ClashUWP.Backend
{
    /// <summary>
    /// 【后端 · 启动自动探测】两个「装包后第一次启动跑一次」的探测：
    ///   <see cref="RunProbeOnceAsync"/>      安全连通性自测（闸门 selftest.txt）
    ///   <see cref="RunDatafaceProbeOnceAsync"/> 小路由数据面探测（闸门 dataface-v4.txt）
    ///
    /// 两者的共同点：都用「闸门文件存在与否」保证只跑一次，都用**小路由 probe profile**
    /// （203.0.113.0/24 文档保留段 + 8.8.8.8/32），不碰真实流量，因此不会断网 ——
    /// 这正是它们替代「UIA 点按钮测主 profile」的原因。
    ///
    /// ★ 收尾一律走 <see cref="GracefulTeardown"/> 那道闸：探测完删 profile 之前
    ///   必须确认平台已把会话收干净，否则留下孤儿 NRPT + 幽灵路由 = 整机断网
    ///   （2026-10-16 00:21 的实测事故）。
    /// </summary>
    internal static class StartupProbes
    {
        /// <summary>
        /// 跑一次安全连通性探测，结果写 selftest.txt。
        ///
        /// ★ 用 selftest.txt 是否存在做闸门：只跑一次，
        ///   免得以后每次启动应用都自动去连一遍（虽然安全，但不该有这种副作用）。
        ///   注意本沙箱写不了包目录，所以这个闸门只能由应用自己创建/识别。
        /// </summary>
        internal static async Task RunProbeOnceAsync()
        {
            try
            {
                if (await LocalState.ExistsAsync("selftest.txt"))
                    return;

                string result = await VpnProfileSetup.ProbeConnectAsync();

                await LocalState.WriteTextAsync("selftest.txt",
                    $"Time={DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n{result}");
            }
            catch (Exception ex)
            {
                // ★ 探测本身的失败也必须落盘，否则外部只看到「文件没生成」，
                //   分不清是没跑、跑了失败、还是跑挂了。
                await LocalState.WriteTextQuietAsync("selftest.txt",
                    $"ERROR={ex.GetType().Name}: {ex.Message}");
                RollingLog.Error("probe-selftest", ex);
            }
        }

        /// <summary>
        /// mihomo 数据面自动探测（闸门 <c>LocalState/dataface.txt</c>，只跑一次）。
        ///
        /// 流程：建小路由 probe profile → 新鲜回读后连接（硬约束：不能用自建对象连）
        /// → TCP SYN 触发流量（203.0.113.0/24 路由在 VPN 接口上，必然进 Encapsulate）
        /// → 断开 → 读插件 Disconnect 时落盘的 dataface-plugin.txt → 汇总写 dataface.txt
        /// → 删 probe profile。
        ///
        /// 路由只有 203.0.113.0/24（文档保留段），不碰真实流量，不会断网 ——
        /// 这正是它替代「UIA 点按钮测主 profile」的原因。
        /// </summary>
        internal static async Task RunDatafaceProbeOnceAsync()
        {
            const string probeName = "ClashUWP-Dataface";

            try
            {
                if (await LocalState.ExistsAsync("dataface-v4.txt"))
                    return;

                var sb = new StringBuilder();
                sb.AppendLine($"Time={DateTime.Now:yyyy-MM-dd HH:mm:ss}");

                // ★【1.0.88 · Docs/38 官方第④条】探测也是拨号，官方按<b>至多一条活动会话</b>建模
                //   （sample L80 "There should be zero or one of these at all times"）⇒
                //   已经有隧道在飞时不再连这条 probe（它会占用插件唯一那份 activeChannel）。
                //   判据共用前台那一份 <see cref="ClashUWP.Vpn.VpnProfileSetup.FindActiveSessionAsync"/>，
                //   不在这里另写一遍"什么叫有会话"。结果照旧落盘，跳过这件事本身要看得见。
                var live = await ClashUWP.Vpn.VpnProfileSetup.FindActiveSessionAsync();
                if (live != null)
                {
                    sb.AppendLine($"跳过=已有活动会话 [{live.ProfileName}]（官方建模至多一条，探测不得并行拨号）");
                    await LocalState.WriteTextAsync("dataface-v4.txt", sb.ToString());
                    return;
                }

                // ★【1.0.82 · Docs/34 §六】这条 probe 自己就是"建 + 连 + 断 + 删 + 中途枚举"的
                //   一整串 profile 存储 RPC，且它是在应用启动阶段跑的 —— 那时前台 UI 的 3 秒轮询、
                //   甚至健康哨兵都可能正在读同一个存储。全部改走串行层；下面每一步都是
                //   "await 完拿结果再走下一步"的叶子调用，没有嵌套，不会自锁。

                // 建 probe profile。route 由插件 Connect 从 CustomConfiguration 解析成
                // VpnRouteAssignment 下发 —— 与 selftest 的 safeConfig 同一机制。
                var profile = new Windows.Networking.Vpn.VpnPlugInProfile
                {
                    ProfileName = probeName,
                    VpnPluginPackageFamilyName = Windows.ApplicationModel.Package.Current.Id.FamilyName,
                    RequireVpnClientAppUI = false,
                    CustomConfiguration =
                        "<clash><clientIpV4>10.7.0.2</clientIpV4><mtu>1400</mtu>" +
                        "<maxFrame>1500</maxFrame><route>203.0.113.0/24</route>" +
                        "<route>8.8.8.8/32</route></clash>",
                    AlwaysOn = false,
                };
                profile.ServerUris.Add(new Uri("http://127.0.0.1"));

                var add = await ClashUWP.Vpn.ProfileStore.AddProfileFromObjectAsync(profile);
                sb.AppendLine($"Add={add}");
                if (add != Windows.Networking.Vpn.VpnManagementErrorStatus.Ok)
                {
                    await LocalState.WriteTextAsync("dataface-v4.txt", sb.ToString());
                    return;
                }

                // ★ 硬约束：连接必须用 GetProfilesAsync 新鲜回读的对象，自建对象会 CannotFindProfile。
                Windows.Networking.Vpn.VpnPlugInProfile? fresh = null;
                var all = await ClashUWP.Vpn.ProfileStore.GetProfilesAsync();
                foreach (var p in all)
                    if (p is Windows.Networking.Vpn.VpnPlugInProfile pp && pp.ProfileName == probeName)
                        { fresh = pp; break; }

                if (fresh == null)
                {
                    sb.AppendLine("FreshRead=失败（GetProfilesAsync 找不到刚建的 profile）");
                    await LocalState.WriteTextAsync("dataface-v4.txt", sb.ToString());
                    return;
                }

                var conn = await ClashUWP.Vpn.ProfileStore.ConnectProfileAsync(fresh);
                sb.AppendLine($"Connect={conn}");

                // 插件 Connect 是同步回调（含 bridge 启动），ConnectAsync 返回时桥已就位；
                // 留 3 秒让状态机从 Connecting 走稳。
                await Task.Delay(3000);

                // 触发流量：TCP SYN 到 203.0.113.1。该段路由在 VPN 接口上，
                // SYN 包必然经 Encapsulate 进 mihomo bridge。对端不存在，
                // 连接失败是预期 —— 我们只要「包发出去了」这个事实。
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        using var tcp = new System.Net.Sockets.TcpClient();
                        var dial = tcp.ConnectAsync("203.0.113.1", 80);
                        await Task.WhenAny(dial, Task.Delay(1500));
                    }
                    catch { /* 连不上是预期 */ }
                }

                // 真实出网验证：8.8.8.8/32 路由在 VPN 接口上，国内直连必不通 ——
                // 握手能成功只有一条路：包进 mihomo → 规则 IP-CIDR 命中 ClashUWP-Auto
                // url-test 组 → 经有效节点代理出网。LocalFolder/config.yaml 缺失时
                // mihomo 用内置最小配置（MATCH direct），此时直连 8.8.8.8 大概率不通。
                bool egress = false;
                try
                {
                    using var probe = new System.Net.Sockets.TcpClient();
                    var dial = probe.ConnectAsync("8.8.8.8", 443);
                    egress = await Task.WhenAny(dial, Task.Delay(8000)) == dial && dial.IsCompletedSuccessfully;
                }
                catch { /* 握手失败 = 节点不可用或直连不通 */ }
                sb.AppendLine($"Egress_8_8_8_8_443={egress}（TCP 握手经 mihomo 节点代理）");

                // 应用层实际连通性：对 8.8.8.8:443 做完整 TLS 握手 + HTTP GET。
                // 8.8.8.8 的 443 是 Google Public DNS 的 HTTPS 端点（SNI dns.google），
                // 能拿到 200 + JSON Answer 才算「真实网络可用」，握手成功只证明包通。
                bool httpsOk = false;
                string httpsDetail = "";
                try
                {
                    using var socket = new Windows.Networking.Sockets.StreamSocket();
                    await socket.ConnectAsync(new Windows.Networking.HostName("8.8.8.8"), "443");
                    // 升级 TLS 并带 SNI/证书校验名：8.8.8.8 的 443 即 Google Public DNS HTTPS 端点。
                    await socket.UpgradeToSslAsync(
                        Windows.Networking.Sockets.SocketProtectionLevel.Tls12,
                        new Windows.Networking.HostName("dns.google"));

                    var writer = new Windows.Storage.Streams.DataWriter(socket.OutputStream);
                    writer.WriteString(
                        "GET /resolve?name=example.com&type=A HTTP/1.1\r\n" +
                        "Host: dns.google\r\nUser-Agent: ClashUWP-Probe\r\n" +
                        "Connection: close\r\n\r\n");
                    await writer.StoreAsync();
                    await writer.FlushAsync();

                    var reader = new Windows.Storage.Streams.DataReader(socket.InputStream);
                    var resp = new StringBuilder();
                    while (true)
                    {
                        var load = reader.LoadAsync(4096);
                        // IAsyncOperation 无 CancellationToken，轮询 Status 实现超时。
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        while (load.Status == Windows.Foundation.AsyncStatus.Started &&
                               sw.ElapsedMilliseconds < 8000)
                            await Task.Delay(100);
                        if (load.Status != Windows.Foundation.AsyncStatus.Completed)
                        { httpsDetail = "读响应超时"; break; }
                        uint read = load.GetResults();
                        if (read == 0) break; // 对端正常关闭
                        resp.Append(reader.ReadString(read));
                        if (resp.Length > 65536) break;
                    }

                    string text = resp.ToString();
                    httpsOk = text.StartsWith("HTTP/1.1 200") && text.Contains("\"Answer\"");
                    httpsDetail = text.Length > 0
                        ? text.Substring(0, Math.Min(140, text.Length)).Replace("\r\n", "\\r\\n")
                        : "空响应";
                }
                catch (Exception ex)
                {
                    httpsDetail = ex.GetType().Name + ": " + ex.Message;
                }
                sb.AppendLine($"HTTPS_dns_google={httpsOk}（TLS+HTTP 往返经 mihomo 节点）");
                sb.AppendLine($"HTTPS_detail={httpsDetail}");

                await Task.Delay(1500);

                var disc = await ClashUWP.Vpn.ProfileStore.DisconnectProfileAsync(fresh);
                sb.AppendLine($"Disconnect={disc}");

                // ★★ 「断开」与「删 profile」之间必须有一道<b>确认</b>闸，不能靠固定延时。
                //   DisconnectProfileAsync 返回 Ok 只代表请求被受理，不代表平台已经把会话收干净；
                //   2026-10-16 00:21 的整机断网就是在平台仍记 CONN=1 时删 profile 造成的 ——
                //   OS 直接扯接口，既不回调插件 Disconnect、也不清 NRPT/路由，
                //   留下根 Namespace 的孤儿 DNS 策略 + 死接口上的幽灵路由。
                //   原来这里写的是 <c>await Task.Delay(2000)</c>，"等两秒"在这种时序上什么也没保证。
                //   ⇒ 与主 profile 的收尾走同一道闸（GracefulTeardown），等不到就<b>不删</b>。
                (bool tornDown, string teardownReport) =
                    await ClashUWP.Vpn.GracefulTeardown.EnsureTornDownAsync(fresh);
                sb.AppendLine($"TeardownGate={tornDown} {teardownReport}");

                // ★ 闸没过就是"这台机器还可能被残留 NRPT/幽灵路由炸一次"，
                //   它必须出现在发布版日志里，而不是只躺在一次性的 dataface-v4.txt 里。
                if (!tornDown)
                    RollingLog.Warn("probe-teardown", $"收尾闸未确认，拒绝删 probe —— {teardownReport}");
                else
                    RollingLog.Info("probe-teardown", $"收尾闸确认通过，probe 将被自删 —— {teardownReport}");

                // 插件 Disconnect 是 fire-and-forget 写快照，给它一点落盘时间
                // （上面的闸至少要轮询两轮，正常情况下这份快照早就写完了，这里只是兜底）。
                await Task.Delay(500);

                if (await LocalState.TryGetFileAsync("dataface-plugin.txt") is Windows.Storage.StorageFile snap)
                {
                    sb.AppendLine("--- dataface-plugin.txt ---");
                    sb.Append(await LocalState.ReadTextAsync(snap));
                }
                else
                {
                    sb.AppendLine("PluginSnapshot=缺失（插件未触发 Disconnect 或写盘失败）");
                }

                // 跑完自删，不留残留 profile —— 但<b>只在闸确认收干净之后</b>才删。
                // 拒绝删除时宁可留下 ClashUWP-Dataface（应用下次启动的清扫路径会处理它），
                // 也不能在会话还挂着的时候删 —— 那才是"拆 VPN 拆出整机断网"的入口。
                if (tornDown)
                {
                    await ClashUWP.Vpn.ProfileStore.DeleteProfileAsync(fresh);
                    sb.AppendLine("删probe=完成");
                }
                else
                {
                    sb.AppendLine("删probe=拒绝（会话未确认收干净，删了会留孤儿 NRPT/幽灵路由）");
                }
                sb.AppendLine("Step=done");

                await LocalState.WriteTextAsync("dataface-v4.txt", sb.ToString());
            }
            catch (Exception ex)
            {
                // 探测主体任何一步炸了都要留一句话在 LocalState，
                // 否则外部只能靠「dataface-v4.txt 没出现」猜是没跑还是跑挂了。
                await LocalState.WriteTextQuietAsync("dataface.txt",
                    $"ERROR={ex.GetType().Name}: {ex.Message}");
                RollingLog.Error("probe-dataface", ex);
            }
        }
    }
}
