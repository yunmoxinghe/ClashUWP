using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

using ClashUWP.Vpn;

namespace ClashUWP.Backend
{
    /// <summary>
    /// 【只读探针】回答 Docs/12 §5 第 11 项留给 G9 的前置问题：
    /// <b>"在我们的沙箱里，能不能按连接拿到别的可执行路径？"</b>
    ///
    /// 【为什么必须实测，不能靠推理】<c>PROCESS-NAME</c> / <c>PROCESS-PATH</c> 这条规则
    ///   能不能生效，完全取决于内核那两个 Win32 调用在 AppContainer 里给不给结果：
    ///   mihomo 用的是 <c>iphlpapi!GetExtendedTcpTable / GetExtendedUdpTable</c>
    ///   （按 <c>(本地地址, 本地端口)</c> 反查 pid）+ <c>kernel32!OpenProcess</c>
    ///   （只要 <c>PROCESS_QUERY_LIMITED_INFORMATION</c>）+
    ///   <c>kernel32!QueryFullProcessImageNameW</c>（拿路径）——
    ///   逐行见我们 pin 的那版源码 <c>component/process/process_windows.go</c>
    ///   （<c>:16-20</c> 函数名与类别常量、<c>:63-101</c> <c>findProcessName</c> 的
    ///   取表+查行、<c>:200-229</c> <c>getExecPathFromPID</c> 的 OpenProcess/Syscall6）。
    ///   这三步里<b>任何一步</b>被沙箱挡住，名单就是"存得下、注得进、但永远不命中"
    ///   —— 而且不报错（查不到进程 ⇒ <c>metadata.Process</c> 是空串 ⇒ 与任何 pattern 都不相等 ⇒
    ///   规则不命中 ⇒ 走订阅原有规则）。所以必须把它变成一个<b>能看见的答案</b>，
    ///   而不是等用户报"分流没生效"。
    ///
    /// 【探针的边界，说清楚】
    ///   · 跑在<b>前台进程</b>。插件（vpnClient 后台任务）与前台同包同 family、同一套
    ///     capability，所以这是一份<b>旁证</b>，不是插件进程内的直接证据 ——
    ///     最终判据仍然是"设一条名单，看那个应用的行为变不变"。
    ///   · ★ 全程只读：不发请求、不动路由、不碰 VPN 会话、不开监听。
    ///   · ★ <b>不自动跑</b>：由设置页那个按钮点出来（口径同 <see cref="AppSettings.AutoProbe"/>
    ///     —— 凡是能产生外部可见动作的代码，都由用户明确发起）。
    /// </summary>
    internal static partial class ProcessLookupProbe
    {
        /// <summary>与 mihomo 同一个类别值：<c>process_windows.go:17 tcpTablePidConn = 4</c>。</summary>
        private const uint TcpTableOwnerPid = 4;

        private const uint AfINet = 2;
        private const uint ProcessQueryLimitedInformation = 0x1000;

        /// <summary>
        /// 跑一次探针，回一段人读的结论（同时进滚动日志）。
        /// 任何一步失败都只影响那一节的文本，不抛出去。
        /// </summary>
        internal static async Task<string> RunAsync()
        {
            // 让 UI 先画完再干活：GetProcesses() 在最坏的机器上要扫几千个句柄。
            await Task.Yield();

            var sb = new StringBuilder();
            try
            {
                sb.Append(StepEnumerate());
                sb.Append(' ');
                sb.Append(StepTcpTable());
                sb.Append(' ');
                sb.Append(StepImagePath());
            }
            catch (Exception ex)
            {
                sb.Append($"探针异常 {ex.GetType().Name}: {ex.Message}");
            }

            string report = sb.ToString();
            RollingLog.Info("appsplit-probe", report);
            return report;
        }

        // ── ① 能不能看见别的进程 ────────────────────────────────────────────
        private static string StepEnumerate()
        {
            try
            {
                Process[] all = Process.GetProcesses();
                int myPid = Environment.ProcessId;
                int others = 0;
                int withName = 0;
                var samples = new StringBuilder();
                foreach (Process p in all)
                {
                    if (p.Id == myPid) continue;
                    others++;
                    try
                    {
                        string name = p.ProcessName;
                        if (name.Length > 0)
                        {
                            withName++;
                            if (samples.Length < 60)
                            {
                                if (samples.Length > 0) samples.Append(", ");
                                samples.Append(name);
                            }
                        }
                    }
                    catch { /* 单个进程读不到名字不算枚举失败 */ }
                    finally { p.Dispose(); }
                }
                return $"枚举=OK({all.Length} 个, 其他进程 {others}, 可读到名 {withName}: {samples}…)";
            }
            catch (Exception ex)
            {
                return $"枚举=失败 {ex.GetType().Name}(err={SafeHr(ex)})";
            }
        }

