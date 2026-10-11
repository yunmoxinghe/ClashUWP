using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            Debug.WriteLine("VpnBackgroundTask.Run: enter.");

            // 【官方/UWP 通用】GetDeferral：告知平台本任务是异步的，
            // 在我完成之前不要挂起或终止宿主进程。
            // sample 注释："To keep the host process from being suspended or terminated
            //               while the background task is running"
            BackgroundTaskDeferral deferral = taskInstance.GetDeferral();
            try
            {
                ClashVpnPlugin plugin;

                var properties = (IDictionary<string, object>)CoreApplication.Properties;
                if (properties.TryGetValue(PluginId, out object? existing) &&
                    existing is ClashVpnPlugin existingPlugin)
                {
                    Debug.WriteLine("VpnBackgroundTask.Run: 复用已有插件实例.");
                    plugin = existingPlugin;
                }
                else
                {
                    Debug.WriteLine("VpnBackgroundTask.Run: 新建插件实例.");
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
                Debug.WriteLine("VpnBackgroundTask.Run 异常: " + ex);
            }
            finally
            {
                deferral.Complete();
            }

            Debug.WriteLine("VpnBackgroundTask.Run: exit.");
        }
    }
}
