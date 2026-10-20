using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using ClashUWP.Vpn;

namespace ClashUWP.Backend
{
    /// <summary>
    /// 【后端 · 健康哨兵】对齐安卓版③「连不上自动退回直连」。
    ///
    /// 谁武装它：两条路 —— UI 的「连接 VPN」按钮走 <see cref="VpnSession"/>，
    /// debug 回路的 connect 命令走 <see cref="DebugCommandLoop"/>。
    /// 两者都必须先武装再连接：全路由隧道一旦黑洞就是整机断网，
    /// 哨兵是唯一把保护性交回直连的自动机制。
    ///
    /// ★ 它是后端而不是前端：判据、断开、删 profile、写 health.txt 全都不需要 UI，
    ///   而且必须能在没人盯着的时候自己收尾。
    /// </summary>
    internal static class HealthMonitor
    {
        private static CancellationTokenSource? _healthCts;

        /// <summary>
        /// 武装健康哨兵（connect 前调用；重复调用只保留最后一个）。
        /// ★ 调用方是 <see cref="VpnSession.ConnectAsync"/> 与
        ///   <see cref="DebugCommandLoop"/>，两条连接路径都必须先武装再连 ——
        ///   漏一条就等于那条路没有黑洞保护。
        /// </summary>
        internal static void Arm()
        {
            bool hadActive = _healthCts != null;
            Cancel();
            _healthCts = new CancellationTokenSource();
            _ = RunAsync(_healthCts.Token);

            // ★ 每次武装都留一行：哨兵是"黑洞时谁把网络交回直连"的唯一自动机制，
            //   事后必须能回答"这一次连接到底有没有保护"。旧写法靠人记，
            //   1.0.35 把武装收进 VpnSession 之后仍要用日志证明它真的发生了。
            RollingLog.Info("health-arm", hadActive ? "重新武装（顶掉上一个哨兵）" : "武装健康哨兵");
        }

        /// <summary>解除哨兵（手动断开即取消：连接已经不在了，没必要再探）。</summary>
        internal static void Cancel()
        {
            // ★ 本方法不记日志：<see cref="Arm"/> 第一句就是它，记了会在每次武装时
            //   多一条假的"已解除"。"谁解除了保护"这件事由调用方（VpnSession）记。
            _healthCts?.Cancel();
            _healthCts = null;

            // ★ 兜底安全垫也一起停掉：隧道都拆了，没必要再探物理网络。
            _physicalWatchdogCts?.Cancel();
            _physicalWatchdogCts = null;
        }

        private static CancellationTokenSource? _physicalWatchdogCts;

        /// <summary>
        /// ★【1.0.80】"这一轮<b>读不出</b>状态"的专用文本（超时或抛异常时用）。
        /// 它必须和 <c>Connecting</c> 这类<b>真实的非终态</c>分开：旧写法把"读不到"
        /// 和"卡在正在连"混成同一个 <c>st</c>，于是下面那句"非终态 ⇒ 断开把网络交回直连"
        /// 会在<b>判据根本不可信</b>的时候拆掉一条其实健康的隧道。
        /// 口径与 <see cref="PhysicalNetworkProbe"/> 一致：判据瞎了只记日志，不动手。
        /// </summary>
        private const string StatusUnreadable = "状态读不出";

