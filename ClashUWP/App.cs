using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.UI.Reactor.Core;
using Reactor.Uwp.Hosting;

using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;

using ClashUWP.Backend;
using ClashUWP.Frontend;
using ClashUWP.Vpn;

namespace ClashUWP
{
    /// <summary>
    /// Reactor 入口 —— <b>只做入口该做的两件事</b>：起探针、起应用。
    ///
    /// 没有 App.xaml —— Reactor 是"用 C# 描述界面，不写 XAML"，所以这里不是
    /// <c>App : Application</c> + <c>InitializeComponent()</c>，而是
    /// <c>ReactorApplication&lt;T&gt;</c> + 手写 Main。
    ///
    /// 别改回 <c>Frame.Navigate(typeof(Page))</c>：纯 C# 的 Page 没有 XAML 生成的
    /// 元数据提供程序，XAML 框架按 Type 激活不了它，会直接访问冲突崩在 coreclr 里
    /// （事件日志是 Application Error 1000 / Faulting module coreclr.dll）。
    /// 要换页面就换 <c>ReactorApplication&lt;T&gt;</c> 的 T，或走框架自己的导航。
    /// </summary>
    internal sealed partial class App : ReactorApplication<MainComponent>
    {
        public static void Main(string[] args)
        {
            // ★ 第一件事：起滚动日志（<c>LocalState\logs\app.log</c>）。
            //   放在所有其它工作之前，因为「进程起来过」本身就是需要取证的一行。
            //   之所以要自建：Debug.WriteLine 带 [Conditional("DEBUG")]，发布版/AOT
            //   里根本不会被编译进去 —— 以前发布版是<b>零日志</b>的。详见 RollingLog 类注释。
            RollingLog.Init(RollingLog.Role.App);
            RollingLog.Info("app", $"启动 version={RollingLog.PackageVersion()} " +
                                   $"pid={System.Environment.ProcessId}");

            // 启动探针（纯读 profile，不碰路由）现在归后端，且<b>不再阻塞启动线程</b>：
            // 原先在这里同步 GetAwaiter().GetResult() 等 WinRT 异步，是 STA 上的标准死锁形状。
            // 详见 StartupProbe 的类注释（审计项 P3）。
            StartupProbe.Start();

            // ★【1.0.53】协议激活订阅<b>不在这里做</b>（1.0.53/1.0.54 实测过两次都不行）：
            //   · 在 Main 里订阅 <c>CoreApplication.GetCurrentView().Activated</c>
            //     ⇒ <c>COMException: 找不到元素</c>（view 还没建，Main 太早）；
            //   · 写 <c>protected override void OnActivated</c> ⇒ <b>一次都没被调用</b>
            //     （Reactor 基类没接激活分发）。
            //   ⇒ 改到 <see cref="OnLaunched"/> 里、<c>base.OnLaunched()</c> 之后：
            //     那时 Reactor 已经建好 Window.Current 并 Activate，view 一定就位。
            //     而协议激活"冷启动"时平台<b>先</b>派发 Activated 再走 Launched 的可能性，
            //     由 <see cref="PendingSubscriptionUrl"/> 兜住（事件可能早于 UI 挂载，
            //     值先暂存，UI 的 UseEffect 挂载时再取走）。

            // ★【1.0.49】订阅 app 生命周期事件 —— 以前<b>一条都没接</b>。
            //   【为什么现在要接】UWP 前台进程会被系统挂起/节流/关闭，
            //   而"进程什么时候要没了"这件事<b>只有这些事件知道</b>。
            //   接上之后至少能做到两件事：
            //     ① 每次挂起/恢复/退出都<b>留一行证据</b>（发布版也能查）——
            //        以前这些时刻在 app.log 里是完全无痕的，
            //        复盘"某个时间点之后 app 就不对了"根本无从下手；
            //     ② 明确知道<b>前台哨兵什么时候会失效</b>：
            //        HealthMonitor 那条物理网络兜底跑在前台，app 一挂起它就停摆
            //        ⇒ 插件进程里那条同功能的看门狗（1.0.49 新增，
            //          ClashUWP.Vpn/PhysicalNetworkWatchdog.cs）才是真正可靠的宿主。
            //   ★ 注意：<b>不在这三个事件里做收尾动作</b>（拆隧道之类）——
            //     隧道归平台管，插件侧才是它的宿主；app 退出时拆隧道是错的语义。
            //     这三个钩子只做"记证据 + 标状态"。
            Windows.ApplicationModel.Core.CoreApplication.Suspending += (s, e) =>
            {
                // Deferred 给出收尾窗口的时限（通常几秒），我们不用它做重活，只记一行。
                RollingLog.Info("lifecycle", "Suspending（app 将被挂起/节流）");
                try { e.SuspendingOperation.GetDeferral().Complete(); }
                catch { /* 不占这个窗口也一样能挂起 */ }
            };
            Windows.ApplicationModel.Core.CoreApplication.Resuming += (s, e) =>
            {
                RollingLog.Info("lifecycle", "Resuming（app 恢复）");
            };
            Windows.ApplicationModel.Core.CoreApplication.Exiting += (s, e) =>
            {
                RollingLog.Info("lifecycle", "Exiting（app 正在退出）");
            };

            Windows.UI.Xaml.Application.Start(_ => new App());
        }

