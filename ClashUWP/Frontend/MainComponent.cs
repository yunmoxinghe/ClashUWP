using System;
using System.Collections.Generic;
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
    public sealed class MainComponent : Component
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
                    while (!cts.IsCancellationRequested)
                    {
                        try
                        {
                            var s = await KernelView.LoadAsync();
                            _setSnap(s);
                            // ★ 同时刷新 VPN 连接状态：系统快速设置发起的连接绕过了我们的
                            //   按钮，前台没有回调，只能靠轮询反映真实 ConnectionStatus。
                            string status = await VpnSession.StatusAsync();
                            _setVpnStatus(status);

                            // ★★【1.0.62】快速面板切订阅的「账要跟上」：
                            //   连的是某条订阅 profile 且与当前生效的不是同一份 ⇒ 回填。
                            //   隧道本身早就在跑那份配置（插件 Connect 自己读的文件），
                            //   这里补的只是 config.yaml + 生效 id 这份书本账。
                            //   幂等：回填后两值相等，下一轮直接跳过（不会每 3 秒重复写）。
                            string connectedSub = await VpnSession.ConnectedSubAsync();
                            if (connectedSub.Length > 0 && connectedSub != _subActive.Id)
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
                        }
                        catch { /* 一次失败不该停掉轮询 */ }
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
                        string sync = await SubscriptionManager.SyncSystemProfilesAsync();
                        RollingLog.Info("profile-sub-sync", sync);
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
                PageProxy => ProxyPage(snap, activeGroup, selectedGroup),
                PageSubscription => SubscriptionPage(subInfo, subProfiles, subActive, subUrl, actionHint),
                PageSettings => SettingsPage(actionHint, logOpen, logText, material, setMaterial,
                        splitLines, splitNote, splitMsg, probeText, splitKind, splitIdentity, splitTarget,
                        dnsText, dnsMsg, dnsSnapshot),
                _ => HomePage(vpnStatus, actionHint, snap),
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
        private Element HomePage(string vpnStatus, string actionHint, KernelView.Snapshot snap) =>
            Page(
                Section("VPN 控制",
                    TextBlock($"当前状态：{vpnStatus}"),
                    actionHint.Length > 0 ? TextBlock(actionHint).Foreground(Theme.SecondaryText) : Empty(),
                    HStack(8,
                        Button("建/重建 profile", () => _ = RunAsync(_setActionHint, "重建中…", VpnSession.RecreateAsync)),
                        Button("连接 VPN", () => _ = RunAsync(_setVpnStatus, "连接中…", () => VpnSession.ConnectAsync())),
                        Button("断开 VPN", () => _ = RunAsync(_setVpnStatus, "断开中…", VpnSession.DisconnectAsync)))),

                Section("内核运行态", KernelStatusPanel(snap)),

                Section("活动连接", ConnectionList(snap)));

        /// <summary>代理页：模式（rule/global/direct）+ 组 + 节点。都是纯内核 REST，不碰隧道。</summary>
        private Element ProxyPage(KernelView.Snapshot snap, string activeGroup, string selectedGroup) =>
            Page(
                Section("模式",
                    HStack(8,
                        Button("mode rule", () => _ = RunAsync(_setActionHint, "切换中…", () => MihomoController.RunModeAsync("rule"))),
                        Button("mode global", () => _ = RunAsync(_setActionHint, "切换中…", () => MihomoController.RunModeAsync("global"))),
                        Button("mode direct", () => _ = RunAsync(_setActionHint, "切换中…", () => MihomoController.RunModeAsync("direct"))))),

                Section("代理组", GroupPicker(snap, selectedGroup)),

                Section("节点", MemberList(snap, activeGroup)));

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

        /// <summary>选中的组；快照里没有就退回第一个（换订阅/组消失时不至于空白）。</summary>
        private static string ResolveGroup(KernelView.Snapshot s, string selected)
        {
            if (s.Groups.Count == 0) return "";
            foreach (var g in s.Groups)
                if (string.Equals(g.Name, selected, StringComparison.Ordinal)) return g.Name;
            return s.Groups[0].Name;
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
                buttons.Add(Button(
                    (isActive ? "▶ " : "") + $"{name}（{g.Members.Count}）",
                    () => _setSelectedGroup(name)));
            }
            return HStack(buttons.ToArray());
        }

        /// <summary>
        /// 成员表：点一下就切。
        /// ★ 组名/节点名<b>原样透传</b>（中文、带 emoji、带前导空格）——
        ///   任何 trim / 规范化都会让内核找不到那个组。
        /// </summary>
        private Element MemberList(KernelView.Snapshot s, string groupName)
        {
            if (s.Groups.Count == 0) return Empty();

            KernelView.Group? g = null;
            foreach (var x in s.Groups)
                if (string.Equals(x.Name, groupName, StringComparison.Ordinal)) { g = x; break; }
            if (g == null) return TextBlock("(组不存在)");

            var items = new List<Element>();
            foreach (var m in g.Members)
            {
                bool isNow = string.Equals(m.Name, g.Now, StringComparison.Ordinal);
                string delay = m.DelayMs < 0 ? "未测" : $"{m.DelayMs}ms";
                string member = m.Name;
                items.Add(Button(
                    $"{(isNow ? "▶ " : "  ")}{m.Name}　{delay}",
                    () => _ = SelectAsync(g.Name, member)));
            }
            if (items.Count == 0) items.Add(TextBlock("(该组没有成员)"));
            return VStack(items.ToArray());
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
                    items.Add(Button(
                        $"{(isActive ? "▶ " : "  ")}{id}  {p.NodeCount}节点/{p.RuleCount}规则  " +
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
        /// ★ 组名与节点名<b>原样拼接</b>，不 trim、不规范化 ——
        ///   内核的组名/节点名带中文与前导空格，改了就不是同一个东西。
        ///   成功后立刻刷一次快照，让"▶"标记跟上。
        /// </summary>
        private async Task SelectAsync(string group, string member)
        {
            try
            {
                string result = await MihomoController.RunSelectAsync($"{group} {member}");
                _setActionHint(result);
                _setSnap(await KernelView.LoadAsync());
            }
            catch (Exception ex)
            {
                _setActionHint($"切节点失败: {ex.GetType().Name}: {ex.Message}");
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
