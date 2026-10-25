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
        /// "首轮轮询还没回来"这个状态的哨兵串（快照的 <c>Error</c> 位）。
        /// ★ 建初值与空态判断共用这一个常量：两处各写一遍字面量，改一个字就变成
        ///   "还没问到"被谎报成"内核未运行"（任务书 §八场景 5 要防的正是这个）。
        /// </summary>
        private const string NotPolledError = "(尚未查询)";

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
        // ★【1.0.119 · G4/G5】内核实时日志（<c>GET /logs</c>）单独一个槽：
        //   它和 logs/app.log（<c>logText</c>）是两种来源 —— 一个是内核推的事件流、
        //   一个是我们自己写的滚动文件。塞进同一个槽会出现"抓了内核日志就把
        //   文件日志顶掉、界面上看不出顶掉的是谁"，与本项目的"来源必须可见"口径冲突。
        private Action<string> _setKernelLog = _ => { };

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

        // ---- 实时流量（★【1.0.119 · G6】累计字节差分出的速率）----
        //   只存<b>一行文本</b>而不是速率数值：速率的算法与"未知"的三种形状全在
        //   <see cref="TrafficRate"/>（纯函数、离线钉死），这里只负责画它。
        private Action<string> _setTraffic = _ => { };
        // ★ 上一条<b>累计值</b>与它的采样时刻 —— 差分要用。放在字段而不是 UseState：
        //   它是轮询循环自己的私有账本，<b>不参与渲染</b>（画出去的是上面那一行文本）。
        //   口径同 _subUrl 那条"渲染之外的代码只读字段"的纪律。
        private long _prevUp, _prevDown;
        private DateTime _prevTrafficAt = DateTime.MinValue;

        // ---- 订阅自动更新档位（★【1.0.119】小时数，0=关）----
        //   ★ 这份状态只是<b>画给按钮看的</b>；执行环读的是 <c>AppSettings.SubAutoHours</c>。
        //   两者由 <c>SetSubscriptionAutoHoursAsync</c> 一起改（落盘 + 改现值），
        //   所以不会出现"按钮亮了、环还在按旧档位跑"。
        private Action<int> _setSubAutoHours = _ => { };

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
            var (snap, setSnap) = UseState(KernelView.Snapshot.Failed(NotPolledError));
            var (logText, setLogText) = UseState("");
            var (logOpen, setLogOpen) = UseState(false);
            var (kernelLog, setKernelLog) = UseState("");
            var (selectedGroup, setSelectedGroup) = UseState("");
            var (subInfo, setSubInfo) = UseState(SubscriptionView.Info.Missing());
            var (subUrl, setSubUrl) = UseState("");
            var (subProfiles, setSubProfiles) = UseState(new List<SubscriptionManager.Profile>());
            var (subActive, setSubActive) = UseState(new SubscriptionManager.ActiveInfo("", ""));
            var (connectedSub, setConnectedSub) = UseState("");
            var (traffic, setTraffic) = UseState("速率 首次采样…");
            // ★【1.0.119 · 代理页排序】默认原序 = 作者顺序，理由全在 NodeSort 的类注释里；
            //   这一档和材质那三档一样<b>故意不落盘</b>（settings.txt 是插件的把手文件，
            //   为一个观感开关引入 read-modify-write 不值当）。
            var (sortMode, setSortMode) = UseState(NodeSort.Mode.Original);
            // ★【1.0.119 · 订阅自动更新】初值先按"关"画，挂载时从 settings.txt 现读一次再纠正
            //   （纠正那一段的理由写在挂载效果里，别在这儿重复）。
            var (subAutoHours, setSubAutoHours) = UseState(0);

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
            _setKernelLog = setKernelLog;
            _setSelectedGroup = setSelectedGroup;
            _setSubInfo = setSubInfo;
            _setSubUrl = setSubUrl;
            _setSubProfiles = setSubProfiles;
            _setSubActive = setSubActive;
            _setConnectedSub = setConnectedSub;
            _setTraffic = setTraffic;
            _setSubAutoHours = setSubAutoHours;
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
                    //   【1.0.72 那条撤销坑怎么防】改为"切换进行中让路"（ProfileGate.SwitchInProgress），
                    //     而不是"长期不比对"。首轮也不再只记基线不判定 —— 启动时"生效 B / 隧道 A"
                    //     现在就是要按隧道（真相）跟账的形状，不再当残留放过。
                    string lastConnectedSub = "";
                    bool firstPoll = true;
                    // ★【1.0.95 · Docs/50】"欠一次对账"的旗子：会话边沿（连→断、A→B）置真，
                    //   切换让路期间保持真，切换一结束立刻补一轮 <c>边沿对账</c> 再清零。
                    //   它是<b>轮询循环的局部量</b>，不跨进程也不跨调用路径共享（Docs/48 §三 那条
                    //   "只有跨进程契约才允许共享可变态"的规矩与此无关 —— 这里就一个前台循环在读）。
                    bool pendingRealign = false;
                    // ★【1.0.101 · Docs/52】补扫节拍计数：台账脏着时每 ~15 秒补一轮对账。
                    //   为什么边沿那一次不够（见下面使用处的注释）。
                    int owedTick = 0;
                    // ★【1.0.104】上一次补扫<b>结束时</b>的台账快照，用来认出"补不动"（见使用处）。
                    string owedAfterLastSweep = "";
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

                            // ★【1.0.119 · G6】实时流量：内核给的是累计字节，速率靠<b>相邻两轮差分</b>。
                            //   判据（首样/计数器回退/间隔非正 ⇒ "未知"而不是 0）全在
                            //   <see cref="TrafficRate"/> 里，离线已逐条钉住；这里只做两件事：
                            //   把本轮累计值存成下一条、把差出来的那一行交给界面。
                            // ★★ 内核不可达（配置视图）时<b>不能只是"不动这条"</b>。
                            //   第一版就是<b>不动这条</b>、让界面保留上一行，理由是"断开时也想知道刚才多少" ——
                            //   那正是 1.0.79/1.0.80 已经栽过的形状：<b>状态文本悄悄停在最后一次
                            //   成功的读数上</b>，界面看着还在跑，实际早就读不出来了
                            //   （memory「Sentinel silent death」）。流量这一行没有任何理由例外：
                            //   "↑512B/s" 挂在一条已经断掉的隧道上，比挂一个「读不到」危险得多。
                            //   ⇒ 现在显式改成读不到，并把差分账本<b>清成"还没有上一条"</b>：
                            //     重连后的第一拍因此报「首次采样」，而不是拿"断开前那次读数"
                            //     与"重连后这次"跨一段不可达窗口相减 —— 那段窗口里内核可能已经
                            //     重启过，计数器回退会被算成一次突发（判据 2 兜得住，但那是运气，
                            //     不是设计；窗口长度本身也是未知的，速率的分母就不成立）。
                            if (s.Ok)
                            {
                                DateTime nowT = DateTime.Now;
                                var rate = TrafficRate.Between(_prevUp, _prevDown, _prevTrafficAt,
                                    s.Totals.Upload, s.Totals.Download, nowT);
                                _prevUp = s.Totals.Upload;
                                _prevDown = s.Totals.Download;
                                _prevTrafficAt = nowT;
                                _setTraffic($"{rate.Describe()}　" +
                                            TrafficRate.DescribeTotals(s.Totals.Upload,
                                                s.Totals.Download, s.Totals.Memory));
                            }
                            else
                            {
                                _prevTrafficAt = DateTime.MinValue;
                                _setTraffic("速率 读不到（内核不可达）　" +
                                            "重连后第一拍是「首次采样」，不拿断开前的读数跨窗口做差");
                            }

                            // ★【1.0.81 · Q4 / 1.0.119 重写】把本轮快照交给回放：
                            //   判据是"内核现值 vs selection.txt 台账有漂移"，不再靠可达边沿
                            //   （旧那只 bool 闩在读台账前就置位，且复位只挂在下面这个 catch 上，
                            //   而内核不可达是 Ok=false 不抛 ⇒ 闩一置上就再也不回放）。
                            //   内核重启/快速重连落在两次轮询之间也追得上；无漂移时一个 PUT 都不发。
                            string replayMsg = await MihomoController.ReplaySelectionsAsync(s);
                            if (replayMsg.Length > 0)
                                RollingLog.Info("selection-replay", replayMsg);
                            // ★ 同时刷新 VPN 连接状态：系统快速设置发起的连接绕过了我们的
                            //   按钮，前台没有回调，只能靠轮询反映真实 ConnectionStatus。
                            // ★【1.0.96 · Docs/51】这一句单独兜一层：状态读数失败只许影响这一行
                            //   （画成「状态读不出」），既不许把异常文本留在状态槽里，
                            //   也不许把整轮轮询（回填 + 边沿对账）一起带走。
                            string status;
                            try
                            {
                                status = await ProfileGate.StatusAsync();
                            }
                            catch (Exception exStatus)
                            {
                                status = ProfileGate.StatusUnreadable;
                                string kindStatus =
                                    $"状态读数失败 {exStatus.GetType().Name}: {exStatus.Message}";
                                if (kindStatus != lastPollError)
                                {
                                    RollingLog.Warn("ui-poll",
                                        $"本轮状态读不出（同类不再重复记）：{kindStatus}");
                                    lastPollError = kindStatus;
                                }
                            }
                            _setVpnStatus(status);

                            // ★【1.0.107 · Docs/54】凡读到活会话而前台还没有哨兵 ⇒ 认领
                            //   （<see cref="VpnSession.AdoptLiveSessionIfUnprotected"/>）。
                            //   覆盖三种"会话不是本前台发起"的形状：快速面板点连接、
                            //   平台自发重拨、臂 C 无头长期在线之后重开 app。
                            //   这一层只负责"别让保护缺位"；哨兵自己会再前置确认，
                            //   判据不可信不动手的闸在 HealthMonitor 里原样保留。
                            if (status == "Connected")
                                ProfileGate.AdoptLiveSession();

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
                            string connectedSub = await ProfileGate.ConnectedSubAsync();
                            _setConnectedSub(connectedSub);

                            // 并发观测仍按边沿：只在读数变化时查一次，不每 3 秒查一遍。
                            bool changed = connectedSub != lastConnectedSub;
                            // ★【1.0.95 · Docs/50】会话边沿 ⇒ 欠一次对账（真正推的位置在下面）。
                            //   【为什么在算完 changed 就当场合上，不在下面补】firstPoll 在
                            //   <c>if (firstPoll)</c> 分支里会被就地置 false，等走到推送那段
                            //   已经分不清"本轮是不是首轮"了 ⇒ 首轮会多推一遍重复的边沿对账。
                            if (changed && !firstPoll)
                                pendingRealign = true;
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
                                string parallel = await ProfileGate.ParallelConnectedAsync();
                                if (parallel.Length > 0)
                                    RollingLog.Warn("profile-parallel",
                                        $"多条 profile 同时 Connected：{parallel}（并发行未实测，仅记录）");
                                if (connectedSub.Length == 0)
                                    RollingLog.Info("profile-sub-adopt",
                                        $"系统侧已断开（此前 {lastConnectedSub}），生效订阅保持不变");
                            }
                            lastConnectedSub = connectedSub;

                            // ★★【1.0.95 · Docs/50】"跳过（断开后再对账）"这句话的执行者在这儿。
                            //   【为什么挂在会话边沿】Ensure/prune 对非 Disconnected 的条目一律跳过，
                            //   而刚被换下/刚断开的那条恰好在这一刻回到 Disconnected —— 它唯一能被改的
                            //   状态。1.0.62~1.0.94 全仓只有"应用挂载"一处会重跑对账 ⇒ 跳过一次就漂到
                            //   下次重启应用，这就是"换了订阅、面板条目没跟着动"的成因。
                            //   【为什么记 pendingRealign 而不是当场推】A 案的切换有几秒窗口，窗口里推
                            //   等于跟切换抢同一条 profile（同下面 adopt 那条让路纪律）；但让路时<b>边沿
                            //   不能丢</b> —— 丢了就再也没有"变化"可言（切换结束后 connectedSub 稳定在新值上），
                            //   那条被跳过的 profile 就永远补不上。记一次"欠对账"，切换一结束立刻补，
                            //   用户这一次动作内闭合。
                            //   【首轮不算边沿】挂载那一轮 <see cref="SubscriptionManager.SyncSystemProfilesAsync"/>
                            //   已经跑过（上面那个 UseEffect），首轮再推纯属重复 —— 合上边沿的地方在
                            //   上面算完 changed 那里，不在这一行。
                            //   【不 prune】1.0.96 · Docs/51 把"读不出=没在连 ⇒ 放行删除"修掉之后，
                            //   这一条<b>不再是安全兜底，只是分工</b>（Docs/50 §二.2 第 3 条）：
                            //   删条目只留给挂载那一轮，边沿这一轮只补条目，免得刚结束的会话
                            //   与删除抢同一个 RPC。★【1.0.111 · Docs/56】这句从前是<b>空头话</b>：
                            //   下面那行调用没带 <c>allowPrune: false</c>（默认值是 true），
                            //   所以 1.0.95~1.0.110 每一条「边沿对账」汇总行结尾都是
                            //   <c>prune 幽灵 profile：删 N / 留 M</c> 而不是 Docs/50 §五 第 3 条
                            //   要求的「本轮不 prune（…）」—— 那条装机判据从未成立过（实测见 Docs/56 §二）。
                            if (pendingRealign && !ProfileGate.SwitchInProgress)
                            {
                                pendingRealign = false;
                                await ProfileGate.ReconcileAsync(
                                    "边沿", allowPrune: false);
                            }

                            // ★★【1.0.101 · Docs/52】"欠对账"台账还脏着 ⇒ 光靠会话边沿那一次不够。
                            //   【为什么不够】边沿那一轮跑在"connectedSub 刚变空"这一刻，
                            //   而平台那条会话此刻常常还在 <c>Disconnecting</c>；写入闸
                            //   （<c>VpnProfileSetup.ProveStoreWriteSafeAsync</c>）照旧判"不干净"，
                            //   于是这一轮又把它欠回去 —— 之后 connectedSub 一直是空，再也没有边沿，
                            //   那条 profile 就漂到下次挂载。这不是理论：Docs/38 实测过
                            //   <c>DisconnectProfileAsync</c> 返回 Ok 之后会话还要一段时间才收干净。
                            //   【所以】按 5 轮一次（≈15 s）补扫，直到台账空（<c>Dirty</c> 由对账轮
                            //   用文件真值刷新）。判据"没会话"用 <c>connectedSub</c> 只是省事的粗筛，
                            //   真正的闸在 Ensure 里面，粗筛错了最多是白扫一轮。
                            //   【不刷屏】这一轮走 <c>profile-sub-sync</c> 汇总行，不额外记；
                            //   <c>Dirty</c> 是进程内提示位，不为它读盘。
                            //   【★ 1.0.104：补扫必须有"补不动"的出口】拦着一条欠账的不一定是会话，
                            //   也可能是<b>这条条目自己读数中毒</b>（Docs/45：状态读不出 ⇒ 既不满足
                            //   "断开后再对账"，也永远不会自己变干净）。那种情况下每 15 秒扫一轮
                            //   = 零进展 + 每轮两条日志 + 反复撞 VpnManagementAgent 的 RPC。
                            //   判据用"连续两轮补扫结束时台账快照逐字相同"来认它 —— 这个比对成立的前提
                            //   是 <c>ReconcileOwed.MarkAsync</c> 从 1.0.104 起<b>判据没变就不写盘</b>
                            //   （旧写法每轮盖一个新时间戳，快照永远在变，等于没有这个出口）。
                            //   停扫只放下提示位，文件原样留着当证物；判据一变会重新置脏，
                            //   会话边沿/切换/挂载那几轮也照扫，所以不会漏补。
                            if (!pendingRealign && ProfileGate.OwedDirty &&
                                ++owedTick >= 5 && !ProfileGate.SwitchInProgress)
                            {
                                owedTick = 0;
                                await ProfileGate.ReconcileAsync("补账", allowPrune: false);
                                string afterSweep = await ProfileGate.OwedSnapshotAsync();
                                if (afterSweep.Length > 0 && afterSweep == owedAfterLastSweep)
                                {
                                    ProfileGate.OwedSetDirty(false);
                                    RollingLog.Warn("profile-owed",
                                        $"补账连续两轮判据逐字相同，暂停补扫（下次边沿/切换/挂载照旧会试）：{afterSweep}");
                                }
                                owedAfterLastSweep = afterSweep;
                            }

                            if (connectedSub.Length > 0
                                && !ProfileGate.SwitchInProgress
                                && !string.Equals(connectedSub, _subActive.Id,
                                                  StringComparison.OrdinalIgnoreCase))
                            {
                                var adopt = await ProfileGate.AdoptSubscriptionAsync(connectedSub);
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
                            // ★【1.0.81 · Q4 / 1.0.119】轮询异常 → 复位回放状态（冷却与汇总基线），
                            //   下次内核可达是新的一轮。注意这条<b>不是</b>回放的常规复位入口：
                            //   内核不可达时 LoadAsync 返回 Ok=false 而不抛，复位走的是那里。
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
                        _setSubProfiles(await ProfileGate.ListSubscriptionsAsync());
                        _setSubActive(await ProfileGate.GetActiveSubscriptionAsync());
                        // ★【1.0.62】启动对账：老版本拉过的订阅还没有系统 profile，
                        //   不补注册的话快速设置面板里就是空的（详见 SyncSystemProfilesAsync）。
                        //   放在读列表之后：有列表才有得补；失败只进日志，不挡 UI。
                        // ★【1.0.72】汇总行由 SyncSystemProfilesAsync 自己记（1.0.62~1.0.71
                        //   里外各记一遍，同一毫秒出现两条一模一样的 profile-sub-sync）。
                        // ★【1.0.101 · Docs/52】欠账要在界面上露一句：这一批起"会话期间不写存储"，
                        //   开机时正连着 ⇒ 有条目建不出来。★ 人话在前、判据在后
                        //   （1.0.100 的教训：提示行只有一两行宽，术语在前头会把出口挤没）。
                        string sync0 = await ProfileGate.ReconcileAsync("启动");
                        int owed0 = await ProfileGate.OwedCountAsync();
                        if (owed0 > 0)
                            _setActionHint($"有 {owed0} 条 VPN 条目还欠着（这个应用不在连接期间改系统 VPN 条目，" +
                                           "断开后会自动补上）" +
                                           $"　内部判据：{sync0}");
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

                    // ★【1.0.119 · 订阅自动更新】档位也从 settings.txt 现读一次再进状态：
                    //   AppSettings 的那个静态字段由启动装配填，而装配是 fire-and-forget，
                    //   第一帧渲染时它可能还没回来 —— 按钮亮在错误的档位上比不亮更糟
                    //   （用户会以为"我设过 6 小时"，实际环读的是 0）。
                    //   这里不等装配、自己读，是因为这条判据错了不会碰网络，
                    //   而装配那条链的失败模式（settings.txt 读失败）默认是"关"，正好一致。
                    try
                    {
                        await AppSettings.LoadAsync();
                        _setSubAutoHours(AppSettings.SubAutoHours);
                    }
                    catch { /* 读不到就停在"关闭"那一档，界面上照样是那句"自动更新 关闭" */ }
                });
            }, Array.Empty<object>());

            // ★★ 四页共用同一份状态与同一条轮询：切页<b>不</b>重启轮询、不重新读文件。
            //   （轮询挂在挂载时的那次渲染上，切页只换 content 子树。）
            Element body = page switch
            {
                PageProxy => ProxyPage(snap, activeGroup, actionHint, sortMode, setSortMode),
                PageSubscription => SubscriptionPage(subInfo, subProfiles, subActive, subUrl,
                                                    actionHint, subAutoHours, setSubAutoHours),                PageSettings => SettingsPage(actionHint, logOpen, logText, kernelLog, snap,
                        material, setMaterial,
                        splitLines, splitNote, splitMsg, probeText, splitKind, splitIdentity, splitTarget,
                        dnsText, dnsMsg, dnsSnapshot),
                _ => HomePage(vpnStatus, actionHint, snap, subActive, connectedSub, traffic),
            };

            return NavigationView(
                    content: body,
                    menuItems: MenuItems,
                    // ★【1.0.125 · 从 Left 改回 Auto】这条写死成 Left 就是"默认窗口宽度下
                    //   按钮点不着"的根因，实测数字在 Docs/66：772px 窗口里展开态的导航栏
                    //   占掉 480/750，正文列只剩 268 ⇒「VPN 控制」那一排三枚按钮（不可换行的
                    //   HStack）画成 174 / 10 / 零尺寸，鼠标点不到第二、三枚。
                    //   Auto 才是框架自己的默认：窄窗口收成图标栏、够宽再展开（同一窗口下
                    //   手动收起导航栏实测正文 676px ⇒ 三枚按钮 174/125/126 全点得着，
                    //   这组数字就是"栏子挤的、不是按钮尺寸写死的"那条判据）。
                    //   本包没有"任何时候都必须看见条目名"的硬要求，而把正文挤到点不着是
                    //   每天都碰的代价。
                    paneDisplayMode: NavPaneDisplayMode.Auto,
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
            SubscriptionManager.ActiveInfo subActive, string connectedSub, string traffic) =>
            Page(
                Section("VPN 控制",
                    TextBlock($"当前状态：{vpnStatus}"),
                    // ★【1.0.96 · Docs/51 · Docs/45 修法第 3 条】读数不可信时界面上必须有一句<b>出口</b>。
                    //   本轮实机的教训：状态标签停在一个说不清的文本上、按钮按下去没反应，
                    //   而这条路只有 系统设置→VPN 走得通 —— 上一条撞同样墙的人不该自己找。
                    vpnStatus == ProfileGate.StatusUnreadable
                        ? TextBlock("读不到 VPN 状态（平台侧这条 profile 的句柄已失效）。" +
                                    "本应用的连接与断开在这种情况下只会拒绝动作、不会瞎拨；" +
                                    "请打开 系统设置→VPN（或 ncpa.cpl）看这条的真实状态，断开后再回来点连接")
                            .Foreground(Theme.SecondaryText)
                        : Empty(),
                    // ★★【1.0.86 · Docs/36 B 案兜底】隧道与账本对不上时，这句话必须画在界面上。
                    //   A 案负责让它一致，这一行负责在它不一致时<b>不骗人</b> ——
                    //   本项目纪律：静默或指错的失败一律算没修。
                    DivergenceLine(subActive, connectedSub),
                    actionHint.Length > 0 ? TextBlock(actionHint).Foreground(Theme.SecondaryText) : Empty(),
                    HStack(8,
                        Button("建/重建 profile", () => _ = RunAsync(_setActionHint, "重建中…", ProfileGate.RebuildAsync)),
                        // ★【快速面板改造】按钮从"连基础 profile"改为"连<b>当前生效订阅</b>的
                        //   系统 profile"——与用户在快速设置里点那条订阅是同一条平台路径，
                        //   连完快速面板亮的就是那条订阅（基础条目已隐藏，不再出现
                        //   "app 里点了连接、面板亮的是裸 ClashUWP"的错位）。
                        //   没有生效订阅就禁用（哥哥的拍板）：基础 profile 只留给诊断路径，
                        //   不再当用户的连接入口。
                        // ★★【1.0.96 · Docs/51】这两个按钮的回话从 <c>_setVpnStatus</c> 改道
                        //   <c>_setActionHint</c>。【为什么要改】<c>vpnStatus</c> 那个槽是
                        //   <b>读数</b>专用的（3 秒轮询写它），而按钮返回的是<b>动作回话</b>——
                        //   两件事塞进同一个槽，于是 0x80070006 那次「未发起=读状态异常:COMException:
                        //   句柄无效。」被当成"当前状态"原样画在页面上，而且因为轮询那一轮
                        //   正好也读不出（异常被 <c>catch</c> 兜住、槽不再更新），那句内部判据
                        //   就永久钉在标题位置。分工从此写死：<b>状态槽只放读数，动作回话走提示行</b>。
                        Button("连接 VPN", () => _ = RunAsync(_setActionHint, "连接中…",
                                () => ProfileGate.ConnectAsync(subActive.Id)))
                            .IsEnabled(subActive.Id.Length > 0),
                        Button("断开 VPN", () => _ = RunAsync(_setActionHint, "断开中…", ProfileGate.DisconnectAsync))),
                    subActive.Id.Length == 0
                        ? TextBlock("尚未设置生效订阅：先到「订阅」页拉取并启用一份，再连接")
                            .Foreground(Theme.SecondaryText)
                        : Empty()),

                Section("内核运行态", KernelStatusPanel(snap)),

                // ★【1.0.119 · G6】实时流量。CMFA 功能对比里那条 P0 缺口（Docs/26）的补法：
                //   <b>不引入 WebSocket</b>，直接把 3 秒轮询<b>已经在打</b>的
                //   <c>GET /connections</c> 根级三字段读出来、按相邻两轮差分成速率。
                //   代价如实写在界面上（"3 秒平均"那句），不假装是每秒瞬时值。
                Section("实时流量",
                    TextBlock(traffic),
                    // ★【1.0.119】内核不可达时上面那一行<b>已经</b>被改成「读不到（内核不可达）」，
                    //   这一句是补它的原因与下一步，不是靠它来打补丁 ——
                    //   轮询里那条注释记着"为什么不保留上一行"（1.0.79/1.0.80 那个形状）。
                    snap.Ok ? Empty()
                        : TextBlock("连上内核后这一行恢复速率；重连后的第一拍会是「首次采样」，" +
                                    "因为断开前的那次读数不能跨一段不可达窗口做差。")
                            .Foreground(Theme.SecondaryText),
                    TextBlock("速率是相邻两次轮询（约 3 秒）之间的「平均」值，不是每秒瞬时；" +
                              "累计与内存来自内核原话（downloadTotal / uploadTotal / memory）。" +
                              "「—」是读不出，不是 0 —— 首次采样、内核重启把计数器清零、" +
                              "都会画成「—」而不是假尖峰或假 0。")
                        .Foreground(Theme.SecondaryText)),

                Section("活动连接", ConnectionList(snap)));

        /// <summary>
        /// 代理页：模式（rule/global/direct）+ 组 + 节点。都是纯内核 REST，不碰隧道。
        /// ★【1.0.75】把 <c>actionHint</c> 也画上：这一页以前<b>不显示任何返回文本</b>，
        ///   所以"点节点没反应"其实是后端在报错（组名拆错了），界面却一言不发。
        ///   现在每次点选的结果（含失败原因、自动组的说明）都落在页顶。
        /// ★【1.0.119 · 代理页排序】<c>sortMode</c>/<c>setSortMode</c> 是成员区那一排的
        ///   视角选择（原序/延迟/名称）。判据不在这里 —— 全在 <see cref="NodeSort"/>，
        ///   本函数只负责把模式送进去、把"多少条未测"那句摘要画出来。
        /// </summary>
        private Element ProxyPage(KernelView.Snapshot snap, string activeGroup,
                                  string actionHint, NodeSort.Mode sortMode,
                                  Action<NodeSort.Mode> setSortMode) =>
            Page(
                actionHint.Length > 0 ? TextBlock(actionHint).Foreground(Theme.SecondaryText) : Empty(),

                // ★★【1.0.118 · 节点浏览与 VPN 解耦】来源横幅。这一页的数据有两种身份，
                //   界面必须当场说清是哪一种（任务书 §五的硬要求）：
                //   · Ok —— 内核实时（REST 可达；含"断开后内核常驻"这一档，照旧全功能）；
                //   · !Ok && FromConfigFile —— config.yaml 的<b>配置视图</b>：组与节点是
                //     磁盘原文的定义，没有延迟、没有实时现值；点节点走"预设 + 回放"，
                //     不是当场改内核；
                //   · !Ok && !FromConfigFile —— 磁盘上也没有可列出的定义（没拉过订阅），
                //     空态要给<b>下一步动作</b>，不许留白。
                //   ★ 首轮轮询还没回来时（Error="(尚未查询)"）画"加载中"而不是"内核未运行" ——
                //     任务书 §八场景 5：内核启动中/首轮未归,不能把"还没问到"谎报成"没有"。
                //   ★【1.0.125】同一档判据再补一条：<b>查询超时</b>也是"没问到"，不是"没在跑"。
                //     这一档在拆隧道的那几十秒必然出现（回环端口开着但内核正在收尾，不回话），
                //     原先它会落到"内核未运行"那句里 —— 主页那一行同一时刻也说"未连接"，
                //     两句合起来就是一次谎报（实物见 Docs/66）。
                snap.Ok ? Empty()
                    : snap.Error == NotPolledError
                        ? TextBlock("正在查询内核与本地配置…").Foreground(Theme.SecondaryText)
                        : snap.QueryTimedOut
                            ? TextBlock(snap.FromConfigFile
                                ? "★ 内核这一轮没答（查询超时：正在启动，或正随隧道收尾）—— " +
                                  "下面是本地订阅（config.yaml）解析出的配置视图，来自磁盘、" +
                                  "不代表内核活着。约 3 秒后自动重试，内核答上来这一段自己消失。"
                                : "★ 内核这一轮没答（查询超时：正在启动，或正随隧道收尾），" +
                                  "本地也没有可列出的节点定义。约 3 秒后自动重试。")
                                .Foreground(Theme.SecondaryText)
                            : TextBlock(snap.FromConfigFile
                                ? "★ 内核未运行 —— 下面是本地订阅（config.yaml）解析出的配置视图：" +
                                  "无延迟数据、无活动连接。点节点会记为预设选择，" +
                                  "下次内核可达（连接或常驻重启）时自动回放生效；不改动内核、也不发起 VPN 连接。"
                                : "★ 内核未运行，本地也没有可列出的节点定义 —— " +
                                  "先在「订阅」页拉取订阅（拉取不需要 VPN 连接）。")
                                .Foreground(Theme.SecondaryText),
                snap.ConfigNote.Length > 0
                    ? TextBlock(snap.ConfigNote).Foreground(Theme.SecondaryText) : Empty(),

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
                    MemberList(snap, activeGroup, sortMode, setSortMode)),

                // ★【1.0.81 · M7/Q10】订阅源（提供方）状态面板：让 provider 级
                //   「刷没刷到、拉了多少节点、流量剩多少」可见（此前只能看到被提升到
                //   /proxies 的节点），对齐官方 GET /providers/proxies 能力。
                Section("订阅源（提供方）", ProviderPanel(snap)));

        /// <summary>
        /// 订阅页：元信息 + 已存 profile + 拉取/更新/热重载 + <b>自动更新档位</b>（1.0.119）。
        /// </summary>
        private Element SubscriptionPage(SubscriptionView.Info info,
                                         List<SubscriptionManager.Profile> profiles,
                                         SubscriptionManager.ActiveInfo active,
                                         string subUrl,
                                         string actionHint,
                                         int subAutoHours,
                                         Action<int> setSubAutoHours) =>
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
                        Button("刷新列表", () => _ = RefreshSubscriptionsAsync()))),

                // ★【1.0.119 · 订阅定时自动更新】判据整段在 SubscriptionAutoPolicy（离线夹具钉着），
                //   这一节只画档位、画"下次"、画"为什么这一拍没跑"。
                //   ★ "下次"在这里<b>现算</b>，不读执行环那个字段：环每 5 分钟才拍一次，
                //   刚改完档位若显示上一次的 null，用户看到的是"开了但下次—"，
                //   而这句恰恰是这一节里唯一能证明"它真的认了你这个档位"的东西。
                Section("自动更新订阅",
                    SubAutoPicker(subAutoHours, setSubAutoHours),
                    TextBlock(SubscriptionAutoPolicy.StatusLine(
                                  subAutoHours,
                                  SubAutoNextDue(subAutoHours, profiles, active),
                                  SubscriptionAutoUpdater.FailStreak))
                        .Foreground(Theme.SecondaryText),
                    TextBlock($"最近一次检查：{SubscriptionAutoUpdater.LastNote}")
                        .Foreground(Theme.SecondaryText),
                    TextBlock("★【1.0.123】到点就拉，连着 VPN 也照样更新（口径同 Clash Verge）：" +
                              "先写正文 + 原地热重载内核，这一步不拆隧道；" +
                              "只有「隧道还记着、内核却不认这份配置」才自己断开重拨一次补上" +
                              "（重载发不出去、或核心重载后失联）；" +
                              "内核明确拒收、会话状态读不出、或台上本来没连接 —— 这三种都绝不动隧道。" +
                              "系统 profile 那条路另有更里层的闸：会话期间不写存储、断开后自动补；" +
                              "且只在本应用开着、没被挂起的时候检查 —— 没有后台任务，应用关了不会自己更新。")
                        .Foreground(Theme.SecondaryText),
                    subAutoHours > 0
                        ? TextBlock("★ 已开启：到点会重写生效配置并热重载内核。" +
                                    "订阅里换了节点、改了规则都会直接进到正在跑的隧道里（不断流），" +
                                    "没连的时候就在下一次连接生效。" +
                                    "唯一会自己动隧道的形状：正在跑的隧道已经不认这份新配置了 —— " +
                                    "那一刻流量本来就不通，所以拆掉重拨一次（这几秒会没网），" +
                                    "一轮更新只拨一次，失败会退避并写在上面那行。")
                              .Foreground(Theme.Accent)
                        : Empty()));

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
                                     string kernelLog, KernelView.Snapshot snap,
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
                              "名单在「每次连接」和「每次热重载」时各重读一次，不需要重建 profile。")
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
                    logOpen ? LogPanel(logText) : Empty()),

                // ★★【1.0.119 · G4/G5】内核实时日志进界面。
                //   在此之前 <c>GET /logs</c> 只挂在 debug-cmd（写文件的 <c>logs</c> 命令）上，
                //   界面上<b>没有任何入口</b> —— 于是"这次拨号失败内核到底报了什么"
                //   只有愿意开命令行、知道去读 LocalState 的人才看得见，而那一层
                //   恰恰是本项目反复出静默失效的地方（Docs/08 的 run 14、Docs/12 §6）。
                //   等级、抬升与恢复的判据全在 <c>MihomoController.RunLogStreamAsync</c>，
                //   这里只负责"点一下、12 秒、把原文画出来"。
                Section("内核实时日志",
                    HStack(8,
                        Button(kernelLog.Length == 0 ? "抓取 12 秒" : "重新抓取 12 秒",
                            () => _ = FetchKernelLogAsync(snap))),
                    TextBlock($"当前内核日志等级：{(snap.LogLevel.Length == 0 ? "(读不到)" : snap.LogLevel)}　" +
                              "抓取时若不是 debug 会临时抬到 debug，结束后恢复这一档；" +
                              "拨号失败原文（dial / bind）在 mihomo 里是 Debug 级，info 档下 /logs 一条都不推。")
                        .Foreground(Theme.SecondaryText),
                    LogPanel(kernelLog)));

        /// <summary>
        /// 界面上那句"下次"的现算：<b>生效那一份</b>订阅文件的写入时刻 + 档位（+ 退避）。
        /// ★ 用 <c>Profile.FetchedAt</c>（就是 <c>subscriptions/{id}.yaml</c> 的 mtime），
        ///   不另开时间戳文件 —— 口径必须和执行环那一支逐字相同，否则界面说的"下次"
        ///   和环真正动手的时刻是两件事（同 <see cref="SubscriptionAutoUpdater"/> 类注释的理由）。
        /// </summary>
        private static DateTime? SubAutoNextDue(int hours,
                                                List<SubscriptionManager.Profile> profiles,
                                                SubscriptionManager.ActiveInfo active)
        {
            if (!SubscriptionAutoPolicy.IsOn(hours)) return null;
            foreach (var p in profiles)
                if (p.Id == active.Id)
                    return SubscriptionAutoPolicy.NextDueAt(p.FetchedAt, hours,
                                                            SubscriptionAutoUpdater.FailStreak);
            return null;   // 生效那份的文件不在清单里：没有"上次"，不猜一个下次
        }

        /// <summary>
        /// 订阅自动更新的档位按钮（★【1.0.119】）。
        ///
        /// 【为什么画整排而不是一个下拉】档位在白名单里，<b>画出来就是可选项全集</b>：
        ///   下拉/输入框会让用户写一个 1、7、1000 这种环不认的数，然后
        ///   <c>NormalizeHours</c> 判成「关」—— 按钮亮着、实际不跑，正是最坏的那种谎。
        /// 【"关闭"必须排在第一个且永远画得出来】默认值必须是"什么都不改"那一档。
        /// </summary>
        private Element SubAutoPicker(int current, Action<int> set)
        {
            var buttons = new List<Element>();
            foreach (int h in SubscriptionAutoPolicy.IntervalHours)
            {
                int hours = h;   // 闭包捕获当前值：直接捕获循环变量在旧 C# 里会全部指向最后一档
                string label = hours == 0 ? "关闭" : $"{hours}h";
                buttons.Add(Button((current == hours ? "▶ " : "") + label,
                    () => _ = SetSubAutoHoursAsync(hours, set)));
            }
            return HStack(8, buttons.ToArray());
        }

        /// <summary>
        /// 改档位：落盘 + 改执行环读的现值 + 改按钮。三步<b>必须都成</b>，
        /// 少任何一步都会得到"界面与后台各认一个档位"。失败时回滚按钮到原档位，
        /// 而不是让它停在用户刚点的那一档上 —— 点了没写成的按钮亮着，
        /// 比不亮危险得多（用户会以为已经开了）。
        /// </summary>
        private async Task SetSubAutoHoursAsync(int hours, Action<int> set)
        {
            int before = AppSettings.SubAutoHours;
            try
            {
                await ProfileGate.SetSubscriptionAutoHoursAsync(hours);
                set(AppSettings.SubAutoHours);
                _setActionHint($"自动更新档位已设为 {SubscriptionAutoPolicy.Label(AppSettings.SubAutoHours)}" +
                               "（连着 VPN 也照常更新，更新当前生效那一份）");
            }
            catch (Exception ex)
            {
                set(before);
                _setActionHint($"改档位失败，已退回 {SubscriptionAutoPolicy.Label(before)}：{ex.Message}");
            }
        }

        /// <summary>
        /// 排序视角的其中一枚按钮（▶ 打在当前那一档上）。
        /// ★ 单独成函数是因为三档必须<b>形态一致</b>：以前这种"三枚按钮"在两处
        ///   各抄一遍，抄漏一枚的结果是"某一档看起来永远选不中"。
        /// </summary>
        private static Element SortModeButton(string label, NodeSort.Mode thisMode,
                                              NodeSort.Mode current,
                                              Action<NodeSort.Mode> set) =>
            Button((current == thisMode ? "▶ " : "") + label, () => set(thisMode));

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
            return p != null ? ProfileGate.DisplayName(p, _subProfiles) : subId;
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

        /// <summary>
        /// 运行态三要素。
        /// ★【1.0.125】不可用那一档<b>分三条</b>说，不许都写成"未连接"：
        ///   首轮还没问（<see cref="NotPolledError"/>）、这一轮<b>没答</b>（超时）、
        ///   确实没在跑（端口关着被瞬时拒绝）—— 只有第三条支持"未连接"这个断言。
        ///   以前只有两档，拆隧道那几十秒界面上就凭空出现一句"内核：未连接
        ///   （…TaskCanceledException…）"，下一拍又自己变回来（实物见 Docs/66）。
        /// ★ 仍然<b>不</b>画成空白或报错堆：原始 <c>Error</c> 只在"确实没在跑"那一档带上，
        ///   因为那一档它才是原因。
        /// </summary>
        private static Element KernelStatusPanel(KernelView.Snapshot s)
        {
            if (!s.Ok)
                return TextBlock("内核：" + (s.Error == NotPolledError
                    ? "正在查询…"
                    : s.QueryTimedOut
                        ? "本轮没问到（查询超时，约 3 秒后自动重试）"
                        : $"未连接（{s.Error}）"));

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
        /// ★【1.0.119 · 排序】页头下面那一排是<b>视角</b>选择，默认「原序」；
        ///   它换的是<b>真节点的先后</b>，组行与内置策略行<b>不动位置</b>，
        ///   所以「📺 动画疯 第一个成员是 🔰 选择节点」这种作者排布在三种视角下都还在。
        ///   分档与判据全在 <see cref="NodeSort"/>（离线夹具钉着），这里只画。
        /// </summary>
        private Element MemberList(KernelView.Snapshot s, string groupName,
                                   NodeSort.Mode sortMode, Action<NodeSort.Mode> setSortMode)
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
            // ★【1.0.118】内核未运行（配置视图）时"现值"标注预设身份、⚡ 换成明示文案：
            //   测速只能由内核发起，本应用<b>绝不</b>为了测速去拨 VPN（任务书 §4.4 与
            //   "永不占死整机网络"的收工纪律同一条）。
            string nowShown = parent.Now.Length == 0 ? "（未选）"
                : s.FromConfigFile ? parent.Now + "（预设，内核可达时回放生效）" : parent.Now;
            items.Add(HStack(8,
                TextBlock($"{parent.Type}　现值 {nowShown}" +
                          $"　真节点 {parent.RealNodeCount} / 成员 {parent.Members.Count}")
                    .Foreground(Theme.SecondaryText),
                s.Ok ? Button("测速", () => _ = HealthCheckAsync(parent.Name))
                     : TextBlock("内核未运行：无法测速（测速不会为此发起 VPN 连接）")
                           .Foreground(Theme.SecondaryText)));
            if (!parent.IsUserSelectable)
                items.Add(TextBlock(
                    $"（该组类型由内核测速/故障转移自己选，和安卓版一样不可手选；" +
                    "下面列的是它的候选，▶ 只是当前赢家。）")
                    .Foreground(Theme.SecondaryText));

            // ── 排序视角（★【1.0.119】默认原序；判据见 NodeSort）──────────
            //   只在组确实有成员时画，空组不给一排点了没反应的按钮。
            if (parent.Members.Count > 0)
            {
                items.Add(HStack(8,
                    SortModeButton("原序", NodeSort.Mode.Original, sortMode, setSortMode),
                    SortModeButton("延迟", NodeSort.Mode.Delay, sortMode, setSortMode),
                    SortModeButton("名称", NodeSort.Mode.Name, sortMode, setSortMode),
                    TextBlock(NodeSort.DelaySummary(parent.Members))
                        .Foreground(Theme.SecondaryText)));
                // ★ 这句只在真的排了序时出现：原序是默认，不需要解释；
                //   一旦切走，必须当场说明"组行没跟着动"，否则用户会以为
                //   ■ 那几行也参与了排序，回头找作者顺序找不到了。
                if (sortMode != NodeSort.Mode.Original)
                    items.Add(TextBlock("（■ 组行与 ◇ 内置策略不参与排序，仍留在原位）")
                        .Foreground(Theme.SecondaryText));
            }

            // ── 正文：一列混排，顺序 = 所选视角下的成员表（默认 = 内核原序） ──
            //   ★ 默认那条纪律没变：不重排、不分块，安卓的 Default 排序就是订阅原序。
            //     重排只在用户主动选了「延迟/名称」时发生，而且组/内置的位置不动。
            foreach (var m in NodeSort.Order(parent.Members, sortMode))
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
            // ★【1.0.118 · 配置视图】server:port 只在本地 config.yaml 解析时填得出来
            //   （REST 的 /proxies 不返回这两个字段）。有就拼上——任务书 §4.1 要的
            //   "详情字段（name/type/server/port）"在离线这一侧补齐。
            string addr = (!m.IsGroup && !m.IsBuiltin && m.Server.Length > 0)
                ? $" · {m.Server}:{m.Port}" : "";
            string body = kind + src + addr;

            // 延迟三档：<b>没测过不写</b>（KernelView 顶部口径）、<b>0 写"不通"</b>、>0 写毫秒。
            // ★【1.0.93】0 必须单列：官方在测速失败时往 history 写的就是 <c>Delay=0</c>
            //   （<c>adapter/adapter.go:170-177</c> + <c>:190</c> 的 defer：alive=false 时 record.Delay 归零），
            //   而不是"没有这条记录"。按老写法 <c>>=0</c> 一律拼 "Nms"，本轮不通的那一员
            //   就显示成 "0ms" —— 在一列数字里它会被读成"这员最快"，正好读反。
            //   组测速（<c>GET /group/{name}/delay</c>）只回拨通的节点，所以点一次 ⚡
            //   之后不通的成员必然以 0 落进 history，这不是边角情形而是主路径。
            string delay = m.DelayMs > 0 ? $"{m.DelayMs}ms"
                       : m.DelayMs == 0 ? "不通"
                       : "";
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
                    string shown = ProfileGate.DisplayName(p, profiles);
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
            var r = await ProfileGate.FetchSubscriptionAsync(_subUrl, activate: true);
            _setActionHint(r.Message);
            await RefreshSubscriptionsAsync();
        }

        private async Task UpdateSubscriptionAsync()
        {
            _setActionHint("更新中…");
            var r = await ProfileGate.UpdateActiveSubscriptionAsync();
            _setActionHint(r.Message);
            await RefreshSubscriptionsAsync();
        }

        private async Task UseSubscriptionAsync(string id)
        {
            // ★【1.0.109 · 用户指令】切订阅从此是三段式（先断开 → 后修改 → 再重连），
            //   连着时这一句会真的先拆掉当前会话再重拨，最长等 20 s 收尾 —— 提示语要说清在等什么。
            _setActionHint($"切换到 {id}…（先断开 → 后修改 → 再重连）");
            // ★ UX 对齐 Verge：点一下即原地热重载（连接中），返回信息里已含重载结果。
            var r = await ProfileGate.ActivateSubscriptionAsync(id);
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
            var r = await ProfileGate.ReloadActiveConfigAsync();
            _setActionHint(r.Message);
            await RefreshSubscriptionsAsync();
        }

        private async Task RefreshSubscriptionsAsync()
        {
            try
            {
                _setSubInfo(await SubscriptionView.LoadAsync());
                _setSubProfiles(await ProfileGate.ListSubscriptionsAsync());
                _setSubActive(await ProfileGate.GetActiveSubscriptionAsync());
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

        /// <summary>
        /// 活动连接：协议 / 目标 / 链路 / 上下行 + <b>逐条强断</b>。
        /// ★【1.0.119 · G4】这一版加了「断开」「全部断开」两个动作。在此之前这一块<b>只能看</b>，
        ///   强断只存在于 debug-cmd（<c>connections kill</c> / <c>kill-all</c>），
        ///   而界面上这一列的 <c>id</c> 早在解析时被丢掉（见 <see cref="KernelView.Conn"/>），
        ///   所以"给它加个按钮"不是加个 UI —— 是把后端那条断链补齐。
        /// ★ 上下行从裸字节改成 <see cref="FormatBytes"/>：以前一行写
        ///   <c>↑1048576 ↓2097152</c>，人要心算才看得出"这条还在长"，
        ///   而"还在长"正是"该不该断它"的判据。
        /// ★ 只显示前 30 条，条数不说谎（<c>…（另有 N 条未列出）</c>）：
        ///   这条列表在真机上是几十上百条的量，全画出来这一页没法用，
        ///   但"没画出来"必须写成数字，不能省略号糊过去。
        /// </summary>
        private Element ConnectionList(KernelView.Snapshot s)
        {
            if (!s.Ok) return TextBlock("(内核未连接)");

            var items = new List<Element>
            {
                HStack(8,
                    Button("全部断开", () => _ = CloseAllConnsAsync()),
                    Button("刷新", () => _ = RefreshNowAsync()),
                    TextBlock($"共 {s.Connections.Count} 条").Foreground(Theme.SecondaryText))
            };
            if (s.Connections.Count == 0)
            {
                items.Add(TextBlock("(当前没有活动连接)"));
                return VStack(items.ToArray());
            }

            int shown = 0;
            foreach (var c in s.Connections)
            {
                if (shown++ >= 30)
                {
                    items.Add(TextBlock($"…（另有 {s.Connections.Count - 30} 条未列出）")
                        .Foreground(Theme.SecondaryText));
                    break;
                }
                items.Add(HStack(8,
                    TextBlock($"{(c.Network.Length > 0 ? c.Network.ToUpperInvariant() + " " : "")}" +
                              $"{c.Target}　{c.Chain}　↑{FormatBytes(c.Upload)} ↓{FormatBytes(c.Download)}"),
                    // ★ id 为空时按钮禁用而不是画一个"点了必失败"的按钮：
                    //   列表是 3 秒前的，强断要的是<b>现在</b>还在的那条。
                    Button("断开", () => _ = CloseConnAsync(c.Id)).IsEnabled(c.Id.Length > 0)));
            }
            return VStack(items.ToArray());
        }

        /// <summary>
        /// 强断一条连接，随后<b>立刻</b>把快照读回来。
        /// ★ 为什么必须自己 reload：内核回 204 只代表"已受理"，界面上那条还在，
        ///   等 3 秒轮询把它抹掉会让用户以为按钮没生效 —— 本项目的老账：静默等于没修。
        /// ★ 结果一律进 <c>actionHint</c>（动作回话），不碰 <c>vpnStatus</c>（读数槽），
        ///   分工是 1.0.96 那条钉死的。
        /// </summary>
        private async Task CloseConnAsync(string id)
        {
            try
            {
                string result = await MihomoController.CloseConnectionAsync(id);
                _setActionHint(result);
                _setSnap(await KernelView.LoadAsync());
            }
            catch (Exception ex)
            {
                _setActionHint($"断开失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>清空所有活动连接（<c>DELETE /connections</c>），同样立刻 reload。</summary>
        private async Task CloseAllConnsAsync()
        {
            try
            {
                string result = await MihomoController.CloseAllConnectionsAsync();
                _setActionHint(result);
                _setSnap(await KernelView.LoadAsync());
            }
            catch (Exception ex)
            {
                _setActionHint($"清空失败: {ex.GetType().Name}: {ex.Message}");
            }
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
                // ★【1.0.118 · 任务书 §4.2 情形 B】先判内核可达性，再决定走哪条切换路：
                //   · 可达（含"断开后内核常驻"的情形 A）→ 原实时路径 PUT+回读，行为一字不改；
                //   · 不可达 → 写预设台账 selection.txt（与实时成功后记的是<b>同一个</b>台账），
                //     内核下次可达由 ReplaySelectionsAsync 回放生效。
                //   ★ 不是 UI-only：不改按钮状态骗人，落的是磁盘台账；
                //   ★ 不借道起内核：内核归插件生命周期管，前台不擅自拨 VPN。
                var s0 = await KernelView.LoadAsync();
                if (!s0.Ok)
                {
                    _setActionHint(ConfigNodeView.RecordPreset(s0.Groups, group, member));
                    _setSnap(await KernelView.LoadAsync());   // 重载配置视图，"现值（预设）"跟着更新
                    return;
                }
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
        /// 组测速（安卓那枚 ⚡）：<c>GET /group/{组}/delay?timeout=&amp;url=</c>
        /// 让内核把这组成员全拨一遍（<c>MihomoController.TriggerGroupHealthCheckAsync</c>，
        /// 路由证据与官方语义全部记在那里）。
        /// ★ 与 <see cref="SelectAsync"/> / <see cref="RefreshProviderAsync"/> 同构：结果进
        ///   <c>actionHint</c>，随后立刻刷一次快照。
        /// ★【1.0.93 订正】"点了不会当场出数"这句话<b>跟着错的路由一起作废</b>：
        ///   旧实现打的是 <c>PUT /proxies/{组}/healthcheck</c>（clash premium 的老 API，
        ///   本内核没有 ⇒ 必然 404），它对应的"204=已触发、延迟异步写回"那套说法也不成立。
        ///   官方在新路上是<b>等整批拨完才返回</b>（<c>groupbase.go:256</c> 的 <c>wg.Wait()</c>
        ///   在 <c>render.JSON</c> 之前），响应回来时延迟已经写进各节点的 history，
        ///   所以下面这一次 <c>LoadAsync</c> 就能看到新数 —— 不需要等 3 秒轮询。
        /// ★ 上限取的是<b>整批</b>（内核拿一个 ctx 罩全组并发），慢机场的一轮可能跑到
        ///   <c>GroupTestBatchTimeoutMs</c>（现 8 s），所以先落一条"测速中"再等结果，
        ///   免得界面在那几秒里一言不发（1.0.75 那条教训）。
        /// </summary>
        private async Task HealthCheckAsync(string group)
        {
            try
            {
                // ★【1.0.118 · 任务书 §4.4】测速走内核 REST，内核不可达时这里先判、直接给出
                //   "核心不可用"这一档，不进"测速中"（那是"已在跑"的口径）。
                //   ★ 这句话同时是纪律：测速绝不为了出数去拨 VPN。
                var s0 = await KernelView.LoadAsync();
                if (!s0.Ok)
                {
                    _setActionHint("内核未运行：无法测速（测速走内核 REST，不会为此发起 VPN 连接）");
                    _setSnap(s0);   // 顺带把快照刷成配置视图，页面与提示口径一致
                    return;
                }
                _setActionHint($"测速中：{group}（要拨完这组所有成员，最慢决定这一轮的成败）");
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
        /// 抓 12 秒内核实时日志并画进「内核实时日志」那块。
        /// ★ 内核不可达时<b>当场拒绝并说明</b>，不发起 VPN 连接 —— 口径同 1.0.118 的测速按钮：
        ///   <c>/logs</c> 走的是回环 REST，隧道不是它的前提；把它和"先连上 VPN"绑起来
        ///   是假的依赖，而"点了按钮给我拨出一条隧道"是本项目明令禁止的动作。
        /// ★ 等级怎么抬、抬完恢复成哪一档，后端已经决定（未知等级回退 <c>info</c>，
        ///   不会把用户的 <c>warning</c> 悄悄改成 <c>info</c>），这里不重复判。
        /// </summary>
        private async Task FetchKernelLogAsync(KernelView.Snapshot snap)
        {
            if (!snap.Ok)
            {
                _setActionHint("内核未运行：抓不了实时日志（这条路只读内核 REST，不会为此发起 VPN 连接）");
                return;
            }
            await RunAsync(_setKernelLog,
                "正在抓取 12 秒内核日志…（期间不刷新这一块，等它自己回来）",
                () => MihomoController.RunLogStreamAsync(12, snap.LogLevel));
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