        /// <summary>
        /// 【AppService 接点 · in-app「ClashUWP.Debug」】clashuwp-cli 的遥控入口。
        ///
        /// 【协议】请求 ValueSet["cmd"] = 一行命令原文；响应 ValueSet["out"] 结果文本，
        ///   失败 ValueSet["error"]。命令表与文件回路同源，见 <see cref="DebugCommandLoop"/>。
        ///
        /// 【为什么走 in-app 形态】清单里 windows.appService 扩展不带 EntryPoint ⇒
        ///   连接激活的是<b>本前台进程</b>，RequestReceived 跑在 UI 线程上，
        ///   VpnSession / 健康哨兵 / DebugCommandLoop 的状态直接可及。
        ///
        /// 【★ deferral 生命周期】TaskInstance 的 deferral 要撑到<b>连接关闭</b>
        ///   （ServiceClosed）才 Complete —— 平台靠它决定宿主任务的生死，提前完成
        ///   等于把会话掐了。每个请求另有一层 RequestReceived deferral,
        ///   响应发完才 Complete。
        ///
        /// 【★ 串行闸】同一条连接可能并发到达多条请求（文件回路靠"消费即删"天然
        ///   串行，这里没有那层保护）。connect/disconnect 交错执行是灾难，
        ///   所以命令执行统一过 gate 串行化。
        ///
        /// 【线程】命令体内全是 await,不阻塞 UI;logs/delay 这类长命令在
        ///   await 期间 UI 照常响应。
        /// </summary>
        protected override void OnBackgroundActivated(BackgroundActivatedEventArgs args)
        {
            base.OnBackgroundActivated(args);

            // 【1.0.44 修复 · AOT 下 TriggerDetails 的投影退化】
            //   1.0.43 实测（app.log 03:25:52.294 pid=13120）平台<b>确实投递了</b>本回调,
            //   但 trigger=WinRT.IInspectable —— 也就是 <c>TriggerDetails</c> 这个
            //   声明为 <c>object</c> 的属性在 Native AOT 下拿不到投影类型,于是
            //   <c>is not AppServiceTriggerDetails</c> 永远成立,激活被我们自己丢掉,
            //   客户端只等到 AppServiceResponseStatus.Failure。
            //
            //   这是 C#/WinRT 的已知缺陷(microsoft/CsWinRT#1860:同样是 AOT + in-app
            //   AppService + Win32 助手,cswinrt 2.1.16 就能复现,至今 open)。维护者
            //   的分析：AOT 里推不出 <c>object</c> 的真实类型,而 <c>As&lt;T&gt;()</c>
            //   "ensures the type doesn't get trimmed",单纯 <c>as</c>/(T) 会被修剪器
            //   当成可裁的引用。
            //
            //   ★ 1.0.44 实测补充（app.log 03:41:32.421 pid=16468）：
            //     path=as rawType=Windows.ApplicationModel.AppService.AppServiceTriggerDetails
            //   —— 走通的是<b>普通 as</b>。原因就是上面那条：本方法里留着 <c>As&lt;T&gt;()</c>
            //     这个调用点,投影类型被根住了,<c>TriggerDetails</c> 的 getter 这才返回
            //     真投影类型。所以下面的 <c>As&lt;T&gt;()</c> 既是兜底,<b>也是让 as 成立的
            //     锚点</b>：删掉它,as 分支会退回 WinRT.IInspectable、通道再次静默失效。
            //     两个分支都要留,path= 会告诉我们这次是哪条救的场。
            object? rawTrigger = args.TaskInstance?.TriggerDetails;

            AppServiceTriggerDetails? details = rawTrigger as AppServiceTriggerDetails;
            string path = "as";                       // 非 AOT / 上游修好后仍走这条
            if (details is null && rawTrigger is not null)
            {
                try
                {
                    details = global::WinRT.CastExtensions.As<AppServiceTriggerDetails>(rawTrigger);
                    path = "As<T>";
                }
                catch (Exception ex)
                {
                    // 投影也拿不到:那就是真不是 AppService 激活(或上游又变了)。
                    // 静默 return 在 1.0.42 咬过我们一次,这里把异常和原始类型都记下来。
                    RollingLog.Error("appservice",
                        $"TriggerDetails 投影失败 rawType={rawTrigger.GetType().FullName}", ex);
                    return;
                }
            }

            if (args.TaskInstance is not { } task || details is null)
            {
                RollingLog.Info("appservice", "无 TaskInstance 或 TriggerDetails 为空，忽略本次激活");
                return;
            }

            // caller=<PFN>:谁连进来。复盘误操作时"哪条命令在什么时候被谁下发过",
            //   这一行就是 AppService 通道的答案(文件通道没有这个信息)。
            // rawType 保留一行:AOT 投影退化如果复发,这里第一个看得见。
            RollingLog.Info("appservice",
                $"连接建立 path={path} rawType={rawTrigger?.GetType().FullName} " +
                $"name={details.Name} caller={details.CallerPackageFamilyName} " +
                $"pid={System.Environment.ProcessId}");

            var connDeferral = task.GetDeferral();
            var gate = new SemaphoreSlim(1, 1);
            var conn = details.AppServiceConnection;

            // 注:lambda 参数不写显式类型 —— CsWinRT 投影把 TypedEventHandler 的
            //   sender 投成了强类型 AppServiceConnection,写 object? 反而编不过。
            conn.RequestReceived += async (sender, e) =>
            {
                var reqDeferral = e.GetDeferral();
                try
                {
                    // 反射核实的真实形状:输入 ValueSet 在 e.Request.Message 上,
                    // 不在 e.Message(args 只有 Request / GetDeferral)。
                    // ★ ValueSet 的值声明成 object,同 TriggerDetails 一样是 AOT 投影退化
                    //   的高危点(CsWinRT#1860 只说了 TriggerDetails,值这条没人验过)。
                    //   一旦退化成 WinRT.IInspectable,"cmd" 会读成空串,表现为"命令没反应"
                    //   这种最难查的假象 —— 所以这里留一条类型日志,不要静默兜空。
                    string raw = "";
                    if (e.Request.Message.TryGetValue("cmd", out object? v))
                    {
                        raw = v as string ?? "";
                        if (v is not null and not string)
                            RollingLog.Info("appservice",
                                $"cmd 值不是 string rawType={v.GetType().FullName}");
                    }

                    string result;
                    await gate.WaitAsync();
                    try { result = await DebugCommandLoop.ExecuteAsync(raw); }
                    finally { gate.Release(); }

                    await e.Request.SendResponseAsync(new ValueSet { ["out"] = result });
                }
                catch (Exception ex)
                {
                    RollingLog.Error("appservice", "request", ex);
                    try
                    {
                        await e.Request.SendResponseAsync(new ValueSet
                        {
                            ["error"] = $"{ex.GetType().Name}: {ex.Message}",
                        });
                    }
                    catch { /* 连接已断:客户端超时自己会退出 */ }
                }
                finally { reqDeferral.Complete(); }
            };

            conn.ServiceClosed += (sender, e) =>
            {
                RollingLog.Info("appservice", $"连接关闭 status={e.Status}");
                connDeferral.Complete();
            };
        }

