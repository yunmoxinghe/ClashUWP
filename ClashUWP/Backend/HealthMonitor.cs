using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using ClashUWP.Vpn;

namespace ClashUWP.Backend
{
    /// <summary>
    /// 【后端 · 健康哨兵】对齐安卓版③「连不上自动退回直连」。
    ///
    /// 谁武装它：两条路 —— UI 的「连接 VPN」按钮走 <see cref="VpnSession"/>，
    /// debug 回路的 connect 命令走 <see cref="DebugCommandLoop"/>。
    /// 两者都必须先武装再连接：全路由隧道一旦黑洞就是整机断网，
    /// 哨兵是唯一把保护性交回直连的自动机制。
    ///
    /// ★ 它是后端而不是前端：判据、断开、删 profile、写 health.txt 全都不需要 UI，
    ///   而且必须能在没人盯着的时候自己收尾。
    /// </summary>
    internal static class HealthMonitor
    {
        private static CancellationTokenSource? _healthCts;

        /// <summary>
        /// 武装健康哨兵（connect 前调用；重复调用只保留最后一个）。
        /// ★ 调用方是 <see cref="VpnSession.ConnectAsync"/> 与
        ///   <see cref="DebugCommandLoop"/>，两条连接路径都必须先武装再连 ——
        ///   漏一条就等于那条路没有黑洞保护。
        /// </summary>
        internal static void Arm()
        {
            bool hadActive = _healthCts != null;
            Cancel();
            _healthCts = new CancellationTokenSource();
            _ = RunAsync(_healthCts.Token);

            // ★ 每次武装都留一行：哨兵是"黑洞时谁把网络交回直连"的唯一自动机制，
            //   事后必须能回答"这一次连接到底有没有保护"。旧写法靠人记，
            //   1.0.35 把武装收进 VpnSession 之后仍要用日志证明它真的发生了。
            RollingLog.Info("health-arm", hadActive ? "重新武装（顶掉上一个哨兵）" : "武装健康哨兵");
        }

        /// <summary>解除哨兵（手动断开即取消：连接已经不在了，没必要再探）。</summary>
        internal static void Cancel()
        {
            // ★ 本方法不记日志：<see cref="Arm"/> 第一句就是它，记了会在每次武装时
            //   多一条假的"已解除"。"谁解除了保护"这件事由调用方（VpnSession）记。
            _healthCts?.Cancel();
            _healthCts = null;

            // ★ 兜底安全垫也一起停掉：隧道都拆了，没必要再探物理网络。
            _physicalWatchdogCts?.Cancel();
            _physicalWatchdogCts = null;
        }

        private static CancellationTokenSource? _physicalWatchdogCts;

