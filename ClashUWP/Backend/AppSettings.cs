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
    ///   <c>LocalSettings.Load()</c>（<c>ClashUWP.Vpn/LocalSettings.cs:342</c>）为了在
    ///   WinRT 同步回调里能用，走的是「<c>AsTask().GetAwaiter().GetResult()</c>」的
    ///   <b>同步等异步</b>。它在 vpnClient 后台进程里没有别的选择，而前台有 ——
    ///   <c>Diagnostics.StartOnce()</c> 是在 <c>MainComponent.Render()</c> 的渲染线程上
    ///   点火的，在那里阻塞一次 WinRT 存储调用就是把渲染线程挂住。
    ///   ⇒ 前台只加这一份<b>全异步</b>读，绝不把插件那份搬过来。
    ///   代价是"同一个文件两个解析器"，所以<b>行格式与布尔口径刻意逐字对齐</b>
    ///   （见下面的 <c>ParseBool</c> 注释），不给自己留第二套真值表。
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
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in text.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#"))
                        continue;
                    int idx = line.IndexOf('=');
                    if (idx <= 0)
                        continue;
                    map[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
                }

                AutoProbe = ParseBool(map.GetValueOrDefault("autoProbe"), AutoProbe);
                Source = "settings.txt";
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
            $"src={Source} autoProbe={(AutoProbe ? 1 : 0)}");

        /// <summary>
        /// 布尔口径 —— <b>逐字照搬</b>插件侧 <c>LocalSettings.ReadBool</c>
        /// （<c>ClashUWP.Vpn/LocalSettings.cs:448</c>）：1/true/on/yes 为真，
        /// 0/false/off/no 为假，其余（含缺键）回落 fallback。
        ///
        /// ★ 之所以两边都要写死同一张表：这个文件同时被两个进程按各自的表读，
        ///   一旦口径分叉，就会出现"前台以为开了、插件以为关了"这类无法复盘的分歧。
        ///   组件里的 <c>ReadBool</c> 是 <c>private static</c> 且收 <c>Dictionary</c>，
        ///   搬不过来（那属于数据面改动，要版本 bump + 重新组包，不该由门控顺带做）。
        /// </summary>
        private static bool ParseBool(string? v, bool fallback)
        {
            if (v == null) return fallback;
            if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("yes", StringComparison.OrdinalIgnoreCase))
                return true;
            if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("no", StringComparison.OrdinalIgnoreCase))
                return false;
            return fallback;
        }
    }
}
