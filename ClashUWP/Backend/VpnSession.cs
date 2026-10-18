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
        /// ★【1.0.62】<paramref name="subId"/> 非空时连<b>该订阅专属</b>的系统 profile ——
        ///   与用户在快速设置里点那条订阅走的是<b>完全同一条</b>平台路径
        ///   （ConnectProfileAsync → 平台 → IVpnPlugIn.Connect），差别只是发起者是我们。
        ///   快速面板本身没法自动化点（系统 UI），这条入口就是它的自动化等价物：
        ///   同一 profile、同一 CustomConfiguration、同一插件读配置逻辑。
        ///   哨兵照常武装 —— 订阅 profile 同样是 0.0.0.0/0 全路由。
        /// </summary>
        internal static async Task<string> ConnectAsync(string? subId = null)
        {
            HealthMonitor.Arm();
            RollingLog.Info("session", subId == null
                ? "用户发起连接（已先武装健康哨兵）"
                : $"用户发起订阅连接 subId={subId}（已先武装健康哨兵）");
            return await VpnProfileSetup.ConnectAsync(subId);
        }

        /// <summary>断开主 profile（恢复网络的兜底手段），并解除哨兵。</summary>
        internal static async Task<string> DisconnectAsync()
        {
            HealthMonitor.Cancel(); // 手动断开即解除哨兵（连接已经不在了，没必要再探）
            RollingLog.Info("session", "用户发起断开（已解除健康哨兵）");
            return await VpnProfileSetup.DisconnectAsync();
        }

        /// <summary>
        /// 只读当前 profile 的连接状态文本（"Connected"/"Disconnected"/"Connecting"…）。
        ///
        /// ★★★ 这是修复「快速面板（系统 VPN 快速设置）点了连接、app 里没反应 / 没回调」的关键：
        /// 系统快速设置发起的连接走的是 <c>VpnManagementAgent.ConnectProfileAsync</c> → 平台
        /// → <c>IVpnPlugIn.Connect</c>（vpnClient 后台任务），<b>前台 app 完全不参与、也不被激活</b>。
        /// UWP 前台<b>没有</b>任何 VPN 连接状态变更事件（<c>VpnChannel.ActivityChange</c> 只在
        /// 插件进程内有效，前台订阅不到；<c>VpnManagementAgent</c> 也没有 StatusChanged 事件），
        /// 所以前台只能<b>主动轮询</b>这份状态来反映系统驱动的连接/断开。
        /// 本方法就是那个轮询读数入口，由 MainComponent 的 3 秒循环调用。
        /// </summary>
        internal static Task<string> StatusAsync() => VpnProfileSetup.OurStatusAsync();

        /// <summary>
        /// 当前<b>已连接</b>的订阅 id（空串 = 没连着 / 连的是基础 profile）。
        /// 由 MainComponent 的 3 秒轮询调用：快速面板切了订阅，前台要能自己跟上。
        /// </summary>
        internal static Task<string> ConnectedSubAsync() => VpnProfileSetup.ConnectedSubIdAsync();
    }
}
