using System;
using System.Collections.Generic;
using Windows.ApplicationModel.Background;
using Windows.ApplicationModel.Core;
using Windows.Networking.Vpn;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// S3 · 后台任务入口 —— 平台每次要让插件干活，都从这里进。
    ///
    /// 清单侧对应（见 Package.appxmanifest）：
    ///   <c><uap:Task Type="vpnClient" /></c>
    ///   【官方/sample 注释】：<i>"When your application is installed this background task will
    ///   automatically be registered by the VPN platform."</i>
    ///   → <b>不需要自己 BackgroundTaskBuilder.Register</b>，装包时平台自动注册。
    ///
    /// 必须为 public sealed：它由 VPN 平台通过 WinRT 激活（manifest 里的
    /// <c>ActivatableClass ActivatableClassId</c>），不是被我们自己的代码 new 出来的。
    /// 加了 <c>PublishAot</c> 之后尤其要注意：这类"没人直接引用、靠清单激活"的类型
    /// 是裁剪器最容易误删的一类。
    /// </summary>
    public sealed partial class VpnBackgroundTask : IBackgroundTask
    {
        /// <summary>插件实例在进程属性包里的键。</summary>
        private const string PluginId = "ClashVpnPlugin";

        /// <summary>
        /// sample 注释原文：
        /// <i>"It is important that on each invocation of this background task, we provide the
        /// same instance of the IVpnPlugin object to the platform. This is because the plug-in
        /// will have internal state which needs to be preserved across multiple invocations
        /// (e.g. the plug-in may have a partially decapsulated packet stored while it awaits
        /// more data to be available for decapsulation)."</i>
        ///
        /// 载体是 <c>CoreApplication.Properties</c> —— 注意这是<b>进程内</b>字典，
        /// 宿主进程一重建就丢（80 号实测：每次 Connect 可能是全新 pid）。
        /// 所以任何"必须活过进程重建"的状态都不能放这儿，要落盘（后续 mihomo 配置、
        /// fake-ip 表、DNS 缓存都属于这一类）。
        /// </summary>
        public void Run(IBackgroundTaskInstance taskInstance)
        {
            // ★ 进程入口第一件事就是把日志立起来（滚动、非阻塞、发布版也写）。
            //   这里原先只有 Debug.WriteLine —— 它带 [Conditional("DEBUG")]，
            //   发布版里这些调用根本不会被编译进去，于是"插件被叫起来过几次"
            //   这种最基本的问题在真机上无据可查。
            //   注意本方法<b>在平台回调线程上</b>：Init/写日志都只允许入队，不允许落盘。
            RollingLog.Init(RollingLog.Role.Plugin);
            // 版本号 + pid 各记一行：sessions 汇的格式是机器契约不能动，
            // 而"这一行是哪一版写出来的、和上一条是不是同一个进程"恰恰只有自由文本这边答得了。
            RollingLog.Info("bgtask", $"插件进程启动 version={RollingLog.PackageVersion()} " +
                                      $"pid={Environment.ProcessId}");
            RollingLog.Sessions(ClashVpnPlugin.ConnectCalls,
                $"evt=run-enter stage=background-task");

            // 【官方/UWP 通用】GetDeferral：告知平台本任务是异步的，
            // 在我完成之前不要挂起或终止宿主进程。
            // sample 注释："To keep the host process from being suspended or terminated
            //               while the background task is running"
            BackgroundTaskDeferral deferral = taskInstance.GetDeferral();

            // ★【1.0.49】订阅 <see cref="IBackgroundTaskInstance.Canceled"/> ——
            //   平台取消这个后台任务（系统要收进程了）时的唯一通知。
            //   【为什么必须订阅】本类的 deferral 在 <c>ProcessEventAsync</c> 之后
            //   <b>立刻</b> Complete（官方语义：那条调用横跨整个 VPN 会话，不能 await），
            //   ⇒ 宿主进程<b>随时可能</b>被系统回收。Docs/09 §13.4 第 1 条记的
            //   "T1 收尾跑在一个随时可能被回收的进程里"，本质就是这条。
            //   订阅它至少能<b>留下一行"任务被取消了"的证据</b> ——
            //   之前这种时刻是<b>完全无痕</b>的（plugin.log 里什么都没有）。
            //   ★ 官方 remarks 明确：Canceled <b>不保证</b>触发
            //   （"background tasks may be terminated without any warning"），
            //   所以它只能当"补一条证据"，<b>不能当收尾钩子</b>。
            //   真正的收尾仍然是 <c>IVpnPlugIn.Disconnect</c>（平台给的那一个）。
            taskInstance.Canceled += (s, e) =>
            {
                RollingLog.Warn("bgtask", "后台任务被平台取消（Canceled）——宿主进程可能被回收");
                RollingLog.Sessions(ClashVpnPlugin.ConnectCalls, "evt=bgtask-canceled");
                try { deferral.Complete(); }
                catch { /* 重复 Complete 无害 */ }
            };

            try
            {
                ClashVpnPlugin plugin;

                var properties = (IDictionary<string, object>)CoreApplication.Properties;
                if (properties.TryGetValue(PluginId, out object? existing) &&
                    existing is ClashVpnPlugin existingPlugin)
                {
                    // 复用实例 ⇒ 常驻内核那套状态（bridge/settings）还在同一个进程里。
                    RollingLog.Sessions(ClashVpnPlugin.ConnectCalls,
                        "evt=run-instance reused=1");
                    plugin = existingPlugin;
                }
                else
                {
                    // 新建实例 ⇒ 宿主进程大概率是被平台重建的（80 号实测：每次 Connect
                    // 可能是全新 pid）。这条和上一条的区别只能靠日志看出来。
                    RollingLog.Sessions(ClashVpnPlugin.ConnectCalls,
                        "evt=run-instance reused=0");
                    plugin = new ClashVpnPlugin();
                    properties[PluginId] = plugin;
                }

                // 【官方】ProcessEventAsync(Object, Object)："Processes any pending VpnChannel events."
                // 第二参数官方定义："The event object that was passed to the app from a background
                //                   task Run method parameter. App code ... treats this object as
                //                   an opaque blob."
                // → 别自己解析 TriggerDetails，原样透传。
                //
                // ★ 为什么这里不 await：sample 注释说得很直白 ——
                //   "This call is blocking and the background task will not complete until the
                //    platform has finished using our plug-in to do work."
                //   它横跨<b>整个 VPN 会话</b>（Connect → 若干次 Encapsulate/Decapsulate →
                //   Disconnect）。一旦 await，Run 就卡住不返回，deferral 永远不 Complete，
                //   宿主进程会一直停在 Running。所以这里是刻意的 fire-and-forget。
                VpnChannel.ProcessEventAsync(plugin, taskInstance.TriggerDetails);
            }
            catch (Exception ex)
            {
                // sample 只记日志不 rethrow —— 保证 deferral 一定被 Complete，
                // 否则进程会永远卡在 Running 状态。
                RollingLog.Error("run", ex);
                RollingLog.Sessions(ClashVpnPlugin.ConnectCalls,
                    $"evt=run-error err={ex.GetType().Name}");
            }
            finally
            {
                deferral.Complete();
            }

            RollingLog.Info("bgtask", "Run 返回（deferral 已 Complete，会话由 ProcessEventAsync 继续驱动）");
        }
    }
}
