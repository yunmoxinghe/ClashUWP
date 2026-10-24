using System;
using System.Text;
using System.Threading.Tasks;

using Windows.Networking.Vpn;

using ClashUWP.Vpn;

namespace ClashUWP.Backend
{
    /// <summary>
    /// 【后端 · 进程启动探针】进程一起来就把 <c>GetProfilesAsync</c> 的结果写进
    /// <c>LocalState/probe.txt</c>。
    ///
    /// 【为什么单独立一个类，而不是留在 App.Main 里】它排查的是「平台到底认不认
    ///   我们这个 profile」，跟界面无关；放在 App 里让入口文件兼职取证，
    ///   也让人误以为"入口必须等它跑完"。
    ///
    /// ★ 这里是<b>纯读</b>：只调 GetProfilesAsync，不建、不连、不删，不碰任何路由
    ///   —— 对现有网络零影响。
    ///
    /// ★ <b>不阻塞启动线程</b>（审计项 P3）。原先 <c>App.Main</c> 里是
    ///   <c>GetProfilesAsync().AsTask().GetAwaiter().GetResult()</c> 这种形态：
    ///   在 STA 主线程上同步等待需要回到本上下文续接的 WinRT 异步，是标准死锁形状。
    ///   当时能稳定出 probe.txt 只能说明这条路径<b>恰好</b>没踩到，不能当验证结论。
    ///   现在丢到线程池跑，主线程直接进 <c>Application.Start</c>；
    ///   代价只是 probe.txt 可能晚几十毫秒出现，而它本来就是给人读的证据文件。
    ///
    /// ★ 分两段落盘：进 Main 就先写一次，再写完整结果。
    ///   这样即使中途抛异常或卡住，外部也能知道「进程起来了、探针起了」，
    ///   文件缺失本身才是一个明确的结论。
    /// </summary>
    internal static class StartupProbe
    {
        internal static void Start()
        {
            // fire-and-forget：RunAsync 内部全程 try，不会有异常漏出来。
            _ = Task.Run(RunAsync);
        }

        private static async Task RunAsync()
        {
            var sb = new StringBuilder();

            sb.AppendLine($"Time={DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"FamilyName={Windows.ApplicationModel.Package.Current.Id.FamilyName}");
            sb.AppendLine("Step=main-entered");
            await LocalState.WriteTextQuietAsync("probe.txt", sb.ToString());

            try
            {
                // ★【1.0.82 · Docs/34 §六】走串行层，不再自建 agent（见 ProfileDump 同处注释）。
                var all = await ClashUWP.Vpn.ProfileStore.GetProfilesAsync();

                sb.AppendLine($"Step=GetProfilesAsync");
                sb.AppendLine($"Count={all.Count}");

                foreach (var p in all)
                {
                    sb.AppendLine($"--- Type={p.GetType().Name}");
                    sb.AppendLine($"    ProfileName={p.ProfileName}");

                    if (p is VpnPlugInProfile pp)
                    {
                        // ConnectionStatus 只在具体类型上，IVpnProfile 接口没有。
                        // ★【1.0.97 · Docs/51】这里原来裸读 <c>pp.ConnectionStatus</c>：与
                        //   ProfileDump 同一个病 —— 一条读数中毒就抛穿 foreach，<b>probe.txt
                        //   停在中毒那条上、后面的条目一行不剩</b>，app.log 里只留一行
                        //   <c>probe 启动探针失败 | COMException: 句柄无效</c>，看着像"探针没跑"。
                        //   取证面自己先崩等于销毁现场，降级归一到 <see cref="VpnProfileSetup.StatusOf"/>。
                        sb.AppendLine($"    ConnectionStatus={VpnProfileSetup.StatusOf(pp)}");
                        sb.AppendLine($"    PluginPackageFamilyName={pp.VpnPluginPackageFamilyName}");
                        sb.AppendLine($"    RequireVpnClientAppUI={pp.RequireVpnClientAppUI}");
                        sb.AppendLine($"    CustomConfiguration={(pp.CustomConfiguration?.Length ?? 0)} chars");
                        sb.AppendLine($"    ServerUris={pp.ServerUris.Count}");

                        foreach (Uri u in pp.ServerUris)
                            sb.AppendLine($"      Uri={u} | Host={u.Host} | Port={u.Port}");
                    }
                }

                sb.AppendLine("Step=done");
                RollingLog.Info("probe", $"启动探针完成，profile 数={all.Count}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"ERROR={ex.GetType().Name}: {ex.Message}");
                // ★ 一并进滚动日志：probe.txt 里写着 ERROR 而日志里一片安静，
                //   是"发布版没日志"时代的老问题；现在两边都要能查到。
                RollingLog.Error("probe", "启动探针失败", ex);
            }

            await LocalState.WriteTextQuietAsync("probe.txt", sb.ToString());
        }
    }
}
