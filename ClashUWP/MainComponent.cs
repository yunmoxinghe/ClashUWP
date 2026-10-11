using System;
using System.Threading.Tasks;

using Microsoft.UI.Reactor.Core;
using Windows.Networking.Vpn;

using ClashUWP.Vpn;
using static Microsoft.UI.Reactor.Factories;

namespace ClashUWP
{
    /// <summary>
    /// 骨架期主界面：除了验证 Reactor.Uwp 已接管渲染，
    /// 再加两个按钮走通 <see cref="VpnProfileSetup"/> 的建 profile / 连接 / 断开。
    ///
    /// 这么做的用处：连接成功后，在本机 <c>ping 10.7.0.1</c>（任一走 VPN 的地址），
    /// 如果能收到 ICMP echo reply，就说明
    ///   前台建 profile → 平台拉起 vpnClient 后台任务 → VpnChannel.Start 建 L3 网卡
    ///   → Encapsulate 拿到原始 IP 包 → 主动注入 → 系统协议栈收到
    /// 整条链路都是通的。此时再换真内核（mihomo dll），风险就收敛到内核本身了。
    /// </summary>
    public sealed class MainComponent : Component
    {
        public override Element Render()
        {
            var (count, setCount) = UseState(0);
            var (vpnStatus, setVpnStatus) = UseState("未连接");

            return VStack(
                TextBlock($"ClashUWP —— Reactor.Uwp 已接管（{count}）"),
                TextBlock($"VPN：{vpnStatus}"),
                Button("点一下", () => setCount(count + 1)),
                Button("连接 VPN", () => { _ = ConnectAsync(setVpnStatus); }),
                Button("断开 VPN", () => { _ = DisconnectAsync(setVpnStatus); }));
        }

        private static async Task ConnectAsync(Action<string> setStatus)
        {
            setStatus("连接中…");

            try
            {
                VpnManagementErrorStatus status = await VpnProfileSetup.ConnectAsync();
                setStatus(status == VpnManagementErrorStatus.Ok
                    ? "已连接（试试 ping 10.7.0.1）"
                    : $"连接失败: {status}");
            }
            catch (Exception ex)
            {
                // 最常见的失败原因是缺 networkingVpnProvider 受限能力（AccessDenied），
                // 或包还没以开发者模式部署。带出异常类型，排查时省一轮。
                setStatus($"异常: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static async Task DisconnectAsync(Action<string> setStatus)
        {
            setStatus("断开中…");

            try
            {
                VpnManagementErrorStatus status = await VpnProfileSetup.DisconnectAsync();
                setStatus(status == VpnManagementErrorStatus.Ok ? "未连接" : $"断开失败: {status}");
            }
            catch (Exception ex)
            {
                setStatus($"异常: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
