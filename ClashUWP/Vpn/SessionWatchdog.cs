using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Networking.Vpn;
using Windows.Storage;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// 插件进程内的会话看门狗（1.0.16 新增；1.0.17 补上整机口径的 fake-ip 判据）。
    ///
    /// 【为什么必须搬进插件进程】run 30 那一轮隧道 00:12 就死了，而判定和断开都跑在
    ///   <b>前台 UWP 进程</b>里 —— UWP 前台进程会被生命周期挂起/节流：那一轮
    ///   <c>health.txt</c> 始终没写入本轮块、<c>debug-out.txt</c> 也没人应答，
    ///   两个进程都还活着（就是挂起态的表现）。<b>看门的人先掉线，等于没有看门。</b>
    ///   VPN 后台任务进程在会话期间是活跃的（数据面就在里面跑），所以判死人交给它。
    ///
    /// 【判据一 · 隧道黑洞】每 10 s 探一次<b>隧道内</b> DNS 服务器的 443（IP 字面量，
    ///   不依赖解析）。这些 IP 在 narrow 下有 /32 inclusion、在 wide 下被 0/0 覆盖，
    ///   所以探到的一定是<b>隧道</b>而不是物理网卡。连续两轮全失败才判死，抗抖动。
    ///   ★ 前提：<c>dnsInTunnel=1</c>。若 <c>dnsInTunnel=0</c> 且 <c>includeMode=narrow</c>，
    ///   这些 IP 走物理网卡，探它们量不到隧道 —— 这种组合下本判据<b>主动弃权</b>
    ///   （写 <c>probe=弃权(...)</c> 明说），只留重拨保险丝和硬上限，绝不假装看得见。
    ///   ★★ 这条判据单独用<b>不够</b>，见下面判据二（run 31 实测到的盲区）。
    ///
    /// 【判据二 · 整机口径的 fake-ip（1.0.17 新增，run 31 逼出来的）】
    ///   run 31 的形状：会话跑到 t+234 s 宿主<b>全死</b>（google/baidu/223.5.5.5 一律 000、
    ///   宿主 <c>Resolve-DnsName www.google.com</c> 返回空），而本看门狗<b>每轮都报 OK</b>，
    ///   <c>encap</c> 冻结在 7188。判据一看不见它，因为它探的 223.5.5.5 <b>物理网卡也照样能到</b>：
    ///   隧道一塌、流量退回直连，这一路探针自然还是绿的 —— 探针量的是"这个 IP 可达"，
    ///   不是"别的应用还在走我的隧道"。和 run 30 那次"整机没了但没人断开"是同一件事。
    ///   所以换成一个<b>只有隧道活着才可能成立</b>的判据：让系统解析器查
    ///   <c>www.google.com</c>，答案必须落在<b>本次生效的</b>假 IP 池里
    ///   （默认 <c>198.18.0.0/16</c>；1.0.40 起可被 <c>dns-config.txt</c> 改，
    ///   所以判据吃的是调用方传进来的那份 CIDR，不写死）。
    ///   直连解析拿的是污染真 IP（<c>2001::1</c> / <c>174.132.167.252</c>，23:59 已复测），
    ///   隧道 DNS 挂了则解析报错或超时 —— 两种坏形状都会被判 NO。
    ///   ★ 弃权条件有两个，都必须写进日志而不是猜：
    ///     · <c>dnsInTunnel=0</c>：解析请求不进隧道，fake-ip 无从出现；
    ///     · <c>selfSplitTunnel!=0</c>：本包（含本插件进程）被 filter 拆到物理网卡，
    ///       那<b>我们自己</b>就不是宿主应用的代表，拿自己的解析结果判宿主的死活是错的。
    ///   ★ 另有 30 s 宽限期（<c>FakeIpSettleSeconds</c>）：DNS 策略是连接结果提交时才装上的，
    ///     头两轮量到"还没生效"不该判死 —— 误拆一条会好的隧道，代价是用户白掉线。
    ///
    /// 【判据三 · 内核控制器】GET <c>127.0.0.1:9090/version</c>。
    ///   ★ 只记录、<b>不</b>参与判死。两条理由都来自实测：
    ///     · 控制器就活在<b>本进程</b>里（mihomo 桥在插件进程内），所以它证明不了"出网通"，
    ///       只能证明"桥的 HTTP 监听还活着" —— 拿它判死等于拿自己量自己；
    ///     · Docs/08 §10 记录过本包回环 REST 在某些臂里变成"积极拒绝"，
    ///       这个失败与隧道死活无关，做判据会把好隧道拆掉。
    ///   它的用途是<b>归因</b>：判据一/二同时变 NO 时，「内核没了」和「路由断了」
    ///   靠这一列分开。
    ///
    /// 【判据四 · 重拨保险丝】插件每次被平台叫起来都往 <c>dataface-sessions.txt</c>
    ///   追加一行 <c>evt=connect-begin</c>。我们只发起过一次连接，所以武装之后
    ///   数到 &gt;1 次就说明平台在自发重拨；允许自愈 1 次（实测 828 后平台立刻重拨，
    ///   第二段会话反而稳定），再多一次就是"97 s 一拆一拨"的风暴形状，立刻拆干净。
    ///
    /// 【判据五 · 硬上限】无条件兜底 30 分钟。前几条都依赖判据本身可信，
    ///   一旦探针有 bug，整机的网络会被长期占住 —— 到点必断，不依赖任何判断。
    ///
    /// 【触发之后做什么】只走 <see cref="GracefulTeardown"/>：平台级断开 →
    ///   <b>等会话确认收干净</b> → 才删 profile → 残留自检。
    ///   ★ 不用 <c>channel.Stop()</c>：官方语义是"回应平台的断开请求"（sample 只在
    ///   <c>IVpnPlugIn.Disconnect</c> 里调），插件自发调它属于超出文档用法，
    ///   猜错的代价就是我们刚刚修掉的那两类残留 + RasMan 重拨风暴。
    ///   这里宁可慢一点、多花 20 s 等收尾，也不再把不确定的调用发给平台。
    /// </summary>
    internal static class SessionWatchdog
    {
        private const int IntervalSeconds = 10;
        private const int MaxConsecutiveFailures = 2;
        private const int HardCapSeconds = 1800;
        private const int MaxUnmanagedRedials = 1;
        private const int ProbeTimeoutMs = 3000;
        private const int DnsTimeoutMs = 4000;
        private const int ControllerTimeoutSeconds = 4;

        /// <summary>fake-ip 探针查的域名：境外、且订阅的 fake-ip 规则一定命中它。</summary>
        private const string FakeIpProbeName = "www.google.com";

        /// <summary>
        /// 判据二的<b>宽限期</b>（秒）：会话开始前 30 s 内不因 fake-ip 缺失判死。
        /// 平台是在连接结果提交时才把 DNS 策略/路由装好的，头两轮完全可能量到"还没生效"，
        /// 而误拆一条本来会好的隧道，代价是用户白掉线 + 一次实验作废。
        /// 真故障的形状（run 31）出现在 t+234 s，30 s 宽限不影响抓它。
        /// </summary>
        private const int FakeIpSettleSeconds = 30;

        private static CancellationTokenSource? _cts;
        /// <summary>只允许一次触发的门闩（0=未触发，1=已触发）。</summary>
        private static int _tripped;

        /// <summary>
        /// 武装本轮看门狗；重复调用会先停掉上一轮。
        /// ★【1.0.40 · DNS 可配】<paramref name="fakeIpCidr"/> 与 <paramref name="nrptDns"/>
        ///   必须是<b>本次连接实际生效</b>的那一份（由 <see cref="ClashVpnPlugin"/> 计算）：
        ///   假 IP 池可被 <c>dns-config.txt: fake-ip-range</c> 改，NRPT 服务器可被
        ///   <c>nrpt-dns</c> 改 —— 看门狗若仍按内置 198.18/16 与 profile 默认判，
        ///   会把一条好隧道判死（改池子）或探一个没人应答的旧 DNS（改服务器）。
        /// </summary>
        internal static void Start(VpnChannel channel,
                                   DateTime connectBeganAt, ClashVpnPlugin owner,
                                   string fakeIpCidr, IList<string> nrptDns)
        {
            Stop("重新武装");
            if (!LocalSettings.SessionWatchdog)
            {
                Log("evt=watchdog-off reason=settings sessionWatchdog=0（本轮不判黑洞，只剩前台哨兵与外部 guard）");
                RollingLog.Warn("watchdog", "按设置关闭：本轮插件侧不判黑洞，只剩前台哨兵与外部 guard");
                return;
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            Interlocked.Exchange(ref _tripped, 0);
            _ = Task.Run(() => LoopAsync(channel, connectBeganAt, owner,
                                         fakeIpCidr, nrptDns, cts.Token));
        }

        internal static void Stop(string why)
        {
            var cts = _cts;
            if (cts == null) return;
            _cts = null;
            try { cts.Cancel(); } catch { }
            try { cts.Dispose(); } catch { }
            Log($"evt=watchdog-stop why={why}");
        }

        private static async Task LoopAsync(VpnChannel channel,
                                            DateTime connectBeganAt, ClashVpnPlugin owner,
                                            string fakeIpCidr, IList<string> nrptDns,
                                            CancellationToken token)
        {
            int seq = 0;
            int failures = 0;
            int dnsFailures = 0;
            string tunnelProbes = TunnelProbeTargets(nrptDns);
            string? trip = null;

            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), token); }
                catch (OperationCanceledException) { return; }
                if (token.IsCancellationRequested) return;

                seq++;
                double alive = owner.SessionSeconds();

                // ── 判据一 · 隧道数据面可达（探"只能经隧道才可达"的地址）──────────
                string probeNote;
                if (tunnelProbes.Length == 0)
                {
                    // 弃权：当前 include/dns 组合下没有"只能经隧道才可达"的地址可探。
                    probeNote = "弃权(dnsInTunnel=0 且 narrow：候选 IP 都走物理网卡，量不到隧道)";
                    failures = 0;
                }
                else
                {
                    var parts = new List<string>();
                    bool any = false;
                    foreach (string ip in tunnelProbes.Split(','))
                    {
                        string r = await TcpProbeAsync(ip);
                        parts.Add($"{ip}={r}");
                        if (r == "OK") any = true;
                    }
                    probeNote = string.Join("|", parts);
                    failures = any ? 0 : failures + 1;
                }

                // ── 判据二 · ★ 整机口径：系统解析还在不给 fake-ip ──────────────────
                //   判据一的盲区（探的 IP 物理网卡也到得了），详见类注释里那一段 run 31 取证。
                string fakeNote;
                if (!LocalSettings.DnsIntoTunnel)
                {
                    fakeNote = "fake-ip=弃权(dnsInTunnel=0)";
                    dnsFailures = 0;
                }
                else if (LocalSettings.SelfSplitTunnel != 0)
                {
                    // 本进程被 traffic filter 拆到物理网卡 ⇒ 我们的解析结果代表不了宿主。
                    fakeNote = "fake-ip=弃权(selfSplitTunnel=" +
                               LocalSettings.SelfSplitTunnel + "：本包走物理网卡，非宿主代表)";
                    dnsFailures = 0;
                }
                else
                {
                    string fip = await FakeIpProbeAsync();
                    // ★【1.0.40】按本次生效的池判，不再硬写 198.18.（池子可被 dns-config 改）。
                    bool ok = DnsConfig.InCidr(fip, fakeIpCidr);
                    fakeNote = $"fake-ip={(ok ? "OK" : "NO")}:{fip} pool={fakeIpCidr}";
                    dnsFailures = ok ? 0 : dnsFailures + 1;
                }

                // ── 判据三 · 内核控制器（只记录、不判死，理由见类注释）─────────────
                string ctrl = await ControllerProbeAsync();

                int connects = await CountConnectsSinceAsync(
                    connectBeganAt.AddSeconds(-2));
                int unmanaged = Math.Max(connects - 1, 0);

                string round = $"{seq} alive={alive:F0}s {probeNote} {fakeNote} 内核={ctrl} " +
                               $"failStreak={failures} dnsStreak={dnsFailures} " +
                               $"dials={connects} 非本次={unmanaged} {owner.DatafaceCounters()}";

                Log("evt=hb " + round);
                await WriteSnapshotAsync(
                    $"last={DateTime.Now:yyyy-MM-dd HH:mm:ss} {round}\r\n");

                if (unmanaged > MaxUnmanagedRedials)
                {
                    trip = $"{unmanaged} 次非本次发起的 connect（阈值 {MaxUnmanagedRedials}）→ 平台在自动重拨";
                    break;
                }
                if (failures >= MaxConsecutiveFailures)
                {
                    trip = $"连续 {failures} 轮隧道内 DNS 不可达 → 隧道黑洞";
                    break;
                }
                if (dnsFailures >= MaxConsecutiveFailures && alive > FakeIpSettleSeconds)
                {
                    // ★ 这条就是 run 31 里"整机已经没了、判据一却一路绿灯"的那道补眼。
                    trip = $"连续 {dnsFailures} 轮系统解析不再返回 fake-ip（最后一轮 {fakeNote}）" +
                           $"→ 隧道的 DNS 侧已断，宿主应用不再经隧道，判死";
                    break;
                }
                if (alive > HardCapSeconds)
                {
                    trip = $"到达硬上限 {HardCapSeconds}s（无条件兜底）";
                    break;
                }
            }

            if (trip == null) return;

            // 门闩：Disconnect 回调可能同时把这里 Stop 掉，但已经决定的收尾要继续跑完。
            if (Interlocked.CompareExchange(ref _tripped, 1, 0) != 0) return;

            Log($"evt=watchdog-trip reason={trip}");
            // ★ 看门狗动手 = 插件自己把这条隧道拆了。这是"隧道为什么突然没了"的
            //   第一责任现场，必须能在发布版的 plugin.log 里直接读到原因。
            RollingLog.Warn("watchdog-trip", trip);
            string report;
            try
            {
                report = await GracefulTeardown.DisconnectThenDeleteAsync($"插件看门狗：{trip}");
            }
            catch (Exception ex)
            {
                report = $"收尾异常={ex.GetType().Name}: {ex.Message}";
            }
            Log($"evt=watchdog-teardown {report}");
            RollingLog.Info("watchdog-teardown", report);
            await WriteSnapshotAsync($"TRIP {DateTime.Now:yyyy-MM-dd HH:mm:ss} reason={trip}\r\n{report}\r\n");
            try { channel.LogDiagnosticMessage("ClashUWP: 看门狗收尾 " + report); } catch { }
        }

        /// <summary>
        /// 能代表隧道的探针地址 = 本次下发进隧道的 DNS 服务器（IPv4）。
        /// 返回空串 = 弃权（见类注释）。
        /// </summary>
        private static string TunnelProbeTargets(IList<string> nrptDns)
        {
            if (!LocalSettings.DnsIntoTunnel)
                return "";
            var v4 = new List<string>();
            foreach (string dns in nrptDns)
                if (dns.IndexOf(':') < 0)
                    v4.Add(dns);
            return string.Join(",", v4);
        }

        private static async Task<string> TcpProbeAsync(string host)
        {
            try
            {
                using var socket = new StreamSocket();
                var conn = socket.ConnectAsync(new HostName(host), "443")
                    .AsTask(CancellationToken.None);
                return await Task.WhenAny(conn, Task.Delay(ProbeTimeoutMs)) == conn ? "OK" : "超时";
            }
            catch (Exception ex)
            {
                return ex.GetType().Name;
            }
        }

        /// <summary>
        /// fake-ip 探针：让<b>系统解析器</b>查 <see cref="FakeIpProbeName"/>，把第一个 IPv4
        /// 答案原样带回来（没有 IPv4 就带回第一个答案）。
        ///
        /// 【为什么用 <c>System.Net.Dns</c> 而不是 WinRT 的解析接口】
        ///   要的就是"和别的应用完全相同的一条路"：NRPT → 隧道 DNS → 内核 fake-ip。
        ///   <c>GetHostAddressesAsync</c> 底下是 GetAddrInfoExW，和宿主侧
        ///   <c>Resolve-DnsName</c> 看到的是同一份答案，Native AOT 下也可用。
        ///
        /// 【返回值口径】
        ///   · 正常 → 地址字符串（隧道活着时应落在生效的假 IP 池里；退回直连时是污染真 IP）
        ///   · 无记录 → <c>解析失败:NoData</c> / <c>NoHostKnown</c>
        ///   · 卡住 → <c>解析超时</c>
        ///   ★ 超时和失败<b>都算 NO</b>：run 31 现场宿主的 <c>Resolve-DnsName</c> 就是空返回，
        ///   这两种形状都是要抓的故障，不能当"探针没测到"放过。
        ///   <c>Task.WhenAny</c> 之后不再 await 那条尾巴（解析已完成才取值）——
        ///   心跳不能被供应商的慢响应卡死。
        /// </summary>
        private static async Task<string> FakeIpProbeAsync()
        {
            try
            {
                var pending = System.Net.Dns.GetHostAddressesAsync(FakeIpProbeName);
                if (await Task.WhenAny(pending, Task.Delay(DnsTimeoutMs)) != pending)
                    return "解析超时";

                System.Net.IPAddress[] addrs = await pending;
                if (addrs.Length == 0) return "解析空返回";

                foreach (System.Net.IPAddress a in addrs)
                    if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        return a.ToString();
                return addrs[0].ToString();
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                return $"解析失败:{ex.SocketErrorCode}";
            }
            catch (Exception ex)
            {
                return $"解析异常:{ex.GetType().Name}";
            }
        }

        /// <summary>
        /// 内核控制器探：<c>GET http://127.0.0.1:9090/version</c>，只把版本号截出来。
        /// ★ 只进日志、<b>不</b>参与判死（判据三的三条理由在类注释里）。
        ///
        /// 用法与 <c>ClashVpnPlugin.TryHotReload</c> 一致：<c>UseProxy=false</c> 必须写 ——
        /// 系统代理正是自家内核在听，开着代理会让这个请求绕回隧道，自激。
        /// 每轮新建一个 client：10 s 才一次，不值得维护连接池，还能避开 keep-alive 卡尾。
        /// </summary>
        private static async Task<string> ControllerProbeAsync()
        {
            try
            {
                using var http = new System.Net.Http.HttpClient(
                    new System.Net.Http.HttpClientHandler { UseProxy = false });
                http.Timeout = TimeSpan.FromSeconds(ControllerTimeoutSeconds);

                string json = await http.GetStringAsync("http://127.0.0.1:9090/version");
                const string key = "\"version\":\"";
                int i = json.IndexOf(key, StringComparison.Ordinal);
                if (i < 0) return "OK(无版本字段)";
                int s = i + key.Length;
                int e = json.IndexOf('"', s);
                return e > s ? "OK:" + json.Substring(s, Math.Min(e - s, 20)) : "OK";
            }
            catch (OperationCanceledException) { return "超时"; }
            catch (Exception ex) { return ex.GetType().Name; }
        }

        /// <summary>
        /// 数 <c>dataface-sessions.txt</c> 里晚于 <paramref name="since"/> 的
        /// <c>evt=connect-begin</c> 行数；读不到返回 0（宁可漏判一次，也不能把
        /// 用户刚连好的隧道误拆 —— 详见前台哨兵同名方法的注释）。
        /// </summary>
        private static async Task<int> CountConnectsSinceAsync(DateTime since)
        {
            try
            {
                var item = await ApplicationData.Current.LocalFolder.TryGetItemAsync("dataface-sessions.txt");
                if (item is not StorageFile file) return 0;
                string raw = await FileIO.ReadTextAsync(file);
                int n = 0;
                foreach (string line in raw.Split('\n'))
                {
                    if (!line.Contains("evt=connect-begin") || line.Length < 19) continue;
                    if (!DateTime.TryParseExact(line.Substring(0, 19), "yyyy-MM-dd HH:mm:ss",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime ts)) continue;
                    if (ts >= since) n++;
                }
                return n;
            }
            catch { return 0; }
        }

        private static void Log(string line) => ClashVpnPlugin.LogSession(line);

        private static async Task WriteSnapshotAsync(string content)
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder
                    .CreateFileAsync("watchdog.txt", CreationCollisionOption.OpenIfExists);
                await FileIO.WriteTextAsync(file, content);
            }
            catch { /* 诊断写不了就不写 */ }
        }
    }
}
