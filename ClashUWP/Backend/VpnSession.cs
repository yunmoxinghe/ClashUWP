using System;
using System.Threading.Tasks;

using ClashUWP.Vpn;

namespace ClashUWP.Backend
{
    /// <summary>
    /// 【后端 · 会话动作】前端唯一被允许调用的 VPN 动作入口。
    ///
    /// 这层存在的理由只有一条：<b>把「谁该被保护」这件事固定在动作里</b>。
    /// 健康哨兵原先是 UI 按钮和 debug 回路各自记得去武装一次，这种"靠人记"的
    /// 写法迟早漏一条路 —— 而漏掉的那条路一旦黑洞就是整机断网。
    /// 现在 connect 必武装、disconnect 必解除，前端只管把返回的状态字符串显示出来。
    ///
    /// 分工：<see cref="VpnProfileSetup"/> 负责平台 API（建/连/断/删 profile），
    /// 本类负责<b>顺序与配套保护</b>，前端负责<b>显示与异常文案</b>。
    /// 异常一律原样向上抛，由前端翻译成状态文本 —— 后端不猜 UI 想显示什么。
    /// </summary>
    internal static class VpnSession
    {
        /// <summary>
        /// 重建主 profile。按钮分工照官方 sample：<b>建/重建</b>与<b>连接</b>是两个
        /// 独立动作 —— 合成一个「连接 VPN」等于自己制造
        /// 「刚建出来还没被平台索引」和「旧 profile 里的无效配置永远修不掉」两个坑。
        /// </summary>
        internal static Task<string> RecreateAsync() => VpnProfileSetup.RecreateProfileAsync();

        /// <summary>
        /// 连接主 profile（全路由）。
        /// ★ 先武装健康哨兵再连接：即使连接卡死/异常，哨兵的硬上限到点也必然断开，
        ///   网络绝不会被长期占死。
        /// </summary>
        internal static async Task<string> ConnectAsync()
        {
            HealthMonitor.Arm();
            RollingLog.Info("session", "用户发起连接（已先武装健康哨兵）");
            return await VpnProfileSetup.ConnectAsync();
        }

        /// <summary>断开主 profile（恢复网络的兜底手段），并解除哨兵。</summary>
        internal static async Task<string> DisconnectAsync()
        {
            HealthMonitor.Cancel(); // 手动断开即解除哨兵（连接已经不在了，没必要再探）
            RollingLog.Info("session", "用户发起断开（已解除健康哨兵）");
            return await VpnProfileSetup.DisconnectAsync();
        }
    }
}