        /// <summary>
        /// 读一次连接状态，<b>最多等 <paramref name="timeoutMs"/> 毫秒</b>。
        ///
        /// 【为什么要限时】<c>VpnManagementAgent.GetProfilesAsync()</c> 是平台 RPC，
        ///   活动会话期间它有可能长时间不返回（本机 1.0.79 实测症状：武装后 30 轮前置确认
        ///   一次都没走出来，前台黑洞判定与 1800 s 硬上限全程没跑，日志零痕迹）。
        ///   超时不是失败，只是"这一轮没看见" —— 返回 <see cref="StatusUnreadable"/>，
        ///   调用方下一轮再读；真正的判定要靠"读得出来"的那些轮次。
        /// 【为什么不用 token 取消底层操作】投影出来的 IAsyncOperation 没有可靠的
        ///   取消语义（Cancel 只取消我们这一侧的等待，平台那边照样跑完）。所以这里
        ///   故意留一条"迟到也没人读"的任务：它最多占一个 RPC，不会连锁影响判定，
        ///   换来的是<b>哨兵绝不会被一次不返回的读操作钉死在第一步</b>。
        /// </summary>
        private static async Task<string> ReadStatusBoundedAsync(int timeoutMs)
        {
            Task<string> read = VpnProfileSetup.OurStatusAsync();
            Task winner = await Task.WhenAny(read, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (winner != read) return StatusUnreadable;
            return await read.ConfigureAwait(false);
        }

        /// <summary>
        /// ★【1.0.80 · 实测根因】哨兵的<b>外壳</b>：只干一件事 —— 让 <see cref="RunCoreAsync"/>
        ///   的任何异常都必须留痕。
        ///
        /// 【为什么单独拆一层】<see cref="Arm"/> 是 `_ = RunAsync(token)`（发后即忘），
        ///   而 core 第一句日志要等到 30 次前置确认跑完才出现。于是 core 里任何一次抛异常
        ///   都会变成一个<b>没有任何痕迹</b>的失败任务（.NET 里未observed的 Task 异常不崩进程、
        ///   不进日志）。1.0.79 实机就是这样：两次武装（07:11:49 / 07:13:40）之后
        ///   既没有 <c>health 隧道已建立</c>、也没有 <c>health 隧道未建立</c>、
        ///   更没有前台 <c>physical-watchdog 启动</c>，而同一进程里插件侧看门狗与 3 秒轮询
        ///   全程正常 ⇒ 前台黑洞判定与 1800 s 硬上限<b>整轮根本没在跑</b>，日志上却完全看不出来。
        ///   这条保护是"黑洞时谁把网络交回直连"的唯一自动机制，它瞎了必须出声。
        /// </summary>
        private static async Task RunAsync(CancellationToken token)
        {
            try
            {
                await RunCoreAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 手动断开走的就是取消这条路，属于正常退出，不是故障 —— 不进 Error。
            }
            catch (Exception ex)
            {
                // ★ 这一行是"哨兵死过一次"的唯一证据，必须是 Error 级别：
                //   它意味着从这一刻起，这次连接既没有黑洞判定也没有硬上限。
                RollingLog.Error("health-crash", ex);
            }
        }

        /// <summary>
        /// 健康哨兵主体（对齐安卓版③「连不上自动退回」）。
        ///
        /// ★【1.0.80】本方法内<b>每一个 await 都带 <c>ConfigureAwait(false)</c></b>：
        ///   武装点在 <see cref="VpnSession.ConnectAsync"/> 里，而那是<b>UI 按钮</b>的调用链
        ///   （<c>MainComponent</c> 的连接按钮）。发后即忘的任务会继承调用方的
        ///   SynchronizationContext ⇒ 哨兵的每一步续作都要排队回 UI 线程。一个后台保险丝
        ///   不该把命脉挂在"用户那个窗口还在泵消息"上 —— UI 卡顿/挂起/被关，保护就一起没了。
        ///   加上外壳的 try/catch，1.0.79 那个"武装之后整条静默"的症状两种成因
        ///   （续作回不来 / 抛异常没人接）现在要么消失、要么出声。
        ///
        /// 【对照安卓】ClashMetaForAndroid 里<b>没有</b>隧道健康看门狗：它的「自动退回」
        ///   靠的是 <c>protect()</c> 逐 socket 绑定物理网卡 + 路由空洞 + 规则组兜底，
        ///   真正的兜底动作只有 <c>finally { tun.close(); stopSelf(); }</c>（进程/会话结束
        ///   就回收 VPN）。Windows 侧没有 <c>protect()</c>，等价物就是平台给的
        ///   <c>Ipv4ExclusionRoutes</c>（本次已下发节点 /32）+ <b>主动断开</b>：
        ///   隧道不健康时把网络交还给直连，这就是本方法存在的全部理由。
        ///
        /// 【判定口径】只认「国内 IP 直连不通」= 隧道黑洞。
        ///   探针选国内必达的 DoH 端点（223.5.5.5:443 / 119.29.29.29:443，
        ///   都是 IP 字面量 → 不依赖 DNS，避免把 DNS 故障误判成断网）；
        ///   两个都失败才算一轮失败，<b>连续两轮</b>才断开（抗抖动）。
        ///   DNS 探针（www.baidu.com）单独记录，不参与断开判定。
        ///   国外探针（8.8.8.8）只作为节点质量参考，失败绝不触发断开 ——
        ///   否则「节点慢」会被误判成「隧道坏了」，反复踢连接。
        ///
        /// ★ 兜底安全垫：即使隧道内探针全绿，只要<b>整机物理出口</b>连续
        ///   5 分钟不可达（每 30 秒一轮，一轮里多候选探针 + 平台自评全失败），
        ///   就认为整机网络已坏，自动拆隧道并退出。这条是最后防线，不影响现有逻辑。
        ///   ★【1.0.78 订正】判据与动作口径见 <see cref="PhysicalNetworkProbe"/>：
        ///     旧口径（单探 114.114.114.114:443）在<b>健康机器上必然失败</b>，
        ///     会把隧道拆掉并把基础 profile 删了（Docs/30 §8-B、Docs/32 §四 P2）。
        ///   ★ 而且它现在只在<b>隧道确实建立之后</b>才被武装（Docs/30 §8-A）：
        ///     连接根本没建起来的会话没有兜底对象，不该由它删 profile。
        ///
        /// 【为什么断开后还要删 profile】哨兵收尾的会话都是「已经判死」的会话，
        ///   而只要 profile 还在，RasMan 就会无条件重拨（实测单日 413 次、
        ///   终止原因码 828 占 411 次、发起者 SYSTEM）。断开只停当下一次，
        ///   删 profile 才是拆掉自动重拨的燃料。代价：想再连必须先点「建/重建 profile」。
        ///   ★【1.0.78 收窄 · Docs/30 §8-A】前提"<b>已经判死</b>"要真的成立才删：
        ///     隧道从未 Connected 的会话（空壳不拨号、平台拒绝）不在此列，直接 return；
        ///     物理兜底那条在<b>判据不可信</b>时也不在此列。
        ///     删 profile 让入口消失、拆隧道可逆，两者代价不对等，不许落在猜的那一侧。
        ///
        /// 【为什么仍保留硬上限】黑洞判定依赖探针本身可信；一旦探针逻辑有 bug，
        ///   整机网络会被长期占住。硬上限是无条件兜底：到点必断，不依赖任何判断。
        ///
        /// ★ 1.0.15：上限从 <c>240 s</c>（两个重拨周期的实验时长）改回 <c>1800 s</c>。
        ///   240 s 是"先保住机器再用"的临时护栏，不是产品行为 —— 用户要的是连上之后
        ///   能一直用。真正防止"无限占网"的换成下面这条<b>重拨检测</b>：
        ///   插件每次被平台叫起来都会在 <c>LocalState\dataface-sessions.txt</c>
        ///   追加一行 <c>evt=connect-begin</c>，哨兵只数这个计数；我们只发起过 1 次连接，
        ///   所以计数 &gt; 基线+1 就是 RasMan 自己重拨了。实测 828 之后系统会立即重拨
        ///   （单日 413 次、间隔 97±2 s 就是这么来的），一旦看到第 2 次非我们发起的
        ///   connect，立刻断开 + 删 profile —— 判据驱动，不再靠计时器。
        /// </summary>
        private static async Task RunCoreAsync(CancellationToken token)
        {
            const int intervalSeconds = 10;      // 每 10 秒一轮
            const int maxConsecutiveFailures = 2; // 连续 2 轮失败 → 判定黑洞
            const int hardCapSeconds = 1800;      // 无条件兜底 30 分钟（判据失灵时的保险丝）
            const int maxUnmanagedRedials = 1;    // 允许自愈 1 次，第 2 次非我们发起的 dial 就拆

            // ★【1.0.78 订正 · Docs/30 §8-A】兜底安全垫的武装点<b>挪到前置确认之后</b>（见下方
            //   tunnelUp 分支）。原先这里是"武装哨兵第一件事就起物理兜底"，而物理兜底的动作是
            //   拆隧道 + <b>删 profile</b> —— 于是"这次连接根本没建起来"的会话（空壳不拨号、
            //   节点全挂、平台拒绝）也会被它在 5 分钟后删掉基础 profile，
            //   用户视角就是"快速设置里我们的条目自己消失了"。
            //   物理兜底保护的是"隧道装着但出口死了"，隧道不存在时它没有任何保护对象。
            _physicalWatchdogCts?.Cancel(); // 停掉上一任

            var sb = new StringBuilder();
            sb.AppendLine($"=== 健康哨兵 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");

            int failures = 0;
            string reason = "";
            var deadline = DateTime.Now.AddSeconds(hardCapSeconds);

            // ★ 基线 = 本哨兵被武装的时刻。之后每轮数「这之后出现过几次 connect-begin」，
            //   减掉我们自己那一次，剩下的就是 RasMan 自发的重拨。
            DateTime armedAt = DateTime.Now;
            sb.AppendLine($"基线时刻 {armedAt:HH:mm:ss}（只数这之后的 connect-begin，" +
                          $"允许非本次发起的重拨 {maxUnmanagedRedials} 次）");

            // ★ 前置确认：隧道真的建立了才进黑洞判定。没隧道时探针量到的是<b>直连</b>
            //   网络（国内 OK、国外超时、收尾 断开=NoConnection），会被读成
            //   「隧道健康但海外不通」——上一轮就是这么把整条排查带偏的。
            //
            // ★【1.0.80】三次加固，全部因为 1.0.79 实机"武装后整条静默"：
            //   ① 读状态<b>单次限时 3 s</b>（<see cref="ReadStatusBoundedAsync"/>）：
            //      平台 profile 存储被活动会话占住时 GetProfilesAsync 有可能永不返回，
            //      旧写法一个 await 卡死 = 哨兵永远停在第一步。
            //   ② 读状态<b>抛异常只跳过这一轮</b>，不再让异常穿出整条链路
            //      （旧写法一抛就靠外壳兜底，兜住了也等于这次连接没有保护）。
            //   ③ 第一步就记一行 <c>health-precheck</c>：
            //      "哨兵进循环了没有"从此有正面证据，不再只能靠"后面那行没出现"反推。
            string st = StatusUnreadable;
            int precheckErrors = 0;
            int precheckSuccesses = 0;   // ★【1.0.80 源码态】正面证据行按"首次<b>成功</b>"打
            for (int i = 0; i < 30; i++)
            {
                string read;
                try
                {
                    read = await ReadStatusBoundedAsync(3000).ConfigureAwait(false);
                }
                catch (TaskCanceledException) { return; }
                catch (Exception ex)
                {
                    precheckErrors++;
                    read = StatusUnreadable;
                    RollingLog.Warn("health-precheck",
                        $"第 {i + 1} 次读状态异常：{ex.GetType().Name}: {ex.Message}");
                }
                st = read;
                // ★【1.0.80 源码态】判据从 i==0 改成"第一次读到真状态"：
                //   1.0.80 装机版仍按 i==0 打，实测第 1 次就抛 COMException(0x80070006) ⇒
                //   那一次只有 warn 行、没有正面证据行（1.0.80 靠后面那行
                //   "隧道已建立，进入黑洞判定循环"反推哨兵活着，勉强够）。
                //   下一版起：首轮异常后第二轮读通时照样打一行，
                //   "哨兵进循环了没有"从第一次<b>成功读数</b>起就有正面证据。
                if (st != StatusUnreadable && ++precheckSuccesses == 1)
                    RollingLog.Info("health-precheck",
                        $"第 {i + 1} 次读状态={st}（成功 1 次，异常 {precheckErrors} 次）");
                if (st == "Connected") break;
                try { await Task.Delay(1000, token).ConfigureAwait(false); }
                catch (TaskCanceledException) { return; }
            }

            bool tunnelUp = st == "Connected";
            // ★【1.0.80】状态读不出（超时/异常）时<b>不许当成"卡住的连接"</b>去断开：
            //   判据不可信就不动手，口径与 <see cref="PhysicalNetworkProbe"/> 一致
            //   （代价不对等：误断一个健康隧道的代价远大于这一轮少看一次）。
            bool statusBlind = st == StatusUnreadable;
            if (statusBlind)
                RollingLog.Warn("health",
                    $"前置确认 30 轮读不出状态（异常 {precheckErrors} 次）⇒ 本轮不做黑洞判定、不做断开");
            else if (!tunnelUp)
                reason = $"隧道未建立（状态={st}）→ 探针只会量到直连网络，不做黑洞判定";
            else
                sb.AppendLine("隧道已连接，进入探测循环");

            // ★ 这一行决定后面所有探针读数的口径（有隧道=隧道内，无隧道=直连），
            //   所以必须进发布版日志：health.txt 里同样有，但它是追加式流水账，
            //   按时间轴对齐 app.log / plugin.log 时以滚动日志为准。
            RollingLog.Info("health", statusBlind
                ? "状态读不出，跳过黑洞判定（不拆不删）"
                : tunnelUp
                ? $"隧道已建立，进入黑洞判定循环（上限 {hardCapSeconds}s）"
                : $"隧道未建立（状态={st}），跳过黑洞判定");

            if (statusBlind)
            {
                sb.AppendLine("★1.0.80：状态读不出 ⇒ 判据不可信，不动手（不断开、不删 profile）");
                try { await LocalState.AppendTextAsync("health.txt", sb + "\r\n").ConfigureAwait(false); }
                catch (Exception ex) { RollingLog.Error("health-write", ex); }
                return;
            }

            // ★【1.0.78 · Docs/30 §8-A】隧道根本没建立的会话<b>不进收尾</b>。
            //   "断开后必须删 profile"这条设计的前提写在方法注释里：
            //   <b>哨兵收尾的会话都是「已经判死」的会话</b> —— 没建成的连接不是判死的会话，
            //   它没有 RasMan 重拨燃料要拆（Docs/30 §6 第 5 项实测：abort 后平台一次都没重拨）。
            //   旧写法只把 reason 设成"隧道未建立…"就照样往下走 L190 的
            //   Disconnect + DropProfile，于是把承载面板条目的基础 profile 删了。
            //   ★ 收尾只留给"曾经 Connected"的会话 —— 这一条同时也修了 §8-B
            //   里"空壳拨号 5 分钟后条目自己消失"的那一半。
            if (!tunnelUp)
            {
                sb.AppendLine(reason);
                // ★ 唯一保留的动作：卡在"正在连"这类<b>非终态</b>时把网络交回直连
                //   （这正是"先武装再连接"想要的保证 —— 连接卡死也不能长期占网）。
                //   但<b>不删 profile</b>：卡死的连接没有 RasMan 重拨燃料要拆，
                //   而删掉 profile 的代价是入口消失，两者不对等（Docs/30 §8-A/§8-B ②）。
                //   终态（Disconnected / Profile缺失）连这一步都不需要，直接记完就走。
                //   ★ 判定用"排除终态"而不是枚举非终态名：<c>VpnConnectionStatus</c> 的
                //     成员名不凭记忆写（这层是枚举原文，多出什么状态不重要，一律按"还没落地"处理）。
                string? stuckDisc = null;
                if (st != "Disconnected" && st != "Profile缺失")
                {
                    try { stuckDisc = await VpnProfileSetup.DisconnectAsync().ConfigureAwait(false); }
                    catch (Exception ex) { stuckDisc = $"断开异常: {ex.GetType().Name}: {ex.Message}"; }
                    sb.AppendLine($"非终态（{st}）→ 只断开把网络交回直连：{stuckDisc}");
                }
                sb.AppendLine("★1.0.78：隧道未建立 ⇒ 不删 profile（收尾只留给曾 Connected 的会话）");
                RollingLog.Info("health-trigger",
                    $"隧道未建立（状态={st}）：{(stuckDisc ?? "终态，无需动作")}，不删 profile");
                try { await LocalState.AppendTextAsync("health.txt", sb + "\r\n").ConfigureAwait(false); }
                catch (Exception ex) { RollingLog.Error("health-write", ex); }
                return;
            }

            // ★ 到这里隧道确实连着 —— 才是物理兜底该存在的时刻（"隧道装着、出口死了"）。
            _physicalWatchdogCts = new CancellationTokenSource();
            _ = PhysicalNetworkWatchdogAsync(_physicalWatchdogCts.Token);

            while (tunnelUp && DateTime.Now < deadline)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), token).ConfigureAwait(false); }
                catch (TaskCanceledException) { return; } // 手动断开，正常退出

