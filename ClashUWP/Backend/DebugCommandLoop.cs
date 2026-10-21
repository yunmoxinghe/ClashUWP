using System;
using System.Threading.Tasks;

using ClashUWP.Backend.Mihomo;
using ClashUWP.Vpn;

namespace ClashUWP.Backend
{
    /// <summary>
    /// 【后端 · 调试命令分发】一条命令表,两个入口:
    ///   1. 文件回路 <see cref="RunAsync"/>:轮询 <c>LocalState/debug-cmd.txt</c>,
    ///      结果写 <c>LocalState/debug-out.txt</c> —— 免重装 MSIX 就能操作 VPN。
    ///   2. AppService 通道(in-app「ClashUWP.Debug」,由 clashuwp-cli 调用,
    ///      接点在 <c>App.OnBackgroundActivated</c>):请求 ValueSet["cmd"] 一行命令,
    ///      响应 ValueSet["out"] / ValueSet["error"]。
    /// 两条通道必须逐字同义:同一条命令、同样的参数口径(大小写敏感的网卡名/
    /// 组名/节点名)、同样的 RollingLog("cmd") 记录 —— 误操作复盘时
    /// "哪条命令在什么时候被谁下发过"不能因为走了新门就查不到。
    ///
    /// 【命令表】
    ///   status / connect / disconnect / recreate / probe /
    ///   controller / conf / groups / logs /
    ///   select &lt;组名&gt; &lt;节点名&gt; / bind &lt;网卡名|-&gt; / mode &lt;rule|global|direct&gt; /
    ///   delay [节点名] / connections [kill &lt;id&gt;|kill-all] /
    ///   logtail [app|plugin|sessions] [行数]
    ///
    /// ★ 命令语义与写它的纪律：凡是 connect / disconnect / recreate / probe
    ///   这类会碰隧道或改 profile 的，外部不明确指定一条就不写。
    ///   ★ groups / conf / logtail / status / connections 是纯读；select / bind / mode / delay 只改
    ///     内核运行态（不动 config.yaml、不重装包），但 select 会改变"下一个节点是谁"，
    ///     复现网络问题时记得它被改过（内核把选择记在 &lt;home&gt;/cache.db，
    ///     而 home 目前是只读目录 ⇒ 重启后回到默认，见 Docs/12 §7）。
    ///   ★ 已删除 watchdog-off：全路由连接取消看门狗 = 整机断网后无法远程恢复。
    ///     现在只有「健康哨兵」这一种自动保护，见 <see cref="HealthMonitor"/>。
    /// </summary>
    internal static class DebugCommandLoop
    {
        /// <summary>
        /// 命令分发核心 —— 输入一行命令原文,返回结果文本。两个入口共用。
        ///
        /// 【异常约定】这里不做兜底 catch:调用方各自决定错误去向 ——
        ///   文件回路写进 debug-out.txt 的 ERROR= 行,AppService 回 ValueSet["error"]。
        /// </summary>
        internal static async Task<string> ExecuteAsync(string raw)
        {
            raw = raw.Trim();
            string cmd = raw.ToLowerInvariant();
            // ★ 参数部分保留原始大小写：网卡友好名（WLAN）是区分大小写的，
            //   mihomo 的 iface 缓存按 net.Interface.Name 精确匹配，
            //   "wlan" 会被判成「找不到网卡」→ 拨号直接失败。
            string arg = raw.IndexOf(' ') < 0
                ? "" : raw.Substring(raw.IndexOf(' ') + 1).Trim();

            // 命令原文进滚动日志：debug-out.txt 只留最后一次结果，
            // 而"哪条命令在什么时候被谁下发过"恰恰是复盘误操作时唯一要看的东西。
            RollingLog.Info("cmd", raw);

            switch (cmd)
            {
                case "status":
                    // ★ status：读内核运行态三要素（mode / log-level / interface-name）。
                    //   纯 REST，不碰隧道。G8 的最小可用通道。
                    return await MihomoController.RunStatusAsync();
                // ★ 三个会话动作一律走 VpnSession：武装/解除健康哨兵固定在
                //   VpnSession 里做，不再靠这条回路自己记得。顺序与原来一致：
                //   connect 先武装再连（即使连接卡死/异常，哨兵硬上限到点必断，
                //   网络绝不会被长期占死），disconnect 先解除哨兵再断。
                case var c when c == "connect" || c.StartsWith("connect "):
                    // ★ connect [订阅id]：不带参 = 连基础 profile；带参 = 连该订阅专属的系统 profile，
                    //   即「在快速设置里点那条订阅」的自动化等价物（同一平台路径，见 VpnSession.ConnectAsync）。
                    //   ★ subId 用 arg（原大小写）：cmd 已被 ToLowerInvariant，订阅 id 由 URL 派生，
                    //     路径段大小写可能非小写，lower 会拼不出文件名。
                    return await VpnSession.ConnectAsync(arg.Length > 0 ? arg : null);
                case "disconnect":
                    return await VpnSession.DisconnectAsync();
                case "recreate":
                    return await VpnSession.RecreateAsync();
                case "probe":
                    await StartupProbes.RunDatafaceProbeOnceAsync();
                    return await LocalState.ReadIfExistsAsync("dataface-v4.txt");
                case "controller":
                    return await MihomoController.RunControllerProbeAsync();
                case "conf":
                    return await MihomoController.RunConfigsDumpAsync();
                case "groups":
                    // ★ groups：列出可切换的组与成员（带最近一次测速）。
                    //   选节点前必须先看它 —— 组名是订阅派生的中文、节点名带
                    //   emoji 和前导空格，靠猜必错。纯读，不碰隧道。
                    return await MihomoController.RunGroupsAsync();
                case "logs":
                    return await MihomoController.RunLogStreamAsync(12);
                case var c when c.StartsWith("bind"):
                    // bind WLAN / bind -（清空）：运行时改出站绑卡，单变量 A/B 用，
                    // 不必动 config.yaml、也就不必重装包。
                    string want = arg;
                    if (want is "-" or "off" or "none") want = "";
                    return await MihomoController.RunBindAsync(want);
                case var c when c.StartsWith("mode"):
                    // ★ mode <rule|global|direct>：运行时改内核分流模式。
                    //   和 bind 同一个目的：单变量 A/B 不必动 config.yaml，
                    //   也就不必重装包。PATCH /configs 的 mode 字段即时生效。
                    return await MihomoController.RunModeAsync(arg);
                case var c when c.StartsWith("delay"):
                    // delay [节点名]：让内核立刻拨一次该节点并把结果原文带回来。
                    // 不填就用当前选中项。这是「拨号到底死在哪一步」最短的路，
                    // 也绕开了 /logs 的流式时序问题。
                    return await MihomoController.RunDelayAsync(arg);
                case var c when c.StartsWith("select"):
                    // ★ select <组名> <节点名> / select <节点名>：把切换动作
                    //   打进内核（PUT /proxies/{组}，实测只有 PUT 生效）。
                    //   ★【1.0.75 改口径】参数不再按"第一个空格"拆 ——
                    //     后端拿<b>本次 /proxies 的组名做最长前缀匹配</b>还原 (组, 节点)，
                    //     因为订阅派生的组名自己就有空格（实测 11 个组里 10 个带空格），
                    //     旧口径拆出来的是半截组名，绝大多数组根本选不动。
                    //   参数口径：整行先试着用组名当前缀，中不了才退回"第一个空格"，
                    //   所以带空格的节点名照抄可用，带空格的组名现在也可用。
                    //   ⚠ arg 是 Trim 过的：节点名<b>首尾</b>真带空格时这条路喂不进去
                    //     （先 groups 看清名字，那种命名极罕见；UI 走 (组,节点) 两个值，没这问题）。
                    //   只改内核运行态，不动 config.yaml ⇒ 不重装包、不碰隧道；
                    //   但下一次连接真的走哪个节点是它说了算，所以要复现网络
                    //   问题时得记得它把选中项改了。
                    return await MihomoController.RunSelectAsync(arg);
                case var c when c.StartsWith("connections") || c == "conn":
                    // ★ connections [kill <id>|kill-all]：G4 连接视图/强断。
                    //   纯读：列出活动连接的目标、链路、流量；
                    //   kill <id>：强断指定连接（DELETE /connections/{id}）；
                    //   kill-all：清空所有连接（DELETE /connections）。
                    //   不碰隧道也不改 profile，纯 REST 干预通道。
                    return await MihomoController.RunConnectionsAsync(arg);
                case var c when c.StartsWith("logtail"):
                    // ★ logtail：读滚动日志尾部。纯读，不碰隧道也不改 profile。
                    return RunLogTail(arg);
                case "sub" or "subscription":
                    // ★ sub：读 LocalState\config.yaml 的元信息（节点/规则/组/协议）。
                    //   纯读：不碰隧道、不重装包、不改订阅原文。
                    //   【刻意只读不做管理】从 URL 拉订阅、更新、多 profile 是 Docs/12 §5
                    //   的 G1 缺口，本命令只回答"现在装的是什么订阅"，不假装能换。
                    return SubscriptionView.OneLine(await SubscriptionView.LoadAsync());
                case var c when c.StartsWith("subfetch") || c.StartsWith("fetch"):
                    // ★ subfetch <URL>：拉一份订阅，落成 profile，并立即设为生效。
                    //   ★ 碰什么：写 LocalState\subscriptions\ 与 settings.txt，
                    //     并把生效那份复制到 config.yaml。**不碰隧道、不重启内核** ——
                    //     内核是常驻的，改了文件它不知道，要 reload 才生效（见 reload 命令）。
                    //   ★ 与 MihomoController 相反：这里必须走系统代理（订阅是公网地址，
                    //     用户在墙内；而那边的 UseProxy=false 是防"被自己下发的代理劫走"）。
                    {
                        string u = arg.Length > 0 ? arg : "";
                        if (u.StartsWith("subfetch")) u = u.Substring(8).Trim();
                        var r = await SubscriptionManager.FetchAsync(u, activate: true);
                        return r.Message;
                    }
                case var c when c.StartsWith("subupdate") || c == "update":
                    // ★ subupdate：按当前生效 profile 的 URL 重拉一次。
                    {
                        var r = await SubscriptionManager.UpdateActiveAsync();
                        return r.Message;
                    }
                case var c when c.StartsWith("sublist"):
                    // ★ sublist：列出所有 profile（▶ = 当前生效）。
                    {
                        var active = await SubscriptionManager.GetActiveAsync();
                        var list = await SubscriptionManager.ListAsync();
                        return SubscriptionManager.OneLine(list, active);
                    }
                case var c when c.StartsWith("subuse") || c.StartsWith("subswitch"):
                    // ★ subuse <id>：切到某份 profile（复制到 config.yaml）。
                    //   ★【1.0.86 · Docs/36 A 案】这条现在<b>会碰隧道</b>：ActivateAsync 末尾串行
                    //   把会话切到新订阅的 profile（已连接时先断旧的、等收尾收干净再拨新的）。
                    //   CLI 侧已把它从 state 提到 tunnel 并要求 --force —— 别在没带 --force 的
                    //   自动化里发它，更别在网络敏感时刻发。
                    {
                        string id = arg;
                        foreach (string pfx in new[] { "subuse", "subswitch" })
                            if (id.StartsWith(pfx)) { id = id.Substring(pfx.Length).Trim(); break; }
                        if (id.Length == 0) return "subuse 需要参数：subuse <profile id>（先用 sublist 看）";
                        var r = await SubscriptionManager.ActivateAsync(id);
                        return r.Message;
                    }
                case var c when c.StartsWith("subdel"):
                    // ★ subdel <id>：删一份 profile（不许删当前生效的）。
                    {
                        string id = arg.StartsWith("subdel") ? arg.Substring(6).Trim() : arg;
                        if (id.Length == 0) return "subdel 需要参数：subdel <profile id>";
                        var r = await SubscriptionManager.DeleteAsync(id);
                        return r.Message;
                    }
                case var c when c == "profiles" || c.StartsWith("profiles"):
                    // ★ profiles：列出本包名下的系统 VPN profile（名称/subId/是否进系统UI/状态）。
                    //   纯读。核验「快速设置 VPN 面板会出现哪几条」的判据就在这里 ——
                    //   系统 UI 不可自动化点击，但 RequireVpnClientAppUI 的回读值可以。
                    return await VpnProfileSetup.ListOurProfilesAsync();
                case var c when c.StartsWith("reload"):
                    // ★ reload <绝对路径>：主动热重载内核配置（PUT /configs?force=true）。
                    //   ★ 为什么必须有它：内核是**常驻**的（residentBridge=1），
                    //     Disconnect 不停桥 ⇒ 下次 Connect 走"复用常驻内核"分支，
                    //     内核**不会重新读磁盘上的 config.kernel.yaml**。
                    //     实测：改了磁盘文件的 tolerance，断开重连后 plugin.log 记的仍是
                    //     "复用常驻内核（…配置未变）" ⇒ 改了不生效且无任何提示。
                    //   ★ 不碰隧道（隧道由平台管），但会改内核运行态 ⇒ 与 select/mode/bind 同级。
                    //   路径不给时用默认的 LocalState\config.kernel.yaml。
                    return await MihomoController.RunReloadAsync(
                        arg.Length > 0 ? arg : DefaultKernelConfigPath());
                default:
                    RollingLog.Warn("cmd-unknown", cmd);
                    return $"未知命令: {cmd}（支持 status/connect [订阅id]/disconnect/recreate/probe/controller/conf/groups/profiles/logs/sub/sublist/subfetch <URL>/subupdate/subuse <id>/subdel <id>/select <组名> <节点名>/bind <网卡名|->/mode <rule|global|direct>/delay [节点名]/connections [kill <id>|kill-all]/logtail [app|plugin|sessions] [行数]/reload [配置绝对路径]；★ 已无 watchdog-off）";
            }
        }

