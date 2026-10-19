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
        /// ★【1.0.75】返回 <b>null</b> 有两种：<c>config.yaml</c> 不存在，或改写结果<b>没有任何可用出站</b>
        ///   （空壳不热重载）。后者绝不落盘、绝不 PUT —— 具体取舍见方法内的说明。
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
                string effective;
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
                    effective = KernelConfig.Transform(raw, AppSplit.Load(),
                        LocalSettings.DnsConfigEnabled ? dnsCfg : null,
                        LocalSettings.Ipv6, pool6,
                        out string splitNote, out string dnsNote);
                    RollingLog.Info("config",
                        $"热重载配置已重生成 应用分流={splitNote}；DNS 覆盖={dnsNote} v6pool={pool6}");
                }
                else
                {
                    // fakeIp=0：内核读的就是 config.yaml 原文，改写器整个不进。
                    effective = raw;
                }

                // ★【1.0.75 · 空壳不热重载】口径同插件步 4.1 的"空壳不拨号"：
                //   隧道正在跑的时候 PUT 一份零出站的配置，等于把"能用的旧内核配置"
                //   换成"承接不了任何流量的新配置"—— 面板还是 Connected，流量全进黑洞。
                //   这里 return null（而不是 throw、也不是照旧返回路径）＝<b>维持现状</b>：
                //   旧 config.kernel.yaml 不动、内核继续用已经在跑的那份，前台会给出明确提示。
                //   判据自身出错一律放行（见 <see cref="KernelConfig.ReasonIfNoOutbound"/> 的
                //   "假阴性安全、假阳性危险"），所以宁可多做一次热重载也不会拦错。
                string emptyReason = KernelConfig.ReasonIfNoOutbound(effective);
                if (emptyReason.Length > 0)
                {
                    RollingLog.Warn("config", "空壳不热重载（保持原有内核配置）：" + emptyReason);
                    return null;
                }

                return LocalSettings.FakeIp
                    ? KernelConfig.WriteEffectiveFile(effective)   // config.kernel.yaml 绝对路径
                    : rawPath;
            }
            catch
            {
                return null;
            }
        }
    }
}
