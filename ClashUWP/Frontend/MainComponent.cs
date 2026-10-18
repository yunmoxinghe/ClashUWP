using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.UI.Reactor.Core;

using ClashUWP.Backend;
using ClashUWP.Backend.Mihomo;
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

        public override Element Render()
        {
            var (count, setCount) = UseState(0);

            var (vpnStatus, setVpnStatus) = UseState("未连接");
            var (actionHint, setActionHint) = UseState("");
            var (snap, setSnap) = UseState(KernelView.Snapshot.Failed("(尚未查询)"));
            var (logText, setLogText) = UseState("");
            var (logOpen, setLogOpen) = UseState(false);
            var (selectedGroup, setSelectedGroup) = UseState("");

            _setVpnStatus = setVpnStatus;
            _setActionHint = setActionHint;
            _setSnap = setSnap;
            _setLogText = setLogText;
            _setLogOpen = setLogOpen;
            _setSelectedGroup = setSelectedGroup;

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

            return ScrollViewer(
                VStack(
                    TextBlock($"ClashUWP —— Reactor.Uwp 已接管（{count}）"),
                    TextBlock($"VPN：{vpnStatus}"),
                    actionHint.Length > 0 ? TextBlock(actionHint) : Empty(),

                    // ================= VPN 控制 =================
                    TextBlock("═══ VPN 控制 ═══"),
                    HStack(
                        Button("建/重建 profile", () => _ = RunAsync(_setActionHint, "重建中…", VpnSession.RecreateAsync)),
                        Button("连接 VPN", () => _ = RunAsync(_setVpnStatus, "连接中…", VpnSession.ConnectAsync)),
                        Button("断开 VPN", () => _ = RunAsync(_setVpnStatus, "断开中…", VpnSession.DisconnectAsync))),

                    // ================= 内核运行态（G8）=================
                    TextBlock("═══ 内核运行态 ═══"),
                    KernelStatusPanel(snap),

                    // ================= 模式切换 =================
                    HStack(
                        Button("mode rule", () => _ = RunAsync(_setActionHint, "切换中…", () => MihomoController.RunModeAsync("rule"))),
                        Button("mode global", () => _ = RunAsync(_setActionHint, "切换中…", () => MihomoController.RunModeAsync("global"))),
                        Button("mode direct", () => _ = RunAsync(_setActionHint, "切换中…", () => MihomoController.RunModeAsync("direct")))),

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
