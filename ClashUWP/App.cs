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
    public sealed partial class App : ReactorApplication<MainComponent>
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
    }
}
