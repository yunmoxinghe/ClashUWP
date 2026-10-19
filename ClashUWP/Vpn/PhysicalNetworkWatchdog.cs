using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Windows.Storage;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// ★【1.0.49】插件进程内的<b>整机物理网络兜底看门狗</b> ——
    /// 把前台 <c>HealthMonitor.PhysicalNetworkWatchdogAsync</c> 那条保护<b>搬进插件进程</b>。
    ///
    /// 【★ 为什么必须搬（这是本轮最实质的修复）】
    ///   前台那条兜底（探物理直连的 114.114.114.114:443，连续 10 次失败即拆隧道）
    ///   跑在 <b>UWP 前台进程</b>里。前台进程会被系统<b>挂起/节流/关闭</b>：
    ///   息屏、切到别的应用、内存压力、用户直接关窗口 —— 任意一条都会让那个
    ///   <c>while</c> 循环停下来（不是崩溃，是<b>不再被调度</b>）。
    ///   ⇒ <b>app 一挂起，"物理网卡断了但隧道还装着"这条兜底就没了</b>。
    ///   而这恰恰是最需要兜底的场景：隧道还在、系统却已经连不上物理网络，
    ///   用户看到的是<b>整机断网</b>，而没人去拆它。
    ///
    ///   ★ 同一份道理 <see cref="SessionWatchdog"/> 的类注释里已经写过一次
    ///   （"看门的人先掉线，等于没有看门"，run 30 那一轮隧道 00:12 就死了）——
    ///   那次搬的是"判隧道黑洞"，<b>这次补的是漏掉的那一条</b>。
    ///
    /// 【为什么插件进程是可靠宿主】
    ///   VPN 后台任务进程在<b>会话期间是活跃的</b>（数据面就在里面跑，
    ///   平台为了转发包会保活它），而且它<b>不随前台 app 的生命周期走</b>。
    ///   ⇒ 判死与拆隧道都放在这里，前台死掉不影响。
    ///
    /// 【探针为什么必须绕开隧道】
    ///   探的是 <b>114.114.114.114</b>（国内公共 DNS）。窄 include（<c>includeMode=narrow</c>）
    ///   下进隧道的只有 fake-ip 池 <c>198.18.0.0/16</c> 与隧道内 DNS 的两条 /32
    ///   ⇒ 114.114.114.114 <b>不在隧道里</b>，这条探针走的就是物理网卡。
    ///   这正是它能当"物理网络是否还活着"判据的原因（前台那条也是同一口径）。
    ///
    /// 【★ 与前台那条的关系：不是替代，是并行】
    ///   前台那条<b>继续保留</b>（它能在 app 还活着时更快地收尾、且写的是 app.log）。
    ///   两条各自独立触发，<b>先动手的那个拆完，隧道就没了，另一条自然探不到失败</b>。
    ///   ⇒ 唯一要防的是"两边同时动手"，用 <see cref="Interlocked"/> 门闩处理。
    /// </summary>
    internal static class PhysicalNetworkWatchdog
    {
        /// <summary>探针目标：国内公共 DNS，超时设 3 秒（慢网络上别卡太久）。</summary>
        private const string Target = "114.114.114.114";
        private const int TimeoutMs = 3000;
        private const int IntervalSeconds = 30;
        private const int MaxFailures = 10;

        private static CancellationTokenSource? _cts;

        /// <summary>门闩：两条看门狗（前台 + 插件）都指向同一个拆隧道动作，只许一个动手。</summary>
        private static int _tripped;

        /// <summary>起（可重复调用，重复即"重新武装"）。</summary>
        internal static void Start()
        {
            Stop("重新武装");
            var cts = new CancellationTokenSource();
            _cts = cts;
            Interlocked.Exchange(ref _tripped, 0);
            _ = Task.Run(() => LoopAsync(cts.Token));
        }

        /// <summary>停（会话结束 / 用户断开时调）。</summary>
        internal static void Stop(string why)
        {
            var cts = _cts;
            if (cts == null) return;
            _cts = null;
            try { cts.Cancel(); }
            catch { /* 取消本身不抛 */ }
            try { cts.Dispose(); }
            catch { }
            RollingLog.Info("physical-watchdog-plugin", $"停止（{why}）");
        }

        private static async Task LoopAsync(CancellationToken token)
        {
            int failures = 0;
            RollingLog.Info("physical-watchdog-plugin",
                $"启动：每 {IntervalSeconds}s 探 {Target}，{MaxFailures} 次失败即拆隧道（插件进程）");

            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), token); }
                catch (TaskCanceledException) { return; }

                string result;
                try { result = await TcpProbeAsync(Target, TimeoutMs); }
                catch (Exception ex) { result = $"{ex.GetType().Name}: {ex.Message}"; }

                if (result == "OK")
                {
                    if (failures > 0)
                        RollingLog.Info("physical-watchdog-plugin", $"恢复：{Target} 可达（之前连续失败 {failures} 次）");
                    failures = 0;
                    continue;
                }

                failures++;
                RollingLog.Warn("physical-watchdog-plugin",
                    $"{Target} 不可达（{result}），累计失败 {failures}/{MaxFailures}");

                if (failures < MaxFailures) continue;

                // 门闩：前台那条看门狗可能同时判死 —— 只许一个动手拆。
                if (Interlocked.CompareExchange(ref _tripped, 1, 0) != 0)
                {
                    RollingLog.Info("physical-watchdog-plugin", "另一条看门狗已先动手，本条不再重复拆");
                    return;
                }

                RollingLog.Error("physical-watchdog-plugin",
                    $"连续 {MaxFailures} 次物理网络不可达 → 整机网络已坏，自动拆隧道（插件进程判死）");

                string report;
                try
                {
                    // ★ 走 GracefulTeardown：平台级断开 → 确认收干净 → 删 profile。
                    //   与 SessionWatchdog 判死时<b>完全同一条路</b>（不自己发明拆法）。
                    report = await GracefulTeardown.DisconnectThenDeleteAsync(
                        $"插件物理网络看门狗：{Target} 连续 {MaxFailures} 次不可达");
                }
                catch (Exception ex)
                {
                    report = $"收尾异常={ex.GetType().Name}: {ex.Message}";
                }

                RollingLog.Info("physical-watchdog-plugin", $"收尾动作：{report}");
                ClashVpnPlugin.LogSession($"evt=physical-watchdog-teardown {report}");
                await WriteSnapshotAsync($"TRIP {DateTime.Now:yyyy-MM-dd HH:mm:ss} reason=物理网络不可达\r\n{report}\r\n");
                return;
            }
        }

        /// <summary>
        /// 纯 TCP 探针：只验证"能不能建连"，不读数据。
        /// ★ 必须 <c>UseProxy=false</c>？—— 这里用裸 <see cref="TcpClient"/>，不碰系统代理。
        /// </summary>
        private static async Task<string> TcpProbeAsync(string host, int timeoutMs)
        {
            try
            {
                using var client = new TcpClient();
                var connect = client.ConnectAsync(host, 443);
                var done = await Task.WhenAny(connect, Task.Delay(timeoutMs));
                if (done != connect) return "超时";
                if (!client.Connected) return "未连接";
                return "OK";
            }
            catch (Exception ex)
            {
                return $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        private static async Task WriteSnapshotAsync(string content)
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder
                    .CreateFileAsync("physical-watchdog-plugin.txt", CreationCollisionOption.OpenIfExists);
                await FileIO.WriteTextAsync(file, content);
            }
            catch
            {
                // 快照写不了不影响收尾本身。
            }
        }
    }
}
