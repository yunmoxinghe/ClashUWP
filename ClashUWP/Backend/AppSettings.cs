using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using ClashUWP.Vpn;

namespace ClashUWP.Backend
{
    /// <summary>
    /// 【后端 · 前台侧设置读】把 <c>LocalState/settings.txt</c> 读到前台进程里来，
    /// 只给前台用得上的那几个开关。
    ///
    /// 【为什么不直接用组件里的 <see cref="LocalSettings"/>】那套是<b>插件</b>的口径：
    ///   <c>LocalSettings.Load()</c>（<c>Vpn\LocalSettings.cs</c>）为了在
    ///   WinRT 同步回调里能用，走的是「<c>AsTask().GetAwaiter().GetResult()</c>」的
    ///   <b>同步等异步</b>。它在 vpnClient 后台进程里没有别的选择，而前台有 ——
    ///   <c>Diagnostics.StartOnce()</c> 是在 <c>MainComponent.Render()</c> 的渲染线程上
    ///   点火的，在那里阻塞一次 WinRT 存储调用就是把渲染线程挂住。
    ///   ⇒ 前台只加这一份<b>全异步</b>读，绝不把插件那份搬过来。
    ///   ★【1.0.93 订正】以前这里写的代价是「同一个文件两个解析器」，靠布尔表
    ///     两边逐字照搬维系 —— 现在<b>解析只剩一份</b>（<see cref="SettingsParse"/>：
    ///     行格式 + 布尔/整数/字符串口径 + 回落规则），两边各自保留的只是
    ///     <b>读文件的 I/O 方式</b>（前台异步、插件同步）。分层口径见 Docs/48。
    ///
    /// 【为什么只放一个键】前台现在唯一必须问用户的决定是「第一次开应用要不要自动拨
    ///   探测 profile」（Docs/12 §5 第 1 项 G12）。以后 G8/G3 把 15 个把手做进界面时，
    ///   writer 和更多 reader 都收在这个类里，不再到处开新口子。
    /// </summary>
    internal static class AppSettings
    {
        /// <summary>
        /// 启动探测的<b>授权门控</b>。<c>false</c> = 本次进程绝不建 profile、绝不连 VPN。
        ///
        /// ★ 默认 <b>false</b>，这一点是这次改动的全部目的：探测用的 profile 虽然只有
        ///   203.0.113.0/24 + 8.8.8.8/32 这种文档保留段的小路由、按实测不会断网，
        ///   但它仍然<b>真的建了一个 VPN 会话</b>。而本项目出过一次"拆不干净导致整机断网、
        ///   只能重置网络才恢复"的事故（Docs/08 与滚动日志 2026-10-16 00:21）——
        ///   凡是"不需要用户同意就会碰 VPN"的代码，都不该由装配顺序决定它跑不跑。
        ///   防事故优先于加功能。
        ///
        /// 【怎么打开】往 <c>%LOCALAPPDATA%\Packages\&lt;包名&gt;\LocalState\settings.txt</c>
        ///   里写一行 <c>autoProbe=1</c>（该文件本来就是 15 个实验把手的入口，
        ///   插件侧读不认识的键会直接忽略，所以加这一行不影响数据面）。
        ///   两个探测各自还有"闸门文件"（<c>selftest.txt</c> / <c>dataface-v4.txt</c>），
        ///   所以 <c>autoProbe=1</c> 最多也只是<b>再各跑一次</b>，不会每次启动都拨。
        ///
        /// 【不想动文件怎么办】<c>debug-cmd.txt</c> 里的 <c>probe</c> 命令是同一份代码的
        ///   显式入口 —— 门控关掉的是<b>自动</b>，不是<b>能</b>。
        /// </summary>
        internal static bool AutoProbe = false;

        /// <summary>
        /// ★【1.0.115】快速设置面板是否列出<b>每条订阅</b>的独立条目。
        /// <c>false</c>（默认）= 面板里只出现一条 "ClashUWP"（基础 profile），
        /// 功能等于纯开关：点一下连当前生效订阅、再点断开。
        /// <c>true</c> = 每条订阅各占一行（旧行为），用户可以在面板里直接切订阅。
        ///
        /// 【为什么默认关】UWP VPN Plugin 平台限制：A 处于 Connected 时点 B，
        /// 平台<b>不会</b>把 Connect(B) 送进插件（Docs/43 官方 sample 同形状、
        /// Docs/57 §一 第三种形状"零插件活动"），UI 停在"验证你的登录信息"
        /// 且 Disconnect 按钮灰、无出口。用户必须先手动断开 A 再连 B。
        /// 开启前界面必须明确告知这条限制。
        ///
        /// 【怎么打开】<c>settings.txt</c> 里写 <c>panelMultiEntry=1</c>。
        /// </summary>
        internal static bool PanelMultiEntry = false;

        /// <summary>读到的是默认值还是 settings.txt —— 进日志用，免得又出现"以为切了其实没切"。</summary>
        internal static string Source = "默认值";

        /// <summary>
        /// 异步读一次 <c>settings.txt</c>，把值落到静态字段上并记一行日志。
        ///
        /// 读不到文件 / 解析失败<b>不是错误</b>：保持默认值（<see cref="AutoProbe"/>=false），
        /// 也就是"不自动拨"。这与插件侧 <c>LocalSettings.Load()</c> 的处理一致 ——
        /// 一个取证通道的缺失，绝不该变成一次意外的 VPN 连接。
        /// ★ 每次调用都先恢复默认，避免上一次的值残留（同插件侧的理由）。
        /// </summary>
        internal static async Task LoadAsync()
        {
            AutoProbe = false;
            PanelMultiEntry = false;
            Source = "默认值";

            try
            {
                Windows.Storage.StorageFile? file =
                    await LocalState.TryGetFileAsync("settings.txt");
                if (file == null)
                {
                    // 没文件是"全新装包后什么都没配"的正常状态，不打警告。
                    Log();
                    return;
                }

                string text = await LocalState.ReadTextAsync(file);
                // ★ 解析走共享层唯一实现（<see cref="SettingsParse"/>），本类不再有第二套真值表。
                Dictionary<string, string> map = SettingsParse.Parse(text);

                AutoProbe = SettingsParse.Bool(map, "autoProbe", AutoProbe);
                PanelMultiEntry = SettingsParse.Bool(map, "panelMultiEntry", PanelMultiEntry);
                Source = "settings.txt";

                // ★【1.0.115】开启多条目时留一条显式警告：平台限制不会因为我们开了开关就消失。
                if (PanelMultiEntry)
                    RollingLog.Warn("appsettings",
                        "panelMultiEntry=1：订阅条目将出现在快速设置面板。" +
                        "受 UWP VPN Plugin 平台限制，A 连着时点 B 会卡在「验证你的登录信息」" +
                        "（请求不进插件，Docs/43+57），必须先手动断开 A 再连 B。");
            }
            catch (Exception ex)
            {
                // ★ 只进滚动日志，不写 debug-out：这条路是启动装配，没有命令方在读结果。
                Source = $"默认值（读取失败 {ex.GetType().Name}）";
                RollingLog.Error("appsettings", "settings.txt", ex);
            }

            Log();
        }

        private static void Log() => RollingLog.Info("appsettings",
            $"src={Source} autoProbe={(AutoProbe ? 1 : 0)} panelMultiEntry={(PanelMultiEntry ? 1 : 0)}");
    }
}
