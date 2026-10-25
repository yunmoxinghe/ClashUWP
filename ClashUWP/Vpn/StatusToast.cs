using System;
using System.Threading;
using System.Threading.Tasks;

using Windows.Data.Xml.Dom;
using Windows.Storage;
using Windows.UI.Notifications;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// ★【1.0.66】插件进程内的<b>状态通知</b>（对齐安卓 <c>DynamicNotificationModule</c>）——
    /// 隧道连上之后，通知中心常驻一条「实时上下行」，原位刷新，不出横幅、不响。
    ///
    /// 【为什么放插件进程】
    ///   字节计数（encap / inject）就在插件进程里，而插件进程<b>在会话期间一定活着</b>
    ///   （包在它这里转发，平台为了转发保活它）。前台 UWP 进程会被挂起/节流/关闭 ——
    ///   放前台就等于"app 一关，状态通知不再刷新"。理由与
    ///   <see cref="PhysicalNetworkWatchdog"/> 搬进插件那一次完全相同（见它的类注释）。
    ///   ★【1.0.123-B 订正】"发通知这件事住在哪个进程"与"通知被登记在哪个入口名下"
    ///     是两件事，此前把它们混成一件了：1.0.106 起插件宿主挂在自己那个无头入口
    ///     <c>Application Id="VpnHost"</c>，于是无参 <c>CreateToastNotifier()</c>（官方：
    ///     "绑定到<b>调用应用程序</b>"）把这条通知绑到了 <c>…!VpnHost</c> 名下 ——
    ///     进程仍是对的（会话期间必活），归属却是错的（缺省前台激活唤起的正是那个没 UI 的入口）。
    ///     现在发送器显式绑到带 UI 的 <c>!App</c>，见 <see cref="CreateSessionNotifier"/>。
    ///
    /// 【为什么是"静默常驻 toast + 原位 Update"这个形态】
    ///   Windows 给 UWP 包<b>没有</b>托盘：<c>Microsoft.UI.Windowing.TrayIcon</c> 明确不支持 UWP、
    ///   <c>Shell_NotifyIcon</c> 要 HWND（Win32 only）、Live Tile 在 Win11 已废、
    ///   任务栏进度 <c>ITaskbarList4</c> 也要 Win32。于是"实时上下行"能落的只有通知中心那一条 ——
    ///   点开通知中心就看，和安卓的 UX 一致（用户已认可这个取舍）。
    ///
    /// 【与安卓逐条对齐（源码存档 <c>.local/ref/cmfa_notif/DynamicNotificationModule.kt</c>）】
    ///   · <c>setOngoing(true)</c>（不可划掉的常驻通知）
    ///       → <b>Windows 没有这个语义</b>：toast 用户可以随手划掉，也没有前台服务那种锁。
    ///         做不到就退一步："划掉之后补发（限流）+ 发一条普通通知教用户去设置里彻底关掉"，
    ///         这正是本次要实现的第二个动作。
    ///   · <c>setOnlyAlertOnce(true)</c> + <c>IMPORTANCE_LOW</c> 通道（不响、不弹横幅）
    ///       → <c>ToastNotification.SuppressPopup = true</c> + XML 里 <c>&lt;audio silent='true'/&gt;</c>。
    ///         官方口径（App notification progress bar and data binding 的 replace/update 对照表）：
    ///         "<i>Can reappear as a popup if SuppressPopup is false (or set to true to silently
    ///         send it to Notification Center)</i>"。
    ///   · <c>notify(R.id.nf_clash_status, …)</c>（同 id 覆盖，不新增条目）
    ///       → 固定 <c>Tag</c>+<c>Group</c>，之后只走
    ///         <c>ToastNotifier.Update(NotificationData, tag, group)</c>：官方同一张表里 Update 那列是
    ///         "<i>Leaves the notification in place within Notification Center</i>"、
    ///         "<i>Won't reappear as a popup; the notification's data is silently updated within
    ///         Notification Center</i>" —— 原位刷新；重新 <c>Show</c>（replace）会把条目顶到最上面。
    ///   · <c>ticker(SECONDS.toMillis(1))</c>（每秒一拍）
    ///       → 同样 1 s 一拍，但<b>只在文案真的变了</b>才调 Update（空闲时速率 0、累计不动 ⇒ 不敲平台）。
    ///   · <c>isInteractive</c> / <c>SCREEN_ON|OFF</c> 门（屏灭就不刷）
    ///       → <b>UWP 侧没有等价物</b>（查过：没有"通知中心当前是否可见"的只读入口，
    ///         <c>GetNotificationHistory</c> 在 26100 投影里也不存在）。用"变了才刷"替代，
    ///         目的相同：不做无意义的每秒平台调用。
    ///   · <c>contentTitle</c>=当前 profile / <c>contentText</c>=↑↓速率 / <c>subText</c>=累计
    ///       → 三行 <c>&lt;text&gt;</c>：标题（含订阅 id）、速率、累计。
    ///         ★ 标题里<b>不放</b>存活秒数：那一行每秒都在变，会把"变了才刷"变成"每秒必刷"；
    ///           安卓的 subText 也只放累计，秒数不是这台设备上的有效信息。
    ///   · 数据源 <c>Clash.queryTrafficNow()</c> / <c>queryTrafficTotal()</c>
    ///       → <b>口径不同，必须明说</b>：这里用插件自己的包字节计数（<c>_outBytes</c>/<c>_inBytes</c>），
    ///         量的是<b>隧道 L3 收进来的原始 IP 包</b>与<b>注回本机的原始 IP 包</b>，
    ///         不含隧道封装开销，也不等于内核 REST <c>GET /traffic</c>（那条是代理连接的好速度）。
    ///         选它的理由：零额外 IO、零依赖、热路径只多两次 <c>Interlocked.Add</c>；
    ///         而 <c>/traffic</c> 是 WebSocket，插件侧要另起一条 HTTP 客户端并在 AppContainer
    ///         里回环，成本与风险都不对应这个需求。
    ///
    /// 【★ 被划掉之后怎么办（安卓不需要写、我们必须写的那一段）】
    ///   安卓不用处理"用户删通知"：<c>setOngoing(true)</c> 压根没有划掉按钮，前台服务通知归系统管。
    ///   Windows 的 toast 随手就能划掉，所以两个信号都接：
    ///     ① <c>ToastNotification.Dismissed</c> —— 官方定义 "<i>Occurs when a toast notification
    ///        leaves the screen, either by expiring or being explicitly dismissed by the user.
    ///        Apps that are running subscribe to this event.</i>"，原因在
    ///        <c>ToastDismissedEventArgs.Reason</c>（<c>UserCanceled</c> / <c>ApplicationHidden</c> /
    ///        <c>TimedOut</c>）。那句"Apps that are running"是关键限制：
    ///        <b>进程还在才有事件</b> ⇒ 它只是快通道，不能当唯一依据。
    ///     ② <c>Update()</c> 的返回值 —— 官方对照表写明 "<i>If the user dismissed the notification,
    ///        the update will fail</i>"，且 <c>NotificationUpdateResult</c> 专门有
    ///        <c>NotificationNotFound</c> 这一档。这条<b>不依赖进程活着</b>，是可靠判据。
    ///   任一命中 ⇒ 用同一 tag 静默补发一条，并（每天最多一次、每会话最多一次）发一条
    ///   <b>会出横幅的普通通知</b>，把用户送到 设置 → 系统 → 通知，告诉他怎么彻底关掉横幅：
    ///   因为只要隧道还在跑，这条状态通知就会一直被补回来 —— 想清静只有从设置入手。
    ///   限流两条：<c>MaxReRaise</c> 次之后这个会话就放手（划 4 次还在来是骚扰）；
    ///   引导通知记在 <c>LocalState\toast-guide.txt</c>，同一天不重发。
    ///   ★★【1.0.79 · Docs/30 §8-E 实测订正】上面这段写完时<b>整机没验过</b>，验下来是错的：
    ///     ① 系统会把这条通知按自己的寿命撤下，原因是 <c>TimedOut</c> —— 那不是"用户划掉"，
    ///       可旧逻辑把它一起计进 <c>MaxReRaise</c>，实测 <b>5 秒</b>就把额度烧光、
    ///       从此这个会话不再刷状态通知（"常驻"从未成立），还顺手给每个会话发一条引导横幅。
    ///     ② "设 <c>ExpirationTime</c> 就能常驻"也已被官方文档证伪：那个属性的 Remarks 是
    ///       "<i>Windows attempts to raise toast notifications immediately after you call Show,
    ///       so this property is rarely used</i>"，"到点移除"那句只适用于 Windows Phone 8.x。
    ///     现在的口径（<b>1.0.80 定稿</b>）：<b>只有 <c>UserCanceled</c> 算划掉</b>（计额度、发引导）；
    ///     <c>TimedOut</c> 对 <c>SuppressPopup</c> 的静默通知而言是<b>立刻就会来的正常回调</b>，
    ///     它只记账、<b>不清 <c>_shown</c>、不补发</b>，循环继续对同一条 <c>Update</c> 原地刷新
    ///     （1.0.79 实测：把 TimedOut 当"没了" ⇒ <b>42 次 / 44 秒</b>的每秒重发自我循环）；
    ///     真离场的原因（<c>Expired</c> / <c>ApplicationHidden</c> / <c>SystemCanceled</c> /
    ///     <c>PassiveDismissed</c> / <c>update:NotificationNotFound</c>）才走
    ///     <c>ReRaiseThrottleSeconds</c>=30 s 节流补发 ——
    ///     通知在通知中心能留多久归系统/用户设置管，我们只负责"它真不在了才补回来"，
    ///     并且把每一次补发原因如实记进日志（不再用"常驻"这个词骗自己）。
    ///
    /// 【纪律】这里<b>不碰隧道、不碰网络、不碰内核</b>：只读计数、只发通知。
    ///   会话结束（<c>Disconnect</c>）时 <see cref="Stop"/> 会把常驻那条撤掉（<c>Hide</c>），
    ///   不留"通知显示已连接、隧道其实没了"的假状态。
    /// </summary>
    internal static class StatusToast
    {
        /// <summary>常驻状态通知的 tag / group：对应安卓那个固定的 <c>R.id.nf_clash_status</c>。</summary>
        private const string TagStatus = "clash-status";
        private const string GroupStatus = "clash";

        /// <summary>引导通知另用一条 tag：不覆盖常驻那条，也不被它覆盖。</summary>
        private const string TagGuide = "clash-guide";

        /// <summary>
        /// ★【1.0.122】"断开连接"那个按钮的凭据文件：本进程写、前台读。
        /// 只有本包读得到 ⇒ 网页里那个 <c>clashuwp://disconnect</c> 链接猜不到这串，
        /// 猜不到就不动手（理由见 <see cref="StatusXml"/> 那段与
        /// <c>SubscriptionProtocol.TryParseAppAction</c>）。
        /// </summary>
        private const string NonceFile = "toast-nonce.txt";

        /// <summary>一拍 1 s，与安卓 <c>ticker(TimeUnit.SECONDS.toMillis(1))</c> 同频。</summary>
        private const int IntervalMs = 1000;

        /// <summary>
        /// 一个会话内，<b>被用户划掉</b>最多补发几次；超过就放手（划 4 次还在来是骚扰）。
        /// ★【1.0.79 · Docs/30 §8-E】这条额度以前把 <c>TimedOut</c> 也计进来了 ——
        ///   而 TimedOut 是 toast 的<b>正常寿命</b>（横幅到点自己收），实测每个会话都在
        ///   5 秒内被它烧光额度并"放弃刷状态通知"，还顺手给所有人发一条会出横幅的引导通知。
        ///   现在只有 <c>UserCanceled</c> 计额度。
        /// </summary>
        private const int MaxReRaise = 3;

        /// <summary>
        /// ★【1.0.79 · Docs/30 §8-E】<b>真离场</b>原因的补发限流（秒）：我们从 <c>Update</c>
        ///   返回值只知道"它不在了"（<c>NotificationNotFound</c>，分不清谁干的），
        ///   或平台给了 <c>Expired</c> / <c>ApplicationHidden</c> / <c>SystemCanceled</c> /
        ///   <c>PassiveDismissed</c> —— 这些都要补发，但仍然要节流：
        ///   实测这类回调会在 1–2 秒内连续命中，不限流就变成每秒重 Show 一次。
        ///   ★【1.0.80】<c>TimedOut</c> <b>不再走这里</b>：静默通知（SuppressPopup）Show 之后
        ///     它立刻就回来，而通知其实还在通知中心 —— 把它当"没了"就是 1.0.79 那个
        ///     每秒一条 Show 的自我循环（42 次 / 44 秒）。现在它只记账，见 <c>HandleDismissed</c>。
        ///   30 s 的口径：隧道一跑就是几分钟到半小时，通知中心里那条"在不在"用户不看细节，
        ///   但也不能像 1.0.78 那样 5 秒就永久放手。
        /// </summary>
        private const int ReRaiseThrottleSeconds = 30;

        // 绑定名：XML 里写成 {key}，NotificationData.Values 用同名（官方绑定的就是这套占位）。
        private const string KeyTitle = "statusTitle";
        private const string KeyRate = "statusRate";
        private const string KeyTotal = "statusTotal";

        private static CancellationTokenSource? _cts;

        // ── 会话内状态（每次 Start 全部重置）──
        private static ToastNotifier? _notifier;
        private static ToastNotification? _shown;   // 当前认为挂在通知中心里的那条
        private static string _title = "";
        private static string _lastRate = "";
        private static string _lastTotal = "";
        private static uint _sequence;
        private static int _reRaised;
        private static int _dismissCount;
        private static int _guideSentThisSession;
        private static bool _gaveUp;

        /// <summary>★【1.0.122】本会话那条"断开连接"按钮的凭据（16 位十六进制）。
        /// 每次 Start 换一个并落盘；<c>StatusXml()</c> 把它写进 <c>arguments</c>，
        /// 前台激活时比对 <c>LocalState\toast-nonce.txt</c>。空串 = 落盘失败，
        /// 那一支的代价只是"按钮点不动"（前台会拒绝并记一行日志），不是"谁都能断"。
        /// </summary>
        private static string _nonce = "";

        /// <summary>★【1.0.79】上一次静默补发的时刻（限流用，UTC；只在循环线程读写）。</summary>
        private static DateTime _lastReRaiseUtc = DateTime.MinValue;

        // Dismissed 事件在<b>平台回调线程</b>上触发，动手的却是循环线程（只有一个线程改 _shown）。
        // 所以回调只立牌子：先写原因，再 Volatile.Write 置位 —— 与本仓其它跨线程标记同一口径。
        private static string _dismissWhy = "";
        private static int _dismissPending;

        /// <summary>连续引发失败次数；到 <see cref="MaxShowFailures"/> 就整条循环退出，不再每秒重试。</summary>
        private static int _showFailCount;

        /// <summary>连续失败几次就放弃（<c>CreateToastNotifier</c> 成功但 Show 一直失败，
        /// 通常是系统里这个应用的通知被关掉了 —— 每秒重试只会把日志刷满）。</summary>
        private const int MaxShowFailures = 5;

        /// <summary>
        /// ★ 一把锁，锁住"动通知"这件事（Show / Update / Hide）。
        /// 对手是 <see cref="Stop"/>：它跑在<b>平台回调线程</b>（Disconnect 收尾），
        /// 而这个循环跑在自己的线程池线程上。两边同时动手的形状是：
        /// 收尾把通知撤了，循环下一秒读到 <c>NotificationNotFound</c>（平台说"划掉了"）
        /// ⇒ 于是<b>把通知又发回来</b> —— 会话早就结束，通知中心里却挂着一条"隧道运行中"。
        /// 那正是本类自己最反对的谎报，所以这一段必须锁住，并在动手前核对
        /// "我还是不是当前这一轮循环"（见 <c>ReferenceEquals(_cts, own)</c>）。
        /// 每秒一次、每次都是短临界区，锁不争性能。
        /// </summary>
        private static readonly object _gate = new object();

        /// <summary>起（可重复调用，重复即"重新武装"；<c>statusToast=0</c> 时什么都不做）。</summary>
        internal static void Start(VpnPluginConfig config, ClashVpnPlugin plugin)
        {
            Stop("重新武装");

            if (!LocalSettings.StatusToast)
            {
                RollingLog.Info("toast", "跳过：statusToast=0");
                return;
            }

            // ★【1.0.81】优先用显示名（如"星链机场"），没有则回退到 subId
            string profile = config.SubDisplayName.Length > 0 ? config.SubDisplayName
                          : config.SubId.Length > 0 ? config.SubId
                          : "默认配置";

            var cts = new CancellationTokenSource();
            _cts = cts;
            // _notifier / _shown 不用在这里清：上面的 Stop 已经在锁里清过了，
            // 这里再裸写一次反而给旧循环留一个"读到半新半旧状态"的口子。
            _title = $"ClashUWP · 隧道运行中（{profile}）";
            _lastRate = "";
            _lastTotal = "";
            _sequence = 0;
            _reRaised = 0;
            _dismissCount = 0;
            _guideSentThisSession = 0;
            _gaveUp = false;
            _showFailCount = 0;
            _lastReRaiseUtc = DateTime.MinValue;
            _dismissWhy = "";
            Volatile.Write(ref _dismissPending, 0);

            // ★【1.0.122】先换凭据再发通知：StatusXml() 里那一串 arguments 与
            //   LocalState\toast-nonce.txt 必须是同一份。落盘用"发出去不管"的形式，
            //   因为第一拍 Show 至少在 IntervalMs(1s) 之后 —— 前台真点击时文件早就在了。
            //   ★ 落盘失败<b>不</b>退回"没有凭据也算通过"：那样等于把
            //     "任何网页都能把用户的隧道断开"这条路重新打开，宁可按钮点不动。
            _nonce = Guid.NewGuid().ToString("N").Substring(0, 16);
            _ = SaveNonceAsync(_nonce);

            _ = Task.Run(() => LoopAsync(cts.Token, cts, plugin));
        }

        /// <summary>★【1.0.122】把本会话的凭据写进 <c>LocalState\toast-nonce.txt</c>。</summary>
        private static async Task SaveNonceAsync(string nonce)
        {
            try
            {
                StorageFile target = await ApplicationData.Current.LocalFolder
                    .CreateFileAsync(NonceFile, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(target, nonce);
                RollingLog.Info("toast", $"断开按钮凭据已落盘（{NonceFile}）");
            }
            catch (Exception ex)
            {
                RollingLog.Warn("toast",
                    $"断开按钮凭据写不出，前台会拒掉那一支 err={ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// ★【1.0.122】前台侧：核对通知里带回来的凭据是不是本会话那一份。
        /// 读不到文件、为空、对不上 —— 一律 false（"不确定就不动手"是本仓一贯的口径）。
        /// </summary>
        internal static async Task<bool> MatchesNonceAsync(string nonce)
        {
            if (nonce.Length == 0)
            {
                RollingLog.Warn("toast", "激活参数里没有断开按钮的凭据，拒绝");
                return false;
            }
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(NonceFile);
                string saved = (await FileIO.ReadTextAsync(file)).Trim();
                bool ok = saved.Length > 0 && saved == nonce;
                if (!ok)
                    RollingLog.Warn("toast",
                        $"断开按钮凭据不匹配（通知里那串与本包记录的不同），拒绝 —— 这条激活不是本应用的通知发出来的");
                return ok;
            }
            catch (Exception ex)
            {
                // 从没发过通知（文件不存在）或读不出：不能当成"通过"。
                RollingLog.Warn("toast",
                    $"读断开按钮凭据失败，拒绝 err={ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>停（会话结束 / 用户断开时调）：撤掉常驻通知并停循环。</summary>
        internal static void Stop(string why)
        {
            var cts = _cts;
            if (cts != null)
            {
                _cts = null;
                try { cts.Cancel(); } catch { /* 取消本身不抛 */ }
                try { cts.Dispose(); } catch { }
            }

            try
            {
                // ★ 锁住"撤"这件事，并在锁里把 _notifier/_shown 一起清空：
                //   循环那边动手前会核对"我还是不是当前这一轮"（_cts 已被上面置 null），
                //   所以撤完之后不会再有一条被补发回来。理由见 _gate 的注释。
                lock (_gate)
                {
                    var n = _notifier;
                    if (n != null)
                    {
                        // ★【1.0.79 · Docs/30 §8-E】只在"我们认为它还在"时才 Hide：
                        //   官方对 Hide 的前提是这条通知当前挂着，实测在通知已被系统按寿命撤下、
                        //   循环早已放手之后再 Hide，平台直接回
                        //   <c>COMException: 通知无法隐藏，除非它已经显示</c> ——
                        //   那是一条纯噪音的 Error 级日志，会把真问题盖住。
                        if (_shown != null)
                        {
                            Hide(n);
                            RollingLog.Info("toast", $"已撤下状态通知（{why}）");
                        }
                        else
                        {
                            RollingLog.Info("toast", $"无需撤下（通知早已不在，{why}）");
                        }
                    }
                    _notifier = null;
                    _shown = null;
                }
            }
            catch (Exception ex)
            {
                // 撤不掉只是通知中心里留一条旧条目，不值得把 Disconnect 的收尾流程带崩。
                RollingLog.Warn("toast", $"撤下失败（{why}）err={ex.GetType().Name}: {ex.Message}");
                _notifier = null;
                _shown = null;
            }
        }

        /// <summary>
        /// ★★【1.0.123-B · 用户口径「通知的路由入口是后台任务，把他接到前台去」】
        /// 发送器不再绑在<b>本进程所属的无头入口</b>（<c>…!VpnHost</c>）上，而是显式绑到
        /// 带 UI 的前台入口（<c>…!App</c>）。
        ///
        /// 【官方给的把手】<c>CreateToastNotifier(String applicationId)</c> 的重载表原文：
        ///   「创建并初始化 ToastNotifier 的新实例，该实例绑定到指定的应用
        ///   （<b>通常是同一包中的另一个应用</b>）」，注解「由 applicationId 标识的应用
        ///   <b>必须属于与调用方相同的包</b>」，以及「无法把 Toast 通知发送到辅助磁贴，
        ///   因此必须是<b>应用磁贴</b>的 ID」。
        ///   对照无参重载那句「绑定到<b>调用应用程序</b>」—— 无参时绑的就是发起进程自己的
        ///   身份，而插件宿主挂的是 <c>Application Id="VpnHost"</c>
        ///   （<c>Package.appxmanifest:233</c>，1.0.106 臂 C 为了修"关窗 2 秒断流"才拆出去的，
        ///   隔离性证据在 Docs/53 §五）。于是通知被登记在 <c>…!VpnHost</c> 名下，
        ///   缺省 <c>foreground</c> 激活那句"launch <i>your</i> foreground app"里的
        ///   "你的前台应用"= 无头宿主 ⇒ 点击拉起 <c>!VpnHost</c>，界面从没露过面
        ///   （Docs/63 §一：wpndatabase 里那条归属 + 点击后新起的 pid 3316/4612）。
        ///   ⇒ 绑到 <c>!App</c> 后，通知的身份、设置开关（<c>notifier.Setting</c>）、
        ///     缺省前台激活三者一起搬到前台入口那一格。
        ///
        /// 【为什么绑的是 PFN!App 这个拼法】<c>PFN + "!" + Application/@Id</c> 是本仓
        ///   <c>Tools\UwpBridge\Program.cs:732</c> 一直在用、且装机拉起过的那把尺子。
        ///   ★【1.0.123 装机定论】当初三条候选逐个试（官方只写"必须是应用磁贴的 ID"，
        ///   没有可抄的实物例证），实测<b>第一条就被平台接受</b>：
        ///   <c>plugin.log</c> 里 <c>发送器=8B8CC4F4.ClashUWP_4tjvwvefxrz66!App</c>、
        ///   <c>发送器绑不进</c> 零行 ⇒ 另两条候选按"不留假想需求"删了。
        ///   ★ 家族名取 <c>Package.Current.Id.FamilyName</c> 而不是写死发布者哈希后缀：
        ///   换一张签名证书就换家族（Docs/38 §十四 F5），写死等于把"重签 = 换家族"打进通知通道里。
        ///
        /// 【装机后三条风险全部闭合】① 候选被接受（上面那行）；② 绑 <c>!App</c> 后 <c>Update</c>
        ///   仍命中同一 <c>Tag</c>+<c>Group</c>（Docs/64 §七 #9；注意点击会把那条通知摘掉，
        ///   所以点击后第一次 <c>Update</c> 必然回 <c>NotificationNotFound</c> 并走节流补发，
        ///   那是平台行为不是退化）；③ 前台 <c>Stop</c> 撤得下（Docs/64 §七 #10）。
        ///   三条都不碰隧道：通知这件事失败只让通知没，不动网络（本类纪律）。
        ///
        /// 【退回无参重载 = 本改动之前的行为】不是"修好了"，只是不回归。
        ///   点击落点由协议激活那两行兜住（<see cref="StatusXml"/>：<c>launch</c> 与按钮都
        ///   <c>activationType='protocol'</c>，按 URI 归属路由，与发起方身份无关），
        ///   本条是把"通知的身份归属"也搬正，两道各自成立。
        /// </summary>
        private static (ToastNotifier, string) CreateSessionNotifier()
        {
            string foregroundId;
            try
            {
                foregroundId = Windows.ApplicationModel.Package.Current.Id.FamilyName + "!App";
            }
            catch (Exception ex)
            {
                RollingLog.Warn("toast",
                    $"读包家族名失败，退回本进程入口 err={ex.GetType().Name}: {ex.Message}");
                return (ToastNotificationManager.CreateToastNotifier(), "缺省(调用方入口)");
            }

            try
            {
                return (ToastNotificationManager.CreateToastNotifier(foregroundId), foregroundId);
            }
            catch (Exception ex)
            {
                RollingLog.Warn("toast",
                    $"发送器绑不进 {foregroundId} err={ex.GetType().Name}: {ex.Message}");
                return (ToastNotificationManager.CreateToastNotifier(), "缺省(调用方入口)");
            }
        }

        private static async Task LoopAsync(CancellationToken token, CancellationTokenSource own,
                                           ClashVpnPlugin plugin)
        {
            ToastNotifier notifier;
            string notifierId;
            try
            {
                // ★【1.0.123-B】发送器改绑到带 UI 的前台入口（见 CreateSessionNotifier 那段）。
                (notifier, notifierId) = CreateSessionNotifier();
                lock (_gate)
                {
                    // 建好之前这一刻都可能已经被 Stop/新一轮 Start 摘走（连接刚成就断是实测形态），
                    // 所以赋值也要在锁里核对身份，别把一个已经结束的会话的发送器留下。
                    if (!ReferenceEquals(_cts, own)) return;
                    _notifier = notifier;
                }
                RollingLog.Info("toast",
                    $"启动：每 {IntervalMs}ms 原位刷新 tag={TagStatus} setting={notifier.Setting} " +
                    $"发送器={notifierId} 补发上限={MaxReRaise} 引导通知=每天一次");
            }
            catch (Exception ex)
            {
                RollingLog.Warn("toast",
                    $"CreateToastNotifier 失败，本会话不发通知 err={ex.GetType().Name}: {ex.Message}");
                return;
            }

            long lastUp = 0, lastDown = 0;
            bool first = true;

            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(IntervalMs, token); }
                catch (TaskCanceledException) { return; }

                plugin.ReadTrafficBytes(out long up, out long down);

                double upRate = 0, downRate = 0;
                if (first)
                {
                    // 第一拍只取基线：没有"上一拍"，算出来的速率没意义。
                    // 但这一拍就把通知挂上 —— "连上了"这件事要立刻可见，不用等第二秒。
                    first = false;
                    lastUp = up;
                    lastDown = down;
                }
                else
                {
                    upRate = (up - lastUp) * 1000.0 / IntervalMs;
                    downRate = (down - lastDown) * 1000.0 / IntervalMs;
                    lastUp = up;
                    lastDown = down;
                    if (upRate < 0) upRate = 0;     // 计数每会话归零，理论上不会负；防御一口
                    if (downRate < 0) downRate = 0;
                }

                string rate = $"↑ {Format(upRate, "/s")}　↓ {Format(downRate, "/s")}";
                string total = $"累计 ↑ {Format(up, "")}　↓ {Format(down, "")}";

                // ★ 动手的整段都在 _gate 里，并先核对"我还是不是当前这一轮循环"：
                //   Stop（平台回调线程）刚撤过通知的话，这里再 Show/Update 就等于
                //   在会话结束之后把"隧道运行中"发回去。见 _gate 的注释。
                lock (_gate)
                {
                    if (!ReferenceEquals(_cts, own)) return;

                    // 快通道：Dismissed 回调只立牌子，动手在这里（保证只有一个线程改 _shown）。
                    // ★【1.0.80】"它在不在"这件事<b>全部交给 HandleDismissed 判定</b>，这里不再
                    //   抢先清 <c>_shown</c> —— 牌子只有原因字符串，而"这个原因等不等于没了"
                    //   是 HandleDismissed 的职责（见它的方法注释）。两处各判一次，
                    //   1.0.79 实测就是这样把一条明明还在通知中心的通知每秒重发一次。
                    bool dismissedNow = Interlocked.Exchange(ref _dismissPending, 0) == 1;
                    string dismissWhy = _dismissWhy;
                    if (dismissedNow)
                        HandleDismissed(notifier, dismissWhy, rate, total);

                    if (_gaveUp)
                    {
                        // 两种放手：用户持续划掉（HandleDismissed 设的）/ 连续引发失败（ShowSilent 设的）。
                        // 具体原因前一行日志里已经写过，这里只交代"这条循环到此为止"。
                        RollingLog.Info("toast", "本会话不再刷状态通知（原因见上一行 toast 日志）");
                        return;
                    }

                    if (_shown == null)
                    {
                        // ★ 节流闸门：上一次引发不满 ReRaiseThrottleSeconds 就不再试
                        //   （用户划掉那条例外 —— HandleDismissed 里已经当场补过发了）。
                        if ((DateTime.UtcNow - _lastReRaiseUtc).TotalSeconds < ReRaiseThrottleSeconds)
                            continue;
                        ShowSilent(notifier, rate, total);
                        continue;
                    }

                    // 安卓那条 ticker 每秒无脑 notify；这里改成"变了才敲平台"，
                    // 空闲（速率 0、累计不动）时一次调用都不发。
                    if (rate == _lastRate && total == _lastTotal) continue;

                    try
                    {
                        NotificationUpdateResult result =
                            notifier.Update(BuildData(rate, total), TagStatus, GroupStatus);
                        if (result == NotificationUpdateResult.Succeeded)
                        {
                            _lastRate = rate;
                            _lastTotal = total;
                            continue;
                        }

                        // ★ 可靠判据（官方：用户划掉之后 update 会失败；这里读到的就是
                        //   NotificationNotFound / Failed）。它不依赖进程活着，所以 Dismissed
                        //   事件丢了也能靠这一条把"通知已经不在了"发现出来。
                        HandleDismissed(notifier, "update:" + result, rate, total);
                    }
                    catch (Exception ex)
                    {
                        RollingLog.Warn("toast",
                            $"刷新异常，本拍放弃 err={ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// 处理"通知已经不在通知中心"：静默补发（限流）+ 引导通知（会话一次 / 一天一次）。
        /// 只在循环线程上调用（两个入口 —— Dismissed 牌子、Update 返回值 —— 都汇到这里）。
        ///
        /// ★【1.0.79 · Docs/30 §8-E】原因必须<b>分类</b>，不能一律当"用户划掉"：
        ///   官方对 <c>ToastDismissedReason</c> 的口径里 <c>TimedOut</c> = "通知到期了"，
        ///   那是 toast 的<b>正常寿命</b>（横幅显示完自动收起），不是用户在表达"别烦我"。
        ///   实测（Docs/30 §8-E 与 1.0.78 复现）：从"启动"到"本会话不再刷状态通知"
        ///   总共 <b>5 秒</b>、用户一次没碰，四条全是 <c>dismissed:TimedOut</c> ——
        ///   于是"常驻 ↑↓"从来没常驻过，人人都还收到一条本该只给"反复划掉的人"的引导横幅。
        /// ★【1.0.80 · §8-E 实测把机制钉死】<c>TimedOut</c> <b>不等于"通知没了"</b>：
        ///   我们这条是 <c>SuppressPopup=true</c> 的静默通知 —— 它<b>从来不弹横幅</b>，
        ///   Show 之后系统只是把它放进通知中心，于是"显示时机结束"（= TimedOut）
        ///   几乎<b>立刻</b>就回来了。1.0.79 装机实测：<c>07:13:46</c> 起
        ///   <b>42 次 / 44 秒</b>，全是 <c>dismissed:TimedOut</c>，用户一次没碰 ——
        ///   因为旧逻辑把它当成"没了"，清掉 <c>_shown</c> 又立刻重新 Show，
        ///   新那条再 TimedOut……形成<b>每秒一条 Show 的自我循环</b>（比 1.0.78 的
        ///   "5 秒烧光额度后彻底放手"更吵，而且一直在写平台）。
        ///   ⇒ 现在：<c>TimedOut</c> 只<b>记账</b>，<b>不动 <c>_shown</c>、不补发</b>，
        ///     循环继续对它 <c>Update</c> 原地刷新（这才是安卓版那条 ticker 的等价物）。
        ///     通知到底还在不在，由 <c>Update</c> 的返回值说了算（<c>NotificationNotFound</c>
        ///     /Failed 才当真没了）—— 这条判据不依赖事件、也不依赖我们的猜测。
        ///
        /// 【现在的分法】只有 <c>UserCanceled</c> 计 <see cref="MaxReRaise"/> 额度并发引导通知；
        ///   真正离场的原因（<c>Expired</c> / <c>ApplicationHidden</c> / <c>SystemCanceled</c> /
        ///   <c>PassiveDismissed</c>、以及 <c>update:NotificationNotFound</c>）
        ///   清 <c>_shown</c> 并走 <see cref="ReRaiseThrottleSeconds"/> 限流补发；
        ///   <c>TimedOut</c> 按上一段只记账。
        ///
        /// ★【1.0.81 · 新增】点击"断开连接"按钮后的行为：
        ///   按钮使用 <c>hintAfterActivationBehavior='pendingUpdate'</c>，点击后通知进入"待更新"状态
        ///   （不会消失，但会显示一个加载指示器）。此时 App.OnActivated 会收到激活参数，
        ///   调用 VpnSession.DisconnectAsync 发起断开。断开成功后，Stop() 会 Hide 掉这条通知。
        ///   如果用户在 pendingUpdate 状态下手动划掉通知，那会被计为 UserCanceled。
        ///
        /// ★ 关于"想常驻就设 <c>ExpirationTime</c>"这条：已按官方文档<b>证伪</b> ——
        ///   <see cref="ToastNotification.ExpirationTime"/> 的 Remarks 写的是
        ///   "<i>Windows attempts to raise toast notifications immediately after you call Show,
        ///   so this property is rarely used</i>"，而"到点从动作中心移除"那句被限定在
        ///   "<i>For Windows Phone 8.x app</i>"。⇒ 它不是桌面端的常驻开关，本类不设它。
        ///   toast 在通知中心能留多久由系统的通知设置决定（用户可清、可关），不在我们手里；
        ///   所以我们能做、也只是做对的事 = <b>用 Update 的返回值判生死，发现它真不在了
        ///   才按节流补回来</b>，并把这件事如实写进日志（不再假装"常驻"）。
        /// </summary>
        private static void HandleDismissed(ToastNotifier notifier, string why, string rate, string total)
        {
            int which = Interlocked.Increment(ref _dismissCount);
            bool userCanceled = why.EndsWith(":UserCanceled", StringComparison.Ordinal);
            bool timedOut = why.EndsWith(":TimedOut", StringComparison.Ordinal);
            string kind = userCanceled ? "用户划掉：计补发额度"
                : timedOut ? "横幅显示时机到期（静默通知必然立刻到期）：只记账，通知仍在通知中心"
                : "非用户划掉：只按节流补发，不计额度";
            RollingLog.Info("toast", $"状态通知回调（第 {which} 次，{why}，{kind}）");

            // ★【1.0.80】横幅到期 ⇒ 对象还活着，什么都不动（下一拍照常 Update 它）。
            if (timedOut && !userCanceled) return;

            // 到这里的都是"平台说它不在了"：我们也不再当它在（后续 Show 会重建并重新挂事件）。
            _shown = null;

            if (userCanceled)
            {
                if (_reRaised >= MaxReRaise)
                {
                    _gaveUp = true;
                    RollingLog.Info("toast",
                        $"用户已连续划掉 {which} 次，达到上限 {MaxReRaise} ⇒ 本会话放手，不再补发");
                    return;
                }
                _reRaised++;
                _lastReRaiseUtc = DateTime.UtcNow;
                ShowSilent(notifier, rate, total);

                if (Interlocked.Exchange(ref _guideSentThisSession, 1) == 0 && TryClaimGuideToday())
                {
                    try
                    {
                        ShowGuide(notifier);
                        RollingLog.Info("toast", "已发引导通知（普通横幅 +「打开通知设置」按钮）");
                    }
                    catch (Exception ex)
                    {
                        RollingLog.Warn("toast",
                            $"引导通知发不出 err={ex.GetType().Name}: {ex.Message}");
                    }
                }
                return;
            }

            // 系统自己撤下的（TimedOut / ApplicationHidden）或从 Update 返回值分不清原因的
            // （NotificationNotFound）：这里<b>只记账不补发</b> —— 补发统一由循环的节流闸门
            // （_shown==null 那条）按 ReRaiseThrottleSeconds 决定，否则一秒一条 Show。
        }

        /// <summary>静默引发常驻那条：SuppressPopup + silent audio ⇒ 只进通知中心，不出横幅。</summary>
        private static void ShowSilent(ToastNotifier notifier, string rate, string total)
        {
            try
            {
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(StatusXml());
                ToastNotification toast = new ToastNotification(doc)
                {
                    Tag = TagStatus,
                    Group = GroupStatus,
                    SuppressPopup = true,
                    Data = BuildData(rate, total)
                };
                toast.Dismissed += OnToastDismissed;
                toast.Failed += OnToastFailed;

                notifier.Show(toast);
                _shown = toast;
                // ★【1.0.79 · §8-E】每一次"真的动了平台"都记时刻：循环那边的节流闸门读它。
                //   失败路径<b>不</b>更新（失败要按 MaxShowFailures 连续计数尽快放手，
                //   不该被节流拖成 5 次×30 秒）。
                _lastReRaiseUtc = DateTime.UtcNow;
                // 记下这一拍渲染的内容 —— "变了才刷"的比较基准必须包含 Show 带进去的那一份初值，
                // 否则下一拍会拿旧基准做一次没意义的 Update。
                _lastRate = rate;
                _lastTotal = total;
                _showFailCount = 0;
            }
            catch (Exception ex)
            {
                // 连续失败到上限就不再重试：多半是系统里本应用的通知被关掉了，
                // 每秒一次只会把 plugin.log 刷满同一句话（热路径禁记的那条纪律同样适用在这里）。
                if (++_showFailCount >= MaxShowFailures)
                {
                    _gaveUp = true;
                    RollingLog.Warn("toast",
                        $"连续 {MaxShowFailures} 次引发失败，本会话不再重试 err={ex.GetType().Name}: {ex.Message}");
                    return;
                }
                RollingLog.Warn("toast", $"引发失败 err={ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>引导那条：普通通知（<b>不</b>压横幅，用户要看得见才会照做）。</summary>
        private static void ShowGuide(ToastNotifier notifier)
        {
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(GuideXml());
            ToastNotification toast = new ToastNotification(doc)
            {
                Tag = TagGuide,
                Group = GroupStatus,
                SuppressPopup = false
            };
            notifier.Show(toast);
        }

        private static void OnToastDismissed(ToastNotification sender, ToastDismissedEventArgs args)
        {
            string why;
            try { why = args.Reason.ToString(); }
            catch { why = "?"; }
            _dismissWhy = "dismissed:" + why;
            Volatile.Write(ref _dismissPending, 1);
        }

        private static void OnToastFailed(ToastNotification sender, ToastFailedEventArgs args)
        {
            // Show 之后平台自己报错（多半是通知被策略/设置压掉了）。
            RollingLog.Warn("toast", $"平台引发失败 err={args.ErrorCode}");
        }

        private static void Hide(ToastNotifier notifier)
        {
            var toast = _shown;
            if (toast != null)
            {
                notifier.Hide(toast);
                return;
            }
            // 极端时序（刚 Show 就 Stop，或 Show 失败过）：Hide 只认 tag+group，
            // 造一条同 tag/group 的空壳去撤即可，内容无所谓。
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(StatusXml());
            ToastNotification stub = new ToastNotification(doc)
            {
                Tag = TagStatus,
                Group = GroupStatus
            };
            notifier.Hide(stub);
        }

        /// <summary>
        /// 抢"今天这一次"引导名额：<c>LocalState\toast-guide.txt</c> 记最近一次的日期。
        /// 读写失败时<b>当作没抢到</b> —— 宁可少发一条，也不要每次划掉都弹一次横幅。
        /// </summary>
        private static bool TryClaimGuideToday()
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            try
            {
                string last = "";
                try
                {
                    StorageFile file = ApplicationData.Current.LocalFolder
                        .GetFileAsync("toast-guide.txt").AsTask().GetAwaiter().GetResult();
                    last = FileIO.ReadTextAsync(file).AsTask().GetAwaiter().GetResult().Trim();
                }
                catch { /* 还没这个文件 = 从没发过 */ }

                if (string.Equals(last, today, StringComparison.Ordinal)) return false;

                StorageFile target = ApplicationData.Current.LocalFolder
                    .CreateFileAsync("toast-guide.txt", CreationCollisionOption.ReplaceExisting)
                    .AsTask().GetAwaiter().GetResult();
                FileIO.WriteTextAsync(target, today).AsTask().GetAwaiter().GetResult();
                return true;
            }
            catch (Exception ex)
            {
                RollingLog.Warn("toast", $"引导名额记账失败，本次不发 err={ex.GetType().Name}");
                return false;
            }
        }

        private static NotificationData BuildData(string rate, string total)
        {
            NotificationData data = new NotificationData();
            data.Values[KeyTitle] = _title;
            data.Values[KeyRate] = rate;
            data.Values[KeyTotal] = total;
            // 官方："<i>When multiple NotificationData objects are received, the system displays
            // the NotificationData with the greatest non-zero number.</i>" ⇒ 必须单调递增，
            // 否则慢一拍到达的更新会把新值盖掉。
            data.SequenceNumber = ++_sequence;
            return data;
        }

        /// <summary>
        /// 常驻那条的 XML：三行<b>全部是 <c>{key}</c> 占位</b>，之后只靠 Update 换值。
        /// ★ 官方「支持数据绑定的元素」清单只有两条：进度栏的所有属性、<b>顶层 text 元素的 Text</b>；
        ///   绑定写法是把 <c>{绑定名}</c> 直接写进内容（WindowsAppSDK 文档里那句
        ///   "<i>Binds to {progressTitle} in xml payload</i>" 说的就是这个占位形式，
        ///   <c>NotificationData</c> 的 key 用同名）。
        /// ★ 这里<b>不放</b> <c>&lt;progress&gt;</c>：一是官方标注它只支持桌面端 toast；
        ///   二是上下行速率不是"完成度"，硬造一个参考速率只会误导；
        ///   三行文字才是与安卓 <c>contentTitle / contentText / subText</c> 一一对应的形态。
        ///   <c>&lt;audio silent='true'/&gt;</c> 是第二重保险：万一横幅因为别的原因出现，也不响。
        ///
        /// ★★【1.0.122 · 点击落点换成协议激活，这一条是修 1.0.81 那个"点了没反应"】
        ///   1.0.81 写的是 <c>launch='action=viewMainWindow'</c> + 按钮
        ///   <c>activationType='foreground'</c> + <c>hintAfterActivationBehavior='pendingUpdate'</c>，
        ///   装机实测（本机 1.0.121，取证链在 Docs/63 §一）是<b>整条不成立</b>：
        ///     ① 这条通知由<b>插件宿主进程</b>引发，而 1.0.106 起插件宿主挂在自己那个
        ///        无头入口上，于是 Windows 把通知登记到 <c>…!VpnHost</c> 的 AUMID
        ///        （<c>%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db</c>
        ///        里 <c>!App</c> 与 <c>!VpnHost</c> 两条 setting 记录都在，而引发这条 tag=clash-status
        ///        的进程就是 VpnHost 那个 pid）；
        ///     ② 官方对缺省的 <c>foreground</c> 的语义是 "<i>Your foreground app is launched</i>"
        ///        —— "你的前台应用"= 这条通知所属的那个入口，也就是<b>无头宿主</b>，
        ///          不是带 UI 的 <c>!App</c>；实测点击后 app.log 里出现的是<b>新起的进程</b>
        ///          （pid=3316 / 4612）而界面从没露过面，约 17 秒后就被挂起
        ///          —— 官方那句"没在 Current 窗口上调 Activate 且不可见 ⇒ 15 秒后终止"对得上；
        ///     ③ <c>App.OnActivated</c> 这个 override 在本应用<b>一次都没被调过</b>
        ///        （1.0.53~1.0.57 为协议激活实测五轮钉死的那条限制，对 toast 同样成立），
        ///        所以 1.0.81 那段分支从一开始就是死代码：真会触发的分发只有
        ///        <c>CoreApplicationView.Activated</c>，而它只认 <c>Kind==Protocol</c>。
        ///   ⇒ 现在两行都写 <c>activationType='protocol'</c>：官方同一张表里它的语义是
        ///     "<i>Launch a different app using protocol activation</i>"，按 <b>URI 归属</b>路由，
        ///     而 <c>clashuwp</c> 只注册在带 UI 的 <c>&lt;Application Id="App"&gt;</c> 上
        ///     ⇒ 点击必然回到前台，而且落在<b>已经装机验过的那条分发</b>（协议导入）上。
        ///   ★ <c>hintAfterActivationBehavior='pendingUpdate'</c> <b>一并删掉</b>：官方
        ///     「正在等待更新的应用通知」那一页写死了"在<b>后台</b>激活按钮上把
        ///     afterActivationBehavior 设为 pendingUpdate —— 这仅适用于
        ///     <c>activationType="background"</c> 的按钮"，1.0.81 那句"PendingUpdate 行为需要
        ///     前台激活"是把它记反了。所以"点完通知还在不在"这件事不再交给平台承诺：
        ///     装机那遍要读 plugin.log 里点击落到的 <c>Dismissed</c> 原因，按实物写清
        ///     （判据行 Docs/63 §五 #3）。
        ///   ★ 按钮参数带 <c>?n=&lt;nonce&gt;</c>：断开是动网络状态的动作，而协议是
        ///     <b>外部可触发</b>的（任何网页一个链接都能唤起本进程，见
        ///     <see cref="ClashUWP.Backend.SubscriptionProtocol"/> 顶部那条边界）。
        ///     nonce 每次会话随机、由本进程写进 <c>LocalState\toast-nonce.txt</c>，
        ///     前台比对不上就只记日志不动手 —— 网页猜不到这串。
        /// </summary>
        private static string StatusXml() =>
            "<toast launch='clashuwp://open' activationType='protocol'>" +
              "<visual>" +
                "<binding template='ToastGeneric'>" +
                  $"<text>{{{KeyTitle}}}</text>" +
                  $"<text>{{{KeyRate}}}</text>" +
                  $"<text>{{{KeyTotal}}}</text>" +
                "</binding>" +
              "</visual>" +
              "<actions>" +
                $"<action content='断开连接' activationType='protocol' arguments='clashuwp://disconnect?n={_nonce}'/>" +
              "</actions>" +
              "<audio silent='true'/>" +
            "</toast>";

        /// <summary>
        /// 引导那条的 XML：正文写清"隧道不受影响 + 怎么彻底关掉横幅"，
        /// 并给一个 <c>activationType='protocol'</c> 按钮直达 <c>ms-settings:notifications</c>。
        /// ★ 做不到"一键关闭" —— 系统不给应用自助改自己通知设置的权限，只能把人送到设置页。
        /// </summary>
        private static string GuideXml() =>
            "<toast>" +
              "<visual>" +
                "<binding template='ToastGeneric'>" +
                  "<text>ClashUWP：状态通知被划掉了</text>" +
                  "<text>隧道仍在正常工作，划掉通知不影响它。这条状态通知显示实时上下行，" +
                  "被划掉后还会自动补发几次。</text>" +
                  "<text>想彻底清静：设置 → 系统 → 通知 → 找到 ClashUWP → 关闭横幅，" +
                  "或直接关掉这个应用的通知。</text>" +
                "</binding>" +
              "</visual>" +
              "<actions>" +
                "<action content='打开通知设置' activationType='protocol' arguments='ms-settings:notifications'/>" +
              "</actions>" +
            "</toast>";

        /// <summary>字节数格式化：<c>suffix="/s"</c> 时是速率，空串时是累计。</summary>
        private static string Format(double bytes, string suffix)
        {
            if (bytes < 1024) return $"{bytes:F0} B{suffix}";
            double kb = bytes / 1024.0;
            if (kb < 1024) return $"{kb:F1} KB{suffix}";
            double mb = kb / 1024.0;
            if (mb < 1024) return $"{mb:F2} MB{suffix}";
            return $"{mb / 1024.0:F2} GB{suffix}";
        }
    }
}
