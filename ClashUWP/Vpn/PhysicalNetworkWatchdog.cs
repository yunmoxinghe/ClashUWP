using System;
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
    ///   前台那条兜底（当时的口径是"探物理直连的 114.114.114.114:443，连续 10 次失败即拆隧道"）
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
    /// 【探针为什么必须绕开隧道 —— ★【1.0.78】这条口径已订正】
    ///   旧写法是"单目标 <c>114.114.114.114:443</c>，10 次失败即拆"，理由写着
    ///   "该 IP 不在窄 include 里，所以探的就是物理网卡"。前半句对，后半句<b>被实测推翻</b>：
    ///   普通 TCP 走的是<b>操作系统默认路由</b>，任何第三方 VPN 抢走默认路由都会让它必然失败
    ///   （Docs/30 §8-B）；而这个目标连<b>干净直连</b>都不应答（Docs/32 §二）。
    ///   ⇒ 现在判据与动作都收进 <see cref="PhysicalNetworkProbe"/>：
    ///   多候选 OR + 平台自评背书，且<b>判据不可信时绝不动手、绝不删 profile</b>。
    ///
    /// 【★ 与前台那条的关系：不是替代，是并行】
    ///   前台那条<b>继续保留</b>（它能在 app 还活着时更快地收尾、且写的是 app.log）。
    ///   两条各自独立触发，<b>先动手的那个拆完，隧道就没了，另一条自然探不到失败</b>。
    ///   ⇒ 唯一要防的是"两边同时动手"，用 <see cref="Interlocked"/> 门闩处理。
    ///   ★ 1.0.78：两条的<b>判据</b>不再是两份实现，共用 <see cref="PhysicalNetworkProbe"/>，
    ///     免得哪天又漂移到两个口径上（这正是本轮事故的形状）。
    /// </summary>
    internal static class PhysicalNetworkWatchdog
    {
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
                $"启动：每 {IntervalSeconds}s 一轮，{MaxFailures} 轮全失败才动手；" +
                $"判据=多候选探针 OR 平台自评（前台同一条，共用实现）");

            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), token); }
                catch (TaskCanceledException) { return; }

                bool healthy;
                bool trusted;
                string detail;
                try
                {
                    (healthy, trusted, detail) = await PhysicalNetworkProbe.CheckAsync();
                }
                catch (Exception ex)
                {
                    // ★ 探针自己抛 = 判据瞎了。瞎了就不许动手（Docs/30 §8-B ②：
                    //   删 profile 的代价不对等，不能落在"探针不可信"的分支里）。
                    RollingLog.Warn("physical-watchdog-plugin",
                        $"本轮判定异常，按不可信处理：{ex.GetType().Name}: {ex.Message}");
                    return;
                }

                if (healthy)
                {
                    if (failures > 0)
                        RollingLog.Info("physical-watchdog-plugin", $"恢复：出口可达（之前连续失败 {failures} 轮）");
                    failures = 0;
                    continue;
                }

                if (!trusted)
                {
                    // ★ 1.0.78 的核心改法：判据不可信时<b>不动手也不计数</b>，
                    //   记一行原因后结束本条循环 —— 旧写法会把这种场景一路数到 10 次，
                    //   在第 5 分钟把隧道拆掉、把基础 profile 删掉（Docs/30 §8-B 的定时炸弹）。
                    RollingLog.Warn("physical-watchdog-plugin",
                        $"出口判为不可达但{detail} ⇒ 本条兜底解除，不做拆隧道/删 profile 动作");
                    await WriteSnapshotAsync(
                        $"RELIEVE {DateTime.Now:yyyy-MM-dd HH:mm:ss} 判据不可信，主动解除\r\n{detail}\r\n");
                    return;
                }

                failures++;
                RollingLog.Warn("physical-watchdog-plugin",
                    $"出口不可达，累计失败 {failures}/{MaxFailures}　{detail}");

                if (failures < MaxFailures) continue;

                // 门闩：前台那条看门狗可能同时判死 —— 只许一个动手拆。
                if (Interlocked.CompareExchange(ref _tripped, 1, 0) != 0)
                {
                    RollingLog.Info("physical-watchdog-plugin", "另一条看门狗已先动手，本条不再重复拆");
                    return;
                }

                // 动手前再确认一次（Docs/30 §8-B：动作落在"两个独立信号同时说死"的那一刻）：
                //   计数期间已经攒了 10 轮失败，但拆隧道 + 删 profile 是不可逆入口损失，
                //   所以这里要求<b>此刻</b>仍判不出可达、且判据仍然可信。
                bool stillDown = false;
                string recheck = "";
                try
                {
                    (bool h2, bool t2, string d2) = await PhysicalNetworkProbe.CheckAsync();
                    stillDown = !h2 && t2;
                    recheck = d2;
                }
                catch (Exception ex) { recheck = $"复核异常={ex.GetType().Name}"; }

                if (!stillDown)
                {
                    RollingLog.Info("physical-watchdog-plugin",
                        $"累计 {MaxFailures} 轮后复核撤回动手（判据不可信或已恢复）：{recheck}");
                    return;
                }

                RollingLog.Error("physical-watchdog-plugin",
                    $"连续 {MaxFailures} 轮物理出口不可达且复核一致 → 整机网络已坏，自动拆隧道（插件进程判死）");

                string report;
                try
                {
                    // ★ 走 GracefulTeardown：平台级断开 → 确认收干净 → 删 profile。
                    //   与 SessionWatchdog 判死时<b>完全同一条路</b>（不自己发明拆法）。
                    report = await GracefulTeardown.DisconnectThenDeleteAsync(
                        $"插件物理网络看门狗：连续 {MaxFailures} 轮不可达（复核一致）");
                }
                catch (Exception ex)
                {
                    report = $"收尾异常={ex.GetType().Name}: {ex.Message}";
                }

                RollingLog.Info("physical-watchdog-plugin", $"收尾动作：{report}");
                ClashVpnPlugin.LogSession($"evt=physical-watchdog-teardown {report}");
                await WriteSnapshotAsync($"TRIP {DateTime.Now:yyyy-MM-dd HH:mm:ss} reason=物理出口不可达\r\n{detail}\r\n复核：{recheck}\r\n{report}\r\n");
                return;
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
