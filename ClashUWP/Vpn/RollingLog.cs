using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// 滚动日志（rolling log）—— <b>发布版也一定有</b>的一条取证通道。
    ///
    /// 【为什么不能用现成的那几条】
    ///   1) <c>Debug.WriteLine</c> 带 <c>[Conditional("DEBUG")]</c>：Release/AOT 发布版里
    ///      这些调用<b>根本不会被编译进去</b> —— 本轮之前的真实状态就是"发布版零日志"。
    ///   2) 平台的 <c>VpnChannel.LogDiagnosticMessage</c>：要 RAS tracer / ETL 才看得见，
    ///      实测还常被平台日志清空/丢失（09 号文档记过这个坑）。
    ///   3) 各诊断文件（<c>dataface-plugin.txt</c> / <c>profile-dump.txt</c> …）都是
    ///      <b>覆盖式快照</b>，只有"最后一次"，答不了"第 3 分钟发生了什么"。
    ///   4) <c>dataface-sessions.txt</c> 是追加式，但<b>不封顶</b>（跑久了无上限地长），
    ///      而且旧实现每行都新开一个 <c>Task.Run</c> ⇒ 并发追加，行序都不保证。
    ///
    /// 【本类的口径】
    ///   ★ 调用方永不阻塞、永不抛：只做一次入队；队列满就<b>丢并计数</b>，
    ///     丢了多少由工作线程补一行 <c>[dropped=N]</c>，让"少了几行"本身可见。
    ///     数据面热路径（Encapsulate / 注入泵 / keep-alive / GetKeepAlivePayload）
    ///     <b>一律不调它</b> —— 那些地方要的是计数器，日志只记生命周期与异常。
    ///   ★ 单工作线程落盘，每批最多 64 行、最长 200 ms 强制 Flush。
    ///     进程被平台直接杀掉时的损失窗口 ≤ 200 ms（这是刻意的取舍：
    ///     换成每行同步刷盘就是把磁盘延迟挂到 VPN 线程旁边）。
    ///   ★ 按大小滚动：<c>cap × keep</c>（默认 512 KB × 3 = 每个汇最多约 1.5 MB），
    ///     到点把 <c>X → X.1 → X.2</c> 依次改名，最老的覆盖掉。
    ///     滚动失败（有读者占着文件 / 权限）只吞掉这一批，绝不反压调用方。
    ///   ★ 用 <c>System.IO</c> 直接写 <c>ApplicationData.Current.LocalFolder.Path</c>：
    ///     打包应用读写<b>自己的</b>应用数据不需要额外能力，AOT 下也不依赖反射。
    ///     代价要说明白：路径在包目录内 ⇒ 卸载 / Remove-AppItem 会连带删掉日志，
    ///     与 LocalState 里那些诊断文件同命运。
    ///
    /// 【两个汇（sink）为什么分开】
    ///   <see cref="Log"/> —— 自由文本，给<b>人</b>读：<c>logs/app.log</c>、<c>logs/plugin.log</c>。
    ///   <see cref="Sessions"/> —— <b>机器在解析</b>：<c>dataface-sessions.txt</c> 的行首被
    ///     <c>DateTime.TryParseExact("yyyy-MM-dd HH:mm:ss")</c> 按<b>定长 19 字符</b>截取，
    ///     插件 <c>SessionWatchdog</c> 与前台 <c>HealthMonitor</c> 都靠它数
    ///     <c>evt=connect-begin</c> 当"平台自动重拨"保险丝。所以这个汇的行格式
    ///     <b>一个字符都不能改</b>（加毫秒 ⇒ 解析失败 ⇒ 保险丝静默失灵）。
    ///     因此分流靠入队时的<b>显式标记</b>，不靠猜行长相。
    /// </summary>
    internal static class RollingLog
    {
        /// <summary>进程角色 —— 决定自由文本落到哪个文件。</summary>
        internal enum Role
        {
            /// <summary>没显式 <see cref="Init"/> 就先用这个：宁可单独一个文件，
            /// 也不要"没日志"被误读成"没发生"。</summary>
            Unknown = 0,
            /// <summary>前台 app 进程 ⇒ <c>logs/app.log</c>。</summary>
            App = 1,
            /// <summary>vpnClient 后台任务进程 ⇒ <c>logs/plugin.log</c>。</summary>
            Plugin = 2,
        }

        // ── 容量口径 ──
        private const long CapBytes = 512 * 1024;   // 单文件上限
        private const int KeepFiles = 3;            // 含当前文件
        private const int QueueCapacity = 4096;     // 内存队列上限（超了就丢）
        private const int BatchMax = 64;            // 每批最多几行
        private const int WakeEveryMs = 200;        // 最长多久强制刷盘一次

        private sealed record Item(string Text, bool ToSessions);

        private static readonly BlockingCollection<Item> _queue = new(QueueCapacity);
        private static readonly object _initLock = new();

        private static int _role = (int)Role.Unknown;
        private static int _started;
        private static long _dropped;
        private static string _dir = "";

        // ────────────────────────────────────────────────────────────────────
        // 起日志
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 起日志。幂等、绝不抛。<b>每个进程入口必须调一次</b>：
        /// 前台 <c>App.Main</c> 调 <see cref="Role.App"/>，插件 <c>VpnBackgroundTask.Run</c>
        /// 调 <see cref="Role.Plugin"/>。
        /// </summary>
        internal static void Init(Role role)
        {
            Interlocked.Exchange(ref _role, (int)role);
            try { EnsureStarted(); }
            catch { /* 日志起不来不能把宿主带崩 */ }
        }

        // ────────────────────────────────────────────────────────────────────
        // 写入口（全部非阻塞、全部不抛）
        // ────────────────────────────────────────────────────────────────────

        /// <summary>普通信息行。<paramref name="tag"/> 用短英文键，方便 grep。</summary>
        internal static void Info(string tag, string message) => Write('I', tag, message);

        /// <summary>可疑但不致命。</summary>
        internal static void Warn(string tag, string message) => Write('W', tag, message);

        /// <summary>
        /// 错误级、但<b>手上没有异常对象</b>的场合（判定结果为坏、收尾闸没过、自检发现残留…）。
        /// 【为什么要单独一个重载】不这么写就只能降级成 Warn，或者凭空造一个 Exception ——
        /// 前者让"grep E 级"失去意义，后者是假证据。日志的级别必须如实反映结论，
        /// 哪怕它的来源是一个返回值而不是一个 throw。
        /// </summary>
        internal static void Error(string tag, string message) => Write('E', tag, message);

        /// <summary>异常：带类型与消息。AOT 发布版里堆栈参考价值有限且很占地，故不记堆栈。</summary>
        internal static void Error(string tag, Exception ex) =>
            Write('E', tag, $"{ex.GetType().Name}: {ex.Message}");

        /// <summary>异常（自定义描述 + 异常）。</summary>
        internal static void Error(string tag, string message, Exception ex) =>
            Write('E', tag, $"{message} | {ex.GetType().Name}: {ex.Message}");

        private static void Write(char lvl, string tag, string message) =>
            Enqueue(new Item($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid={Environment.ProcessId} " +
                             $"{RoleName()} {lvl} {tag} {message}\r\n", false));

        /// <summary>
        /// 会话事件行 —— 进 <c>dataface-sessions.txt</c>，<b>行格式与旧实现逐字相同</b>：
        /// <c>yyyy-MM-dd HH:mm:ss pid=N connectCalls=N &lt;evt&gt;</c>（无毫秒，定长 19 字符时间戳）。
        /// 这个汇只允许插件进程写、前台读。改格式 = 让两处重拨保险丝静默失灵。
        /// </summary>
        internal static void Sessions(uint connectCalls, string evt) =>
            Enqueue(new Item($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} pid={Environment.ProcessId} " +
                             $"connectCalls={connectCalls} {evt}\r\n", true));

        /// <summary>
        /// 有界等待落盘。语义就是"给工作线程最多 <paramref name="waitMs"/> 把队列排空"，
        /// <b>不要</b>在平台同步回调里调（那是把磁盘延迟接进 VPN 线程）。
        /// </summary>
        internal static void Flush(int waitMs = 300)
        {
            if (Volatile.Read(ref _started) == 0) return;
            try
            {
                var deadline = DateTime.Now.AddMilliseconds(waitMs);
                while (_queue.Count > 0 && DateTime.Now < deadline)
                    Thread.Sleep(10);
            }
            catch { /* 等不到就算了 */ }
        }

        // ────────────────────────────────────────────────────────────────────
        // 读（只读，供前台 debug-cmd 的 logtail 用；读不到要说清楚而不是抛）
        // ────────────────────────────────────────────────────────────────────

        /// <summary>会话日志尾部。</summary>
        internal static string TailSessions(int lines)
        {
            try
            {
                EnsureStarted();
                return TailFile(Path.Combine(_dir, "dataface-sessions.txt"), lines);
            }
            catch (Exception ex)
            {
                return $"(读 sessions 失败 {ex.GetType().Name}: {ex.Message})";
            }
        }

        /// <summary>自由文本日志尾部。</summary>
        internal static string TailLog(Role role, int lines)
        {
            try
            {
                EnsureStarted();
                return TailFile(LogPathFor(role), lines);
            }
            catch (Exception ex)
            {
                return $"(读 {role} 日志失败 {ex.GetType().Name}: {ex.Message})";
            }
        }

        /// <summary>诊断汇总：写在哪、丢了多少、线程起没起。让"日志本身可信不可信"也有一个数。</summary>
        internal static string Diag() =>
            $"log={LogPathFor((Role)Volatile.Read(ref _role))} dropped={Interlocked.Read(ref _dropped)} " +
            $"queued={_queue.Count} started={Volatile.Read(ref _started)} cap={CapBytes / 1024}KB×{KeepFiles}";

        /// <summary>
        /// 包版本（<c>名字/主.次.构建.修订</c>）；取不到回 <c>?</c>，<b>绝不抛</b>。
        ///
        /// 【为什么放在日志类里】两个进程（前台 app、vpnClient 后台任务）都要在开头记一次
        ///   版本，写在各自入口就会有两份实现、早晚漂移。
        /// 【为什么要记】日志跨会话、跨重装累积，而本项目大量结论是"从哪一版开始出现的"
        ///   —— 没有版本号的行无法归因。非打包上下文（工具、单测）里 <c>Package.Current</c>
        ///   会抛，那正好说明这行不是包内写的，回 <c>?</c> 就够了，不值得为它失败。
        /// </summary>
        internal static string PackageVersion()
        {
            try
            {
                var p = Windows.ApplicationModel.Package.Current;
                var v = p.Id.Version;
                return $"{p.Id.Name}/{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch { return "?"; }
        }

        // ────────────────────────────────────────────────────────────────────
        // 内部
        // ────────────────────────────────────────────────────────────────────

        private static void Enqueue(Item item)
        {
            try
            {
                EnsureStarted();
                // 超时 0 ⇒ 队列满就立刻失败：宁可丢日志，绝不阻塞调用方（它可能是平台同步回调）。
                if (!_queue.TryAdd(item, 0))
                    Interlocked.Increment(ref _dropped);
            }
            catch
            {
                // BlockingCollection 完成/释放后可能抛；日志永远不是失败原因。
            }
        }

        private static void EnsureStarted()
        {
            if (Volatile.Read(ref _started) != 0) return;
            lock (_initLock)
            {
                if (_started != 0) return;

                try { _dir = Windows.Storage.ApplicationData.Current.LocalFolder.Path; }
                catch { _dir = Path.GetTempPath(); }   // 非打包上下文（工具/单测）也不抛

                try { Directory.CreateDirectory(Path.Combine(_dir, "logs")); }
                catch { /* 建目录失败：留到写的时候再兜一层 */ }

                // _started 先置起来：路径算不出来也只走这一遍，不再每次写都重跑失败路径。
                Volatile.Write(ref _started, 1);
                new Thread(WorkerLoop)
                {
                    Name = "ClashUWP-RollingLog",
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                }.Start();
            }
        }

        private static void WorkerLoop()
        {
            var sessions = new StringBuilder(32 * 1024);
            var log = new StringBuilder(32 * 1024);
            long dropsReported = 0;

            while (true)
            {
                int taken = 0;
                if (_queue.TryTake(out Item? first, WakeEveryMs) && first != null)
                {
                    Append(first);
                    taken = 1;
                }

                while (taken < BatchMax && _queue.TryTake(out Item? more) && more != null)
                {
                    Append(more);
                    taken++;
                }

                // 丢过的行数补一行，让"少了几行"本身可见。
                long drops = Interlocked.Read(ref _dropped);
                bool dropline = drops != dropsReported;
                if (dropline) dropsReported = drops;

                if (sessions.Length == 0 && log.Length == 0 && !dropline)
                    continue;

                try
                {
                    if (sessions.Length > 0)
                    {
                        AppendRotating(Path.Combine(_dir, "dataface-sessions.txt"), sessions.ToString());
                        sessions.Clear();
                    }
                    if (log.Length > 0)
                    {
                        AppendRotating(LogPathFor((Role)Volatile.Read(ref _role)), log.ToString());
                        log.Clear();
                    }
                    if (dropline)
                    {
                        // 丢日志这件事记在自由文本里（sessions 的格式不能塞非事件行）。
                        AppendRotating(LogPathFor((Role)Volatile.Read(ref _role)),
                            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid={Environment.ProcessId} " +
                            $"{RoleName()} W log-dropped [dropped={drops}]\r\n");
                    }
                }
                catch
                {
                    // 磁盘满 / 文件被占 / 权限：吞掉。日志不可用不许影响 VPN 会话。
                    sessions.Clear();
                    log.Clear();
                }
            }

            void Append(Item it)
            {
                if (it.ToSessions) sessions.Append(it.Text);
                else log.Append(it.Text);
            }
        }

        /// <summary>追加并按需滚动。★ 只在 <see cref="WorkerLoop"/> 线程里调，无并发。</summary>
        private static void AppendRotating(string path, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);

            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length + bytes.Length > CapBytes)
                Rotate(path);

            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(true);   // 到磁盘，别只到 OS 缓存：进程被杀也保得住已写的行
        }

        /// <summary>从后往前挪：X.2←X.1←X，最老的一档被覆盖。总占用恒定 = cap × keep。</summary>
        private static void Rotate(string path)
        {
            for (int i = KeepFiles - 1; i >= 1; i--)
            {
                string src = i == 1 ? path : path + "." + (i - 1);
                if (File.Exists(src))
                    File.Move(src, path + "." + i, overwrite: true);
            }
        }

        private static string LogPathFor(Role role) => Path.Combine(_dir, "logs", role switch
        {
            Role.App => "app.log",
            Role.Plugin => "plugin.log",
            _ => "unknown.log",
        });

        private static string RoleName() => ((Role)Volatile.Read(ref _role)) switch
        {
            Role.App => "app",
            Role.Plugin => "plugin",
            _ => "?",
        };

        /// <summary>
        /// 读尾部 <paramref name="lines"/> 行。★ 以 <c>FileShare.ReadWrite</c> 打开：
        /// 插件正在写也要读得到（读不到只是少看几行，写不进去才是问题）。
        /// </summary>
        private static string TailFile(string path, int lines)
        {
            if (lines <= 0) lines = 40;

            if (!File.Exists(path))
                return $"(无 {Path.GetFileName(path)})";

            var ring = new List<string>(lines * 2);
            int total = 0;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    total++;
                    ring.Add(line);
                    if (ring.Count > lines) ring.RemoveAt(0);
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine($"--- {Path.GetFileName(path)} 尾部 {ring.Count}/{total} 行 ---");
            foreach (string l in ring) sb.AppendLine(l);
            return sb.ToString();
        }
    }
}
