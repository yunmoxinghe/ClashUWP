using System;
using System.Text;
using System.Threading.Tasks;

using Windows.Networking.Sockets;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// ★【1.0.78】物理出口探针 —— 前台 <c>HealthMonitor.PhysicalNetworkWatchdogAsync</c> 与
    ///   插件 <see cref="PhysicalNetworkWatchdog"/> <b>共用的唯一口径</b>。
    ///
    /// ══ 为什么要有这个类（两条实测把原来的写法判死了）══
    ///   旧口径是「单目标 TCP：<c>114.114.114.114:443</c>，10 次失败即拆隧道 + 删 profile」，
    ///   前台与插件<b>各写了一份、各自计数</b>。两处都被实机证伪：
    ///   · <b>目标本身不通</b>（Docs/32 §二、§四 P2）：2026-10-20 退出 Clash Verge 后的
    ///     干净直连基线实测 <c>114.114.114.114:443</c> 的 TCP 握手<b>不通</b>（同 IP 的 53 通），
    ///     而那一刻 <c>www.baidu.com</c> 是 200/0.036s —— 也就是说<b>机器是好的，探针在失败</b>。
    ///     10 拍 × 30 s = 5 分钟就走到「拆隧道 + 删 profile」，等于一个定时炸弹。
    ///   · <b>判据不专属</b>（Docs/30 §8-B）：普通 TCP 连接走操作系统默认路由，
    ///     任何第三方 VPN 抢走默认路由都会把它变成必然失败。旧注释里
    ///     「探的是物理直连而非隧道，所以结果可信」这句<b>已被实测推翻</b>。
    ///   ⇒ 结论：这条最后防线的判据不能是「能不能连上某个公网 IP」，
    ///     必须同时有<b>平台自己的信号</b>背书，并且在判据不可信时<b>明确不动手</b>。
    ///
    /// ══ 现在的口径 ══
    ///   【健康】两个信号<b>任一</b>成立即算健康（各自都可能假失败，不各自都可能假通过）：
    ///     ① 候选探针<b>任一</b>通（多目标 + 多端口，OR 语义）；
    ///     ② 平台自评 <c>GetInternetConnectionProfile().GetNetworkConnectivityLevel()</c>
    ///        是 <c>InternetAccess</c>（NCSI 是操作系统自己的"有没有互联网出口"判定，
    ///        不依赖我们挑的 IP）。
    ///   【可信】两种情况判据作废、调用方<b>不许动手</b>，只记日志：
    ///     ① 当前持有 Internet 的链路就是<b>我们自己的 VPN</b>（profile 名以
    ///        <see cref="GracefulTeardown.ProfileName"/> 开头）—— 窄 include 下不会发生，
    ///        一旦发生说明默认路由进了我们的隧道，探针量到的是隧道内，读不出物理状态；
    ///     ② <b>读不到</b>持有 Internet 的链路（返回空/异常）—— 没有归属就没有结论。
    ///   这条订正对应 Docs/30 §8-B 的 ②：<b>拆隧道是可逆的，删 profile 会让入口消失，
    ///   代价不对等</b> ⇒ 「探针不可信的分支」里绝不出现删除动作。
    ///
    /// ══ 候选目标怎么挑 ══
    ///   · 全部是 <b>IP 字面量</b>，不依赖 DNS —— 否则 DNS 故障会被读成"没网"（旧口径同一条理由）；
    ///   · 全部<b>不在我们的 include 集合里</b>：窄 include 进隧道的只有 fake-ip 池
    ///     <c>198.18.0.0/16</c> 与 NRPT 的两个 nameserver（<c>223.5.5.5</c> / <c>119.29.29.29</c>），
    ///     所以候选里<b>绝不能</b>出现这两个 IP，否则隧道一开探针就必通，防线整条失效；
    ///   · 端口按本轮实测取：<c>114.114.114.114</c> 是 <b>53 通、443 不通</b>，
    ///     两条都留在候选里（443 在别的网络上是好的）；
    ///     <c>223.6.6.6</c> / <c>119.28.28.28</c>（阿里二级、DNSPod 二级）与
    ///     <c>8.8.8.8:53</c>、<c>1.1.1.1:53</c> 作为补充候选 ——
    ///     ★ 这两组<b>尚未在本机直连下实测</b>，装机的验收判据之一就是读回它们的结果，
    ///       读不到的那一条下次订正（不凭记忆当已验）。
    ///   · OR 语义下多一个候选只会<b>减少</b>误杀，增加的是漏判风险，
    ///     而漏判还有主健康哨兵（隧道黑洞与异常重拨判据）接着。
    /// </summary>
    internal static class PhysicalNetworkProbe
    {
        /// <summary>候选探针目标（host, port）。顺序 = 实测把握度顺序，任一通即健康。</summary>
        private static readonly (string Host, string Port)[] Candidates =
        {
            ("114.114.114.114", "53"),    // 本轮干净直连实测：通
            ("114.114.114.114", "443"),   // 本轮实测：不通（保留：别的网络上它是好的）
            ("223.6.6.6", "53"),          // 阿里二级 DNS（★ 未实测）
            ("119.28.28.28", "53"),       // DNSPod 二级 DNS（★ 未实测）
            ("8.8.8.8", "53"),            // 海外候选（★ 境内直连常被干扰，只作 OR 补位）
            ("1.1.1.1", "53"),            // 同上
        };

        /// <summary>单个候选的握手超时。</summary>
        private const int CandidateTimeoutMs = 2500;

        /// <summary>
        /// 本轮判定。返回 (healthy, trusted, detail)：
        /// <paramref name="healthy"/> 假才计一次失败；<paramref name="trusted"/> 假则
        /// 调用方<b>必须</b>放弃动手（只记日志、解除自己那条循环）。
        /// </summary>
        internal static async Task<(bool healthy, bool trusted, string detail)> CheckAsync()
        {
            string owner = InternetOwnerName();
            string level = ConnectivityLevelName();

            // ① 判据是否专属：默认路由在我们自己手里 ⇒ 不可信。
            bool oursOwnsInternet = owner.StartsWith(
                GracefulTeardown.ProfileName, StringComparison.Ordinal);
            bool noOwner = owner.Length == 0;

            var sb = new StringBuilder();
            sb.Append($"出口[{(noOwner ? "读不到" : owner)}] 自评[{level}] 探针[");

            bool anyProbeOk = false;
            foreach ((string host, string port) in Candidates)
            {
                string r = await TcpProbeAsync(host, port, CandidateTimeoutMs);
                if (r == "OK") anyProbeOk = true;
                sb.Append($"{host}:{port}={(r == "OK" ? "OK" : "×")} ");
            }
            sb.Append(']');

            // ★ 平台自评只认 InternetAccess：ConstrainedInternet（ captive portal / 部分连通）
            //   不算健康，也不算"出口死了"，交给候选探针那一路判断。
            bool ncsiInternet = level == nameof(Windows.Networking.Connectivity.NetworkConnectivityLevel.InternetAccess);

            bool healthy = anyProbeOk || ncsiInternet;
            bool trusted = !oursOwnsInternet && !noOwner;

            string detail = sb.ToString();
            if (!trusted)
                detail += oursOwnsInternet
                    ? " ★判据不可信：默认路由在我们自己的隧道上（探针量到的是隧道内）"
                    : " ★判据不可信：读不到持有 Internet 的链路";

            return (healthy, trusted, detail);
        }

        /// <summary>当前持有 Internet 的链路名（读不到返回空串，绝不抛）。</summary>
        private static string InternetOwnerName()
        {
            try
            {
                return Windows.Networking.Connectivity.NetworkInformation
                    .GetInternetConnectionProfile()?.ProfileName ?? "";
            }
            catch { return ""; }
        }

        /// <summary>平台自评的连接级别（读不到返回 None 之外的标记串 <c>?</c>，绝不抛）。</summary>
        private static string ConnectivityLevelName()
        {
            try
            {
                var p = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
                return p == null ? "无链路" : p.GetNetworkConnectivityLevel().ToString();
            }
            catch (Exception ex) { return "?" + ex.GetType().Name; }
        }

        /// <summary>
        /// 纯 TCP 握手探针（不读数据、不走系统代理）。
        /// ★ 用 WinRT <see cref="StreamSocket"/> 而不是 <c>TcpClient</c>：包应用里
        ///   这条是前台 <c>HealthMonitor</c> 一直在用的同一实现，行为已在实机读过。
        /// </summary>
        private static async Task<string> TcpProbeAsync(string host, string port, int timeoutMs)
        {
            try
            {
                using var socket = new StreamSocket();
                var conn = socket.ConnectAsync(
                    new Windows.Networking.HostName(host), port).AsTask();
                if (await Task.WhenAny(conn, Task.Delay(timeoutMs)) == conn) return "OK";
                return "超时";
            }
            catch (Exception ex)
            {
                return ex.GetType().Name;
            }
        }
    }
}
