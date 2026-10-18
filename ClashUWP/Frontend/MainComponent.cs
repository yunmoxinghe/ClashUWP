using System;
using System.Collections.Generic;
using System.Threading.Tasks;

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
    /// </summary>
    public sealed class MainComponent : Component
    {
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

        public override Element Render()
        {
            var (count, setCount) = UseState(0);

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
                });
            }, Array.Empty<object>());

            return ScrollViewer(
                VStack(
                    TextBlock($"ClashUWP —— Reactor.Uwp 已接管（{count}）"),
                    TextBlock($"VPN：{vpnStatus}"),
                    actionHint.Length > 0 ? TextBlock(actionHint) : Empty(),

                    // ================= VPN 控制 =================
                    TextBlock("═══ VPN 控制 ═══"),
                    HStack(
                        Button("建/重建 profile", () => _ = RunAsync(_setActionHint, "重建中…", VpnSession.RecreateAsync)),
                        Button("连接 VPN", () => _ = RunAsync(_setVpnStatus, "连接中…", () => VpnSession.ConnectAsync())),
                        Button("断开 VPN", () => _ = RunAsync(_setVpnStatus, "断开中…", VpnSession.DisconnectAsync))),

                    // ================= 内核运行态（G8）=================
                    TextBlock("═══ 内核运行态 ═══"),
                    KernelStatusPanel(snap),

                    // ================= 模式切换 =================
                    HStack(
                        Button("mode rule", () => _ = RunAsync(_setActionHint, "切换中…", () => MihomoController.RunModeAsync("rule"))),
                        Button("mode global", () => _ = RunAsync(_setActionHint, "切换中…", () => MihomoController.RunModeAsync("global"))),
                        Button("mode direct", () => _ = RunAsync(_setActionHint, "切换中…", () => MihomoController.RunModeAsync("direct")))),

                    // ================= 订阅（G1）=================
                    TextBlock("═══ 订阅 ═══"),
                    SubscriptionPanel(subInfo, subProfiles, subActive),
                    HStack(
                        TextBox(subUrl, v => _setSubUrl(v), "https://… 订阅链接"),
                        Button("拉取并启用", () => _ = FetchSubscriptionAsync())),
                    HStack(
                        Button("更新当前订阅", () => _ = UpdateSubscriptionAsync()),
                        Button("重新加载配置", () => _ = ReloadConfigAsync()),
                        Button("刷新列表", () => _ = RefreshSubscriptionsAsync())),

                    // ================= 选节点（G2）=================
                    TextBlock("═══ 选节点 ═══"),
                    GroupPicker(snap, selectedGroup),
                    MemberList(snap, activeGroup),

                    // ================= 活动连接（G4）=================
                    TextBlock("═══ 活动连接 ═══"),
                    ConnectionList(snap),

                    // ================= 调试工具 =================
                    TextBlock("═══ 调试工具 ═══"),
                    HStack(
                        Button("诊断内核", () => _ = RunAsync(_setActionHint, "诊断中…", () => MihomoController.DiagnoseKernelAsync())),
                        Button("查看配置", () => _ = RunAsync(_setActionHint, "读取中…", () => MihomoController.RunConfigsDumpAsync())),
                        Button("立即刷新", () => _ = RefreshNowAsync())),
                    Button(logOpen ? "收起日志" : "查看日志",
                        () => _ = ToggleLogAsync(logOpen)),

                    // ================= 日志（可滚动）=================
                    logOpen ? LogPanel(logText) : Empty(),

                    // ================= 计数器 =================
                    TextBlock("═══ 调试 ═══"),
                    Button("点一下", () => setCount(count + 1))
                )
            );
        }

        // ==================== 各区块 ====================

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