                string cn1 = await TcpProbeAsync("223.5.5.5", 6000).ConfigureAwait(false);
                string cn2 = await TcpProbeAsync("119.29.29.29", 6000).ConfigureAwait(false);
                string dns = await TcpProbeAsync("www.baidu.com", 6000).ConfigureAwait(false);
                string overseas = await TcpProbeAsync("8.8.8.8", 6000).ConfigureAwait(false); // 仅参考

                // ★ 重拨检测（占网保险丝，取代原来的 240 s 计时器）：
                //   武装之后出现的 connect-begin 减掉我们自己那一次，剩下的都是平台自发的。
                //   允许 1 次是刻意留的自愈空间：实测 828 后平台立刻重拨，第二段会话
                //   反而稳定（run 29-B/29-C 各活到哨兵上限），一刀切会让用户白掉线；
                //   再多一次就说明进入了"97 s 一拆一拨"的死循环，立刻拆干净。
                int connects = await CountRecentConnectsAsync(armedAt).ConfigureAwait(false);
                int unmanaged = connects - 1;

                bool roundOk = cn1 == "OK" || cn2 == "OK";
                failures = roundOk ? 0 : failures + 1;
                sb.AppendLine($"{DateTime.Now:HH:mm:ss} 国内[{cn1}|{cn2}] DNS[{dns}] 国外[{overseas}]" +
                              $" 非本次dial={Math.Max(unmanaged, 0)}" +
                              $" → {(roundOk ? "隧道健康" : $"不健康 x{failures}")}");

