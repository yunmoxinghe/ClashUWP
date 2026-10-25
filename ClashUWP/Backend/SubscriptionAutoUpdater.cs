using System;
using System.Threading;
using System.Threading.Tasks;

using ClashUWP.Vpn;

namespace ClashUWP.Backend
{
    /// <summary>
    /// ★【1.0.119 · 订阅定时自动更新】按间隔重新拉取<b>当前生效那份</b>订阅的执行环。
    ///
    /// 【判据不在这里】"该不该动手"整段是纯函数 <see cref="SubscriptionAutoPolicy"/>，
    ///   离线夹具钉着（默认关、认不出的档位当关、失败退避、连着 VPN 也照样动手）。
    ///   本类只做三件事：读现值 → 问判据 → 动手并记账。
    ///
    /// 【★ 只在"应用开着"的时候跑，这一点不装成后台服务】
    ///   本项目没有注册任何后台任务，这个环挂在 <see cref="Diagnostics.StartOnce"/> 上，
    ///   进程退出即止。所以它的真实语义是"<b>你开着应用的这段时间里，到点了就顺手拉一次</b>"，
    ///   不是"每天固定时刻更新"。界面那行小字必须写「下次」而不是保证一个时刻 ——
    ///   把"应用没开所以没跑"伪装成"已经更新过了"是这一类功能最常见的谎。
    ///   ★ 更要紧的一层：UWP 被<b>挂起</b>时整个进程冻住，<c>Task.Delay</c> 跟着停表，
    ///   所以"开着应用"严格说是"处于恢复态"。被挂起期间到点不会跑，恢复后的下一拍才跑
    ///   （不是错过就作废：判据读的是 mtime 与现在时刻，不读"第几拍"）。
    ///
    /// 【为什么用 fetchedAt（文件 mtime）而不是另开一个时间戳文件】
    ///   <c>subscriptions/{id}.yaml</c> 的写入时刻<b>就是</c>"上次拉成功"的时刻，
    ///   而且 <c>SubscriptionManager.ListAsync</c> 已经在读它。再开一个
    ///   <c>lastFetch.txt</c> 会出第二种真相：写失败、或程序在写完 yaml 之前崩，
    ///   两个文件就永久分叉。口径同 <c>Profile.FetchedAt</c>。
    ///
    /// 【连续失败只在内存里计】落盘要往 <c>settings.txt</c> 追加，而那个文件是
    ///   <b>追加胜出</b>的语义，把易变的失败计数写进去会让它只增不减；
    ///   重启后清零的后果只是"多试一次"，可接受。
    /// </summary>
    internal static class SubscriptionAutoUpdater
    {
        private static int _started;
        private static int _failStreak;

        /// <summary>
        /// 当前连续失败次数（界面那句"已退避"要读它）。★ 只暴露读、不暴露写：
        /// 计数归零只有一种方式 —— 真的拉成功一次。
        /// </summary>
        internal static int FailStreak => _failStreak;

        /// <summary>上次判据的结论文本（画在订阅页那行小字上，让用户看得见"为什么没跑"）。</summary>
        internal static string LastNote = "还没检查";

        /// <summary>幂等启动：重复调用只有一次生效。</summary>
        internal static void StartOnce()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            _ = LoopAsync();
        }

        private static async Task LoopAsync()
        {
            // ★ 首拍先等一个检查节拍再动手：StartOnce 发生在第一次渲染时，
            //   那时订阅清单还没读上来（下面等的就是这份清单），第一拍必然只能 Skip，
            //   白记一行日志不如不记（滚动日志的行格式是热路径契约，见 Docs/32）。
            while (true)
            {
                await Task.Delay(SubscriptionAutoPolicy.CheckEvery);
                try
                {
                    await TickOnceAsync();
                }
                catch (Exception ex)
                {
                    // 环不能死：这一支一死，"自动更新"就变成界面上一句永远不变的谎。
                    // 只记日志不动状态，下一拍照样问。
                    RollingLog.Error("sub-auto", "检查节拍异常（环继续）", ex);
                    LastNote = $"检查异常 {ex.GetType().Name}（环未退出）";
                }
            }
        }

        private static async Task TickOnceAsync()
        {
            int hours = AppSettings.SubAutoHours;
            var active = await SubscriptionManager.GetActiveAsync();
            var profiles = await SubscriptionManager.ListAsync();
            DateTime? fetchedAt = null;
            foreach (var p in profiles)
                if (p.Id == active.Id) fetchedAt = p.FetchedAt;

            // ★★【1.0.122 · 这里不再读会话状态】原先每一拍先 <c>ProfileGate.StatusAsync()</c>
            //   再据此 Skip，为的是"别把在跑的隧道换掉"。现读 Verge：自动与手动同一个
            //   <c>update_profile</c>，判据里没有会话这一项，更新当前那份就是
            //   <c>PUT /configs?force=true</c> 原地热重载（证据逐条在
            //   <see cref="SubscriptionAutoPolicy"/> 类注释与 Docs/63 §四）。
            //   我们这一路本来就等价：写正文 + 热重载，而写系统 VPN profile 那一步
            //   照旧过 1.0.101 的全局写入闸（Docs/52：会话挂着就跳过并记欠账、断开后由边沿对账补）。
            //   ⇒ 少读一次状态还有一层好处：判据不再依赖那条最容易中毒的读数
            //     （Docs/45 的 0x80070006 读数污染），到没到点只由文件 mtime 决定。
            var d = SubscriptionAutoPolicy.Decide(hours, DateTime.Now, fetchedAt,
                                                  ProfileGate.SwitchInProgress,
                                                  active.Id.Length > 0, _failStreak);
            LastNote = d.Why;

            if (!d.ShouldRun) return;

            var r = await ProfileGate.UpdateActiveSubscriptionAsync();
            if (r.Ok)
            {
                _failStreak = 0;
                LastNote = $"已自动更新：{r.Message}";
                RollingLog.Info("sub-auto", $"更新成功（间隔 {hours}h） {r.Message}");
            }
            else
            {
                _failStreak++;
                // ★ 日志里的"下次"按<b>当前</b> streak 现算：动手之前那个时刻配的是旧 streak，
                //   直接拿来写会把"退避到 12:00"和"已失败 1 次"配成一对，
                //   而按新 streak 真正的下次是 18:00 —— 日志里的时刻必须是这一拍算的。
                //   界面那行"下次"由 <c>MainComponent</c> 自己按现值算，这里不留第二份状态。
                DateTime? nextDue = fetchedAt == null ? null
                    : SubscriptionAutoPolicy.NextDueAt(fetchedAt.Value, hours, _failStreak);
                LastNote = $"自动更新失败（连续 {_failStreak} 次，已退避）：{r.Message}";
                // ★ 失败必须出声：静默失败 = 用户的订阅悄悄过期几周，
                //   而他以为"开了自动更新就不用管"。口径同 1.0.80 的轮询哨兵。
                RollingLog.Warn("sub-auto",
                    $"连续失败 {_failStreak} 次，下次退避到 " +
                    $"{(nextDue == null ? "—" : nextDue.Value.ToString("MM-dd HH:mm"))}：{r.Message}");
            }
        }
    }
}
