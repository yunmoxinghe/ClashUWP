using System;
using System.IO;

using Windows.Storage;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// 【内核 home 目录】把 mihomo 的 <c>constant.Path.homeDir</c> 指到<b>可写</b>的
    /// <c>LocalState</c>，并保证里面有 <c>Country.mmdb</c>。
    ///
    /// 【为什么必须改】home 原先是 <c>Package.Current.InstalledLocation.Path</c>
    ///   （MSIX 包安装目录，<b>只读</b>）。它当初只是为了喂 GEOIP 规则需要的
    ///   <c>Country.mmdb</c>（随包打在根目录），但 mihomo 把 home 当<b>写</b>目录用：
    ///   按 v1.19.32 的 <c>constant/path.go</c>（我们桥里 <c>go.mod</c> pin 的就是这一版，
    ///   与 §7 实测用的官方二进制同版本）——
    ///   <c>Cache() = &lt;home&gt;/cache.db</c>（代理组选择的持久化，Docs/12 §7 实测：
    ///   硬杀进程再起来选择还在，靠的就是这个文件）；
    ///   <c>GetPathByHash(prefix,name) = &lt;home&gt;/&lt;prefix&gt;/&lt;hash&gt;</c>（provider /
    ///   rule-provider 落盘）；<c>MMDB()</c> 也是 <c>ReadDir(home)</c> 里找 mmdb 文件。
    ///   ⇒ home 只读时这些写全部静默失败：选节点"当场生效、下次没了"（G2 因此是半截功能），
    ///   带 provider 的订阅直接废掉（G11），热重载也过不了安全阀（见下）。
    ///
    /// 【顺带解锁的第二个功能】<c>PUT /configs</c> 带 <c>path</c> 时，内核会查
    ///   <c>IsSafePath(path)</c> —— 源码里 <c>SafePaths() = [homeDir] + SAFE_PATHS 环境变量</c>，
    ///   也就是<b>只有 home 之内的路径才允许被热重载</b>（除非构建带 CMFA tag 或
    ///   <c>allowUnsafePath</c>，我们两者都没有：<c>Tools/mihomo-bridge</c> 全量 grep
    ///   只命中 <c>SetHomeDir</c>，没有 <c>SetEmbedMode</c>/CMFA）。
    ///   而改写后的订阅配置<b>就写在 LocalState</b>（<c>KernelConfig.EffectiveFileName
    ///   = "config.kernel.yaml"</c>，<see cref="KernelConfig.WriteEffectiveFile"/>）。
    ///   ⇒ home = LocalState 之后，热重载的 path 才过得了这道阀；home 在包目录时
    ///   它<b>必然</b> 400。这是 Docs/12 §7"热重载从来没生效过"背后的第二把锁
    ///   （第一把是 path 拼在了 query 上）。
    ///
    /// 【失败口径 —— 这条是安全垫】复制 mmdb 不成功（或 LocalState 根本拿不到）时
    ///   <b>退回包目录</b>，保持今天已经跑通的行为：GEOIP 规则在 <c>config.Parse</c>
    ///   阶段就要读 MMDB（见 <c>Tools/mihomo-bridge/bridge/core.go:127-128</c>），
    ///   home 里没 mmdb 会让规则解析出问题 —— 宁可丢掉持久化这个新功能，
    ///   也不能把已经能连的数据面换出一条新的失败路径。
    ///   用了哪一侧、为什么，一律进滚动日志（<c>kernel-home</c>），
    ///   这样"持久化怎么又没了"能在 <c>logtail app</c> 里一眼查到。
    /// </summary>
    internal static class KernelHome
    {
        /// <summary>上一次 Ensure 的结论，只为让日志不重复刷同一条。</summary>
        private static string _lastLogged = "";

        /// <summary>
        /// 返回该传给桥的 <c>mmdbDir</c>（即内核 home）。保证返回值目录里已有
        /// <c>Country.mmdb</c>；做不到就退回包安装目录。
        /// </summary>
        internal static string Ensure()
        {
            // 可空是刻意的：CsWinRT 把 InstalledLocation.Path 标成可能为 null，
            // 而它又是"退回包目录"这条兜底路径唯一依赖的值。
            string? packageDir = "";
            try { packageDir = Windows.ApplicationModel.Package.Current.InstalledLocation.Path; }
            catch { /* 后台任务偶发拿不到包信息 */ }

            string local;
            try { local = ApplicationData.Current.LocalFolder.Path; }
            catch (Exception ex)
            {
                // 记忆里这条不是假设：插件进程里 ApplicationData 偶发不可用（见 ClashVpnPlugin
                // 里写诊断文件那几处的同款兜底注释）。
                return Fallback(packageDir, "LocalState 不可用 " + ex.GetType().Name);
            }

            if (string.IsNullOrEmpty(local))
                return Fallback(packageDir, "LocalState 路径为空");

            string src = Path.Combine(packageDir ?? "", "Country.mmdb");
            string dst = Path.Combine(local, "Country.mmdb");

            try
            {
                if (!NeedsCopy(src, dst))
                {
                    Log($"home=LocalState（mmdb 已就位）{Detail(dst)}");
                    return local;
                }
                File.Copy(src, dst, overwrite: true);
                Log($"home=LocalState（已复制 Country.mmdb）{Detail(dst)}");
                return local;
            }
            catch (Exception ex)
            {
                return Fallback(packageDir,
                    $"复制 Country.mmdb 失败 {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 该不该（重）复制：目标不存在、或源与目标<b>长度不同</b>、或源比目标新。
        /// ★ 只看长度是刻意的：MSIX 重装后包内文件的时间戳可能整体变新而内容没变，
        ///   每次连接都重拷一遍 4 MB 不值当；但订阅换了包（mmdb 更新）一定体现在长度上，
        ///   时间戳这一路只作补充，两个条件都放行才不会漏更新。
        /// </summary>
        private static bool NeedsCopy(string src, string dst)
        {
            if (!File.Exists(src)) return false;   // 源都没有：复制没意义，交给下面的退回逻辑
            if (!File.Exists(dst)) return true;
            try
            {
                var s = new FileInfo(src);
                var d = new FileInfo(dst);
                if (s.Length != d.Length) return true;
                return s.LastWriteTimeUtc > d.LastWriteTimeUtc;
            }
            catch { return false; }
        }

        /// <param name="packageDir">
        /// 包安装目录；<b>可以为 null</b> —— 后台任务里 <c>InstalledLocation</c> 偶发取不到，
        /// 那时唯一的 home 候选也没了，返回值是空串，由 <c>StartMihomoBridge</c> 那侧
        /// 按"桥拿不到 mmdbDir"的既有口径处理（GEOIP 规则会缺 MMDB，日志里有记录）。
        /// </param>
        private static string Fallback(string? packageDir, string reason)
        {
            string home = string.IsNullOrEmpty(packageDir) ? "" : packageDir;
            Log($"home={(home.Length == 0 ? "(空!)" : "包目录")} 退回原因={reason}" +
                " ⇒ cache.db/provider/热重载 path 这三样用不了（Docs/12 G10）");
            return home;
        }

        private static string Detail(string dst)
        {
            try { return $" size={new FileInfo(dst).Length}B"; }
            catch { return ""; }
        }

        private static void Log(string line)
        {
            if (line == _lastLogged) return;   // 常驻桥会多次走 Start，同一条不重复刷
            _lastLogged = line;
            try { RollingLog.Info("kernel-home", line); }
            catch { /* 日志通道从不反过来影响数据面 */ }
        }
    }
}