        // ── ② 能不能拿到"连接→pid"那张表 ───────────────────────────────────
        private static string StepTcpTable()
        {
            // 只读表头那个 DWORD（行数），不解析行 —— 行结构的字段偏移在 x64/arm64 上不同，
            // 而这个探针要回答的只是"这个调用在沙箱里给不给结果"，不是"我们能不能解析它"。
            //   实际解析由内核自己做（它用的是自己那份 searcher，见 process_windows.go:106-178）。
            // ★ 缓冲区用 char 计长的 uint[]：4096 个 uint = 16 KB，够装几千行；
            //   不够时 API 回 122 并把需要的字节数写回第二个参数，那也算"调用可达"。
            // ★ 缓冲区直接传 <c>uint[]</c>：元素是 blittable，LibraryImport 生成的是<b>固定指针</b>
            //   （不复制、不需要 unsafe），与本包 <c>DisableRuntimeMarshalling=true</c> 的口径一致。
            uint[] buf = new uint[4096];
            uint size = (uint)(buf.Length * 4);
            uint rc = GetExtendedTcpTable(buf, ref size, 0, AfINet, TcpTableOwnerPid, 0);
            if (rc == 0)
            {
                uint rows = buf[0];
                return $"TcpTable=OK(类 {TcpTableOwnerPid}, {rows} 行, {size} B)";
            }
            if (rc == 122)   // ERROR_INSUFFICIENT_BUFFER ⇒ 调用本身通了，只是缓冲区小
                return $"TcpTable=OK(需 {size} B，本次缓冲 {buf.Length * 4} B，调用可达)";
            return $"TcpTable=失败 rc={rc} (win32 err={Marshal.GetLastPInvokeError()})";
        }

        // ── ③ 拿到 pid 之后能不能读到可执行文件路径（最关键的一步） ────────
        private static string StepImagePath()
        {
            int tried = 0, ok = 0, denied = 0;
            var firstPath = new StringBuilder();
            uint firstErr = 0;

            try
            {
                foreach (Process p in Process.GetProcesses())
                {
                    using (p)
                    {
                        // 只测 12 个：够回答"能不能"，又不至于在慢机器上卡住 UI。
                        if (tried >= 12) break;
                        tried++;

                        IntPtr h = OpenProcess(ProcessQueryLimitedInformation, 0, (uint)p.Id);
                        if (h == IntPtr.Zero)
                        {
                            denied++;
                            if (firstErr == 0) firstErr = (uint)Marshal.GetLastPInvokeError();
                            continue;
                        }
                        try
                        {
                            // 520 = 2×MAX_PATH(260)，够装普通路径。
                            // ★ 内核自己用的是 <c>syscall.MAX_LONG_PATH</c>（32768，
                            //   process_windows.go:216），所以这里<b>可能</b>比它短 ——
                            //   那是<b>探针</b>的上限，不是规则的：路径真那么长时探针会截断，
                            //   但内核那条链照样拿得到。判读时别把"探针截断"当成"读不到"。
                            var name = new char[520];
                            uint chars = (uint)name.Length;
                            if (QueryFullProcessImageName(h, 0, name, ref chars) && chars > 0)
                            {
                                ok++;
                                if (firstPath.Length == 0) firstPath.Append(new string(name, 0, (int)chars));
                            }
                            else if (firstErr == 0)
                            {
                                firstErr = (uint)Marshal.GetLastPInvokeError();
                            }
                        }
                        finally { CloseHandle(h); }
                    }
                }
            }
            catch (Exception ex)
            {
                return $"路径=异常 {ex.GetType().Name}";
            }

            string verdict = ok > 0
                ? "⇒ PROCESS-NAME/PROCESS-PATH 有戏"
                : "⇒ 读不到别人进程的路径，规则层会静默不命中";
            string deniedPart = denied > 0 ? $", OpenProcess 被拒 {denied}" +
                                            (firstErr > 0 ? $"(err {firstErr})" : "") : "";
            string sample = firstPath.Length > 0 ? $", 样例={firstPath}" : "";
            return $"路径=试 {tried} 个, 成功 {ok}{deniedPart}{sample} {verdict}";
        }

        private static string SafeHr(Exception ex) =>
            ex is COMException com ? $"0x{com.HResult:X8}" : "n/a";

        // ── P/Invoke ───────────────────────────────────────────────────────
        // ★ 四个入口全部照 mihomo 实际调用的那一组 API 写（见类注释里的源码行号），
        //   这样探针测到的就是内核将要走的那条路，不是一条"差不多"的路。
        //
        // ★★ 一律用 <see cref="LibraryImportAttribute"/> 而不是 <c>DllImport</c>：
        //   主工程开了 <c>DisableRuntimeMarshalling</c>（<c>ClashUWP.csproj:15</c>），
        //   而 <c>DllImport</c> 的 <c>SetLastError=true</c>、<c>ref</c> 参数、
        //   <c>StringBuilder</c> 参数<b>全靠运行时封送</b> —— 编译期只给 CA1420 警告，
        //   跑起来就是"AOT 下这些封送被禁止"的那一类坑（本包 AOT 发布，警告不当错误处理不行）。
        //   LibraryImport 由源生成器把封送代码写死在编译期，是这条路上的唯一正解。
        //
        // ★ BOOL 参数写 <c>int</c>、缓冲区写 <c>char[]</c>/<c>uint[]</c>：
        //   blittable 类型直接生成固定指针，不复制、不用 unsafe。
        //   参数一律裸整型，与 mihomo 的 syscall 形态一致 ——
        //   它传的就是 AF_INET=2 与 tcpTablePidConn=4（process_windows.go:64-104）。
        //   BOOL 返回值必须写明封送（SYSLIB1051 是<b>错误</b>不是警告）：
        //   <c>UnmanagedType.Bool</c> = Win32 的 4 字节 BOOL，与这两个 API 的返回类型一致。
        [LibraryImport("iphlpapi.dll", EntryPoint = "GetExtendedTcpTable", SetLastError = true)]
        private static partial uint GetExtendedTcpTable(uint[] tcpTable, ref uint tcpTableLength,
            int sort, uint afinet, uint tableClass, uint reserved);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial IntPtr OpenProcess(uint access, int inheritHandle, uint pid);

        [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool QueryFullProcessImageName(IntPtr process, uint flags,
            char[] exeName, ref uint size);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr handle);
    }
}