        /// <summary>
        /// 默认的内核配置文件路径（<c>LocalState\config.kernel.yaml</c>）。
        /// ★【1.0.89 · Docs/38 §十四】改 <c>internal</c>：全仓只有<b>这里</b>算这个路径是对的
        ///   （走 <c>ApplicationData.Current.LocalFolder</c>），而 <c>MihomoController.DiagnoseKernelAsync</c>
        ///   在 <c>:1571</c> 把包目录名写死成了 <c>421769c5-…_y0tg80em3rma2</c> —— 那是 1.0.75 改名<b>之前</b>
        ///   的家族名（现读 <c>git log -S</c> 为证：主清单 Identity Name 在 <c>3b11f69</c> 才换成
        ///   <c>8B8CC4F4.ClashUWP</c>），所以那条诊断从 1.0.75 起<b>永远</b>打"内核配置文件不存在"。
        ///   出口只留一个：诊断侧引这个方法，不许再自己拼路径。
        /// ★【1.0.91】算法搬进 <see cref="LocalStatePaths"/>，这里只留一层转发。原因是上一批搬完之后
        ///   <c>MihomoController.cs</c> 出现对 <c>DebugCommandLoop</c> 的引用，而离线夹具
        ///   （<c>Tools/YamlShapeTest</c>，靠 link 源文件编译）link 了 <c>MihomoController.cs</c>
        ///   却没 link 也 link 不动这个类 —— 它依赖命令分发、RollingLog、VPN 会话一整条链
        ///   ⇒ 夹具当场 <c>CS0103 不存在名称"DebugCommandLoop"</c>（当时只验了主工程 Debug 编译，没跑夹具，
        ///   属于自查漏掉）。转发的意义：命令侧的既有调用点与文档口径都不用改。
        /// </summary>
        internal static string DefaultKernelConfigPath() => LocalStatePaths.DefaultKernelConfigPath();

