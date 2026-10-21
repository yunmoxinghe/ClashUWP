using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;

using ClashUWP.Backend;
using ClashUWP.Backend.Mihomo;
using ClashUWP.Vpn;
using static Microsoft.UI.Reactor.Factories;

namespace ClashUWP.Frontend
{
    /// <summary>
    /// 【前端 · 唯一界面组件】
    ///
    /// ★ 1.0.48 重写：把"后端已有、前端没露"的能力全部接到界面上。
    ///   后端早就有 12 个入口（status / groups / select / connections / mode / bind /
    ///   delay / conf / logs / connect / disconnect / recreate），
    ///   但 1.0.47 的界面只有一排按钮 + 一行状态文本，**能力全在库里、用户一点都摸不到** ——
    ///   那就是"实验台"而不是"产品"。这一版把它们摆出来。
    ///
    /// 【这一版加了什么】
    ///   ① <b>滚动</b>：整页放进 <c>ScrollViewer</c>（Reactor 0.1.0-alpha.13 有
    ///      <c>ScrollViewerHandler</c>），日志/连接/节点列表不再被顶出屏幕；
    ///   ② <b>选节点</b>：组 → 节点两级列表，点一下就 <c>PUT /proxies/{组}</c>（G2 的 UI 化）；
    ///      两级在 1.0.84 按安卓版收成正好两层：<b>顶部一排 = 组页签</b>，<b>下面一块 = 该组那一页</b>
    ///      （标题就是组名），页内一列混排、按订阅原序，组行用加粗 + 灰字副标题与节点行拉开层级
    ///      （<c>MemberList</c>）；
    ///   ③ <b>连接视图</b>：活动连接的目标/链路/上下行，3 秒一次自动刷新（G3/G4）；
    ///   ④ <b>运行态</b>：mode / log-level / interface-name 常驻显示（G8 的 UI 化）；
    ///   ⑤ <b>日志窗</b>：可滚动的日志尾部。
    ///
    /// 【★ 为什么自动刷新用 3 秒】内核侧 <c>url-test</c> 组自带 <c>interval:300</c>（秒级），
    ///   3 秒足够跟上手感，又不至于把包内回环打满（AppContainer 里每一次 HTTP 都有开销）。
    ///   ★ 刷新是<b>纯读</b>（<c>GET /configs</c> / <c>/proxies</c> / <c>/connections</c>），
    ///   <b>不碰隧道</b> —— 内核没起时它就返回"未连接"，不会出事。
    ///
    /// 【纪律】连接/断开/重建这三下是碰隧道的，UI 上就是按钮，点了就是点了；
    ///   自动刷新<b>绝不会</b>去调它们。
    ///
    /// ★★【1.0.67 · 侧边栏 + 云母】这一版只动"怎么摆"，不动"有什么"：
    ///   ① 根元素从 <c>ScrollViewer(VStack(…))</c> 换成 <c>NavigationView(…)</c>，
    ///      左侧四页：<b>主页 / 代理 / 订阅 / 设置</b>（原先 11 个区块竖着排一条，
    ///      找一个动作要滚半屏）；
    ///   ② 根元素挂 <c>.Backdrop(BackdropKind.Mica)</c>。★ UWP 侧的云母不是 WinUI 3
    ///      那条 <c>Window.SystemBackdrop</c>：框架在 <c>ReactorHost.ApplyBackdrop</c>
    ///      里走 WinUI 2 的 <c>BackdropMaterial.ApplyToRootOrPageBackground(root, true)</c>
    ///      并把 root 背景置空（反编译 alpha.13 的 <c>Reactor.uwp.dll</c> 核过这段，
    ///      自家画廊 <c>samples/Reactor.Gallery/SampleShell.cs</c> 也是同一挂法），
    ///      所以"有没有云母"要拿实机截图判，不能靠代码断言；
    ///      设置页给了「云母 / 桌面亚克力 / 无」三档当场对比（<b>不落盘</b>，理由见
    ///      <see cref="MaterialPicker"/>）。
    ///   ③ <b>没有</b>铺卡片背景：WinUI 2.8 的资源键集合里没有 WinUI 3 那套 Fluent 颜色令牌
    ///      （<c>CardBackgroundFillColorDefaultBrush</c> 一类），而框架的
    ///      <c>ThemeResource.Brush()</c> 找不到键时返回<b>透明笔刷而不是抛</b>
    ///      （<c>BackdropExtensions</c> 同族代码，反编译可见 <c>?? new SolidColorBrush(Colors.Transparent)</c>）
    ///      ⇒ <c>.Background(Theme.CardBackground)</c> 在这里等于"画了个寂寞"，
    ///      所以层级靠间距与小标题（<see cref="Section"/>），不靠假卡片。
    ///   ④ 删掉 1.0.48 遗留的脚手架：<c>"Reactor.Uwp 已接管（N）"</c> 那行标题与
    ///      <c>点一下</c> 计数器 —— 接管验证早就过了，留着只会挡在主页最上面。
    /// </summary>
    internal sealed class MainComponent : Component
    {
        // ==================== 外壳：侧边栏四项 ====================

        // 索引即 MenuItems 的顺序，也是 NavigationView.SelectedIndex 的取值。
        private const int PageHome = 0;
        private const int PageProxy = 1;
        private const int PageSubscription = 2;
        private const int PageSettings = 3;

        /// <summary>
        /// 侧边栏条目。★ 图标是<b>原始字形码</b>（框架把它直接塞进 <c>FontIcon.Glyph</c>，
        /// 见 <c>NavigationViewHandler.FillMenuGroup</c>），所以字形必须来自
        /// Segoe MDL2 Assets 的真表：这四个码位是从官方 WinUI-Gallery 的
        /// <c>WinUIGallery/Samples/Iconography/IconsData.json</c>（name→code 对照表）
        /// 逐条查出来的，不是凭印象写的 —— 猜错的码位不会报错，只会画成另一个图标或豆腐块。
        /// </summary>
        private static readonly NavigationViewItemData[] MenuItems =
        {
            NavItem("主页", "\uE80F"),   // Home
            NavItem("代理", "\uE774"),   // Globe
            NavItem("订阅", "\uE896"),   // Download
            NavItem("设置", "\uE713"),   // Setting
        };

        // ---- 会话状态 ----
        private Action<string> _setVpnStatus = _ => { };
        private Action<string> _setActionHint = _ => { };

        // ---- 内核快照（G3/G4/G8 的 UI 化）----
        private Action<KernelView.Snapshot> _setSnap = _ => { };

        // ---- 日志 ----
        private Action<string> _setLogText = _ => { };
        private Action<bool> _setLogOpen = _ => { };

        // ---- 选节点（G2 的 UI 化）----
        private Action<string> _setSelectedGroup = _ => { };

        // ---- 订阅元信息（G1 的展示部分）----
        private SubscriptionView.Info _subInfo = SubscriptionView.Info.Missing();
        private Action<SubscriptionView.Info> _setSubInfo = _ => { };

        // ---- 订阅管理（G1：拉取 / 切换 / 更新）----
        private string _subUrl = "";
        private Action<string> _setSubUrl = _ => { };
        private List<SubscriptionManager.Profile> _subProfiles = new();
        private Action<List<SubscriptionManager.Profile>> _setSubProfiles = _ => { };
        private SubscriptionManager.ActiveInfo _subActive = new("", "");
        private Action<SubscriptionManager.ActiveInfo> _setSubActive = _ => { };

        // ---- 隧道实际连接的订阅 id（★【1.0.86 · Docs/36 B 案】分歧必须看得见）----
        private Action<string> _setConnectedSub = _ => { };

        // ---- 应用分流（G9）----
        // ★ 状态里存的是<b>规范化后的名单行</b>（<c>kind|标识|去向</c>），不是 AppSplit 对象：
        //   每次增删都换一个<b>新的 List</b> 交给 UseState ——  Reactor 的 UseState 靠
        //   " setter 收到新值"重渲染，把同一个 List 原地改了再 set 回去，
        //   很可能一次重渲染都不触发（引用相等）。这条与 <see cref="_subProfiles"/> 同一口径。
        private Action<List<string>> _setSplitLines = _ => { };
        private Action<string> _setSplitNote = _ => { };
        private Action<string> _setSplitMsg = _ => { };
        private Action<string> _setProbeText = _ => { };
        private Action<string> _setSplitKind = _ => { };
        private Action<string> _setSplitIdentity = _ => { };
        private Action<string> _setSplitTarget = _ => { };
        // 输入框的即时值：按钮是"渲染之外"的代码，只能读字段（见 1.0.63 的值镜像纪律）。
        private string _splitKind = "name";
        private string _splitIdentity = "";
        private string _splitTarget = "DIRECT";

        // ---- DNS 覆盖（1.0.40 · DNS 可配）----
        // 编辑器直接拿整份 dns-config.txt 的原文：它是一行一个键值的配置文本，
        // 头部注释就是使用说明 ⇒ 保存回原文，不丢用户备注（理由见 DnsConfigStore）。
        private Action<string> _setDnsText = _ => { };
        private Action<string> _setDnsMsg = _ => { };
        private Action<string> _setDnsSnapshot = _ => { };
        // 按钮在渲染之外读它（同 _splitIdentity 的值镜像纪律）。
        private string _dnsText = "";

