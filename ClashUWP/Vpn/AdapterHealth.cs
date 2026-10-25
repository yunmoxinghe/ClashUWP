using System;
using System.Collections.Generic;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// ★【G6 · P3 修复】"桥现在到底绑在哪张卡上、那张卡还算不算数"的判据 —— 纯函数。
    ///
    /// 【这条缺口原来是什么形状】Docs/32 §四 P3（Docs/33 §六.4 记的"本轮仍未修"）：
    ///   <c>residentBridge=1</c> 的复用路径不重跑 <c>clash_bridge_start</c>，于是 Go 侧
    ///   <c>rebindInterface()</c>（<c>Tools/mihomo-bridge/bridge/core.go:66-104</c>）
    ///   沿用上一次探测的结果；桥回话 <c>bound_iface=Meta</c> —— 那是别的程序留下的
    ///   wintun 设备，此刻已经 Disconnected，真正的出口是 WLAN。而 <c>net-follow</c> 报
    ///   <c>指纹=未变</c> 于是连"可疑"都没说出来 —— 因为它比的是 WinRT 的
    ///   <c>GetConnectionProfiles()</c> 枚举（<b>profile</b> 不是链路状态，26100 的
    ///   winmd 里 <c>NetworkAdapter</c> 只有 IanaInterfaceType / NetworkAdapterId /
    ///   NetworkItem / 收发速率五个成员，没有名字也没有连接状态，Docs/21 §四 实测同一条）。
    ///   杀进程重建桥后立刻 <c>bound_iface=WLAN</c> —— 说明缺的从来不是能力，是<b>重评一次</b>
    ///   和<b>看得见</b>。
    ///
    /// 【现在拿什么判】系统适配器表（<c>System.Net.NetworkInformation.NetworkInterface</c>，
    ///   采集在 <see cref="NetworkFollow"/> 里，本类只吃数据不吃 API）：
    ///   <c>Name</c> 与 Go 侧 <c>ifc.Name</c> 同源（都取 Windows 的接口友好名，实测
    ///   <c>Meta</c> / <c>WLAN</c> 这种写法两边一致），<c>OperationalStatus</c> 与
    ///   <c>NetworkInterfaceType</c> 给"这张卡还在不在、还是不是物理卡"。
    ///
    /// 【★ 只在有正证据时动手】判据分成两半是刻意的：
    ///   · <b>坏</b>（名字找得到、但状态不是 Up / 没有 IPv4 / 桥根本没绑）
    ///     ⇒ <see cref="ShouldRebind"/> 为真：重跑一次探测严格比留着死绑定好；
    ///   · <b>不确定</b>（名字在表里找不到 —— 可能只是命名口径不一致）
    ///     ⇒ 只报不猜、不动手。把"我们的观测缺口"当成"网络变了"去驱动一次
    ///     <c>clash_bridge_start</c>（本项目有前科的病态路径，Docs/05 §2④）是拿数据面赌。
    ///   · 绑到了隧道/第三方 VPN 设备（<see cref="Bound.Tunnelish"/>）同样<b>只报不动手</b>：
    ///     重跑探测会挑出同一个名字（Go 的 <c>isVirtualNIC</c> 名单里没有 wintun/tap，
    ///     <c>core.go:49-61</c> 逐字读过），动手只会白撞一次桥。这条留给 Go 侧改名排除，
    ///     不在这里假装修好。
    /// </summary>
    internal static class AdapterHealth
    {
        /// <summary>
        /// 一张适配器的观测值。<b>字符串而不是枚举</b>：状态与类型名直接进指纹和日志，
        /// 换 .NET 版本或多出来的状态不会在这里被静默吃掉。
        /// </summary>
        internal readonly record struct Adapter(
            string Name, string Description, string Status, string TypeName, bool HasIpv4);

        /// <summary>桥侧绑定名对这张表体检出来的结论。</summary>
        internal enum Bound
        {
            /// <summary>绑着的卡存在、Up、有 IPv4，且不像隧道 —— 正常。</summary>
            Ok,
            /// <summary>桥回话里没有绑定名（Go 探测失败或没跑过）。</summary>
            None,
            /// <summary>找得到名字，但状态不是 Up（P3 的 <c>Meta</c> 就是这个形态）。</summary>
            NotUp,
            /// <summary>Up，但这张卡上没有非回环 IPv4 —— 拨出去必然不通。</summary>
            NoIpv4,
            /// <summary>表里找不到这个名字（命名口径不一致 / 卡已消失），<b>不足以据此动手</b>。</summary>
            Missing,
            /// <summary>能绑上，但它是隧道/虚拟设备（第三方 VPN 的 wintun、tap 等）—— 防自环形同虚设。</summary>
            Tunnelish,
        }

        /// <summary>
        /// "看起来不是物理出口"的名字/描述特征。前 11 个标签与 Go 侧
        /// <c>core.go:49-61 isVirtualNIC</c> <b>逐字同源</b>（两条实现不许各判一套），
        /// 后面几个是本类<b>多出来</b>的（wintun / tap / 第三方 VPN 的常见命名）：
        /// Go 那边不排它们，所以这里只用来"报"，不用来"动"。
        /// </summary>
        private static readonly string[] VirtualTags =
        {
            "veth", "hyper-v", "vmware", "vbox", "virtual", "loopback",
            "isatap", "teredo", "wsl", "bluetooth", "microsoft wi-fi direct",
            // ↓ 只有本类认、Go 不认（判据不对称是事实，写在上面那段注释里）
            "wintun", "tap-", "tap adapter", "tun adapter", "utun", "ppp",
            "clash", "verge", "sing-box", "surfshark", "nordvpn", "expressvpn",
            "mullvad", "wireguard", "anyconnect", "cisco",
        };

        /// <summary>体检：绑定名 × 适配器表 → 结论。表为空（采集失败）一律按 <see cref="Bound.Missing"/>。</summary>
        internal static Bound Evaluate(string boundName, IReadOnlyList<Adapter> table)
        {
            if (string.IsNullOrEmpty(boundName)) return Bound.None;
            Adapter? hit = FindByName(boundName, table);
            if (hit == null) return Bound.Missing;
            var a = hit.Value;
            if (!string.Equals(a.Status, "Up", StringComparison.OrdinalIgnoreCase)) return Bound.NotUp;
            if (!a.HasIpv4) return Bound.NoIpv4;
            if (LooksVirtual(a)) return Bound.Tunnelish;
            return Bound.Ok;
        }

        /// <summary>该结论是否构成"请求桥重绑"的正证据。</summary>
        internal static bool ShouldRebind(Bound b) =>
            b is Bound.NotUp or Bound.NoIpv4 or Bound.None;

        /// <summary>结论的人读句子（进 <c>net-follow</c> 日志与 <c>dataface-plugin.txt</c> 快照）。</summary>
        internal static string Reason(Bound b) => b switch
        {
            Bound.Ok => "绑定正常",
            Bound.None => "未绑定（桥没探到物理卡）",
            Bound.NotUp => "绑定的卡已断开",
            Bound.NoIpv4 => "绑定的卡没有 IPv4",
            Bound.Missing => "绑定名在系统适配器表里找不到（命名口径存疑）",
            Bound.Tunnelish => "绑定落在隧道/虚拟设备上（防自环不成立）",
            _ => b.ToString(),
        };

        /// <summary>按名字找卡：<b>先 Name 再 Description</b>，都忽略大小写（订阅派生的名字常有大小写差）。</summary>
        internal static Adapter? FindByName(string name, IReadOnlyList<Adapter> table)
        {
            foreach (Adapter a in table)
                if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return a;
            foreach (Adapter a in table)
                if (string.Equals(a.Description, name, StringComparison.OrdinalIgnoreCase)) return a;
            return null;
        }

        /// <summary>这张卡像不像"非物理出口"（名字或驱动描述命中特征）。</summary>
        internal static bool LooksVirtual(Adapter a)
        {
            if (string.Equals(a.TypeName, "Loopback", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.TypeName, "Tunnel", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.TypeName, "Ppp", StringComparison.OrdinalIgnoreCase))
                return true;
            string hay = (a.Name + " " + a.Description).ToLowerInvariant();
            foreach (string tag in VirtualTags)
                if (hay.Contains(tag, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// 指纹用的紧凑适配器串：稳定排序、只留会变的那几维
        /// （<c>名字:状态(+4/-4)</c>，<c>+4</c> = 有非回环 IPv4）。
        /// 【为什么进指纹】"换了张卡"和"这张卡掉了"必须改指纹 —— P3 报"未变"就是因为
        /// 旧指纹里只有 profile 枚举，没有链路状态。类型名<b>不</b>进指纹：驱动枚举顺序
        /// 之类的抖动不该算换网。上限 <paramref name="max"/> 张，超出标 <c>…</c>（可见的截断）。
        /// </summary>
        internal static string Format(IReadOnlyList<Adapter> table, int max = 24)
        {
            if (table.Count == 0) return "sys=?";
            var parts = new List<string>(table.Count);
            foreach (Adapter a in table)
                parts.Add($"{a.Name}:{(a.Status.Length == 0 ? "?" : a.Status)}" +
                          (a.HasIpv4 ? "+4" : "-4"));
            parts.Sort(StringComparer.Ordinal);
            if (parts.Count > max)
                return "sys=" + string.Join(",", parts.GetRange(0, max)) + $",…(+{parts.Count - max})";
            return "sys=" + string.Join(",", parts);
        }
    }
}
