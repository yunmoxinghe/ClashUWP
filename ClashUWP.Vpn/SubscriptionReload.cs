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
                    // ★【1.0.35 · G9】走同一条路径把注入结果记进日志：
                    //   前台点「重载」之后热重载吃的就是这份文件，名单没注进去的话
                    //   下一次连接会把热重载的结果覆盖掉 —— 两条路必须留同一句证据。
                    //   本方法跑在<b>前台进程</b>里，RollingLog 按角色自动落 logs/app.log。
                    // ★【1.0.70 · G7】ipv6 两个参数必须与插件侧逐字同源：热重载吃的
                    //   就是这份文件，前台漏传会让"重载后 tun.inet6-address 消失"，
                    //   而插件下一次连接又改回来 —— 同一个开关两种行为，最难查。
                    // ★【1.0.40 · DNS 可配】dns-config.txt 与生效 v6 池同样必须同源：
                    //   热重载若漏叠 DNS 覆盖，会把用户配置打回订阅原文，直到下次连接。
                    DnsConfig dnsCfg = LocalSettings.DnsConfigEnabled ? DnsConfig.Load() : new DnsConfig();
                    string pool6 = dnsCfg.EffectiveFakeIpRange6(LocalSettings.Ipv6FakeRange);
                    string text = KernelConfig.Transform(raw, AppSplit.Load(),
                        LocalSettings.DnsConfigEnabled ? dnsCfg : null,
                        LocalSettings.Ipv6, pool6,
                        out string splitNote, out string dnsNote);
                    RollingLog.Info("config",
                        $"热重载配置已重生成 应用分流={splitNote}；DNS 覆盖={dnsNote} v6pool={pool6}");
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
