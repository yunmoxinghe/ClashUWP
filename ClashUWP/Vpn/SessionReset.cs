using System;
using System.Text;
using System.Threading.Tasks;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// 【G5 的"重置"那一半】会话边界通过 mihomo REST 控制器清内核缓存。
    /// 档由 <c>settings.txt: sessionReset=0|1|2</c> 决定（<see cref="LocalSettings.SessionReset"/>，
    /// 默认 0 = 什么都不做，本类直接返回）。
    ///
    /// ══ 三个端点是照着 pin 住的那版源码写的，不是猜的 ══
    ///   <c>go.mod</c> pin <c>github.com/metacubex/mihomo v1.19.32</c>，
    ///   源码在 <c>%GORODIR%\pkg\mod\...\mihomo@v1.19.32</c>，逐条对过：
    ///   · <c>hub/route/server.go:132,135</c> —— <c>r.Mount("/connections", …)</c> /
    ///     <c>r.Mount("/cache", …)</c>，所以路径前缀就是这两个名字；
    ///   · <c>hub/route/cache.go:12-15</c> —— <c>r.Post("/fakeip/flush", flushFakeIPPool)</c>、
    ///     <c>r.Post("/dns/flush", flushDnsCache)</c>（★ 都是 <b>POST</b>，GET 会 405）；
    ///     成功一律 <c>render.NoContent</c> = <b>204</b>；
    ///     fakeip 那条在 <c>resolver.FlushFakeIP()</c> 报错时才 400 —— 而
    ///     <c>component/resolver/enhancer.go:73-78</c> 里 <c>DefaultHostMapper == nil</c>
    ///     （redir-host）时返回 <c>nil</c> ⇒ 也是 204，<b>不能把 204 读成"清掉了"</b>，
    ///     只能读成"控制器活着、端点在"。
    ///   · <c>hub/route/connections.go:16-20</c> + <c>:81-87</c> —— <c>DELETE /connections</c>
    ///     = <c>closeAllConnections</c>，遍历 <c>statistic.DefaultManager</c> 逐个 <c>c.Close()</c>，
    ///     恒 204。★ 这一条会<b>掐断在途连接</b>，所以只在会话边界调，绝不在会话中途调。
    ///
    /// ══ 为什么"挂起"那一半没做 ══
    ///   安卓的 <c>Clash.suspendCore(...)</c> 是 golang bind 直调内核函数；我们的桥是 C ABI
    ///   （<see cref="MihomoBridge"/>：start/stop/put_packet/get_packet/last_error/bound_iface），
    ///   REST 侧也没有 suspend 端点。加 suspend 要改 Go 重编 <c>clash_bridge.dll</c>，
    ///   本机没有 Go 工具链、且"装工具链属于改用户机器"（Docs/12 §5 第 12 项：未获同意不动）。
    ///   ★ 唯一"看起来能挂起"的 <c>POST /restart</c> 是<b>地雷</b>：它重新 spawn 进程，
    ///     而我们的内核就跑在承载 VPN 通道的那个进程里 —— 调它等于把数据面和平台通道一起掀掉。
    ///     本文件与本仓库任何入口都不得调用它。
    ///
    /// ══ 为什么 fire-and-forget ══
    ///   两个调用点都是平台同步回调（<c>Connect</c> 尾部的桥启动、<c>Disconnect</c> 的收尾主体）。
    ///   在回调里等 HTTP 就是把网络延迟挂到平台状态机上（同 <c>TryHotReload</c> 的理由），
    ///   而 <c>Disconnect</c> 之后进程随时可能被挂起/回收 ⇒ 会话结束那一臂本来就是<b>尽力而为</b>；
    ///   真正保证"干净起点"的是会话开始那一臂。两句都写进 plugin.log，读得到才算数。
    /// </summary>
    internal static class SessionReset
    {
        /// <summary>
        /// 控制器基址。★ 与 <c>config.yaml</c> 里的 <c>external-controller: 127.0.0.1:9090</c>
        ///   一致，也与 <c>ClashVpnPlugin.TryHotReload</c> 原先写死的那个地址一致 ——
        ///   现在两边共用这一个常量，换端口只改一处。
        /// </summary>
        internal const string ControllerBase = "http://127.0.0.1:9090";

        /// <summary>最近一次执行的结果句，写进 <c>dataface-plugin.txt</c> 的快照用。</summary>
        internal static string LastNote = "未执行";

        /// <summary>会话开始那一臂（桥已就绪之后）。延迟 1.5 s：让 <c>TryHotReload</c> 先落地。</summary>
        internal static void AtSessionStart() => Run("start", 1500);

        /// <summary>会话结束那一臂（尽力而为，进程可能随即被挂起）。</summary>
        internal static void AtSessionEnd() => Run("end", 0);

        private static void Run(string when, int delayMs)
        {
            int arm = LocalSettings.SessionReset;
            if (arm <= 0)
            {
                LastNote = $"when={when} 跳过（sessionReset=0）";
                return;
            }
            if (!s_bridgeKernelMayBeUp())
            {
                LastNote = $"when={when} 跳过（内核未运行）";
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    if (delayMs > 0)
                        await Task.Delay(delayMs);

                    var note = new StringBuilder($"when={when} arm={arm}");
                    using var http = new System.Net.Http.HttpClient(
                        new System.Net.Http.HttpClientHandler { UseProxy = false })
                    {
                        Timeout = TimeSpan.FromSeconds(3)
                    };

                    // 顺序：先关在途连接，再清解析缓存，最后才动 fake-ip 池
                    //（越靠后越不可逆：fake-ip 池是落盘的，见 LocalSettings.SessionReset 的档 2 注释）。
                    await PostAsync(http, note, "conns",
                        new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Delete,
                            ControllerBase + "/connections"));
                    await PostAsync(http, note, "dns",
                        new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post,
                            ControllerBase + "/cache/dns/flush"));
                    if (arm >= 2)
                    {
                        await PostAsync(http, note, "fakeip",
                            new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post,
                                ControllerBase + "/cache/fakeip/flush"));
                    }
                    else
                    {
                        note.Append(" fakeip=skip");
                    }

                    LastNote = note.ToString();
                    RollingLog.Info("session-reset", LastNote);
                }
                catch (Exception ex)
                {
                    // ★ 只记异常类型，不记 message 里的 URL —— 与本仓库其它通道一致，
                    //   失败必须可见，但绝不能把它冒到平台回调或数据面上。
                    LastNote = $"when={when} 失败={ex.GetType().Name}";
                    RollingLog.Warn("session-reset", LastNote + " :: " + ex.Message);
                }
            });
        }

        /// <summary>
        /// 发一个请求并把结果拼进 <paramref name="note"/>。
        /// 单个端点失败<b>不中断其余两个</b>：这条链的产出是"哪些端点通了"的证据，
        /// 不是必须原子的操作。
        /// </summary>
        private static async Task PostAsync(System.Net.Http.HttpClient http, StringBuilder note,
                                            string label, System.Net.Http.HttpRequestMessage req)
        {
            using (req)
            {
                try
                {
                    using System.Net.Http.HttpResponseMessage resp = await http.SendAsync(req);
                    note.Append($" {label}={(int)resp.StatusCode}");
                }
                catch (Exception ex)
                {
                    note.Append($" {label}={ex.GetType().Name}");
                }
            }
        }

        /// <summary>
        /// 内核此刻在不在跑 —— 读桥的进程级门闩（<c>StartMihomoBridge</c> 置位、
        /// 停桥/启动失败清零）。没有这一步，会话结束那一臂会在"内核从没起来"的轮次里
        /// 留下三条无意义的失败码，把证据搅浑。
        /// </summary>
        private static bool s_bridgeKernelMayBeUp()
        {
            try
            {
                return ClashVpnPlugin.BridgeRunningForReset;
            }
            catch
            {
                return false;
            }
        }
    }
}