        /// <summary>
        /// LocalState 文件调试入口 —— 免重装操作 VPN。
        ///
        /// 【用法】写 <c>LocalState/debug-cmd.txt</c>（单行命令），1 秒内被本循环
        /// 消费并重写为 <c>LocalState/debug-out.txt</c>(命令语义见 <see cref="ExecuteAsync"/>)。
        ///
        /// 【为什么还保留文件通道】AppService 通道要求主进程活着且清单已声明
        ///   服务;文件通道是断网自救的兜底(隧道出问题时主应用可能正处于
        ///   非响应态,写文件不依赖任何 IPC 通路)。两条通道并存,命令表同源。
        /// </summary>
        internal static async Task RunAsync()
        {
            while (true)
            {
                // 命令原文提到 try 外面：catch 里也要知道是哪条命令炸的
                // （否则日志只有一句"回路异常"，对不上是哪次下发）。
                string raw = "";
                try
                {
                    Windows.Storage.StorageFile? cmdFile =
                        await LocalState.TryGetFileAsync("debug-cmd.txt");
                    if (cmdFile != null)
                    {
                        raw = (await LocalState.ReadTextAsync(cmdFile)).Trim();
                        // ★ 消费即删：不删的话下一轮会把同一条命令再执行一次 ——
                        //   对 connect/disconnect 这种动作来说，重复执行就是灾难。
                        await LocalState.DeleteQuietAsync(cmdFile);

                        string result = await ExecuteAsync(raw);

                        await LocalState.WriteTextAsync("debug-out.txt",
                            $"cmd={raw.ToLowerInvariant()}\ntime={DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{result}");
                    }
                }
                catch (Exception ex)
                {
                    // ★ 回路的异常只进结果文件，绝不让这个 while 死掉 ——
                    //   它一停，外部就再也无法通过写文件断开 VPN，等于把自己锁死。
                    //   记一行是因为"回路还活着但这条命令炸了"和"回路死了"从外部
                    //   看是同一个现象（debug-out.txt 不再更新），得能在日志里分开。
                    RollingLog.Error("cmd-loop", raw, ex);
                    await LocalState.WriteTextQuietAsync("debug-out.txt",
                        $"ERROR={ex.GetType().Name}: {ex.Message}");
                }

                await Task.Delay(1000);
            }
        }