        /// <summary>
        /// ★【1.0.57】订阅 <c>CoreApplicationView.Activated</c>（协议激活的真正入口）。
        /// 【★ 为什么 OnLaunched 与 OnActivated 两条都要接】1.0.53~1.0.57 实测五轮才钉死：
        ///   · 只写 <c>OnActivated</c> override ⇒ 协议冷启动时它<b>会</b>被调（1.0.56 日志实证：
        ///     "激活 kind=Protocol argsType=WinRT.IInspectable"），但 Reactor 的窗口
        ///     <b>只在 OnLaunched 里建</b> —— 协议冷启动不走 OnLaunched ⇒ <b>UI 白屏</b>
        ///     （1.0.57 实测 Pane 为空，URL 解析成功了却没地方显示）；
        ///   · 在 Main 里订阅 <c>CoreApplication.GetCurrentView().Activated</c>
        ///     ⇒ <c>COMException: 找不到元素</c>（view 未建，Main 比 view 早）。
        ///   ⇒ 订阅放 <c>OnLaunched</c>（view 已就位）；协议冷启动的白屏由
        ///     <see cref="HandleActivation"/> 里补调 <c>base.OnLaunched(null!)</c> 解决
        ///     （反编译确认 Reactor 的 OnLaunched <b>完全不读 args</b>，传 null 安全）。
        /// 【Once 模式】OnLaunched 可能被调多次（多视图），重复订阅会让同一协议触发多次。
        /// </summary>
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // ★★【1.0.72 修复 · 重复 Launch 不许再建一套 Reactor 外壳】
            //   ReactorApplication.OnLaunched 每次都会 <c>new ReactorHost(CreateRootComponent())</c>，
            //   而 <c>Window.Current.Content ??= host.Root</c> 只在空窗口时生效 ——
            //   也就是说第二次 Launch（用户再次点应用图标、系统重放启动激活等）会
            //   <b>新建并渲染一个永远不会上屏的 MainComponent</b>：它的 UseEffect 照跑，
            //   于是多出一条 3 秒轮询（重复的 KernelView / VPN 状态 / 回填判定），
            //   并且这个组件实例不会被卸载（清理函数永远不执行）。
            //   本机 1.0.64 日志实证：同一次激活后 profile-sub-sync 在同一毫秒出现两份、
            //   随后启动对账的 Add 各刷一遍；再叠加下面 Adopt 的旧逻辑就出现"每 3 秒
            //   回填一次"的放大效应。
            //   ⇒ 外壳已经建好时，只补做"激活事件订阅"，不调 base。
            if (Windows.UI.Xaml.Window.Current?.Content != null)
            {
                RollingLog.Info("launch", "已有窗口内容树，跳过重复的 Reactor 外壳构建");
                EnsureActivationSubscribed();
                return;
            }