        public override Element Render()
        {
            // ---- 外壳状态（【1.0.67】）----
            var (page, setPage) = UseState(PageHome);
            var (material, setMaterial) = UseState(0);   // 0=云母 1=桌面亚克力 2=无

            var (vpnStatus, setVpnStatus) = UseState("未连接");
            var (actionHint, setActionHint) = UseState("");
            var (snap, setSnap) = UseState(KernelView.Snapshot.Failed("(尚未查询)"));
            var (logText, setLogText) = UseState("");
            var (logOpen, setLogOpen) = UseState(false);
            var (selectedGroup, setSelectedGroup) = UseState("");
            var (subInfo, setSubInfo) = UseState(SubscriptionView.Info.Missing());
            var (subUrl, setSubUrl) = UseState("");
            var (subProfiles, setSubProfiles) = UseState(new List<SubscriptionManager.Profile>());
            var (subActive, setSubActive) = UseState(new SubscriptionManager.ActiveInfo("", ""));
            var (connectedSub, setConnectedSub) = UseState("");

            // ---- 应用分流（G9）状态 ----
            var (splitLines, setSplitLines) = UseState(new List<string>());
            var (splitNote, setSplitNote) = UseState("(还没读过)");
            var (splitMsg, setSplitMsg) = UseState("");
            var (probeText, setProbeText) = UseState("");
            var (splitKind, setSplitKind) = UseState("name");
            var (splitIdentity, setSplitIdentity) = UseState("");
            var (splitTarget, setSplitTarget) = UseState("DIRECT");

            // ---- DNS 覆盖（1.0.40）状态 ----
            var (dnsText, setDnsText) = UseState("");
            var (dnsMsg, setDnsMsg) = UseState("");
            var (dnsSnapshot, setDnsSnapshot) = UseState("(还没读过)");

            _setVpnStatus = setVpnStatus;
            _setActionHint = setActionHint;
            _setSnap = setSnap;
            _setLogText = setLogText;
            _setLogOpen = setLogOpen;
            _setSelectedGroup = setSelectedGroup;
            _setSubInfo = setSubInfo;
            _setSubUrl = setSubUrl;
            _setSubProfiles = setSubProfiles;
            _setSubActive = setSubActive;
            _setConnectedSub = setConnectedSub;
            _setSplitLines = setSplitLines;
            _setSplitNote = setSplitNote;
            _setSplitMsg = setSplitMsg;
            _setProbeText = setProbeText;
            _setSplitKind = setSplitKind;
            _setSplitIdentity = setSplitIdentity;
            _setSplitTarget = setSplitTarget;
            _setDnsText = setDnsText;
            _setDnsMsg = setDnsMsg;
            _setDnsSnapshot = setDnsSnapshot;
            // ★ 值镜像同 _subUrl 那条纪律：「添加」/「保存」按钮在渲染之外读这些字段。
            _splitKind = splitKind;
            _splitIdentity = splitIdentity;
            _splitTarget = splitTarget;
            _dnsText = dnsText;

            // ★【1.0.63】值镜像：字段（_subUrl/_subActive）是给"渲染之外"的代码用的
            //   （轮询回填读 _subActive.Id、拉取按钮读 _subUrl），而 UseState 的值只活在
            //   本次渲染的局部变量里 —— 只赋 setter 不赋值的话，字段永远是初始值：
            //   · _subActive 恒为 ("","") ⇒ 轮询判定"连的订阅≠生效"永真，
            //     每 3 秒重复回填一次 config.yaml（1.0.62 实测日志连续刷 profile-sub-adopt）；
            //   · _subUrl 恒为 "" ⇒「拉取并启用」永远报"订阅 URL 不能为空"。
            _subUrl = subUrl;
            _subActive = subActive;

            // 取证与调试通道的装配交给后端，前端只负责在首次渲染时点一次火。
            Diagnostics.StartOnce();

            // ★ 自动刷新：首次渲染后起一个循环，纯读，3 秒一次。
            //   依赖数组为空 ⇒ 只在挂载时启动一次；清理函数在卸载时取消循环。
            UseEffect(() =>
            {
                var cts = new System.Threading.CancellationTokenSource();
                _ = Task.Run(async () =>
                {
                    // ★★【1.0.86 · Docs/36】回填判据从"边沿触发"改回<b>电平触发</b>。
                    //   【1.0.72 当初为什么改成边沿】那时 app 内切订阅（ActivateAsync）只热重载、
                    //     不重连 profile，于是"隧道仍是 A、生效已改成 B"被当成<b>正常中间态</b>；
                    //     电平逻辑每 3 秒把 A 回填成生效，用户刚点的切换被自己撤销
                    //     （本机 1.0.64 日志：profile-sub-adopt 连续刷了几分钟）。
                    //   【这条前提现在已不成立】A 案让 ActivateAsync 真正把会话切到新 profile，
                    //     "账本与隧道长期分叉"不再是正常态；而边沿触发为此付出的代价是致命的：
                    //     它只看 connectedSub 变没变，分叉时那个值一次都没变 ⇒
                    //     唯一能发现错位的观察者失明（Docs/36 E10：整份 app.log 零条 adopt）。
                    //   【1.0.72 那条撤销坑怎么防】改为"切换进行中让路"（VpnSession.SwitchInProgress），
                    //     而不是"长期不比对"。首轮也不再只记基线不判定 —— 启动时"生效 B / 隧道 A"
                    //     现在就是要按隧道（真相）跟账的形状，不再当残留放过。
                    string lastConnectedSub = "";
                    bool firstPoll = true;
                    // ★【1.0.80】轮询异常要出声，但<b>只在"错误类型变了"时出声</b>：
                    //   这里是 3 秒一次的循环，每轮都记就成了刷屏（滚动日志的行格式是
                    //   看门狗的机器契约，热路径禁记）。旧写法是裸 <c>catch { }</c>，
                    //   症状和 1.0.79 的前台哨兵一模一样 —— 状态文本悄悄停在最后一次成功
                    //   的读数上，界面看着"Connected"，实际早就读不出来了，日志里查不到任何痕迹。
                    string lastPollError = "";

                    while (!cts.IsCancellationRequested)
                    {
                        try
                        {
                            var s = await KernelView.LoadAsync();
                            _setSnap(s);

                            // ★【1.0.81 · Q4】内核刚起来（边沿触发，每轮只回放一次）→
                            //   回放上次记下的节点选择，弥补 home 只读导致的跨重启不持久。
                            string replayMsg = await MihomoController.ReplaySelectionsAsync();
                            if (replayMsg.Length > 0)
                                RollingLog.Info("selection-replay", replayMsg);
                            // ★ 同时刷新 VPN 连接状态：系统快速设置发起的连接绕过了我们的
                            //   按钮，前台没有回调，只能靠轮询反映真实 ConnectionStatus。
                            string status = await VpnSession.StatusAsync();
                            _setVpnStatus(status);

                            // ★★【1.0.86 · Docs/36 B 案】回填改成<b>电平触发</b>。
                            //   【为什么边沿不够】边沿只看"connectedSub 变没变"，而 Docs/36 那次
                            //   分叉里 connectedSub 从头到尾都是同一条（旧订阅），一次都没变 ⇒
                            //   唯一能发现"账本≠隧道"的观察者结构性失明（实测：整份 app.log
                            //   零条 profile-sub-adopt）。现在只要"隧道连着 X、账本记着 Y"就跟上。
                            //   【判据为什么是"隧道为准"】插件把 subId 钉在 profile 的
                            //   CustomConfiguration 上，每次（重）拨号都按它覆写 config.kernel.yaml
                            //   ⇒ profile 是真相，config.yaml 只是账本，账本跟隧道走。
                            //   【为什么要让路给切换】A 案的切换有几秒窗口：账本已改成新订阅、
                            //   旧会话还没断干净，这一刻闯进来就会把用户刚点的切换撤销
                            //   （= 1.0.72 那条坑换个方向复发）。切换进行中一律不动账本。
                            string connectedSub = await VpnSession.ConnectedSubAsync();
                            _setConnectedSub(connectedSub);

                            // 并发观测仍按边沿：只在读数变化时查一次，不每 3 秒查一遍。
                            bool changed = connectedSub != lastConnectedSub;
                            if (firstPoll)
                            {
                                firstPoll = false;
                                if (connectedSub.Length > 0)
                                    RollingLog.Info("profile-sub-adopt",
                                        $"启动基线：隧道连着 {connectedSub}，生效记为 {_subActive.Id}" +
                                        "（电平判定，本轮起按隧道跟账）");
                            }
                            else if (changed)
                            {
                                // ★【快速面板改造】切换瞬间顺带看一眼有没有"两条同时 Connected"：
                                //   平台对同插件并行没有实测背书，这里只记日志不拆会话。
                                string parallel = await VpnSession.ParallelConnectedAsync();
                                if (parallel.Length > 0)
                                    RollingLog.Warn("profile-parallel",
                                        $"多条 profile 同时 Connected：{parallel}（并发行未实测，仅记录）");
                                if (connectedSub.Length == 0)
                                    RollingLog.Info("profile-sub-adopt",
                                        $"系统侧已断开（此前 {lastConnectedSub}），生效订阅保持不变");
                            }
                            lastConnectedSub = connectedSub;

                            if (connectedSub.Length > 0
                                && !VpnSession.SwitchInProgress
                                && !string.Equals(connectedSub, _subActive.Id,
                                                  StringComparison.OrdinalIgnoreCase))
                            {
                                var adopt = await SubscriptionManager.AdoptAsync(connectedSub);
                                if (adopt.Ok)
                                {
                                    _setActionHint(adopt.Message);
                                    await RefreshSubscriptionsAsync();
                                }
                                else
                                {
                                    RollingLog.Warn("profile-sub-adopt", adopt.Message);
                                }
                            }

                            // 恢复也要出声一次：否则"只剩失败行"会被读成"到现在还瞎着"。
                            if (lastPollError.Length > 0)
                            {
                                RollingLog.Info("ui-poll", "轮询已恢复");
                                lastPollError = "";
                            }
                        }
                        catch (Exception ex)
                        {
                            // ★【1.0.81 · Q4】内核掉线 → 复位回放边沿，下次起来再回放一次。
                            MihomoController.MarkKernelUnreachable();

                            // ★ 一次失败不该停掉轮询（这句老注释的语义没变），但"失败"本身
                            //   必须能查到 —— 只在错误类型<b>变化</b>时记一行，避免 3 秒刷屏。
                            //   1.0.79 实机教训：前台哨兵整条静默而日志零痕迹，
                            //   这种"保护自己死了没人知道"比漏判一次严重得多。
                            string kind = $"{ex.GetType().Name}: {ex.Message}";
                            if (kind != lastPollError)
                            {
                                RollingLog.Warn("ui-poll", $"本轮刷新失败（同类不再重复记）：{kind}");
                                lastPollError = kind;
                            }
                        }
                        try { await Task.Delay(3000, cts.Token); }
                        catch { break; }
                    }
                });
                return () => cts.Cancel();
            }, Array.Empty<object>());

            // 选中的组若不在当前快照里（换订阅/组消失），回落到第一个。
            string activeGroup = ResolveGroup(snap, selectedGroup);

            // ★★ 协议激活带入的订阅 URL（"一键导入"）—— <b>每次渲染都查，不在 UseEffect 里</b>。
            //   【1.0.57 踩过的坑】这段原本写在 UseEffect（空依赖）里，结果协议冷启动时：
            //     补建外壳 → 订阅 → 解析 URL → 写 PendingSubscriptionUrl，
            //     而 UseEffect 在<b>外壳建好的那一刻</b>就跑了 ⇒ 那时 pending 还是空，
            //     URL 永远填不进输入框（1.0.58 实测：TextBox 只显示 placeholder）。
            //   ⇒ 改成渲染时直读。取一次就清空，所以不会重复填。
            //   ★ 用户点「拉取并启用」才真正发请求（安全边界见 App.HandleActivation）。
            {
                string pending = System.Threading.Volatile.Read(ref App.PendingSubscriptionUrl);
                if (pending.Length > 0)
                {
                    System.Threading.Volatile.Write(ref App.PendingSubscriptionUrl, "");
                    _setSubUrl(pending);
                    _setActionHint("已从浏览器导入订阅链接，确认后点「拉取并启用」");
                }
            }

            // ★ 订阅元信息<b>不</b>进 3 秒轮询：config.yaml 是手工放置的，不会自己变；
            //   而每次轮询都读一遍 36 KB 文件纯属浪费。只在挂载时读一次 + 手动刷新按钮读。
            UseEffect(() =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _setSubInfo(await SubscriptionView.LoadAsync());
                        _setSubProfiles(await SubscriptionManager.ListAsync());
                        _setSubActive(await SubscriptionManager.GetActiveAsync());
                        // ★【1.0.62】启动对账：老版本拉过的订阅还没有系统 profile，
                        //   不补注册的话快速设置面板里就是空的（详见 SyncSystemProfilesAsync）。
                        //   放在读列表之后：有列表才有得补；失败只进日志，不挡 UI。
                        // ★【1.0.72】汇总行由 SyncSystemProfilesAsync 自己记（1.0.62~1.0.71
                        //   里外各记一遍，同一毫秒出现两条一模一样的 profile-sub-sync）。
                        await SubscriptionManager.SyncSystemProfilesAsync();
                    }
                    catch { /* 读不到就显示"不存在"，不让 UI 白屏 */ }

                    // ★ 应用分流名单也只在挂载时读一次（同上一条理由：手工文件不会自己变）。
                    //   单独一个 try：订阅那边读失败不该让名单也不显示。
                    try
                    {
                        var view = await AppSplitStore.LoadAsync();
                        _setSplitLines(RuleLines(view.Split));
                        _setSplitNote(view.LastInjection);
                        if (view.Split.Rejected.Count > 0)
                            _setSplitMsg($"文件里有 {view.Split.Rejected.Count} 行没被采纳：" +
                                         string.Join(" / ", StrList(view.Split.Rejected)));
                    }
                    catch { _setSplitNote("(读取名单失败)"); }

                    // ★ DNS 覆盖（1.0.40）同样只在挂载时读一次：dns-config.txt 是手工文件，
                    //   不会自己变。文件不存在时 DnsConfigStore 会给出带键说明的模板正文。
                    try
                    {
                        var dv = await DnsConfigStore.LoadAsync();
                        _setDnsText(dv.Text);
                        _setDnsSnapshot(dv.LastSnapshot);
                        if (dv.Parsed.Rejected.Count > 0)
                            _setDnsMsg($"文件里有 {dv.Parsed.Rejected.Count} 行没被采纳：" +
                                       string.Join(" / ", StrList(dv.Parsed.Rejected)));
                    }
                    catch { _setDnsSnapshot("(读取 dns-config.txt 失败)"); }
                });
            }, Array.Empty<object>());

            // ★★ 四页共用同一份状态与同一条轮询：切页<b>不</b>重启轮询、不重新读文件。
            //   （轮询挂在挂载时的那次渲染上，切页只换 content 子树。）
            Element body = page switch
            {
                PageProxy => ProxyPage(snap, activeGroup, actionHint),
                PageSubscription => SubscriptionPage(subInfo, subProfiles, subActive, subUrl, actionHint),
                PageSettings => SettingsPage(actionHint, logOpen, logText, material, setMaterial,
                        splitLines, splitNote, splitMsg, probeText, splitKind, splitIdentity, splitTarget,
                        dnsText, dnsMsg, dnsSnapshot),
                _ => HomePage(vpnStatus, actionHint, snap, subActive, connectedSub),
            };

            return NavigationView(
                    content: body,
                    menuItems: MenuItems,
                    paneDisplayMode: NavPaneDisplayMode.Left,
                    selectedIndex: page,
                    // 受控选择：只认落在四项里的 index。内置那条「设置」第五项
                    // （IsSettingsVisible 默认开着的那个）会报成越界值，本批直接关掉它 ——
                    // 设置是侧边栏第四项，不需要框架再送一条到-pane-底部。
                    onSelectedIndexChanged: index =>
                    {
                        if (index >= 0 && index < MenuItems.Length) setPage(index);
                    },
                    isSettingsVisible: false,
                    // 没有回退栈（四页平级），返回键就该收起来而不是灰着。
                    isBackButtonVisible: NavBackButtonVisible.Collapsed,
                    header: PageHeader(page),
                    paneTitle: "ClashUWP")
                .Backdrop(MaterialKind(material));
        }

        // ==================== 外壳的三个小工 ====================

        /// <summary>页标题（落在 <c>NavigationView.Header</c> 上，由控件模板给 28px 半粗）。</summary>
        private static string PageHeader(int page) => page switch
        {
            PageProxy => "代理",
            PageSubscription => "订阅",
            PageSettings => "设置",
            _ => "主页",
        };

        /// <summary>
        /// 设置页那个材质序号 → 框架的 <see cref="BackdropKind"/>。
        /// ★ 0 是默认（云母）；<c>None</c> 让框架把 root 背景恢复成不透明，用来对照
        ///   "到底是材质没生效，还是材质生效了但内容层把它盖住了"。
        /// </summary>
        private static BackdropKind MaterialKind(int index) => index switch
        {
            1 => BackdropKind.DesktopAcrylic,
            2 => BackdropKind.None,
            _ => BackdropKind.Mica,
        };

        /// <summary>
        /// 一个区块 = 小标题 + 内容。
        /// ★ 标题的字号与字重是<b>写死的</b>（20 / 600），没走 <c>ApplyStyle("SubtitleTextBlockStyle")</c>：
        ///   那条路径要在 <c>Application.Current.Resources</c> 里查到同名样式键才有效，
        ///   而查不到时框架只记一条警告、什么都不画（<c>ElementExtensions.ApplyNamedStyle</c>），
        ///   在本机又没有 XAML 可以对着看 ⇒ 用确定生效的显式值，不用"可能生效"的样式键。
        ///   这套数值就是框架自己的 <c>SubHeading</c> 工厂用的那对（见 Reactor.Uwp 源码
        ///   <c>Elements/Factories.cs</c>），不算另立标准。
        /// ★ 没写 <c>.HeadingLevel(Level2)</c>：alpha.13 的 UWP 版
        ///   <c>ElementExtensions</c> 里<b>没有</b>这个修饰器（对照反编译清单，
        ///   <c>CornerRadius</c> 同样没有），写了就是 CS1061。读屏拿不到标题层级，
        ///   这是本包的现状，不是本批偷懒。
        /// </summary>
        private static Element Section(string title, params Element?[] items) =>
            VStack(8,
                TextBlock(title)
                    .FontSize(20)
                    .FontWeight(new Windows.UI.Text.FontWeight(600)),
                VStack(6, items));

        /// <summary>一整页：外层滚动 + 区块之间的呼吸（20px，4px 网格）。</summary>
        private static Element Page(params Element?[] sections) =>
            ScrollViewer(VStack(20, sections))
                .Padding(24, 20, 24, 20);

        // ==================== 各区块 ====================

        /// <summary>
        /// 主页：先把"隧道在不在、点哪三下"讲完，再给运行态与活动连接。
        /// ★ 这三块共用 3 秒轮询那份快照，切到别的页轮询照跑（见 <c>Render</c> 里的挂载 effect）。
        /// </summary>
        private Element HomePage(string vpnStatus, string actionHint, KernelView.Snapshot snap,
            SubscriptionManager.ActiveInfo subActive, string connectedSub) =>
            Page(
                Section("VPN 控制",
                    TextBlock($"当前状态：{vpnStatus}"),
                    // ★★【1.0.86 · Docs/36 B 案兜底】隧道与账本对不上时，这句话必须画在界面上。
                    //   A 案负责让它一致，这一行负责在它不一致时<b>不骗人</b> ——
                    //   本项目纪律：静默或指错的失败一律算没修。
                    DivergenceLine(subActive, connectedSub),
                    actionHint.Length > 0 ? TextBlock(actionHint).Foreground(Theme.SecondaryText) : Empty(),
                    HStack(8,
                        Button("建/重建 profile", () => _ = RunAsync(_setActionHint, "重建中…", VpnSession.RecreateAsync)),
                        // ★【快速面板改造】按钮从"连基础 profile"改为"连<b>当前生效订阅</b>的
                        //   系统 profile"——与用户在快速设置里点那条订阅是同一条平台路径，
                        //   连完快速面板亮的就是那条订阅（基础条目已隐藏，不再出现
                        //   "app 里点了连接、面板亮的是裸 ClashUWP"的错位）。
                        //   没有生效订阅就禁用（哥哥的拍板）：基础 profile 只留给诊断路径，
                        //   不再当用户的连接入口。
                        Button("连接 VPN", () => _ = RunAsync(_setVpnStatus, "连接中…",
                                () => VpnSession.ConnectAsync(subActive.Id)))
                            .IsEnabled(subActive.Id.Length > 0),
                        Button("断开 VPN", () => _ = RunAsync(_setVpnStatus, "断开中…", VpnSession.DisconnectAsync))),
                    subActive.Id.Length == 0
                        ? TextBlock("尚未设置生效订阅：先到「订阅」页拉取并启用一份，再连接")
                            .Foreground(Theme.SecondaryText)
                        : Empty()),

                Section("内核运行态", KernelStatusPanel(snap)),

                Section("活动连接", ConnectionList(snap)));

        /// <summary>
        /// 代理页：模式（rule/global/direct）+ 组 + 节点。都是纯内核 REST，不碰隧道。
        /// ★【1.0.75】把 <c>actionHint</c> 也画上：这一页以前<b>不显示任何返回文本</b>，
        ///   所以"点节点没反应"其实是后端在报错（组名拆错了），界面却一言不发。
        ///   现在每次点选的结果（含失败原因、自动组的说明）都落在页顶。
        /// </summary>
        private Element ProxyPage(KernelView.Snapshot snap, string activeGroup,
                                  string actionHint) =>
            Page(
                actionHint.Length > 0 ? TextBlock(actionHint).Foreground(Theme.SecondaryText) : Empty(),

                Section("模式",
                    HStack(8,
                        Button("mode rule", () => _ = RunAsync(_setActionHint, "切换中…", () => MihomoController.RunModeAsync("rule"))),
                        Button("mode global", () => _ = RunAsync(_setActionHint, "切换中…", () => MihomoController.RunModeAsync("global"))),
                        Button("mode direct", () => _ = RunAsync(_setActionHint, "切换中…", () => MihomoController.RunModeAsync("direct"))))),

                // ★【1.0.84】这里传 <c>activeGroup</c> 而不是 <c>selectedGroup</c>：
                //   前者才是"下面那块真的在显示哪个组"（selectedGroup 初次为空、或组消失时
                //   会被 ResolveGroup 回落）。以前 ▶ 打在别的组上、节点区显示的却是另一个组，
                //   "谁是谁"对不上，正好是这一轮要治的那个毛病的另一半。
                Section("代理组", GroupPicker(snap, activeGroup)),

                // ★【1.0.84 按安卓版】这一块的<b>标题就是当前那个组名</b>——安卓是"一组一页"，
                //   每页顶部写着这页是谁；我们以前固定叫"节点"，切了组标题也不变，
                //   于是"我现在看的到底是哪个组"全靠上面那排按钮里的 ▶ 猜。
                //   没连上内核（一个组都没有）时退回"节点"，不给空标题。
                Section(activeGroup.Length > 0 ? activeGroup : "节点",
                    MemberList(snap, activeGroup)),

                // ★【1.0.81 · M7/Q10】订阅源（提供方）状态面板：让 provider 级
                //   「刷没刷到、拉了多少节点、流量剩多少」可见（此前只能看到被提升到
                //   /proxies 的节点），对齐官方 GET /providers/proxies 能力。
                Section("订阅源（提供方）", ProviderPanel(snap)));

        /// <summary>订阅页：元信息 + 已存 profile + 拉取/更新/热重载。</summary>
        private Element SubscriptionPage(SubscriptionView.Info info,
                                         List<SubscriptionManager.Profile> profiles,
                                         SubscriptionManager.ActiveInfo active,
                                         string subUrl,
                                         string actionHint) =>
            Page(
                Section("当前生效", SubscriptionPanel(info, profiles, active)),

                Section("添加订阅",
                    HStack(8,
                        TextBox(subUrl, v => _setSubUrl(v), "https://… 订阅链接"),
                        Button("拉取并启用", () => _ = FetchSubscriptionAsync())),
                    // ★ 只填 URL 时给一句明确提示（1.0.63 的坑：字段没镜像时按钮永远报"URL 不能为空"，
                    //   界面上看不出是状态没跟上还是真没填 —— 现在把判定写在界面上）。
                    subUrl.Trim().Length == 0
                        ? TextBlock("（粘贴链接后点「拉取并启用」；浏览器一键导入会自动填进这里）")
                              .Foreground(Theme.SecondaryText)
                        : Empty(),
                    actionHint.Length > 0 ? TextBlock(actionHint).Foreground(Theme.SecondaryText) : Empty()),

                Section("维护",
                    HStack(8,
                        Button("更新当前订阅", () => _ = UpdateSubscriptionAsync()),
                        Button("重新加载配置", () => _ = ReloadConfigAsync()),
                        Button("刷新列表", () => _ = RefreshSubscriptionsAsync()))));

        /// <summary>
        /// 设置页：背景材质 + <b>应用分流</b>（G9）+ 取证/调试入口 + 日志。
        /// ★ 这里<b>不放</b> <c>statusToast</c> / <c>encapReturn</c> 那些实验把手 —— 它们住在
        ///   <c>LocalState\settings.txt</c>，由插件进程读；前台要改就得做 read-modify-write，
        ///   而那个文件是取证通道的命门（写坏一行 = 一整批实验臂失效）。
        ///   界面化那 15 个把手是 Docs/17 计划表里的独立一项，不在本批。
        /// ★ 应用分流的名单文件（<c>split-apps.txt</c>）<b>是</b>界面化的：它只归这一个功能，
        ///   没有第二种读者会被写坏 —— 与上面那条纪律不冲突，别把它当成"把手已经进界面"。
        /// </summary>
        private Element SettingsPage(string actionHint, bool logOpen, string logText,
                                     int material, Action<int> setMaterial,
                                     List<string> splitLines, string splitNote, string splitMsg,
                                     string probeText, string splitKind, string splitIdentity,
                                     string splitTarget, string dnsText, string dnsMsg,
                                     string dnsSnapshot) =>
            Page(
                Section("背景材质",
                    MaterialPicker(material, setMaterial),
                    TextBlock("只影响本次运行；重启回云母。")
                        .Foreground(Theme.SecondaryText)),

                Section("应用分流",
                    AppSplitList(splitLines),
                    HStack(8,
                        Button((splitKind == "name" ? "▶ " : "") + "进程名", () => _setSplitKind("name")),
                        Button((splitKind == "path" ? "▶ " : "") + "全路径", () => _setSplitKind("path")),
                        Button((splitTarget == "DIRECT" ? "▶ " : "") + "直连", () => _setSplitTarget("DIRECT")),
                        Button((splitTarget == "REJECT" ? "▶ " : "") + "屏蔽", () => _setSplitTarget("REJECT"))),
                    HStack(8,
                        TextBox(splitIdentity, v => _setSplitIdentity(v),
                            splitKind == "path" ? @"C:\Windows\System32\notepad.exe" : "chrome.exe"),
                        Button("添加", () => AddSplitRule(splitLines))),
                    HStack(8,
                        Button("保存名单", () => _ = SaveSplitAsync(splitLines)),
                        Button("重新读取", () => _ = RefreshSplitAsync()),
                        Button("探测进程可见性", () => _ = RunProbeAsync())),
                    splitMsg.Length > 0 ? TextBlock(splitMsg).Foreground(Theme.SecondaryText) : Empty(),
                    // ★ 这一行是本功能最重要的一条防线：名单"存下了"和"进没进内核配置"是两件事。
                    TextBlock($"上次连接实际注入：{splitNote}").Foreground(Theme.SecondaryText),
                    probeText.Length > 0 ? TextBlock(probeText).Foreground(Theme.SecondaryText) : Empty(),
                    TextBlock("命中 DIRECT 的应用由内核从物理网卡直接发出去，命中 REJECT 的直接屏蔽；" +
                              "名单为空时内核配置一字不改。")
                        .Foreground(Theme.SecondaryText),
                    TextBlock("★ 生效时机两条都行：断开再连一次，或点「订阅 → 重新加载配置」热重载。" +
                              "名单在<b>每次连接</b>和<b>每次热重载</b>时各重读一次，不需要重建 profile。")
                        .Foreground(Theme.SecondaryText),
                    TextBlock("走的是内核规则层（PROCESS-NAME / PROCESS-PATH），不是平台的 traffic filter：" +
                              "后者只要列表非空就会把没匹配上的应用一起拦掉，实测整机断网（Docs/08 §5.2 run 19）。")
                        .Foreground(Theme.SecondaryText)),

                Section("DNS 覆盖",
                    TextBlock("dns-config.txt：一行一条 key = value，# 开头是注释。空文件 = 内核配置一字不改。")
                        .Foreground(Theme.SecondaryText),
                    // ★ 多行原文编辑器：这份文件是配置文本而不是结构化名单，保存回原文才不丢注释。
                    TextBox(dnsText, v => _setDnsText(v),
                        "nameserver = 223.5.5.5, 119.29.29.29",
                        acceptsReturn: true, maxLength: 20000),
                    HStack(8,
                        Button("保存并校验", () => _ = SaveDnsAsync(_dnsText)),
                        Button("重新读取", () => _ = RefreshDnsAsync())),
                    dnsMsg.Length > 0 ? TextBlock(dnsMsg).Foreground(Theme.SecondaryText) : Empty(),
                    TextBlock($"当前文本：{DnsSummary(dnsText)}").Foreground(Theme.SecondaryText),
                    // ★ 与「上次连接实际注入」同一个理由：存下了 ≠ 内核吃到了。
                    TextBlock($"上次连接实际覆盖：{dnsSnapshot}").Foreground(Theme.SecondaryText),
                    TextBlock("支持键：enhanced-mode / fake-ip-range / fake-ip-range6 / fake-ip-filter / " +
                              "fake-ip-ttl / default-nameserver / nameserver / fallback / " +
                              "proxy-server-nameserver / direct-nameserver / ipv6 / prefer-h3 / " +
                              "ipv6-timeout / nrpt-dns。名单外的键会被拒绝（原因写在保存结果里）；" +
                              "列表类键是整条替换，想保留订阅默认就把原值一起抄进来。")
                        .Foreground(Theme.SecondaryText),
                    TextBlock("★ 级联：改 fake-ip-range，窄 include 路由和看门狗判据会一起跟着走；" +
                              "nrpt-dns 是交给 Windows NRPT 的 DNS，需 settings.txt: dnsInTunnel=1。" +
                              "内核侧断开再连或热重载都生效；nrpt-dns 要断开再连才换。")
                        .Foreground(Theme.SecondaryText),
                    TextBlock("★ fake-ip-range6 只在 settings.txt: ipv6=2 时生效（要配合 Docs/22 的 v6 档）。" +
                              "settings.txt 里写 dnsConfig=0 可整份关闭本功能。")
                        .Foreground(Theme.SecondaryText)),

                Section("诊断",
                    HStack(8,
                        Button("诊断内核", () => _ = RunAsync(_setActionHint, "诊断中…", () => MihomoController.DiagnoseKernelAsync())),
                        Button("查看配置", () => _ = RunAsync(_setActionHint, "读取中…", () => MihomoController.RunConfigsDumpAsync())),
                        Button("立即刷新", () => _ = RefreshNowAsync())),
                    actionHint.Length > 0 ? TextBlock(actionHint).Foreground(Theme.SecondaryText) : Empty()),

                Section("应用日志",
                    Button(logOpen ? "收起日志" : "查看日志", () => _ = ToggleLogAsync(logOpen)),
                    logOpen ? LogPanel(logText) : Empty()));

        /// <summary>
        /// 材质三档。★ <b>故意不落盘</b>：做成"改一次记一次"要往 <c>settings.txt</c> 里加键，
        ///   而那是插件的把手文件，为一个观感开关引入 read-modify-write 不值当。
        ///   这三档的真正用处是<b>当场对比</b>：云母在 UWP 走的是 WinUI 2 的
        ///   <c>BackdropMaterial</c>，成不成立要人眼看；"无"那一档用来分清
        ///   "材质压根没上"和"材质上了但被内容盖住"这两种不同的失败。
        /// </summary>
        private static Element MaterialPicker(int current, Action<int> set) =>
            HStack(8,
                Button((current == 0 ? "▶ " : "") + "云母", () => set(0)),
                Button((current == 1 ? "▶ " : "") + "桌面亚克力", () => set(1)),
                Button((current == 2 ? "▶ " : "") + "无", () => set(2)));

        /// <summary>
        /// 名单行列表（每行一条 + 删除按钮）。
        /// ★ 显示的是<b>规则类型 + 标识 + 去向</b>的人话形式，不是文件里的裸文本：
        ///   用户要看的是"chrome 走直连"，不是 <c>name|chrome.exe|DIRECT</c>。
        ///   翻译复用组件的 <see cref="AppSplit.Parse"/> —— 界面显示的与实际注入的
        ///   必须是同一次解析的结果，不能在界面上再写一套"看起来一样"的格式。
        /// ★ 解析不出来的行<b>不画</b>（它也不会被注入），但调用方在保存时会拿到拒绝清单，
        ///   所以"看不见"不等于"悄悄丢了"。
        /// </summary>
        private Element AppSplitList(List<string> lines)
        {
            if (lines.Count == 0)
                return TextBlock("(名单为空 —— 内核配置不会被改动)")
                    .Foreground(Theme.SecondaryText);

            AppSplit parsed = AppSplit.Parse(string.Join("\n", lines));
            var items = new List<Element>();
            int index = 0;
            foreach (AppSplitRule r in parsed.Rules)
            {
                string type = r.Kind == "path" ? "PROCESS-PATH" : "PROCESS-NAME";
                string where = r.Target == "REJECT" ? "屏蔽" : "直连";
                int at = index;
                items.Add(HStack(8,
                    TextBlock($"{at + 1}. {type}  {r.Identity}  →  {where}"),
                    Button("删除", () => RemoveSplitRule(lines, at))));
                index++;
            }
            if (items.Count == 0)
                items.Add(TextBlock("(这几行全部非法，见下方提示)").Foreground(Theme.SecondaryText));
            return VStack(4, items.ToArray());
        }

        /// <summary>选中的组；快照里没有就退回第一个（换订阅/组消失时不至于空白）。
        /// ★【1.0.84】匹配走 <c>KernelView.Find</c>（Ordinal 原样比）—— 与"进组"用的是同一份
        ///   名字判据，两处各写一个 foreach 早晚会分叉。</summary>
        private static string ResolveGroup(KernelView.Snapshot s, string selected)
        {
            if (s.Groups.Count == 0) return "";
            KernelView.Group? hit = KernelView.Find(s.Groups, selected);
            return hit?.Name ?? s.Groups[0].Name;
        }

        /// <summary>
        /// 订阅 id → 给人看的名字。<b>复用 <see cref="SubscriptionManager.DisplayName"/>（列表里
        /// 重名才追加 host）</b>，不在界面上另写一套取名规则 —— 口径同应用分流那块
        /// （"界面显示的与实际注入的必须是同一次解析的结果"）。
        /// 找不到（订阅清单还没读出来）就原样回 id，绝不回空串把分歧伪装成"没连着"。
        /// </summary>
        private string SubLabel(string subId)
        {
            if (subId.Length == 0) return "(无)";
            var p = _subProfiles.FirstOrDefault(x =>
                string.Equals(x.Id, subId, StringComparison.OrdinalIgnoreCase));
            return p != null ? SubscriptionManager.DisplayName(p, _subProfiles) : subId;
        }

        /// <summary>
        /// 「生效账本 ≠ 隧道实际连接」那一行。一致/未连接时返回 <see cref="Empty"/>，
        /// 不制造常态噪音；一旦分叉就直说两边各是谁，并交代本轮已按隧道回填。
        /// ★ 这里<b>不</b>写"已自动修正"这种话：回填可能因正文缺失而失败（<c>AdoptAsync</c>
        ///   返回失败时上面那行 hint 就是失败原因），说成已修正是又一次谎报。
        /// ★ 这里<b>不</b>设前景色刷：Docs/19 §4 记过资源键缺失时 Reactor 发的是<b>透明画刷</b>
        ///   —— 不抛异常、直接看不见。这行的使命是"必须被看见"，拿一个可能查不到的键去着色，
        ///   等于把整条兜底押在资源表上。默认前景本来就是主文本色，靠 <c>FontWeight(600)</c>
        ///   和 ⚠ 字形区分，任何情况下都不会画成隐形。
        /// </summary>
        private Element DivergenceLine(SubscriptionManager.ActiveInfo subActive, string connectedSub)
        {
            if (connectedSub.Length == 0) return Empty();
            if (string.Equals(connectedSub, subActive.Id, StringComparison.OrdinalIgnoreCase))
                return Empty();

            return TextBlock(
                    $"⚠ 隧道实际连的是「{SubLabel(connectedSub)}」，" +
                    $"生效记为「{SubLabel(subActive.Id)}」—— 正在按隧道跟账，见下方提示")
                .FontWeight(new Windows.UI.Text.FontWeight(600));
        }

        /// <summary>运行态三要素。没连上内核时显示"未连接"，不显示成空白或报错堆。</summary>
        private static Element KernelStatusPanel(KernelView.Snapshot s)
        {
            if (!s.Ok)
                return TextBlock($"内核：未连接（{s.Error}）");

            return VStack(
                TextBlock($"内核：已连接　mode={s.Mode}　log-level={s.LogLevel}　网卡={s.InterfaceName}"),
                TextBlock($"代理组 {s.Groups.Count} 个　活动连接 {s.Connections.Count} 条"));
        }

        /// <summary>组选择：横向排开，点一个换下面那份成员表。</summary>
        private Element GroupPicker(KernelView.Snapshot s, string selected)
        {
            if (s.Groups.Count == 0)
                return TextBlock("(没有可切换的组 —— 内核没起，或订阅里没有代理组)");

            var buttons = new List<Element>();
            foreach (var g in s.Groups)
            {
                string name = g.Name;
                bool isActive = string.Equals(name, selected, StringComparison.Ordinal);
                // ★ 括号里是<b>真节点</b>数，不是成员数（安卓口径：嵌套组与内置策略不算节点）。
                //   这份订阅实测 11 个组里 9 个的"成员"全是别的组，用成员数当节点数
                //   就是"代理组成为节点"在界面上的另一半；两个数不等时把成员数也带上，
                //   让人一眼看出这是个套壳组。
                int real = g.RealNodeCount;
                string count = real == g.Members.Count
                    ? $"（{real}）"
                    : $"（节点 {real}/成员 {g.Members.Count}）";
                buttons.Add(Button(
                    (isActive ? "▶ " : "") + $"{name}{count}",
                    () => _setSelectedGroup(name)));
            }
            return HStack(buttons.ToArray());
        }

        /// <summary>
        /// 成员区 = <b>安卓的"一组一页"里那一页</b>：页头一句"这页是谁"，正文一列混排。
        ///
        /// ★【1.0.84 按安卓版原样收回】上一版把成员拆成"子组 / 真节点 / 内置策略"三块、
        ///   还加了「进组」「选它走整组」两个按钮 —— 那是我自己发明的，安卓没有：
        ///   安卓的代理页是 <c>ProxyPageFactory</c> 给每个组开一页（顶部页签横滑），
        ///   <b>页内是一列卡片</b>（<c>ProxyView</c> 自绘：title + subtitle + 延迟胶囊），
        ///   嵌套组照样排在这一列里、按 <b>内核给的订阅原序</b>（<c>QueryProxyGroup</c> 的
        ///   <c>Default</c> 排序），只是<b>卡片样式与节点不同</b>；点卡片 = <c>patchSelector</c>，
        ///   要换组是去点<b>顶部页签</b>。所以这一版：分块与双按钮全部撤掉，
        ///   组行改成"<b>加粗标题 + 灰字副标题 + 明确的『选它』</b>"，与节点行不再是同款。
        /// ★ 为什么组行是 <c>TextBlock + Button</c> 而不是"整行一个可点卡片"：
        ///   WinUI 2.8 没有 Fluent 的卡片底色键，<c>.Background(Theme.CardBackground)</c>
        ///   查不到键时<b>返回透明笔刷且不抛</b>（Docs/19 §4），安卓那层"组卡有底色"在这儿画不出来，
        ///   只能靠字重 + 灰字副标题 + 记号（■/◇）拉开层级；<c>ProviderPanel</c> 的
        ///   "文字 + 按钮"同一行本来就是本包已验证可用的行形态。
        /// ★ 组名/节点名<b>原样透传</b>（中文、带 emoji、带前导空格）——任何 trim / 规范化
        ///   都会让内核找不到那个组（<c>KernelView.Find</c> 同为 Ordinal）。
        /// ★ 延迟只在测过的时候显示（CMFA：<c>delay in 0..Short.MAX_VALUE</c> 才画胶囊）。
        /// </summary>
        private Element MemberList(KernelView.Snapshot s, string groupName)
        {
            if (s.Groups.Count == 0) return Empty();

            KernelView.Group? found = KernelView.Find(s.Groups, groupName);
            if (found == null) return TextBlock("(组不存在)");
            KernelView.Group parent = found;   // 给闭包用：可空局部进 lambda 会招 CS8602

            var items = new List<Element>();

            // ── 页头：这一页是谁、什么类型、现在走谁、几个真节点 + 那枚 ⚡ ──
            //   ★ 测速按钮放页头而不是每个节点一行：安卓就是这个位置
            //     （<c>ProxyActivity</c> 右上角 ⚡ = <c>healthCheck(当前组)</c>，
            //     一次拨完这组所有成员），且自动组的赢家本来就是测速决定的，逐点没意义。
            items.Add(HStack(8,
                TextBlock($"{parent.Type}　现值 {(parent.Now.Length > 0 ? parent.Now : "（未选）")}" +
                          $"　真节点 {parent.RealNodeCount} / 成员 {parent.Members.Count}")
                    .Foreground(Theme.SecondaryText),
                Button("测速", () => _ = HealthCheckAsync(parent.Name))));
            if (!parent.IsUserSelectable)
                items.Add(TextBlock(
                    $"（该组类型由内核测速/故障转移自己选，和安卓版一样不可手选；" +
                    "下面列的是它的候选，▶ 只是当前赢家。）")
                    .Foreground(Theme.SecondaryText));

            // ── 正文：一列混排，顺序 = 内核给的 all 原序（= 订阅原序） ────
            //   ★ 不重排、不分块：安卓的 Default 排序就是订阅里的原序，重排会让
            //     "📺 动画疯 第一个成员是 🔰 选择节点"这种作者有意为之的顺序丢掉。
            foreach (var m in parent.Members)
            {
                bool isNow = string.Equals(m.Name, parent.Now, StringComparison.Ordinal);
                string mark = isNow ? "▶ " : "　 ";
                string member = m.Name;                          // 闭包捕获：原样传给内核

                if (m.IsGroup)
                {
                    // ★【1.0.84 对齐安卓版】组行也是整行可点的卡片，不是"文字 + 小按钮"。
                    //   安卓的 <c>ProxyView</c> 对嵌套组用加粗标题 + 副标题，整行点 = patchSelector；
                    //   我们以前拆成 TextBlock + Button("选它")，导致交互不一致（节点点整行、组要点小按钮）。
                    //   现在改成整行按钮，靠字重与记号（■）拉开层级，与节点行不再是同款。
                    //   副标题里的"节点 N"来自那个组<b>自己</b>的 RealNodeCount，所以查得到才写。
                    KernelView.Group? sub = KernelView.Find(s.Groups, member);
                    // ★ 先算成局部变量再进内插：`{a ? b : "c"}` 这种"三元直接写在插补里"
                    //   是 C# 语法错（CS8361，内插里的 `:` 与对齐冒号歧义），编译期就挡下了。
                    string kind = m.Type.Length > 0 ? m.Type : "组";
                    string nowText = m.Now.Length > 0 ? m.Now : "未选";
                    string note = sub == null
                        ? $"[{kind}] 内核没把它列成可切换组"
                        : $"[{kind} · 现值 {nowText} · 真节点 {sub.RealNodeCount}]";
                    
                    string groupLabel = mark + "■ " + member + $"　{note}";
                    
                    if (!parent.IsUserSelectable)
                    {
                        Element row = TextBlock(groupLabel)
                            .FontSize(16)
                            .FontWeight(new Windows.UI.Text.FontWeight(600));
                        if (!isNow) row = row.Foreground(Theme.SecondaryText);
                        items.Add(row);
                        continue;
                    }
                    // ★【1.0.84 · 对齐安卓版】嵌套组点的是"进那组的页"，不是把它当父组的值。
                    //   Clash Verge Rev 的 ProxyView 里，子组行点击会切换当前选中组；
                    //   CMFA 的 PagerFragment 同理：点子组 = patchSelector + 刷新列表。
                    //   我们之前写 SelectAsync(parent.Name, member) 是错的 —— 那会把子组名
                    //   当成父组的当前值去 PUT /proxies/{parent}，语义完全不对。
                    items.Add(Button(groupLabel, () => _setSelectedGroup(member))
                        .FontSize(16)
                        .FontWeight(new Windows.UI.Text.FontWeight(600)));
                    continue;
                }

                // 节点行 / 内置策略行：同一形态，只有记号不同（◇ = DIRECT/REJECT 这类策略）。
                string label = (m.IsBuiltin ? mark + "◇ " + m.Name : mark + m.Name);
                string sub2 = MemberSubtitle(m);
                if (sub2.Length > 0) label += $"　{sub2}";

                if (!parent.IsUserSelectable)
                {
                    Element row = TextBlock(label);
                    if (!isNow) row = row.Foreground(Theme.SecondaryText);
                    items.Add(row);
                    continue;
                }
                items.Add(Button(label, () => _ = SelectAsync(parent.Name, member)));
            }

            // 判据用"父组到底有没有成员"，不用 items 的行数 —— items 里还有页头那句。
            if (parent.Members.Count == 0) items.Add(TextBlock("(该组没有成员)"));
            return VStack(items.ToArray());
        }

        /// <summary>
        /// 订阅源（提供方）状态面板（M7/Q10）：显示每个 <c>proxy-providers</c> 提供方的
        /// 类型、已加载节点数、最近更新时间、订阅流量/到期（http 订阅才有）。
        /// 每行一个「刷新」按钮触发 healthcheck（<c>GET /providers/proxies/{name}/healthcheck</c>）。
        /// 没有提供方时给一句明确提示——大多数顶层 <c>proxies:</c> 的普通订阅本就没有
        /// provider 段，这是正常的，不是 bug。
        /// </summary>
        private Element ProviderPanel(KernelView.Snapshot s)
        {
            if (s.Providers.Count == 0)
                return TextBlock("(无订阅源 —— 本订阅没有 proxy-providers 段，节点是直给的，正常)")
                    .Foreground(Theme.SecondaryText);

            var items = new List<Element>();
            foreach (var p in s.Providers)
            {
                // ★ vehicleType 是官方给的 Http/File/Inline，转成中国人话。
                string type = p.VehicleType switch
                {
                    "Http" => "远程",
                    "File" => "本地",
                    "Inline" => "内联",
                    _ => p.VehicleType.Length > 0 ? p.VehicleType : "未知"
                };
                string line = $"{p.Name}　[{type}]　{p.NodeCount} 节点";
                if (p.UpdatedAt.Length > 0) line += $"　更新 {p.UpdatedAt}";

                // 订阅流量/到期（仅 http 订阅有 subscriptionInfo）。
                if (p.SubTotal > 0 || p.SubExpire > 0)
                {
                    long used = p.SubDownload + p.SubUpload;
                    line += $"　流量 {FormatBytes(used)}/{FormatBytes(p.SubTotal)}";
                    if (p.SubExpire > 0)
                    {
                        try { line += $"　到期 {DateTimeOffset.FromUnixTimeMilliseconds(p.SubExpire):yyyy-MM-dd}"; }
                        catch { /* 时间戳异常也不该崩这一行 */ }
                    }
                }

                string name = p.Name;   // 闭包捕获：healthcheck 用原始名（可能含中文/空格）
                items.Add(HStack(8,
                    TextBlock(line),
                    Button("刷新", () => _ = RefreshProviderAsync(name))));
            }
            if (items.Count == 0) items.Add(TextBlock("(没有可用的提供方)"));
            return VStack(items.ToArray());
        }

        /// <summary>
        /// 触发某提供方刷新（healthcheck）并立即重载快照。
        /// ★ 与 <see cref="SelectAsync"/> 同构：结果进 actionHint，随后 reload 快照让
        ///   节点数/延迟跟上（节点是异步刷新的，稍后轮询也会自动更新）。
        /// </summary>
        private async Task RefreshProviderAsync(string name)
        {
            try
            {
                string result = await MihomoController.TriggerProviderHealthCheckAsync(name);
                _setActionHint(result);
                _setSnap(await KernelView.LoadAsync());
            }
            catch (Exception ex)
            {
                _setActionHint($"提供方刷新失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>字节数转人话（B/KB/MB/GB）。</summary>
        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes}B";
            double v = bytes;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            return $"{v:F1}{units[i]}";
        }

        /// <summary>
        /// 一行的副标题：分类（类型）+ 测过的延迟。
        /// ★ 与安卓 <c>ProxyItemProvider</c> 同一呈现口径：先"这是什么"，再"多快"。
        ///   真节点写协议类型（<c>ss</c> / <c>trojan</c> / <c>AnyTLS</c>…），
        ///   内置策略写它的类型（<c>Direct</c> / <c>Reject</c>…）。
        /// ★【1.0.84】<b>嵌套组不再走这里</b>：<c>MemberList</c> 把子组单独成块，
        ///   行文本用的是子组自己的现值与节点数（<c>KernelView.Find</c>），
        ///   因为"一行后缀副标题"恰恰是让 <c>🔰 选择节点</c> 看起来像个节点的那半步。
        ///   这里保留 <c>IsGroup</c> 分支只为防御 —— 万一以后又有哪块把成员混着渲染。
        /// </summary>
        private static string MemberSubtitle(KernelView.Member m)
        {
            string kind;
            if (m.IsGroup)
                kind = $"{(m.Type.Length > 0 ? m.Type : "组")}" +
                       (m.Now.Length > 0 ? $"({m.Now})" : "(未选)");
            else if (m.IsBuiltin)
                kind = m.Type.Length > 0 ? m.Type : "内置策略";
            else
                kind = m.Type;

            // ★【1.0.81 · M4/Q8】provider 提供方节点带 provider-name（官方把提供方节点提升为
            //   顶层条目并附该字段）。补进副标题，让用户分清单子是订阅直给还是某提供方动态拉来。
            string src = (!m.IsGroup && !m.IsBuiltin && m.ProviderName.Length > 0)
                ? $" · {m.ProviderName}" : "";
            string body = kind + src;

            // 延迟：没测过就<b>不写</b>（见 KernelView 顶部那条口径）。
            string delay = m.DelayMs >= 0 ? $"{m.DelayMs}ms" : "";
            if (body.Length > 0 && delay.Length > 0) return $"{body}　{delay}";
            return body.Length > 0 ? body : delay;
        }

        /// <summary>
        /// 订阅区块：当前生效那份的元信息 + profile 列表（可点选切换）。
        /// ★ 这是 G1 的 UI 部分；后端 <see cref="SubscriptionManager"/> 提供拉取/切换/更新/删除。
        ///   「删除」刻意不放 UI —— 误删订阅比"想删删不掉"更糟，留 CLI 的 subdel。
        /// </summary>
        private Element SubscriptionPanel(SubscriptionView.Info info,
                                         List<SubscriptionManager.Profile> profiles,
                                         SubscriptionManager.ActiveInfo active)
        {
            var items = new List<Element>();

            // ① 当前生效那份的元信息（读 config.yaml，也就是内核实际在跑的那份）
            if (info.Found)
            {
                string protos = info.Protocols.Count == 0
                    ? "(无)"
                    : string.Join("　", ToList(info.Protocols, p => $"{p.Key}×{p.Value}"));

                items.Add(TextBlock(
                    $"生效中：{info.NodeCount} 节点 · {info.RuleCount} 规则 · " +
                    $"{info.GroupCount} 组 · {info.SizeBytes / 1024.0:F1} KB"));
                items.Add(TextBlock($"协议：{protos}"));
                if (active.Url.Length > 0)
                    items.Add(TextBlock($"来源：{active.Url}"));
            }
            else
            {
                items.Add(TextBlock($"（{info.Error}）"));
            }

            // ② profile 列表：点一条就切过去
            if (profiles.Count > 0)
            {
                items.Add(TextBlock($"── 已存 {profiles.Count} 份 ──"));
                foreach (var p in profiles)
                {
                    bool isActive = string.Equals(p.Id, active.Id, StringComparison.OrdinalIgnoreCase);
                    string id = p.Id;
                    // ★【1.0.72】显示响应头带来的订阅名（如「星链机场」），
                    //   不再把 URL 派生的长 id 直接糊在按钮上；重名时 DisplayName 会补 host。
                    string shown = SubscriptionManager.DisplayName(p, profiles);
                    items.Add(Button(
                        $"{(isActive ? "▶ " : "  ")}{shown}  {p.NodeCount}节点/{p.RuleCount}规则  " +
                        $"{p.SizeBytes / 1024.0:F1}KB  {p.FetchedAt:MM-dd HH:mm}",
                        () => _ = UseSubscriptionAsync(id)));
                }
            }

            return VStack(items.ToArray());
        }

        private async Task FetchSubscriptionAsync()
        {
            if (_subUrl.Trim().Length == 0)
            {
                _setActionHint("先填订阅链接");
                return;
            }
            _setActionHint("拉取中…");
            var r = await SubscriptionManager.FetchAsync(_subUrl, activate: true);
            _setActionHint(r.Message);
            await RefreshSubscriptionsAsync();
        }

        private async Task UpdateSubscriptionAsync()
        {
            _setActionHint("更新中…");
            var r = await SubscriptionManager.UpdateActiveAsync();
            _setActionHint(r.Message);
            await RefreshSubscriptionsAsync();
        }

        private async Task UseSubscriptionAsync(string id)
        {
            _setActionHint($"切换到 {id}…");
            // ★ UX 对齐 Verge：点一下即原地热重载（连接中），返回信息里已含重载结果。
            var r = await SubscriptionManager.ActivateAsync(id);
            _setActionHint(r.Message);
            await RefreshSubscriptionsAsync();
        }

        /// <summary>
        /// 手动热重载：生成内核实际读取的那份配置并 PUT /configs（不碰隧道）。
        /// 用于"改了东西想立刻让内核重读"或"连接后想让已落盘的新订阅生效"。
        /// </summary>
        private async Task ReloadConfigAsync()
        {
            _setActionHint("重载中…");
            var r = await SubscriptionManager.ReloadActiveConfigAsync();
            _setActionHint(r.Message);
            await RefreshSubscriptionsAsync();
        }

        private async Task RefreshSubscriptionsAsync()
        {
            try
            {
                _setSubInfo(await SubscriptionView.LoadAsync());
                _setSubProfiles(await SubscriptionManager.ListAsync());
                _setSubActive(await SubscriptionManager.GetActiveAsync());
            }
            catch (Exception ex)
            {
                _setActionHint($"刷新订阅失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>把只读列表投影成字符串列表（UI 的 TextBlock 只吃字符串）。</summary>
        private static List<string> ToList<T>(IReadOnlyList<T> src, Func<T, string> project)
        {
            var list = new List<string>(src.Count);
            foreach (T item in src) list.Add(project(item));
            return list;
        }

        /// <summary>活动连接：目标 / 链路 / 上下行。</summary>
        private static Element ConnectionList(KernelView.Snapshot s)
        {
            if (!s.Ok) return TextBlock("(内核未连接)");
            if (s.Connections.Count == 0) return TextBlock("(当前没有活动连接)");

            var items = new List<Element>();
            int shown = 0;
            foreach (var c in s.Connections)
            {
                if (shown++ >= 30) { items.Add(TextBlock("…（更多）")); break; }
                items.Add(TextBlock($"{c.Target}　{c.Chain}　↑{c.Upload} ↓{c.Download}"));
            }
            return VStack(items.ToArray());
        }

        /// <summary>日志：整块放进 ScrollViewer 里，可以滚。</summary>
        private static Element LogPanel(string text)
        {
            if (text.Length == 0) return TextBlock("(日志为空)");
            return ScrollViewer(TextBlock(text));
        }

        // ==================== 应用分流（G9）动作 ====================

        /// <summary>
        /// 把名单对象摊成界面用的行数组（<c>kind|标识|去向</c>）。
        /// ★ 用组件解析出来的<b>规范化值</b>，不用文件原文：大小写、前后空格在
        ///   <see cref="AppSplit.Parse"/> 那一层已经统一掉了，界面再显示原文就会出现
        ///   "看着两条不一样、注进去是一样"的假分歧。
        /// </summary>
        private static List<string> RuleLines(AppSplit split)
        {
            var list = new List<string>(split.Rules.Count);
            foreach (AppSplitRule r in split.Rules)
                list.Add($"{r.Kind}|{r.Identity}|{r.Target}");
            return list;
        }

        /// <summary>
        /// 「添加」只做本地校验并换一个<b>新列表</b>，★ 不写盘。
        /// 写盘留给「保存名单」，理由与订阅那边一样：一次编辑动作不该各发一次存储调用，
        /// 而"存没存"这件事必须在界面上是一个单独、看得见的按钮。
        /// 校验复用组件的解析器 —— 判据必须与实际注入用的那套完全相同，
        /// 否则界面放过、插件丢掉，就成了"存下了但没生效"。
        /// </summary>
        private void AddSplitRule(List<string> current)
        {
            string identity = _splitIdentity.Trim();
            if (identity.Length == 0)
            {
                _setSplitMsg("先填进程名或全路径。");
                return;
            }
            if (current.Count >= AppSplit.MaxRules)
            {
                _setSplitMsg($"到上限 {AppSplit.MaxRules} 条了，先删几条。");
                return;
            }

            AppSplit probe = AppSplit.Parse($"{_splitKind}|{identity}|{_splitTarget}");
            if (probe.Rules.Count != 1)
            {
                string why = probe.Rejected.Count > 0 ? probe.Rejected[0] : "这一行没能解析成规则";
                _setSplitMsg($"加不了：{why}");
                return;
            }

            string line = $"{probe.Rules[0].Kind}|{probe.Rules[0].Identity}|{probe.Rules[0].Target}";
            foreach (string existing in current)
                if (string.Equals(existing, line, StringComparison.OrdinalIgnoreCase))
                {
                    _setSplitMsg($"名单里已经有这条了：{line}");
                    return;
                }

            var next = new List<string>(current.Count + 1);
            foreach (string s in current) next.Add(s);
            next.Add(line);
            _setSplitLines(next);
            _setSplitIdentity("");
            _setSplitMsg($"已加 1 条，现在 {next.Count} 条 —— 还没存盘，点「保存名单」。");
        }

        /// <summary>删一条（同样只动内存）。越界就当没发生，不拿异常当按钮防抖。</summary>
        private void RemoveSplitRule(List<string> current, int index)
        {
            if (index < 0 || index >= current.Count) return;
            var next = new List<string>(current.Count - 1);
            for (int i = 0; i < current.Count; i++)
                if (i != index) next.Add(current[i]);
            _setSplitLines(next);
            _setSplitMsg(next.Count == 0
                ? "名单已清空 —— 保存后内核配置不会被改动。"
                : $"已删 1 条，现在 {next.Count} 条 —— 还没存盘，点「保存名单」。");
        }

        /// <summary>
        /// 存盘 + 读回。★ 消息文本来自 <see cref="AppSplitStore.SaveAsync"/>，
        /// 那里返回的是"重新解析后的结果"，不是"我写成功了" —— 被挡掉的行必须当场看得见。
        /// </summary>
        private async Task SaveSplitAsync(List<string> lines)
        {
            try
            {
                AppSplit split = AppSplit.Parse(string.Join("\n", lines));
                _setSplitMsg(await AppSplitStore.SaveAsync(split));
                _setSplitNote(await AppSplitStore.LastInjectionAsync());
            }
            catch (Exception ex)
            {
                _setSplitMsg($"保存失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>手动重读名单（手改过文件、或想丢弃界面上没存的改动时用）。</summary>
        private async Task RefreshSplitAsync()
        {
            try
            {
                var view = await AppSplitStore.LoadAsync();
                _setSplitLines(RuleLines(view.Split));
                _setSplitNote(view.LastInjection);
                _setSplitMsg(view.Split.Rejected.Count > 0
                    ? $"文件里有 {view.Split.Rejected.Count} 行没被采纳：" +
                      string.Join(" / ", StrList(view.Split.Rejected)) +
                      "（这个版本保存会用界面这份覆盖文件，注释也会被重写）"
                    : $"已重新读取，共 {view.Split.Rules.Count} 条。");
            }
            catch (Exception ex)
            {
                _setSplitMsg($"读取失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ==================== DNS 覆盖（1.0.40）动作 ====================

        /// <summary>
        /// 当前编辑器文本的解析摘要（每次渲染现算，不另存一份 state）。
        /// ★ 用的是组件里同一个 <see cref="DnsConfig.Parse"/> —— 界面显示的"几项有效"
        ///   必须就是插件会读到的结果，不能另写一套。
        /// </summary>
        private static string DnsSummary(string text)
        {
            DnsConfig parsed = DnsConfig.Parse(text);
            string s = parsed.IsEmpty ? "0 项有效" : $"{parsed.Entries.Count} 项有效";
            if (parsed.Rejected.Count > 0)
                s += $"，{parsed.Rejected.Count} 行会被拒";
            return s;
        }

        /// <summary>
        /// 存盘 + 读回。消息文本来自 <see cref="DnsConfigStore.SaveAsync"/>，
        /// 那里返回的是"重新解析后的结果"，不是"我写成功了"。
        /// </summary>
        private async Task SaveDnsAsync(string text)
        {
            try
            {
                _setDnsMsg(await DnsConfigStore.SaveAsync(text));
                _setDnsSnapshot(await DnsConfigStore.LastSnapshotAsync());
            }
            catch (Exception ex)
            {
                _setDnsMsg($"保存失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>手动重读（手改过文件、或想丢弃界面上没存的改动时用）。</summary>
        private async Task RefreshDnsAsync()
        {
            try
            {
                var view = await DnsConfigStore.LoadAsync();
                _setDnsText(view.Text);
                _setDnsSnapshot(view.LastSnapshot);
                _setDnsMsg(view.Parsed.Rejected.Count > 0
                    ? $"文件里有 {view.Parsed.Rejected.Count} 行没被采纳：" +
                      string.Join(" / ", StrList(view.Parsed.Rejected))
                    : $"已重新读取，共 {view.Parsed.Entries.Count} 项有效。");
            }
            catch (Exception ex)
            {
                _setDnsMsg($"读取失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 只读能力探针（<see cref="ProcessLookupProbe"/>）。
        /// ★ 必须由用户点：它读的是别的进程的信息，属于"本包不需要、但一旦跑起来就该有交代"的动作。
        /// ★ 全程不碰网络、不碰隧道，所以不进 3 秒轮询，也不在挂载时自动跑。
        /// </summary>
        private async Task RunProbeAsync()
        {
            _setProbeText("探测中…");
            try
            {
                _setProbeText(await ProcessLookupProbe.RunAsync());
            }
            catch (Exception ex)
            {
                _setProbeText($"探测异常: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static List<string> StrList(IList<string> src)
        {
            var list = new List<string>(src.Count);
            foreach (string s in src) list.Add(s);
            return list;
        }

        // ==================== 动作 ====================

        /// <summary>手动刷新一次快照（轮询之外的即时刷新）。</summary>
        private async Task RefreshNowAsync()
        {
            try
            {
                _setSnap(await KernelView.LoadAsync());
            }
            catch (Exception ex)
            {
                _setActionHint($"刷新失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 切节点：<c>PUT /proxies/{组}</c>。
        /// ★ 组名与节点名<b>各自成参</b>交给 <see cref="MihomoController.SelectAsync"/>，
        ///   不 trim、不规范化，也<b>不拼成一行文本</b> ——
        ///   1.0.74 及以前这里是 <c>RunSelectAsync($"{group} {member}")</c>，
        ///   后端按"第一个空格"拆回去；可组名自己就有空格（订阅派生的），于是拆出来的是
        ///   半截组名，<b>界面上绝大多数组点不动，而且看不见失败原因</b>。
        ///   成功后立刻刷一次快照，让"▶"标记跟上。
        /// </summary>
        private async Task SelectAsync(string group, string member)
        {
            try
            {
                string result = await MihomoController.SelectAsync(group, member);
                _setActionHint(result);
                _setSnap(await KernelView.LoadAsync());
            }
            catch (Exception ex)
            {
                _setActionHint($"切节点失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 组测速（安卓那枚 ⚡）：<c>PUT /proxies/{组}/healthcheck</c> 让内核把这组成员全拨一遍。
        /// ★ 与 <see cref="SelectAsync"/> / <see cref="RefreshProviderAsync"/> 同构：结果进
        ///   <c>actionHint</c>，随后立刻刷一次快照。
        /// ★ 但<b>别期待当场就出数</b>：204 只代表"已触发"，延迟是内核测完异步写回
        ///   <c>history</c> 的，所以这一次 reload 可能还是旧值，随后的 3 秒轮询才会把
        ///   新延迟带上来 —— 这句话必须写在返回文案里，否则"点了没变"会被读成"按钮坏了"
        ///   （1.0.75 那条"界面一言不发所以没人知道后端在报错"的教训，同一个方向的坑）。
        /// </summary>
        private async Task HealthCheckAsync(string group)
        {
            try
            {
                _setActionHint($"测速中：{group}（要拨完这组所有成员，几秒到几十秒）");
                string result = await MihomoController.TriggerGroupHealthCheckAsync(group);
                _setActionHint(result);
                _setSnap(await KernelView.LoadAsync());
            }
            catch (Exception ex)
            {
                _setActionHint($"测速失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private async Task ToggleLogAsync(bool wasOpen)
        {
            if (wasOpen) { _setLogOpen(false); return; }
            _setLogOpen(true);
            try
            {
                var content = await LocalState.ReadIfExistsByPathAsync("logs/app.log");
                if (string.IsNullOrEmpty(content))
                {
                    _setLogText("(日志文件不存在或为空)");
                    return;
                }
                var lines = content.Split('\n');
                var tail = lines.Length > 200
                    ? string.Join("\n", lines[^200..])
                    : content;
                _setLogText(tail);
            }
            catch (Exception ex)
            {
                _setLogText($"读日志失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 前端唯一的动作外壳：占位状态 → 等后端 → 把结果或异常写进状态文本。
        /// </summary>
        private static async Task RunAsync(
            Action<string> setStatus, string pending,
            Func<Task<string>> action)
        {
            setStatus(pending);
            try
            {
                setStatus(await action());
            }
            catch (Exception ex)
            {
                setStatus($"异常: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
