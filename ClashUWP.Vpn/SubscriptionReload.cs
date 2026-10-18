using System;
using System.IO;
using Windows.Storage;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// 【UX 对齐 Verge】前台切/更新订阅后调用：把当前生效的 <c>config.yaml</c> 改写成内核实际读取的
    /// 那份（FakeIp 开时生成 <c>config.kernel.yaml</c>，否则沿用 <c>config.yaml</c>），返回
    /// 应传给 <c>PUT /configs</c> 热重载的绝对路径；文件没就位返回 null。
    ///
    /// ★ 与 <see cref="ClashVpnPlugin"/> 步 4.1 完全同口径（同一个 <see cref="KernelConfig.Transform"/> /
    ///   <see cref="KernelConfig.WriteEffectiveFile"/> / <see cref="LocalSettings"/>），<b>单源</b> ——
    ///   前台只负责拿路径去 <c>PUT /configs</c>（见 <c>MihomoController.RunReloadAsync</c>）。
    ///   绝不能在前台另写一份改写逻辑：那会跟插件侧的口径分叉，重演 run 24「常驻热重载把
    ///   fake-ip 覆盖回 redir-host」的黑洞（Docs/08 §9）。
    ///
    /// ★ 必须是 <c>public</c>：前台通过 nupkg 引用本组件，只能看到 winmd 投影出来的 public 成员；
    ///   <see cref="KernelConfig"/> / <see cref="LocalSettings"/> 都是 internal，前台调不到，
    ///   所以把"重新生成内核配置"这一个动作封成 public 入口，组件改了就 repack+bump（本项目纪律）。
    /// </summary>
    public static class SubscriptionReload
    {
        /// <summary>
        /// 读 <c>config.yaml</c> → 按 FakeIp 改写 → 落 <c>config.kernel.yaml</c> → 返回热重载路径。不抛。
        /// ★ FakeIp 关时内核直接读 <c>config.yaml</c>，故返回 <c>config.yaml</c> 绝对路径；
        ///   FakeIp 开时返回 <see cref="KernelConfig.WriteEffectiveFile"/> 生成的 <c>config.kernel.yaml</c> 绝对路径。
        /// </summary>
        public static string? RegenerateEffectiveFile()
        {
            try
            {
                string baseDir = ApplicationData.Current.LocalFolder.Path;
                string rawPath = Path.Combine(baseDir, "config.yaml");
                if (!File.Exists(rawPath))
                    return null;
                string raw = File.ReadAllText(rawPath);

                LocalSettings.Load();
                if (LocalSettings.FakeIp)
                {
                    string text = KernelConfig.Transform(raw);
                    return KernelConfig.WriteEffectiveFile(text);   // config.kernel.yaml 绝对路径
                }
                return rawPath;
            }
            catch
            {
                return null;
            }
        }
    }
}