                if (unmanaged > maxUnmanagedRedials)
                {
                    reason = $"检测到 {unmanaged} 次非本次发起的 connect（阈值 {maxUnmanagedRedials}）" +
                             $"→ 平台在自动重拨，立即拆掉";
                    break;
                }

                if (failures >= maxConsecutiveFailures)
                { reason = $"连续 {failures} 轮国内不可达 → 隧道黑洞，自动退回直连"; break; }
            }

            if (reason.Length == 0)
                reason = $"到达硬上限 {hardCapSeconds}s（无条件兜底）→ 断开";

            // ★ 哨兵主动动手 = 用户网络被交回直连的那一刻。这一行是"为什么我的 VPN
            //   自己断了"的第一现场，必须能在发布版里查到。
            RollingLog.Warn("health-trigger", reason);

            string disc;
            try { disc = await VpnProfileSetup.DisconnectAsync().ConfigureAwait(false); }
            catch (Exception ex) { disc = $"断开异常: {ex.GetType().Name}: {ex.Message}"; }

            // ★ 断完立刻删 profile：不给 RasMan 留可重拨的对象（详见方法上的说明）。
            string drop;
            try { drop = await VpnProfileSetup.DropProfileAsync().ConfigureAwait(false); }
            catch (Exception ex) { drop = $"删 profile 异常: {ex.GetType().Name}: {ex.Message}"; }

            RollingLog.Info("health-trigger", $"收尾动作：{disc}，{drop}");

            sb.AppendLine($"{reason}\n断开={disc}\n删profile={drop}");
            try
            {
                // health.txt 是流水账（追加），debug-out.txt 是"最后一条命令的结果"（覆盖）——
                // 让外部无论用哪条路径都能看到哨兵为什么动手。
                await LocalState.AppendTextAsync("health.txt", sb + "\r\n").ConfigureAwait(false);
                await LocalState.WriteTextAsync("debug-out.txt",
                    $"cmd=health-monitor-auto-disconnect\ntime={DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{sb}")
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 哨兵已经动过手了，结果文件写不写得了不再影响网络 —— 但要在滚动日志里
                // 留一句，免得将来对着"health.txt 里最后一次还是三小时前"发懵。
                RollingLog.Error("health-write", ex);
            }
        }

        /// <summary>
        /// ★ 兜底安全垫：整机<b>物理出口</b>不可达时自动退出隧道。
        ///
        /// 【为什么要】健康哨兵的主探针走的是<b>隧道内</b>的 TCP 握手（223.5.5.5:443 等），
        ///   如果隧道本身还活着但底层物理网卡已经断网，主探针会一直读到"OK"（因为流量
        ///   被黑洞吞掉后平台可能仍返回连接成功假象）。这条独立探针走<b>默认路由</b>、
        ///   连续失败即认为整机网络已坏。
        ///
        /// 【★【1.0.78】口径全部订正 —— 旧写法在这里被实机判死】
        ///   旧实现是"单目标 <c>114.114.114.114:443</c>，10 次失败 → 拆隧道 + 删 profile"，
        ///   注释写着"探的是物理直连而非隧道，所以结果可信"。<b>两句都不成立</b>：
        ///   · 该目标连干净直连都不应答（Docs/32 §二实测：443 不通、同 IP 的 53 通，
        ///     而那一刻 baidu 200/0.036s —— 机器是好的，探针在失败）；
        ///   · 普通 TCP 走操作系统默认路由，第三方 VPN 抢走默认路由就让它必然失败
        ///     （Docs/30 §8-B 实机：8/10 次全在健康网络上数出来）。
        ///   ⇒ 后果是每台装了常驻第三方 VPN 的机器，连接 5 分钟后隧道被拆、
        ///     基础 profile 被删（入口消失）。
        ///   现在判据与动作统一收进 <see cref="PhysicalNetworkProbe"/>：
        ///   多候选 OR + 平台自评（NCSI）背书，<b>判据不可信时只记日志、不拆不删</b>，
        ///   且动手前要求<b>此刻复核仍一致</b>。
        /// </summary>
        private static async Task PhysicalNetworkWatchdogAsync(CancellationToken token)
        {
            const int intervalSeconds = 30;
            const int maxFailures = 10;

            int failures = 0;
            RollingLog.Info("physical-watchdog",
                $"启动：每 {intervalSeconds}s 一轮，{maxFailures} 轮全失败才动手；" +
                $"判据=多候选探针 OR 平台自评（插件同一条，共用实现）");

            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), token).ConfigureAwait(false); }
                catch (TaskCanceledException) { return; }

                bool healthy;
                bool trusted;
                string detail;
                try
                {
                    (healthy, trusted, detail) = await PhysicalNetworkProbe.CheckAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // 探针自己抛 = 判据瞎了；瞎了就不许动手（代价不对等那条）。
                    RollingLog.Warn("physical-watchdog",
                        $"本轮判定异常，按不可信处理：{ex.GetType().Name}: {ex.Message}");
                    return;
                }

                if (healthy)
                {
                    if (failures > 0)
                        RollingLog.Info("physical-watchdog", $"恢复：出口可达（之前连续失败 {failures} 轮）");
                    failures = 0;
                    continue;
                }

                if (!trusted)
                {
                    RollingLog.Warn("physical-watchdog",
                        $"出口判为不可达但{detail} ⇒ 本条兜底解除，不做拆隧道/删 profile 动作");
                    return;
                }

                failures++;
                RollingLog.Warn("physical-watchdog",
                    $"出口不可达，累计失败 {failures}/{maxFailures}　{detail}");

                if (failures < maxFailures) continue;

                // 动手前复核一次：拆隧道 + 删 profile 的代价不可逆，
                // 要求"此刻"仍判不出可达、且判据仍然可信。
                bool stillDown = false;
                string recheck = "";
                try
                {
                    (bool h2, bool t2, string d2) = await PhysicalNetworkProbe.CheckAsync().ConfigureAwait(false);
                    stillDown = !h2 && t2;
                    recheck = d2;
                }
                catch (Exception ex) { recheck = $"复核异常={ex.GetType().Name}"; }

                if (!stillDown)
                {
                    RollingLog.Info("physical-watchdog",
                        $"累计 {maxFailures} 轮后复核撤回动手（判据不可信或已恢复）：{recheck}");
                    return;
                }

                RollingLog.Error("physical-watchdog",
                    $"连续 {maxFailures} 轮物理出口不可达且复核一致 → 整机网络已坏，自动拆隧道");

                // 停掉主哨兵，避免两边同时动手
                Cancel();

                // ★ 与插件那条走<b>同一个收尾实现</b>（原先前台自己拼 DisconnectAsync + DropProfileAsync，
                //   插件走 GracefulTeardown —— Docs/12 §3 早就点过"同一份代码里两种拆法"）：
                //   GracefulTeardown 会先确认会话真收干净，才肯删 profile，
                //   这正好也是孤儿 NRPT（memory「Orphan NRPT machine blackout」）那道防线。
                string report;
                try
                {
                    report = await GracefulTeardown.DisconnectThenDeleteAsync(
                        $"前台物理网络看门狗：连续 {maxFailures} 轮不可达（复核一致）").ConfigureAwait(false);
                }
                catch (Exception ex) { report = $"收尾异常={ex.GetType().Name}: {ex.Message}"; }

                RollingLog.Error("physical-watchdog", $"收尾动作：{report}");
                return;
            }
        }

        /// <summary>
        /// 数 <c>LocalState\dataface-sessions.txt</c> 里<b>晚于</b> <paramref name="since"/>
        /// 的 <c>evt=connect-begin</c> 行数（插件每次被平台叫起来追加一行，见该方法注释）。
        ///
        /// 【为什么按时间筛而不是"记基线再数总量"】日志是追加式、跨会话累积的。
        ///   若先读基线、之后某次读失败返回 0，下一轮数到的却是今天全部历史，
        ///   差值会被误读成"平台疯狂重拨"，把用户刚连好的隧道一脚拆掉 —— 这类
        ///   "保险丝自己炸"的错误比漏判严重得多。按时间戳筛之后，历史行自然被排除，
        ///   读失败只是这一轮少看一次，下一轮 10 s 后补上。
        ///
        /// 读不到返回 0（文件不存在 / 正被追加占住），绝不影响断开判定本身。
        /// </summary>
        private static async Task<int> CountRecentConnectsAsync(DateTime since)
        {
            try
            {
                Windows.Storage.StorageFile? file =
                    await LocalState.TryGetFileAsync("dataface-sessions.txt").ConfigureAwait(false);
                if (file == null) return 0;

                string raw = await LocalState.ReadTextAsync(file).ConfigureAwait(false);
                int n = 0;
                foreach (string line in raw.Split('\n'))
                {
                    if (!line.Contains("evt=connect-begin")) continue;
                    if (line.Length < 19) continue;
                    if (!DateTime.TryParseExact(line.Substring(0, 19), "yyyy-MM-dd HH:mm:ss",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out DateTime ts)) continue;
                    if (ts >= since) n++;
                }
                return n;
            }
            catch (Exception ex)
            {
                // ★ 读失败必须留痕：这条计数就是"平台自动重拨"保险丝的眼睛，
                //   它瞎了却不出声 ⇒ 该拆的死循环拆不了。返回 0 的语义没变，
                //   变的只是"瞎"这件事本身可见。
                RollingLog.Error("health-sessions-read", ex);
                return 0;
            }
        }

        /// <summary>
        /// TCP 握手探针：成功返回 "OK"，超时/失败返回原因摘要。
        /// <paramref name="host"/> 可以是 IP 字面量（不依赖 DNS）或域名（顺带测隧道 DNS）。
        /// 固定探 443 端口。
        /// </summary>
        private static async Task<string> TcpProbeAsync(string host, int timeoutMs)
        {
            try
            {
                using var socket = new Windows.Networking.Sockets.StreamSocket();
                var conn = socket.ConnectAsync(
                    new Windows.Networking.HostName(host), "443").AsTask(CancellationToken.None);
                if (await Task.WhenAny(conn, Task.Delay(timeoutMs)) == conn)
                    return "OK";
                return "超时";
            }
            catch (Exception ex)
            {
                return $"{ex.GetType().Name}";
            }
        }
    }
}