        /// <summary>
        /// 健康哨兵主体（对齐安卓版③「连不上自动退回」）。
        ///
        /// 【对照安卓】ClashMetaForAndroid 里<b>没有</b>隧道健康看门狗：它的「自动退回」
        ///   靠的是 <c>protect()</c> 逐 socket 绑定物理网卡 + 路由空洞 + 规则组兜底，
        ///   真正的兜底动作只有 <c>finally { tun.close(); stopSelf(); }</c>（进程/会话结束
        ///   就回收 VPN）。Windows 侧没有 <c>protect()</c>，等价物就是平台给的
        ///   <c>Ipv4ExclusionRoutes</c>（本次已下发节点 /32）+ <b>主动断开</b>：
        ///   隧道不健康时把网络交还给直连，这就是本方法存在的全部理由。
        ///
        /// 【判定口径】只认「国内 IP 直连不通」= 隧道黑洞。
        ///   探针选国内必达的 DoH 端点（223.5.5.5:443 / 119.29.29.29:443，
        ///   都是 IP 字面量 → 不依赖 DNS，避免把 DNS 故障误判成断网）；
        ///   两个都失败才算一轮失败，<b>连续两轮</b>才断开（抗抖动）。
        ///   DNS 探针（www.baidu.com）单独记录，不参与断开判定。
        ///   国外探针（8.8.8.8）只作为节点质量参考，失败绝不触发断开 ——
        ///   否则「节点慢」会被误判成「隧道坏了」，反复踢连接。
        ///
        /// ★ 兜底安全垫：即使隧道内探针全绿，只要<b>物理直连国内网络</b>连续
        ///   5 分钟不可达（每 30 秒探一次网关或 114.114.114.114，连续 10 次失败），
        ///   就认为整机网络已坏，自动拆隧道并退出。这条是最后防线，不影响现有逻辑。
        ///
        /// 【为什么断开后还要删 profile】哨兵收尾的会话都是「已经判死」的会话，
        ///   而只要 profile 还在，RasMan 就会无条件重拨（实测单日 413 次、
        ///   终止原因码 828 占 411 次、发起者 SYSTEM）。断开只停当下一次，
        ///   删 profile 才是拆掉自动重拨的燃料。代价：想再连必须先点「建/重建 profile」。
        ///
        /// 【为什么仍保留硬上限】黑洞判定依赖探针本身可信；一旦探针逻辑有 bug，
        ///   整机网络会被长期占住。硬上限是无条件兜底：到点必断，不依赖任何判断。
        ///
        /// ★ 1.0.15：上限从 <c>240 s</c>（两个重拨周期的实验时长）改回 <c>1800 s</c>。
        ///   240 s 是"先保住机器再用"的临时护栏，不是产品行为 —— 用户要的是连上之后
        ///   能一直用。真正防止"无限占网"的换成下面这条<b>重拨检测</b>：
        ///   插件每次被平台叫起来都会在 <c>LocalState\dataface-sessions.txt</c>
        ///   追加一行 <c>evt=connect-begin</c>，哨兵只数这个计数；我们只发起过 1 次连接，
        ///   所以计数 &gt; 基线+1 就是 RasMan 自己重拨了。实测 828 之后系统会立即重拨
        ///   （单日 413 次、间隔 97±2 s 就是这么来的），一旦看到第 2 次非我们发起的
        ///   connect，立刻断开 + 删 profile —— 判据驱动，不再靠计时器。
        /// </summary>
        private static async Task RunAsync(CancellationToken token)
        {
            const int intervalSeconds = 10;      // 每 10 秒一轮
            const int maxConsecutiveFailures = 2; // 连续 2 轮失败 → 判定黑洞
            const int hardCapSeconds = 1800;      // 无条件兜底 30 分钟（判据失灵时的保险丝）
            const int maxUnmanagedRedials = 1;    // 允许自愈 1 次，第 2 次非我们发起的 dial 就拆

            // ★ 兜底安全垫：物理直连国内网络不可达时自动退出。
            _physicalWatchdogCts?.Cancel(); // 停掉上一任
            _physicalWatchdogCts = new CancellationTokenSource();
            _ = PhysicalNetworkWatchdogAsync(_physicalWatchdogCts.Token);

            var sb = new StringBuilder();
            sb.AppendLine($"=== 健康哨兵 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");

            int failures = 0;
            string reason = "";
            var deadline = DateTime.Now.AddSeconds(hardCapSeconds);

            // ★ 基线 = 本哨兵被武装的时刻。之后每轮数「这之后出现过几次 connect-begin」，
            //   减掉我们自己那一次，剩下的就是 RasMan 自发的重拨。
            DateTime armedAt = DateTime.Now;
            sb.AppendLine($"基线时刻 {armedAt:HH:mm:ss}（只数这之后的 connect-begin，" +
                          $"允许非本次发起的重拨 {maxUnmanagedRedials} 次）");

            // ★ 前置确认：隧道真的建立了才进黑洞判定。没隧道时探针量到的是<b>直连</b>
            //   网络（国内 OK、国外超时、收尾 断开=NoConnection），会被读成
            //   「隧道健康但海外不通」——上一轮就是这么把整条排查带偏的。
            string st = "未知";
            for (int i = 0; i < 30; i++)
            {
                st = await VpnProfileSetup.OurStatusAsync();
                if (st == "Connected") break;
                try { await Task.Delay(1000, token); }
                catch (TaskCanceledException) { return; }
            }

            bool tunnelUp = st == "Connected";
            if (!tunnelUp)
                reason = $"隧道未建立（状态={st}）→ 探针只会量到直连网络，不做黑洞判定";
            else
                sb.AppendLine("隧道已连接，进入探测循环");

            // ★ 这一行决定后面所有探针读数的口径（有隧道=隧道内，无隧道=直连），
            //   所以必须进发布版日志：health.txt 里同样有，但它是追加式流水账，
            //   按时间轴对齐 app.log / plugin.log 时以滚动日志为准。
            RollingLog.Info("health", tunnelUp
                ? $"隧道已建立，进入黑洞判定循环（上限 {hardCapSeconds}s）"
                : $"隧道未建立（状态={st}），跳过黑洞判定");

            while (tunnelUp && DateTime.Now < deadline)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), token); }
                catch (TaskCanceledException) { return; } // 手动断开，正常退出

                string cn1 = await TcpProbeAsync("223.5.5.5", 6000);
                string cn2 = await TcpProbeAsync("119.29.29.29", 6000);
                string dns = await TcpProbeAsync("www.baidu.com", 6000);
                string overseas = await TcpProbeAsync("8.8.8.8", 6000); // 仅参考

                // ★ 重拨检测（占网保险丝，取代原来的 240 s 计时器）：
                //   武装之后出现的 connect-begin 减掉我们自己那一次，剩下的都是平台自发的。
                //   允许 1 次是刻意留的自愈空间：实测 828 后平台立刻重拨，第二段会话
                //   反而稳定（run 29-B/29-C 各活到哨兵上限），一刀切会让用户白掉线；
                //   再多一次就说明进入了"97 s 一拆一拨"的死循环，立刻拆干净。
                int connects = await CountRecentConnectsAsync(armedAt);
                int unmanaged = connects - 1;

                bool roundOk = cn1 == "OK" || cn2 == "OK";
                failures = roundOk ? 0 : failures + 1;
                sb.AppendLine($"{DateTime.Now:HH:mm:ss} 国内[{cn1}|{cn2}] DNS[{dns}] 国外[{overseas}]" +
                              $" 非本次dial={Math.Max(unmanaged, 0)}" +
                              $" → {(roundOk ? "隧道健康" : $"不健康 x{failures}")}");

                if (unmanaged > maxUnmanagedRedials)
                {
                    reason = $"检测到 {unmanaged} 次非本次发起的 connect（阈值 {maxUnmanagedRedials}）" +
                             $"→ 平台在自动重拨，立即拆掉";
                    break;
                }

                if (failures >= maxConsecutiveFailures)
                { reason = $"连续 {failures} 轮国内不可达 → 隧道黑洞，自动退回直连"; break; }
            }

            if (reason.Length == 0)
                reason = $"到达硬上限 {hardCapSeconds}s（无条件兜底）→ 断开";

            // ★ 哨兵主动动手 = 用户网络被交回直连的那一刻。这一行是"为什么我的 VPN
            //   自己断了"的第一现场，必须能在发布版里查到。
            RollingLog.Warn("health-trigger", reason);

            string disc;
            try { disc = await VpnProfileSetup.DisconnectAsync(); }
            catch (Exception ex) { disc = $"断开异常: {ex.GetType().Name}: {ex.Message}"; }

            // ★ 断完立刻删 profile：不给 RasMan 留可重拨的对象（详见方法上的说明）。
            string drop;
            try { drop = await VpnProfileSetup.DropProfileAsync(); }
            catch (Exception ex) { drop = $"删 profile 异常: {ex.GetType().Name}: {ex.Message}"; }

            RollingLog.Info("health-trigger", $"收尾动作：{disc}，{drop}");

            sb.AppendLine($"{reason}\n断开={disc}\n删profile={drop}");
            try
            {
                // health.txt 是流水账（追加），debug-out.txt 是"最后一条命令的结果"（覆盖）——
                // 让外部无论用哪条路径都能看到哨兵为什么动手。
                await LocalState.AppendTextAsync("health.txt", sb + "\r\n");
                await LocalState.WriteTextAsync("debug-out.txt",
                    $"cmd=health-monitor-auto-disconnect\ntime={DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{sb}");
            }
            catch (Exception ex)
            {
                // 哨兵已经动过手了，结果文件写不写得了不再影响网络 —— 但要在滚动日志里
                // 留一句，免得将来对着"health.txt 里最后一次还是三小时前"发懵。
                RollingLog.Error("health-write", ex);
            }
        }

        /// <summary>
        /// ★ 兜底安全垫：物理直连国内网络不可达时自动退出隧道。
        ///
        /// 【为什么要】健康哨兵的主探针走的是<b>隧道内</b>的 TCP 握手（223.5.5.5:443 等），
        ///   如果隧道本身还活着但底层物理网卡已经断网，主探针会一直读到"OK"（因为流量
        ///   被黑洞吞掉后平台可能仍返回连接成功假象）。这条独立探针直接探物理网络的网关或
        ///   114.114.114.114，绕开隧道，连续 10 次失败（5 分钟）就拆隧道并记 Error。
        ///
        /// 【为什么选 114.114.114.114】它是国内公共 DNS，NRPT 排除路由通常会放行它；
        ///   即使 NRPT 没配好，探的是物理直连而非隧道，所以结果可信。超时设 3 秒，
        ///   避免在慢网络上卡太久。
        /// </summary>
        private static async Task PhysicalNetworkWatchdogAsync(CancellationToken token)
        {
            const string target = "114.114.114.114";
            const int timeoutMs = 3000;
            const int intervalSeconds = 30;
            const int maxFailures = 10;

            int failures = 0;
            RollingLog.Info("physical-watchdog", $"启动：每 {intervalSeconds}s 探 {target}，{maxFailures} 次失败即拆隧道");

            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), token); }
                catch (TaskCanceledException) { return; }

                string result = await TcpProbeAsync(target, timeoutMs);
                if (result == "OK")
                {
                    if (failures > 0)
                        RollingLog.Info("physical-watchdog", $"恢复：{target} 可达（之前连续失败 {failures} 次）");
                    failures = 0;
                }
                else
                {
                    failures++;
                    RollingLog.Warn("physical-watchdog", $"{target} 不可达 ({result})，累计失败 {failures}/{maxFailures}");

                    if (failures >= maxFailures)
                    {
                        RollingLog.Error("physical-watchdog",
                            $"连续 {maxFailures} 次物理网络不可达 → 整机网络已坏，自动拆隧道");

                        // 停掉主哨兵，避免两边同时动手
                        Cancel();

                        string disc;
                        try { disc = await VpnProfileSetup.DisconnectAsync(); }
                        catch (Exception ex) { disc = $"断开异常: {ex.GetType().Name}: {ex.Message}"; }

                        string drop;
                        try { drop = await VpnProfileSetup.DropProfileAsync(); }
                        catch (Exception ex) { drop = $"删 profile 异常: {ex.GetType().Name}: {ex.Message}"; }

                        RollingLog.Error("physical-watchdog", $"收尾动作：{disc}，{drop}");
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// 数 <c>LocalState\dataface-sessions.txt</c> 里<b>晚于</b> <paramref name="since"/>
        /// 的 <c>evt=connect-begin</c> 行数（插件每次被平台叫起来追加一行，见该方法注释）。
        ///
        /// 【为什么按时间筛而不是"记基线再数总量"】日志是追加式、跨会话累积的。
        ///   若先读基线、之后某次读失败返回 0，下一轮数到的却是今天全部历史，
        ///   差值会被误读成"平台疯狂重拨"，把用户刚连好的隧道一脚拆掉 —— 这类
        ///   "保险丝自己炸"的错误比漏判严重得多。按时间戳筛之后，历史行自然被排除，
        ///   读失败只是这一轮少看一次，下一轮 10 s 后补上。
        ///
        /// 读不到返回 0（文件不存在 / 正被追加占住），绝不影响断开判定本身。
        /// </summary>
        private static async Task<int> CountRecentConnectsAsync(DateTime since)
        {
            try
            {
                Windows.Storage.StorageFile? file =
                    await LocalState.TryGetFileAsync("dataface-sessions.txt");
                if (file == null) return 0;

                string raw = await LocalState.ReadTextAsync(file);
                int n = 0;
                foreach (string line in raw.Split('\n'))
                {
                    if (!line.Contains("evt=connect-begin")) continue;
                    if (line.Length < 19) continue;
                    if (!DateTime.TryParseExact(line.Substring(0, 19), "yyyy-MM-dd HH:mm:ss",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out DateTime ts)) continue;
                    if (ts >= since) n++;
                }
                return n;
            }
            catch (Exception ex)
            {
                // ★ 读失败必须留痕：这条计数就是"平台自动重拨"保险丝的眼睛，
                //   它瞎了却不出声 ⇒ 该拆的死循环拆不了。返回 0 的语义没变，
                //   变的只是"瞎"这件事本身可见。
                RollingLog.Error("health-sessions-read", ex);
                return 0;
            }
        }

        /// <summary>
        /// TCP 握手探针：成功返回 "OK"，超时/失败返回原因摘要。
        /// <paramref name="host"/> 可以是 IP 字面量（不依赖 DNS）或域名（顺带测隧道 DNS）。
        /// 固定探 443 端口。
        /// </summary>
        private static async Task<string> TcpProbeAsync(string host, int timeoutMs)
        {
            try
            {
                using var socket = new Windows.Networking.Sockets.StreamSocket();
                var conn = socket.ConnectAsync(
                    new Windows.Networking.HostName(host), "443").AsTask(CancellationToken.None);
                if (await Task.WhenAny(conn, Task.Delay(timeoutMs)) == conn)
                    return "OK";
                return "超时";
            }
            catch (Exception ex)
            {
                return $"{ex.GetType().Name}";
            }
        }
    }
}
