using System;

namespace ClashUWP.Backend
{
    /// <summary>
    /// 【后端 · LocalState 文件路径的单一出口】
    ///
    /// 【为什么单独一个文件】这个类的存在只为一件事：<b>包目录名不许写死</b>。
    ///   1.0.75 那笔（<c>3b11f69</c>）把主清单 Identity Name 从 GUID 换成 <c>8B8CC4F4.ClashUWP</c>
    ///   并同时换了发布者 ⇒ 家族后缀从 <c>…_y0tg80em3rma2</c> 变成 <c>…_4tjvwvefxrz66</c>，
    ///   于是所有把 <c>%LocalAppData%\Packages\&lt;家族&gt;\…</c> 当字面量写进代码或脚本的地方
    ///   <b>同一天起全部读到"文件不存在"</b> —— 而且它不报错，只把判据说反。
    ///   实际吃过两次：<c>MihomoController.DiagnoseKernelAsync</c> 的诊断（Docs/38 §十四）
    ///   和 <c>Tools/TestScripts/collect-evidence.ps1</c> 的取证。
    ///
    /// 【为什么不像 1.0.89 第一版那样挂在 <c>DebugCommandLoop</c> 上】现读
    ///   <c>Tools/YamlShapeTest/YamlShapeTest.csproj</c>：离线夹具靠 <c>&lt;Compile Link&gt;</c>
    ///   把 <c>MihomoController.cs</c> 等源文件拉进自己的编译单元。挂在 <c>DebugCommandLoop</c> 上
    ///   ⇒ <c>MihomoController.cs</c> 里那一行引用要求夹具也 link <c>DebugCommandLoop.cs</c>，
    ///   而后者又依赖 <c>RollingLog</c>、命令分发、VPN 会话……整条链拖不进一个"不碰网络、不需要设备"
    ///   的离线夹具 ⇒ <c>dotnet run --project Tools/YamlShapeTest</c> 直接
    ///   <c>CS0103 当前上下文中不存在名称"DebugCommandLoop"</c>（1.0.89 就是这么把夹具弄断的，
    ///   当时只跑了主工程 Debug 编译没跑夹具，属于自查漏掉）。
    ///   这个文件除了 <c>Windows.Storage.ApplicationData</c> 不依赖任何东西，夹具能直接 link。
    /// </summary>
    internal static class LocalStatePaths
    {
        /// <summary>
        /// 默认的内核配置文件路径（<c>LocalState\config.kernel.yaml</c>）。
        /// ★ 与 <c>KernelConfig.EffectiveFileName</c> 同一个文件名 —— 那是组件侧的常量，
        ///   前台引得到（组件是 app 的依赖），但写成两处字面量更容易漂，所以显式对照。
        /// ★ 全仓只有这里算这个路径是对的（走 <c>ApplicationData.Current.LocalFolder</c>），
        ///   诊断侧、命令侧一律引这个方法，不许再自己拼包目录。
        /// </summary>
        internal static string DefaultKernelConfigPath()
        {
            try
            {
                return System.IO.Path.Combine(
                    Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                    "config.kernel.yaml");
            }
            catch
            {
                // 非包上下文（离线夹具、桌面进程）里拿不到 ApplicationData ⇒ 退回裸文件名，
                // 让调用方的 File.Exists 自然判假，而不是抛出去打断整条诊断。
                return "config.kernel.yaml";
            }
        }
    }
}
