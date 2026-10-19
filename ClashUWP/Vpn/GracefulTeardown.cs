using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Networking.Vpn;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// 优雅收尾：<b>先等平台把会话收干净，再动 profile</b>。
    ///
    /// 【为什么需要这个类 —— 2026-10-16 00:21 把用户网卡炸了】
    ///   旧的拆法是一行挨一行的三连发：
    ///   <c>Agent.DisconnectProfileAsync(profile)</c> → <c>Agent.DeleteProfileAsync(profile)</c>，
    ///   中间<b>不等</b>。事后取证（Docs/08 §10.10）：删除发生时平台仍记 <c>CONN=1</c>，
    ///   RasClient 里<b>没有</b>本次会话的 20226 终止事件 ⇒ profile 是在会话还被平台
    ///   认为活着的时候被删掉的。OS 于是直接扯掉接口：
    ///     · <b>不</b>回调插件的 <c>Disconnect</c>（我们的收尾代码根本没跑）；
    ///     · <b>不</b>清它自己下发的 DNS 策略 ⇒ 留下一条 Namespace=<c>.</c> 的根 NRPT 规则，
    ///       把整机解析指向已死的隧道 DNS；
    ///     · 隧道接口没了，路由项却还挂在已消失的 ifIndex 上（幽灵路由）。
    ///   两者叠加 = 整机断网，用户只能"网络重置"自救。
    ///
    /// 【官方给的出口，逐个核对过（winrt-26100 的 VpnChannel 方法表）】
    ///   · <c>VpnChannel</c> <b>没有</b> <c>Disconnect()</c> —— 插件不能"自己把会话断了"。
    ///   · <c>Stop()</c>："Destroys a previously established VPN channel object. Disassociates
    ///     and closes the outerTunnelTransport connection to the VPN server." 官方 sample
    ///     只在 <c>IVpnPlugIn.Disconnect</c> 里调它（先遣项目 VpnPlugInImpl.cpp:775 同），
    ///     语义是"回应平台的断开请求"，不是插件发起断开。
    ///   · <c>TerminateConnection(String)</c>："Allows a VPN plugin to indicate an error message
    ///     and terminate <b>the connection attempt</b> performed inside IVpnPlugin.Connect.
    ///     Should not be called on reconnect cases." —— 针对<b>建连尝试</b>，
    ///     拿来拆一条已经跑了几分钟的会话是超出文档用法，不用。
    ///   ⇒ 插件侧唯一站得住的"结束会话"入口，还是<b>用户级</b>的那条：
    ///     <c>VpnManagementAgent.DisconnectProfileAsync</c>（和设置里点断开同一个动作）。
    ///     它会让平台走<b>完整</b>收尾：拆接口、清 NRPT、清路由、回调我们的 Disconnect。
    ///
    /// 【本类做的事 = 把"不等"这个错纠掉】
    ///   ① 发起平台级断开；
    ///   ② 轮询等平台把会话<b>真的</b>收到 <c>Connected</c> 以外（最多 <see cref="WaitSeconds"/> s，
    ///      要求连续两轮都不是 Connected，抗状态抖动）；
    ///   ③ <b>只有</b> ② 成立才删 profile（删它是为了拆掉 RasMan 重拨的燃料，见 Docs/08）；
    ///      等不到就<b>拒绝删除</b>并回话 —— 宁可留一个可重拨的 profile 让外部
    ///      <c>rasdial /disconnect</c> + <c>/delete</c> 兜底，也绝不再制造残留；
    ///   ④ 删完做一次<b>残留自检</b>：IP 字面量探 + 域名探，两个结果交叉判读，
    ///      把"整机断网"和"只剩 DNS 残留"分开（这一步只读不写，不需要管理员）。
    ///
    /// ★ 前台（<c>VpnProfileSetup</c>，靠 AssemblyInfo.cs 的 InternalsVisibleTo 看到本类）
    ///   和插件内的看门狗<b>共用这一份实现</b>，两条路不再有第二种拆法。
    /// </summary>
    internal static class GracefulTeardown
    {
        /// <summary>
        /// 主 profile 名字，<b>唯一定义</b>在此：前台 <c>VpnProfileSetup.ProfileName</c>
        /// 也取这个值，两处不可能漂移。
        /// </summary>
        internal const string ProfileName = "ClashUWP";

        /// <summary>等平台收尾的最长轮询时间（秒）。</summary>
        internal const int WaitSeconds = 20;

        private static readonly VpnManagementAgent Agent = new();

        private static string FamilyName
        {
            get
            {
                try { return Windows.ApplicationModel.Package.Current.Id.FamilyName; }
                catch { return ""; }
            }
        }

        /// <summary>
        /// 按名字找本包的 profile（<paramref name="name"/> 省略时取主 profile）。
        ///
        /// 【为什么两个条件都要】先按<b>包 family name</b> 收窄，是为了不把别的 VPN app
        ///   的同名 profile 当成我们的；再按<b>名字</b>锁定，是因为本包自己就不止一个 profile
        ///   —— 探测会留下 <c>ClashUWP-Probe</c> / <c>ClashUWP-Dataface</c>。
        ///   2026-10-15 实测过只按 family 匹配的后果：recreate 删掉的是残留 Probe，
        ///   主 profile 反而没被重建。收尾删的是"谁"直接决定会不会留下残留，所以这里一条都不能省。
        /// </summary>
        internal static async Task<VpnPlugInProfile?> FindProfileAsync(string? name = null)
        {
            // ★ 拿不到自己的包 family name 就<b>一个都不认</b>：收尾动作是删 profile，
            //   认错对象的后果是删掉别人的 VPN。宁可什么都不做，也不能靠"名字恰好一样"下手。
            string family = FamilyName;
            if (family.Length == 0)
                return null;

            string want = name ?? ProfileName;
            IReadOnlyList<IVpnProfile> profiles = await Agent.GetProfilesAsync();
            foreach (IVpnProfile p in profiles)
            {
                if (p is VpnPlugInProfile pp && pp.ProfileName == want &&
                    pp.VpnPluginPackageFamilyName == family)
                    return pp;
            }
            return null;
        }

        private static string SafeStatus(VpnPlugInProfile? profile)
        {
            if (profile == null) return "ProfileGone";
            try { return profile.ConnectionStatus.ToString(); }
            catch (Exception ex) { return "读状态异常:" + ex.GetType().Name; }
        }

        /// <summary>
        /// 保证 <paramref name="profile"/> 上没有还活着的会话：不是 Connected 就直接通过；
        /// 是 Connected 就发起平台级断开并<b>等</b>它收干净。
        /// 返回 (能否安全删除, 中文说明)。false 时调用方<b>必须</b>放弃删除。
        /// </summary>
        internal static async Task<(bool ok, string report)> EnsureTornDownAsync(VpnPlugInProfile profile)
        {
            string st0 = SafeStatus(profile);
            if (st0 != "Connected")
                return (true, $"无需断开（状态={st0}）");

            var sb = new StringBuilder();
            sb.Append($"状态={st0} → 发起平台断开 ");
            try
            {
                await Agent.DisconnectProfileAsync(profile);
            }
            catch (Exception ex)
            {
                sb.Append($"断开异常={ex.GetType().Name}:{Trim(ex.Message)} ");
            }

            (bool down, string waited) = await WaitSessionDownAsync(profile.ProfileName);
            sb.Append(waited);
            return (down, sb.ToString());
        }

        /// <summary>
        /// 断开 → 等平台收干净 → 才删主 profile → 残留自检。
        /// 全程不抛异常：善后动作再失败也只能变成一句回话，不能把调用方带崩。
        /// </summary>
        internal static async Task<string> DisconnectThenDeleteAsync(string why)
        {
            var sb = new StringBuilder();
            sb.Append($"why={why} ");
            try
            {
                VpnPlugInProfile? profile = await FindProfileAsync();
                if (profile == null)
                    return sb.Append("profile 不存在，无需收尾").ToString();

                // ①② 断开 + 等收尾 —— 这一步就是原来缺的那道闸。
                (bool torn, string report) = await EnsureTornDownAsync(profile);
                sb.Append(report + " ");
                if (!torn)
                {
                    return sb.Append("★拒绝删除：会话没确认收干净，删 profile 会留 NRPT/路由残留 " +
                                     "→ 请手工 rasdial ClashUWP /disconnect 确认断开后再 /delete")
                             .ToString();
                }

                // ③ 删 profile：拆掉 RasMan 自动重拨的燃料（实测单日 413 次重拨就是这么停掉的）。
                profile = await FindProfileAsync();
                if (profile != null)
                {
                    try
                    {
                        await Agent.DeleteProfileAsync(profile);
                        sb.Append("删除=ok ");
                    }
                    catch (Exception ex)
                    {
                        sb.Append($"删除异常={ex.GetType().Name}:{Trim(ex.Message)} ");
                    }
                    sb.Append((await FindProfileAsync()) == null ? "复核=已消失 " : "复核=仍存在 ");
                }

                // ④ 残留自检（只读，判整机是否被我们拆坏）。
                sb.Append(await ResidueReportAsync());
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return sb.Append($"收尾异常={ex.GetType().Name}: {Trim(ex.Message)}").ToString();
            }
        }

        /// <summary>
        /// 只等不删：给"重建 profile / 清扫探测残留"这类必须先删 profile 的路径用。
        /// </summary>
        internal static async Task<(bool ok, string report)> EnsureSessionTornDownAsync(string? name = null)
        {
            VpnPlugInProfile? profile = await FindProfileAsync(name);
            if (profile == null)
                return (true, "profile 不存在");
            return await EnsureTornDownAsync(profile);
        }

        /// <summary>
        /// 轮询到目标 profile 的连接状态<b>连续两轮</b>不是 Connected（或 profile 已消失）。
        /// 每轮<b>重新取</b> profile，不信任旧对象上可能缓存的状态。
        /// </summary>
        private static async Task<(bool ok, string report)> WaitSessionDownAsync(string profileName)
        {
            var seen = new List<string>();
            int downRounds = 0;
            for (int i = 0; i < WaitSeconds; i++)
            {
                await Task.Delay(1000);
                string st;
                try { st = SafeStatus(await FindProfileAsync(profileName)); }
                catch (Exception ex) { st = "读状态异常:" + ex.GetType().Name; }
                seen.Add(st);

                if (st != "Connected")
                {
                    if (++downRounds >= 2)
                        return (true, $"等到收尾={string.Join(">", seen)}（{(i + 1)} s）");
                }
                else
                {
                    downRounds = 0;
                }
            }
            return (false, $"未等到收尾，最后状态={string.Join(">", seen)}");
        }

        /// <summary>
        /// 残留自检：<b>IP 字面量探</b>（不依赖 DNS）+ <b>域名探</b>（依赖 DNS）交叉判读。
        ///   两个都通        → 直连完好；
        ///   IP 通、域名不通 → 只剩 DNS 侧残留（就是那条孤儿 NRPT 的形状）；
        ///   IP 也不通       → 路由侧还有问题（幽灵路由 / 接口没清干净）。
        /// 探针用 <c>223.5.5.5</c> / <c>119.29.29.29</c>：国内必达，且是我们自己下发的隧道 DNS，
        /// 收尾之后它们必然回到物理网卡，量到的就是"拆完之后机器还能不能上网"。
        /// 
        /// ★ 检测到残留时，返回具体的 PowerShell 清理命令（需要管理员权限）。
        /// </summary>
        private static async Task<string> ResidueReportAsync()
        {
            await Task.Delay(1500);
            string ip = await TcpProbeAsync("223.5.5.5");
            if (ip != "OK") ip = await TcpProbeAsync("119.29.29.29");
            string domain = await TcpProbeAsync("www.baidu.com");

            string verdict =
                ip == "OK" && domain == "OK" ? "直连完好" :
                ip == "OK" ? "疑似 DNS 残留（孤儿 NRPT）：IP 可达但域名解析不通" :
                "疑似路由残留/整机不可达：连 IP 字面量都不通";

            // ★ 这一行决定"我们有没有把用户的网卡拆坏"，必须无条件进日志。
            //   它跑在哪个进程就写哪个文件（前台=app.log，插件=plugin.log），
            //   因为收尾本来就是两边都会发起的动作。
            if (verdict == "直连完好")
                RollingLog.Info("teardown-selfcheck", $"IP={ip} 域名={domain} → {verdict}");
            else
                RollingLog.Error("teardown-selfcheck", $"IP={ip} 域名={domain} → {verdict}");

            string report = $"残留自检 IP={ip} 域名={domain} → {verdict}";

            // ★ 检测到残留时，给出具体的清理命令（需要管理员 PowerShell）。
            //   这两条命令来自 Docs/07 §2 L3 强拆 NRPT，是官方推荐的清理方式。
            if (verdict != "直连完好")
            {
                report += "\n\n如需清理残留，请以<b>管理员身份</b>运行以下 PowerShell 命令：\n";
                report += "  Get-DnsClientNrptRule | Remove-DnsClientNrptRule -Force\n";
                report += "  Get-NetRoute | Where-Object {$_.InterfaceAlias -like '*ClashUWP*'} | Remove-NetRoute -Confirm:$false\n";
                report += "\n执行后建议重启网络适配器或运行：ipconfig /release & ipconfig /renew";
            }

            return report;
        }

        /// <summary>443 端口 TCP 连接探（只判可达，不传数据），3 s 超时。</summary>
        private static async Task<string> TcpProbeAsync(string host)
        {
            try
            {
                using var socket = new StreamSocket();
                var conn = socket.ConnectAsync(new HostName(host), "443")
                    .AsTask(CancellationToken.None);
                return await Task.WhenAny(conn, Task.Delay(3000)) == conn ? "OK" : "超时";
            }
            catch (Exception ex)
            {
                return ex.GetType().Name;
            }
        }

        private static string Trim(string s)
        {
            s = s.Replace('\r', ' ').Replace('\n', ' ');
            return s.Length <= 120 ? s : s.Substring(0, 120) + "…";
        }
    }
}