            base.OnLaunched(args);
            EnsureActivationSubscribed();
        }

        /// <summary>
        /// 订阅 <c>CoreApplicationView.Activated</c>（协议激活的真正入口）。
        /// 必须在窗口/视图就位后调用（见 <see cref="OnLaunched"/> 的说明）；
        /// Once 模式，重复调用无副作用。
        /// </summary>
        private void EnsureActivationSubscribed()
        {
            if (_activationSubscribed) return;
            _activationSubscribed = true;

            try
            {
                Windows.ApplicationModel.Core.CoreApplication.GetCurrentView().Activated += (s, e) =>
                {
                    RollingLog.Info("protocol", $"CoreApplicationView.Activated kind={e.Kind}");
                    HandleActivation(e);
                };
                RollingLog.Info("protocol", "已订阅 CoreApplicationView.Activated");
            }
            catch (Exception ex)
            {
                // 订阅失败不影响启动；真需要协议导入时用户再点一次链接即可。
                RollingLog.Warn("protocol", $"订阅 Activated 失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool _activationSubscribed;

        /// <summary>
        /// ★【1.0.53】协议激活 —— 浏览器"一键导入 Clash 订阅"的落点。
        ///
        /// 【★ 调用路径（1.0.53~1.0.54 实测后定稿）】
        ///   ① <c>CoreApplicationView.Activated</c>（<see cref="OnLaunched"/> 里订阅）——
        ///      <b>真正会触发的那条</b>。Reactor 基类没覆盖 OnActivated 也不订阅任何激活事件，
        ///      所以协议事件在它那条启动路径上原本没有分发出口；
        ///   ② 本 <c>OnActivated</c> override —— 万一将来 Reactor 补上分发，这条就是兜底。
        ///   两条都汇到 <see cref="HandleActivation"/>，逻辑只有一份。
        ///
        /// 【触发形态】清单里注册了两条协议（见 Package.appxmanifest 的 Extensions）：
        ///   · <c>clashuwp://subscribe?url=&lt;订阅链接&gt;</c>（自己的）
        ///   · <c>clash://install-config?url=&lt;订阅链接&gt;</c>（Clash 系通用，
        ///     <b>机场面板那个"一键导入"按钮点的就是它</b>）
        /// 用户在浏览器/聊天窗口点这种链接 ⇒ Windows 按 PFN 弹选择器 ⇒ 选 ClashUWP
        /// ⇒ 本方法被调用，进程里 Reactor 那套（MainComponent / UseState）已经就位。
        ///
        /// 【★★ 为什么只"填到输入框"，不自动拉取】
        ///   协议是<b>外部可触发</b>的：任何网页里一个链接都能把本进程唤起，
        ///   而拉取会往那个 URL 发请求（可能带 token，可能被记日志）。
        ///   自动拉 = 用户没确认就把订阅地址发出去，这超出"一键导入"的授权范围。
        ///   ⇒ 这里只做三件安全的事：解析 → 记日志 → 把 URL 填进 UI 的输入框。
        ///     <b>必须用户亲手点「拉取并启用」才真正发请求</b>。
        ///
        /// ★【1.0.81 · 新增】Toast 通知激活 —— 点击状态通知时打开/激活主窗口。
        ///   通知 XML 里 <c>launch="action=viewMainWindow"</c>，这里识别后调
        ///   <see cref="ActivateMainWindow"/> 把窗口带到前台。
        /// </summary>
        protected override void OnActivated(IActivatedEventArgs args)
        {
            // ★【1.0.81】先检查是不是 Toast 激活（要在 HandleActivation 之前判断）
            if (args is Windows.ApplicationModel.Activation.ToastNotificationActivatedEventArgs toastArgs)
            {
                string arguments = toastArgs.Argument ?? "";
                RollingLog.Info("toast", $"Toast 激活 argument={arguments}");

                if (arguments.Contains("action=viewMainWindow"))
                {
                    // 点击通知主体：打开/激活主窗口
                    ActivateMainWindow();
                    return;
                }

                if (arguments.Contains("action=disconnect"))
                {
                    // 点击"断开连接"按钮：触发断开逻辑
                    RollingLog.Info("toast", "用户通过通知点击断开连接");
                    TriggerDisconnectFromToast();
                    return;
                }

                // 其它 toast 参数原样忽略
                RollingLog.Info("toast", "未知 toast 激活参数，忽略");
                return;
            }

            HandleActivation(args);
        }

        /// <summary>
        /// 激活事件的实际处理。★ <see cref="OnActivated"/> 与 <c>CoreApplication.Activated</c>
        /// 两条路都汇到这里，保证逻辑只有一份。
        /// ★ 只处理协议激活，别的 kind（文件/分享/toast）原样忽略 —— 本项目不声明那些扩展。
        /// </summary>
        private void HandleActivation(object args)
        {
            // ★ 先无条件记一行<b>真实类型</b>：1.0.56 实测时这里是
            //   "激活 kind=Protocol argsType=WinRT.IInspectable" ——
            //   args 的运行时类型退化成了 IInspectable（AOT 下声明为 object 的属性的老毛病，
            //   与 1.0.44 修 AppService.TriggerDetails 时同一个坑，Docs/09 §13.4），
            //   但 Kind 仍然是 Protocol ⇒ 事件是对的，只是投影断了。
            RollingLog.Info("protocol",
                $"激活 kind={(args is IActivatedEventArgs a ? a.Kind.ToString() : args.GetType().Name)} " +
                $"argsType={args.GetType().FullName}");

            if (args is not IActivatedEventArgs activated)
            {
                RollingLog.Info("protocol", "激活参数不是 IActivatedEventArgs，忽略");
                return;
            }

            if (activated.Kind != ActivationKind.Protocol)
            {
                RollingLog.Info("protocol", "非协议激活，忽略");
                return;
            }

            // ★★【1.0.57】协议<b>冷启动</b>时 OnLaunched 不会被调用，而 Reactor 的窗口
            //   只在 OnLaunched 里建 ⇒ UI 白屏（1.0.57 实测：URL 解析成功了，
            //   但 Window 内容树是空的，用户根本看不到输入框）。
            //   ⇒ 这里补一次窗口创建。反编译确认 ReactorApplication.OnLaunched
            //     <b>完全不读 args</b>（它只做 BootstrapWinUi2 + 建 ReactorHost +
            //     Window.Current.Activate），所以传 null! 是安全的。
            //   ★ 只在外壳还没建时补（Window.Current.Content == null）——
            //     已经活着的时候再调会把当前内容树替换掉。
            try
            {
                if (Windows.UI.Xaml.Window.Current?.Content == null)
                {
                    RollingLog.Info("protocol", "外壳未建，补调 OnLaunched(null) 创建 UI");
                    OnLaunched(null!);
                }
            }
            catch (Exception ex)
            {
                RollingLog.Warn("protocol", $"补建外壳失败: {ex.GetType().Name}: {ex.Message}");
            }

            // ★★ AOT 下 args 投影成 WinRT.IInspectable（上面那行实测），
            //   与 1.0.44 修 AppService.TriggerDetails 时<b>完全同一个坑</b>
            //   （CsWinRT#1860：AOT 里推不出 object 的真实类型）。
            //   【★ 写法照抄 1.0.44 那次救活通道的两段式】
            //     ① 先 <c>as</c>（非 AOT / 投影完好时走这条）；
            //     ② as 不到再 <c>global::WinRT.CastExtensions.As&lt;T&gt;()</c>（AOT 兜底）。
            //   【★★ 为什么两段都要留】1.0.44 实测发现：<c>As&lt;T&gt;()</c> 这个调用点
            //     <b>本身就是让 as 成立的锚点</b> —— 它"ensures the type doesn't get trimmed"，
            //     单纯 as/(T) 会被修剪器当成可裁的引用。删掉兜底那段，
            //     as 分支会退回 WinRT.IInspectable、协议导入再次静默失效。
            //     所以下面记一行 path= 告诉我们这次是哪条救的场。
            Windows.ApplicationModel.Activation.ProtocolActivatedEventArgs? protocol =
                activated as Windows.ApplicationModel.Activation.ProtocolActivatedEventArgs;
            string path = "as";
            if (protocol is null)
            {
                try
                {
                    protocol = global::WinRT.CastExtensions.As<
                        Windows.ApplicationModel.Activation.ProtocolActivatedEventArgs>(activated);
                    path = "As<T>";
                }
                catch (Exception ex)
                {
                    RollingLog.Warn("protocol", $"投影 ProtocolActivatedEventArgs 失败: {ex.GetType().Name}: {ex.Message}");
                    return;
                }
            }

            if (protocol is null)
            {
                RollingLog.Warn("protocol", "投影 ProtocolActivatedEventArgs 得到 null");
                return;
            }
            RollingLog.Info("protocol", $"投影成功 path={path}");

            string raw = protocol.Uri?.AbsoluteUri ?? "";
            RollingLog.Info("protocol", $"收到激活 uri={raw}");

            SubscriptionProtocol.Result r = SubscriptionProtocol.TryParse(raw);
            if (!r.Ok)
            {
                RollingLog.Warn("protocol", $"解析失败: {r.Error}");
                return;
            }

            RollingLog.Info("protocol", $"解析出订阅 url={r.Url}（等用户确认后拉取）");
            // ★ 进程级暂存：UI 挂载后取走。用 Volatile 写，UI 侧用 Volatile 读 ——
            //   协议激活可能跑在与组件渲染不同的时机（甚至首次渲染之前）。
            System.Threading.Volatile.Write(ref PendingSubscriptionUrl, r.Url);
        }

        /// <summary>
        /// 最近一次协议激活带入的订阅 URL（空串 = 没有待处理的）。
        /// 由 <see cref="HandleActivation"/> 写、由 MainComponent 的挂载 effect 读并清空。
        /// </summary>
        internal static string PendingSubscriptionUrl = "";

        /// <summary>
        /// ★【1.0.81】把主窗口带到前台：Toast 点击时调用。
        /// 如果窗口已存在且有内容就 Activate；如果窗口为空则重建 Reactor 外壳。
        /// </summary>
        private static void ActivateMainWindow()
        {
            try
            {
                var window = Windows.UI.Xaml.Window.Current;
                
                // 窗口不存在或内容为空：需要重建 Reactor 外壳（冷启动路径）
                if (window?.Content == null)
                {
                    RollingLog.Info("toast", "窗口内容为空，补建 Reactor 外壳");
                    // ★ 协议冷启动也是这么做的（见 HandleActivation 386-387 行）：
                    //   反编译确认 ReactorApplication.OnLaunched 完全不读 args，传 null! 安全
                    //   它会做 BootstrapWinUi2 + 建 ReactorHost + Window.Current.Activate
                    var app = Windows.UI.Xaml.Application.Current as App;
                    if (app != null)
                    {
                        app.OnLaunched(null!);
                        RollingLog.Info("toast", "已重建 Reactor 外壳并激活窗口");
                    }
                    else
                    {
                        RollingLog.Warn("toast", "Application.Current 为空，无法重建外壳");
                    }
                    return;
                }
                
                // 窗口已经活着且有内容：把它激活到前台
                window.Activate();
                RollingLog.Info("toast", "已将主窗口激活到前台");
            }
            catch (Exception ex)
            {
                RollingLog.Warn("toast", $"激活主窗口失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// ★【1.0.81】从 Toast 按钮触发断开连接。
        /// 通过 VpnSession.DisconnectAsync 发起断开。
        /// </summary>
        private static async void TriggerDisconnectFromToast()
        {
            try
            {
                // 调用 VpnSession.DisconnectAsync（后台线程，fire-and-forget）
                var result = await ClashUWP.Backend.VpnSession.DisconnectAsync();
                RollingLog.Info("toast", $"Toast 断开连接结果: {result}");
            }
            catch (Exception ex)
            {
                RollingLog.Error("toast", "Toast 断开连接失败", ex);
            }
        }
    }
}