        /// <summary>
        /// <c>logtail [app|plugin|sessions] [行数]</c> —— 读滚动日志尾部。
        ///
        /// 【为什么要这条命令】取证以前要靠桌面端直接打开包目录里的文件，而
        ///   <c>LocalState</c> 在包内（<c>%LOCALAPPDATA%\Packages\&lt;包名&gt;\LocalState</c>），
        ///   包名是个 GUID，每次重装还变；再加上日志按大小滚动出 <c>.1</c> / <c>.2</c>，
        ///   手工找很费劲。这条命令把"取尾部"这件事收进 app 内部，外部只管写一行命令。
        ///
        /// ★ 纯读：不碰隧道、不改 profile、不发消息 —— 按用户的纪律，
        ///   这类只读命令随时可以下发。
        ///
        /// 【为什么默认只给 40 行、上限 400】结果要写进 <c>debug-out.txt</c> 并原样回显，
        ///   把整个日志塞回来会把读它的人淹掉；要看更多就分几次读。
        /// </summary>
        private static string RunLogTail(string arg)
        {
            string[] parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string which = parts.Length > 0 ? parts[0].ToLowerInvariant() : "app";
            int lines = parts.Length > 1 && int.TryParse(parts[1], out int n) ? n : 40;
            if (lines <= 0) lines = 40;
            if (lines > 400) lines = 400;

            // ★ 读之前先把<b>本进程</b>的队列排空一点：日志是攒批落盘的（最多 64 行 / 200 ms），
            //   不 flush 的话"刚发生的事"很可能还排在内存里，读出来像是没记。
            //   这里是 app 的调试回路线程，不是平台同步回调 ⇒ 等得起这 150 ms。
            //   注：plugin 汇归插件进程，前台叫不动它的队列，只能指望那个 200 ms 批窗口。
            RollingLog.Flush(150);

            string body = which switch
            {
                // plugin：vpnClient 后台任务进程写的日志 —— 数据面（Connect/Encapsulate/
                //         Disconnect）那侧的现场只有这个文件里有。
                "plugin" => RollingLog.TailLog(RollingLog.Role.Plugin, lines),
                // sessions：机器在解析的那份会话事件流（重拨保险丝的数据源）。
                "sessions" => RollingLog.TailSessions(lines),
                _ => RollingLog.TailLog(RollingLog.Role.App, lines),
            };

            // ★ 顺带回一句 Diag()：日志自身的健康度（丢了行就是丢了行），
            //   不然"日志里没有"会被误读成"事情没发生"。
            return $"[logtail {which} 尾部 ≤{lines} 行]\n{body}\n{RollingLog.Diag()}";
        }
    }
}
