using System;
using System.Text;
using System.Threading.Tasks;

using ClashUWP.Vpn;

namespace ClashUWP.Backend
{
    /// <summary>
    /// 【后端 · profile 清点】把系统里 VPN profile 的真实内容读出来 —— 纯读，
    /// 不建、不连、不删、不改任何路由，因此对现有网络零影响。
    ///
    /// 【归属】原本这两段长在 UI 组件 MainComponent 里，只是因为它最早是在
    /// 「按按钮之前也想看到平台存了什么」时被顺手写进去的。它跟渲染无关，
    /// 所以抽到后端：前端只负责显示，不负责取证。
    /// </summary>
    internal static class ProfileDump
    {
        /// <summary>
        /// 把 <c>GetProfilesAsync</c> 读到的东西原样落盘，供外部核对。
        ///
        /// 【为什么单看 UI 不够】UI 只在**按了按钮之后**才显示，
        /// 而且只显示第一个匹配上的 profile。要回答「平台里存的到底是什么」，
        /// 就得把**全部**条目、以及 ServerUris 的 Host/Port 都打出来 ——
        /// Uri.Port 对 <c>http://127.0.0.1:0</c> 会是 0，对
        /// <c>http://127.0.0.1</c> 会是 80，一眼就能分辨坏配置还在不在。
        /// </summary>
        internal static async Task DumpAsync()
        {
            var sb = new StringBuilder();
            var pfn = Windows.ApplicationModel.Package.Current.Id.FamilyName;

            sb.AppendLine($"FamilyName={pfn}");
            sb.AppendLine($"Time={DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("Step=entered");

            // 先落一次盘：这样即使后面 GetProfilesAsync 抛异常/挂住，
            // 外部也能知道「进程起来了、dump 起了」，不至于一无所获。
            await LocalState.WriteTextQuietAsync("profile-dump.txt", sb.ToString());

            try
            {
                sb.AppendLine("Step=GetProfilesAsync");
                // ★【1.0.82 · Docs/34 §六】诊断路径也不能绕过串行层：dump 恰恰是在"出事了"的时候跑的，
                //   那时连接 RPC 很可能还在飞 —— 这正是 0x80070006 的形状（叶子调用，不嵌套）。
                var all = await ClashUWP.Vpn.ProfileStore.GetProfilesAsync();
                sb.AppendLine($"Count={all.Count}");

                foreach (var p in all)
                {
                    sb.AppendLine($"--- Type={p.GetType().Name}");
                    sb.AppendLine($"    ProfileName={p.ProfileName}");

                    // ConnectionStatus 不在 IVpnProfile 接口上（接口只有
                    // ProfileName/Routes/AlwaysOn 等），只在具体类型上，所以放这里。
                    if (p is Windows.Networking.Vpn.VpnPlugInProfile pp)
                    {
                        // ★【1.0.96 · Docs/51】这里原来裸读 <c>pp.ConnectionStatus</c>：
                        //   一条读数中毒的条目会抛穿整个 foreach，<b>后面的条目一行都不剩</b> ——
                        //   而 dump 恰恰是出事后唯一的取证面，取证面自己先崩等于销毁现场。
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
                RollingLog.Info("dump", $"profile 清点完成，profile 数={all.Count}");
            }
            catch (Exception ex)
            {
                // 关键：异常也必须落盘。之前只写 Debug.WriteLine，
                // 在没挂调试器时等于静默丢失 —— 表现为「文件根本没生成」。
                sb.AppendLine($"ERROR={ex.GetType().Name}: {ex.Message}");
                RollingLog.Error("dump", "profile 清点失败", ex);
            }

            await LocalState.WriteTextQuietAsync("profile-dump.txt", sb.ToString());
        }

        /// <summary>
        /// 列出全部 profile 及连接状态（debug <c>status</c> 命令的正文）。
        /// 【为什么不写文件而回字符串】这条命令要立刻出现在 debug-out.txt 里被人读，
        /// 落盘版本是上面的 <see cref="DumpAsync"/>，两者别混。
        /// </summary>
        internal static async Task<string> StatusTextAsync()
        {
            var profiles = await ClashUWP.Vpn.ProfileStore.GetProfilesAsync();
            if (profiles.Count == 0) return "profile 数=0";
            var sb = new StringBuilder();
            sb.AppendLine($"profile 数={profiles.Count}");
            foreach (var p in profiles)
            {
                // ConnectionStatus 只在具体类型 VpnPlugInProfile 上（IVpnProfile 没有）。
                if (p is Windows.Networking.Vpn.VpnPlugInProfile pp)
                    sb.AppendLine($"[{pp.ProfileName}] ConnectionStatus={VpnProfileSetup.StatusOf(pp)}");
                else
                    sb.AppendLine($"[{p.ProfileName}] ({p.GetType().Name})");
            }
            return sb.ToString().TrimEnd();
        }
    }
}
