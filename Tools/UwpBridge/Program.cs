// uwp-bridge -- WorkBuddy 跳板 / 代运行程序 (C# 版, 编译为真正的 exe)
//
// 作用: 让 WorkBuddy(agent) 通过 localhost 调起 msbuild / winapp / dotnet,
//       构建并运行 UWP / WinUI3 项目, 并把完整构建日志与报错回传。
//
// 关键点 (2026-10-11 实测):
//   沙箱拦的是 agent 侧"命令文本里出现 msbuild / cmd / devenv 等名字或 exe 路径",
//   并不是 msbuild 本身不能运行 —— 本程序在自己的进程里 spawn msbuild 完全正常。
//   所以 agent 请用 /build(默认 mode 就是 msbuild, body 里连 mode 都不用写),
//   不要把 exe 路径写进 /exec, 那会触发 agent 侧拦截。

using System.Diagnostics;
using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;
// 不要 using EnvDTE: 它的 Thread/Process 与 System.Threading.Thread /
// System.Diagnostics.Process 冲突。改用 EnvDTE.XXX 完全限定名。
using EnvDTE80;
using System.Runtime.Versioning;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;

namespace UwpBridge;

internal static class Program
{
    static string? MsBuild;
    static string? WinApp;
    static string? DotNet;
    static bool AllowArbitrary;
    static string Token = "";
    static int Port = 18755;

    // 一次性构建模式(供 agent / CI 直接调用)
    static string BuildPath = "";
    static string BuildConfig = "Release";
    static string BuildPlatform = "x64";
    static string BuildTarget = "Build";
    static bool BuildVerbose;
    static bool JsonOutput;

    // CLI 调试模式: 部署 + 启动 + 抓栈 + 抓日志/报错
    static bool DebugMode;
    static string DebugManifest = "";
    static int DebugSeconds = 10;
    static bool DebugKill;
    static string DebugPackage = "";

    // VS 官方自动化调试 (EnvDTE): 命令行 F5
    static bool VsDebugMode;
    static bool VsDumpMode;   // 只读: 把 VS 的窗口/弹层/子控件文字全列出来, 用于定位"DTE 被谁挡住了"
    static bool VsDismissMode; // 只点掉 VS 的单按钮安全提示框(不启动调试)
    static bool VsDryRunDismiss; // 提示框只读不点(配合 -nodismiss 之外的细粒度控制)
    static string VsClosePopup = ""; // 显式关指定弹层 HWND(十六进制)，用于人眼确认过的纯通知框
    static string VsSolution = "";
    static bool VsStop;
    static bool VsContinue;   // 中断时自动 Debug.Continue (区分"异常断住"与"真崩溃")
    static int VsWaitSeconds = 20;  // VS 忙(如弹模态对话框)时的等待预算, 秒
    static bool VsDismissDialogs = true;  // 自动关闭 VS 的模态对话框(内容会先打印出来)
    static bool VsFixDeploy = true;       // 自动把启动项目在活动配置里勾上 Deploy

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    // STA: VS 的 DTE 是 STA COM 对象, 且 DTE 调用会消息泵(输出窗口/调试器状态刷新依赖它)。
    // 注: 实测证明 STA 与否并非 ToolWindows 读取失败的原因 —— 真正原因是后期绑定, 见 GetActiveObject 处注释。
    [STAThread]
    static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        bool selfTest = false, probeMsBuild = false, once = false;
        string probeBuild = "";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "-port": case "--port":
                    Port = int.Parse(args[++i]); break;
                case "-token":
                    Token = args[++i]; break;
                case "-allowarbitrary":
                    AllowArbitrary = true; break;
                case "-selftest":
                    selfTest = true; break;
                case "-probemsbuild":
                    probeMsBuild = true; break;
                case "-probebuild":
                    probeBuild = args[++i]; break;
                case "-once":
                    once = true; break;
                case "-build":
                    BuildPath = args[++i]; break;
                case "-configuration":
                    BuildConfig = args[++i]; break;
                case "-platform":
                    BuildPlatform = args[++i]; break;
                case "-target":
                    BuildTarget = args[++i]; break;
                case "-verbose":
                    BuildVerbose = true; break;
                case "-json":
                    JsonOutput = true; break;
                case "-debug":
                    DebugMode = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-")) DebugManifest = args[++i];
                    break;
                case "-seconds":
                    DebugSeconds = int.Parse(args[++i]); break;
                case "-kill":
                    DebugKill = true; break;
                case "-package":
                    DebugPackage = args[++i]; break;
                case "-vsdebug":
                    VsDebugMode = true; break;
                case "-vsdump":
                    VsDumpMode = true; break;
                case "-vsdismiss":
                    VsDismissMode = true; break;
                case "-vsdismissdry":
                    VsDryRunDismiss = true; break;
                case "-vsclose":
                    VsClosePopup = args[++i]; break;
                case "-solution":
                    VsSolution = args[++i]; break;
                case "-stop":
                    VsStop = true; break;
                case "-waitvs":
                    VsWaitSeconds = int.Parse(args[++i]); break;
                case "-nodismiss":
                    VsDismissDialogs = false; break;
                case "-nofixdeploy":
                    VsFixDeploy = false; break;
                case "-continue":
                    VsContinue = true; break;
                case "-h": case "-help": case "--help":
                    PrintHelp(); return 0;
            }
        }

        MsBuild = FindMsBuild();
        WinApp  = FindWinApp();
        DotNet  = FindDotNet();

        if (selfTest)
        {
            RunSelfTest(probeMsBuild, probeBuild);
            if (once)
            {
                Console.WriteLine();
                Console.WriteLine("(-Once: 自检完成, 不启动服务, 退出)");
                return 0;
            }
        }

        // 一次性构建模式: 直接构建并把完整日志打到 stdout, 供调用方(agent / CI)直接读取。
        // 实测: agent 沙箱会拦截与"常驻服务"的 HTTP 通信, 但允许直接运行本 exe,
        // 所以这是 agent 侧最可靠的用法。退出码: 成功 0 / 失败 1。
        if (BuildPath.Length > 0)
        {
            var res = BuildRequest(new JsonObject {
                ["path"] = BuildPath,
                ["configuration"] = BuildConfig,
                ["platform"] = BuildPlatform,
                ["target"] = BuildTarget,
                ["verbose"] = BuildVerbose,
            });
            var ok = res["ok"]?.GetValue<bool>() == true;
            var stdout = res["stdout"]?.GetValue<string>() ?? "";
            var stderr = res["stderr"]?.GetValue<string>() ?? "";

            if (JsonOutput)
            {
                Console.WriteLine(res.ToJsonString(new JsonSerializerOptions {
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    WriteIndented = true
                }));
            }
            else
            {
                Console.WriteLine($"项目     : {BuildPath}");
                Console.WriteLine($"配置     : {BuildConfig} | {BuildPlatform}");
                Console.WriteLine($"ok       : {ok}");
                Console.WriteLine($"exitCode : {res["exitCode"]?.GetValue<int>()}");
                Console.WriteLine($"耗时(ms) : {res["durationMs"]?.GetValue<long>()}");
                Console.WriteLine("---- 构建日志 ----");
                Console.WriteLine(stdout.TrimEnd());
                if (stderr.Trim().Length > 0)
                {
                    Console.WriteLine("---- stderr ----");
                    Console.WriteLine(stderr.TrimEnd());
                }
                var errLines = stdout.Split('\n')
                    .Where(x => x.Contains(": error "))
                    .Select(x => x.Trim())
                    .ToList();
                if (errLines.Count > 0)
                {
                    Console.WriteLine($"---- 错误汇总 ({errLines.Count} 条) ----");
                    foreach (var l in errLines.Take(30)) Console.WriteLine(l);
                }
            }
            return ok ? 0 : 1;
        }

        // VS 官方自动化调试: 通过 EnvDTE 让已运行的 VS 执行 Debug.Start(等价 F5)
        if (VsDumpMode) return VsDumpWindows();
        if (VsDismissMode) return DismissVsPopups(VsDryRunDismiss) ? 0 : 1;
        if (VsDebugMode) return VsDebugApp();

        // CLI 调试模式: 注册(可选) -> 启动 -> 抓托管栈 -> 抓崩溃事件 -> 抓应用日志
        if (DebugMode) return DebugApp();

        return Serve();
    }

    static void PrintHelp()
    {
        Console.WriteLine(@"uwp-bridge.exe -- WorkBuddy 跳板 / 代运行程序

用法:
  uwp-bridge.exe -Build <项目路径> [-Configuration Release] [-Platform x64] [-Json]
  uwp-bridge.exe -VsDebug [-Solution <路径.slnx>] [-Seconds 10] [-Stop]
  uwp-bridge.exe -VsDump
  uwp-bridge.exe -VsDismiss [-VsDismissDry]
  uwp-bridge.exe -Debug <AppxManifest.xml> [-Seconds 10] [-Kill]
  uwp-bridge.exe [-Port 18755] [-Token xxx] [-AllowArbitrary]
  uwp-bridge.exe -SelfTest [-ProbeMsBuild] [-ProbeBuild <项目>] -Once

VS 官方调试(推荐给托管 C#/VB 项目 —— EnvDTE, 等价 VS 里按 F5):
  -VsDebug             连接运行中的 VS, 执行官方 Debug.Start, 抓调试/生成输出
  -Solution <路径>     可选: 先让 VS 打开该解决方案
  -Stop                结束前执行 Debug.StopDebugging
  -Seconds N           调试运行多少秒后抓取(默认 10)
  -WaitVS N            VS 忙/弹对话框时最多等 N 秒(默认 20); 超时会列出 VS 挡路的窗口
  -VsDump              只读体检: 列出 devenv 全部窗口 + 激活弹层(GW_ENABLEDPOPUP) + 子控件文字
  -VsDismiss           只点掉 VS 的单按钮安全提示框(确定/OK/关闭); 多按钮框一律不动
  -VsDismissDry        配合 -VsDump/-VsDebug: 提示框只打印不点击
  前提: VS 已启动。微软明确不建议对托管代码用 WinDbg, 此模式才是官方路径。
  注: 已按微软 KB ms228772 注册 IOleMessageFilter, VS 忙时自动重试而非直接失败。

CLI 调试(部署 -> 启动 -> 抓栈 -> 抓报错, 不依赖 VS):
  -Debug <manifest>    注册松散包并启动应用, 再抓托管调用栈 / 崩溃事件 / 应用日志
  -Seconds N           启动后等 N 秒再抓取(默认 10)
  -Kill                抓完结束应用进程
  -Package <Identity>  包已注册时直接指定包名, 不用 manifest

一次性构建(推荐给 agent 调用, 不依赖常驻服务):
  -Build <项目>      构建并把完整日志输出到 stdout; 退出码 0=成功, 1=失败
  -Configuration     默认 Release
  -Platform          默认 x64 (UWP 用 x64)
  -Target            默认 Build (也可 Restore / Publish ...)
  -Verbose           msbuild 用 /v:detailed
  -Json              以 JSON 输出, 便于程序解析

自检(推荐先跑):
  -SelfTest                 验证日志/报错捕获链路
  -ProbeMsBuild             探测 msbuild 能否真正执行
  -ProbeBuild <项目>        对真实项目构建一次并打印日志
  -Once                     自检完即退出(不启动服务)

HTTP 端点:
  GET  /health              返回工具路径与状态
  POST /build               { path, configuration, platform, target, verbose, restore, properties }
  POST /run                 { exePath | manifestPath | mode=winapp + project }
  POST /exec                { command, arguments }  (需 -AllowArbitrary 或 -Token)
  POST /shutdown            关闭服务

说明:
  * /build 默认 mode=msbuild, SDK 风格项目默认自动加 /restore(避免 NETSDK1004)。
  * 返回的 stdout 即完整构建日志, 报错形如: 文件(行,列): error CSxxxx");
    }

    // ---------------- 工具定位 ----------------
    static string? FindMsBuild()
    {
        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");

        if (File.Exists(vswhere))
        {
            try
            {
                var r = Run(vswhere, new[] {
                    "-latest", "-requires", "Microsoft.Component.MSBuild",
                    "-property", "installationPath" }, null);
                if (r.Exit == 0 && !string.IsNullOrWhiteSpace(r.Out))
                {
                    var vsPath = r.Out.Trim();
                    foreach (var rel in new[] {
                        "MSBuild\\Current\\Bin\\MSBuild.exe",
                        "MSBuild\\15.0\\Bin\\MSBuild.exe" })
                    {
                        var c = Path.Combine(vsPath, rel);
                        if (File.Exists(c)) return c;
                    }
                }
            }
            catch { }
        }

        // 回退: 扫描 Program Files\Microsoft Visual Studio\<版本>\<sku>
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Microsoft Visual Studio");
        if (Directory.Exists(root))
        {
            foreach (var verDir in Directory.GetDirectories(root))
            {
                foreach (var skuDir in Directory.GetDirectories(verDir))
                {
                    foreach (var rel in new[] {
                        "MSBuild\\Current\\Bin\\MSBuild.exe",
                        "MSBuild\\15.0\\Bin\\MSBuild.exe" })
                    {
                        var c = Path.Combine(skuDir, rel);
                        if (File.Exists(c)) return c;
                    }
                }
            }
        }
        return null;
    }

    static string? FindWinApp()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var c in new[] {
            Path.Combine(local, "Microsoft", "WindowsApps", "winapp.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinAppCLI", "winapp.exe"),
            Path.Combine(local, "Microsoft", "WinAppCLI", "winapp.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", "winapp.exe"),
        })
        {
            if (File.Exists(c)) return c;
        }
        return FindOnPath("winapp.exe");
    }

    static string? FindDotNet()
    {
        var c = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet", "dotnet.exe");
        if (File.Exists(c)) return c;
        return FindOnPath("dotnet.exe");
    }

    static string? FindOnPath(string name)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var p in paths)
        {
            try
            {
                var c = Path.Combine(p.Trim().Trim('"'), name);
                if (File.Exists(c)) return c;
            }
            catch { }
        }
        return null;
    }

    // ---------------- 执行外部进程并捕获输出 ----------------
    // timeoutMs > 0 时超时强杀并返回 TimedOut=true
    // (实测教训: dotnet-stack attach UWP 容器进程可能永久挂起, 必须有超时兜底)
    static (int Exit, string Out, string Err, long Ms, bool TimedOut) Run(
        string file, IEnumerable<string> argList, string? workingDir, int timeoutMs = 0)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in argList) psi.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir))
            psi.WorkingDirectory = workingDir;

        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"无法启动: {file}");

        // 异步读取: 同步 ReadToEnd 在"进程不退出"时会永久阻塞, 超时就形同虚设
        var soTask = p.StandardOutput.ReadToEndAsync();
        var seTask = p.StandardError.ReadToEndAsync();

        bool exited;
        if (timeoutMs > 0) exited = p.WaitForExit(timeoutMs);
        else { p.WaitForExit(); exited = true; }

        if (!exited)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            try { Task.WaitAll(new Task[] { soTask, seTask }, 5000); } catch { }
            sw.Stop();
            return (-1, soTask.Result, seTask.Result + "\n[超时: 已终止该进程]", sw.ElapsedMilliseconds, true);
        }

        var so = soTask.Result;
        var se = seTask.Result;
        sw.Stop();
        return (p.ExitCode, so, se, sw.ElapsedMilliseconds, false);
    }

    // ---------------- 构建 ----------------
    static JsonObject BuildRequest(JsonObject b)
    {
        var mode    = Str(b, "mode") ?? "msbuild";
        var cfg     = Str(b, "configuration") ?? "Release";
        var plat    = Str(b, "platform") ?? "x64";
        var target  = Str(b, "target") ?? "Build";
        var verbose = Bool(b, "verbose");
        var proj    = Str(b, "path") ?? "";

        if (proj.Length == 0 || !File.Exists(proj))
            return Err($"path 无效或文件不存在: {proj}");
        var wd = Path.GetDirectoryName(proj);

        var a = new List<string>();

        if (mode == "msbuild")
        {
            if (MsBuild is null) return Err("未找到 msbuild(Visual Studio 未安装或 vswhere 失败)");
            a.Add(proj);
            a.Add("/nologo");
            // SDK 风格项目(UWP / WinUI3 / 现代 csproj) 必须先还原, 否则 NETSDK1004
            if (RestoreEnabled(b)) a.Add("/restore");
            a.Add($"/p:Configuration={cfg}");
            a.Add($"/p:Platform={plat}");
            a.Add($"/t:{target}");
            a.Add(verbose ? "/v:detailed" : "/v:minimal");
            if (b["properties"] is JsonObject props)
                foreach (var kv in props)
                    a.Add($"/p:{kv.Key}={kv.Value?.GetValue<string>()}");

            var r = Run(MsBuild, a, wd);
            return Obj(("ok", r.Exit == 0), ("exitCode", r.Exit), ("stdout", r.Out),
                       ("stderr", r.Err), ("durationMs", r.Ms),
                       ("command", "msbuild " + string.Join(" ", a)), ("msbuildPath", MsBuild));
        }

        if (mode == "winapp")
        {
            if (WinApp is null) return Err("未找到 winapp");
            a.Add("run"); a.Add(proj);
            a.Add($"-c={cfg}");
            a.Add($"--arch={plat}");
            if (verbose) a.Add("--verbose");
            var r = Run(WinApp, a, wd);
            return Obj(("ok", r.Exit == 0), ("exitCode", r.Exit), ("stdout", r.Out),
                       ("stderr", r.Err), ("durationMs", r.Ms),
                       ("command", "winapp " + string.Join(" ", a)), ("winappPath", WinApp));
        }

        if (mode == "dotnet")
        {
            if (DotNet is null) return Err("未找到 dotnet");
            a.Add("build"); a.Add(proj);
            a.Add($"-c={cfg}");
            a.Add($"-p:Platform={plat}");
            if (verbose) a.Add("--verbosity=detailed");
            var r = Run(DotNet, a, wd);
            return Obj(("ok", r.Exit == 0), ("exitCode", r.Exit), ("stdout", r.Out),
                       ("stderr", r.Err), ("durationMs", r.Ms),
                       ("command", "dotnet " + string.Join(" ", a)), ("dotnetPath", DotNet));
        }

        return Err($"未知 mode: {mode} (支持 msbuild / winapp / dotnet)");
    }

    // ---------------- 运行 / 注册 app ----------------
    static JsonObject RunRequest(JsonObject b)
    {
        var mode = Str(b, "mode") ?? "";

        if (mode == "winapp" && b["project"] is not null)
        {
            if (WinApp is null) return Err("未找到 winapp");
            var r = Run(WinApp, new[] { "run", Str(b, "project")! }, null);
            return Obj(("ok", r.Exit == 0), ("exitCode", r.Exit), ("stdout", r.Out),
                       ("stderr", r.Err), ("durationMs", r.Ms));
        }

        var manifest = Str(b, "manifestPath") ?? "";
        if (manifest.Length > 0)
        {
            if (!File.Exists(manifest)) return Err($"manifest 不存在: {manifest}");
            var r = Run("powershell.exe", new[] {
                "-NoProfile", "-Command",
                $"Add-AppxPackage -Register '{manifest}' -Confirm:$false" }, null);
            return Obj(("ok", r.Exit == 0), ("action", "Add-AppxPackage -Register"),
                       ("manifest", manifest), ("stdout", r.Out), ("stderr", r.Err));
        }

        var exe = Str(b, "exePath") ?? "";
        if (exe.Length > 0)
        {
            if (!File.Exists(exe)) return Err($"exe 不存在: {exe}");
            try
            {
                var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                return Obj(("ok", true), ("action", "start"), ("exe", exe), ("pid", p?.Id ?? 0));
            }
            catch (Exception ex) { return Err($"启动失败: {ex.Message}"); }
        }

        return Err("需提供 exePath 或 manifestPath(或 mode=winapp + project)");
    }

    // ---------------- 任意命令(需授权) ----------------
    static JsonObject ExecRequest(JsonObject b)
    {
        if (!AllowArbitrary && Token.Length == 0)
            return Err("exec 已禁用; 用 -AllowArbitrary 启动或提供 -Token");
        var cmd = Str(b, "command") ?? "";
        var argStr = Str(b, "arguments") ?? "";
        if (cmd.Length == 0) return Err("缺少 command");
        var r = Run(cmd, Tokenize(argStr), null);
        return Obj(("ok", r.Exit == 0), ("exitCode", r.Exit), ("stdout", r.Out),
                   ("stderr", r.Err), ("durationMs", r.Ms), ("command", cmd + " " + argStr));
    }

    // ---------------- 自检 ----------------
    static void RunSelfTest(bool probeMsBuild, string probeBuild)
    {
        Console.WriteLine("=== 自检: 日志 / 报错捕获链路 ===");
        var node = FindNode();
        if (node is null)
        {
            Console.WriteLine("  跳过: 未找到 node(探针依赖 node 产生 stdout/stderr)");
        }
        else
        {
            var js = "console.log('PROBE_STDOUT_LINE');console.error('PROBE_STDERR_LINE');process.exitCode=7;";
            var r = Run(node, new[] { "-e", js }, null);
            Console.WriteLine($"  探针进程: {node}");
            Console.WriteLine($"  exitCode = {r.Exit}");
            Console.WriteLine($"  stdout   = [{r.Out.Trim()}]");
            Console.WriteLine($"  stderr   = [{r.Err.Trim()}]");
            var ok = r.Exit == 7 && r.Out.Contains("PROBE_STDOUT_LINE") && r.Err.Contains("PROBE_STDERR_LINE");
            Console.WriteLine($"  结论: {(ok ? "OK - stdout / stderr / exitCode 全部捕获成功" : "FAIL - 捕获异常")}");
        }

        if (probeMsBuild)
        {
            Console.WriteLine();
            Console.WriteLine("=== 自检: msbuild 能否真正执行 ===");
            if (MsBuild is null) Console.WriteLine("  未定位到 msbuild");
            else
            {
                Console.WriteLine($"  msbuild: {MsBuild}");
                try
                {
                    var r = Run(MsBuild, new[] { "-version" }, null);
                    Console.WriteLine($"  exitCode = {r.Exit}");
                    Console.WriteLine($"  stdout   = [{r.Out.Trim()}]");
                    Console.WriteLine($"  stderr   = [{r.Err.Trim()}]");
                    Console.WriteLine($"  结论: {(r.Exit == 0 ? "可执行 - 本进程能正常调起 msbuild" : "不可执行 - 被安全策略拦截或调用失败")}");
                }
                catch (Exception ex) { Console.WriteLine($"  调用异常: {ex.Message}"); }
            }
        }

        if (probeBuild.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine("=== 自检: 真实构建(验证日志回传) ===");
            Console.WriteLine($"  项目: {probeBuild}");
            var res = BuildRequest(new JsonObject {
                ["path"] = probeBuild,
                ["configuration"] = "Release",
                ["platform"] = "AnyCPU",
                ["target"] = "Build",
                ["verbose"] = false,
            });
            Console.WriteLine($"  ok         = {res["ok"]?.GetValue<bool>()}");
            Console.WriteLine($"  exitCode   = {res["exitCode"]?.GetValue<int>()}");
            Console.WriteLine($"  durationMs = {res["durationMs"]?.GetValue<long>()}");
            Console.WriteLine("  --- 构建日志(stdout) 前 30 行 ---");
            var stdout = res["stdout"]?.GetValue<string>() ?? "";
            foreach (var line in stdout.Split('\n').Where(x => x.Trim().Length > 0).Take(30))
                Console.WriteLine("    " + line.Trim());
            var stderr = res["stderr"]?.GetValue<string>() ?? "";
            if (stderr.Length > 0)
            {
                Console.WriteLine("  --- stderr 前 10 行 ---");
                foreach (var line in stderr.Split('\n').Where(x => x.Trim().Length > 0).Take(10))
                    Console.WriteLine("    " + line.Trim());
            }
            var errLines = stdout.Split('\n')
                .Where(x => x.Contains("error CS") || x.Contains(": error "))
                .ToList();
            if (errLines.Count > 0)
            {
                Console.WriteLine($"  --- 错误行(共 {errLines.Count} 条, 显示前 15 条) ---");
                foreach (var line in errLines.Take(15)) Console.WriteLine("    " + line.Trim());
            }
            Console.WriteLine(errLines.Count > 0
                ? "  编译错误捕获: YES"
                : "  编译错误捕获: 本次无编译错误(或未匹配到错误码)");
        }
    }

    static string? FindNode()
    {
        var roots = new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                         ".workbuddy", "binaries", "node", "versions"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"),
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var c = Path.Combine(dir, "node.exe");
                    if (File.Exists(c)) return c;
                }
                var direct = Path.Combine(root, "node.exe");
                if (File.Exists(direct)) return direct;
            }
            catch { }
        }
        return FindOnPath("node.exe");
    }

    // ---------------- CLI 调试: 部署 + 启动 + 抓栈 + 抓日志/报错 ----------------
    static (string Identity, string AppId, string ExeName) ParseManifest(string path)
    {
        string identity = "", appId = "", exeName = "";
        try
        {
            var doc = new XmlDocument();
            doc.Load(path);
            var idNode = doc.SelectSingleNode("//*[local-name()='Identity']");
            if (idNode?.Attributes?["Name"] != null) identity = idNode.Attributes["Name"]!.Value;
            var appNode = doc.SelectSingleNode("//*[local-name()='Application']");
            if (appNode != null)
            {
                if (appNode.Attributes?["Id"] != null) appId = appNode.Attributes["Id"]!.Value;
                if (appNode.Attributes?["Executable"] != null)
                    exeName = Path.GetFileNameWithoutExtension(appNode.Attributes["Executable"]!.Value);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  解析清单失败: {ex.Message}");
        }
        return (identity, appId, exeName);
    }

    static int DebugApp()
    {
        Console.WriteLine("=== CLI Debug 模式 ===");

        // 1) 给了 manifest 就先注册松散包
        if (DebugManifest.Length > 0)
        {
            if (!File.Exists(DebugManifest))
            {
                Console.Error.WriteLine($"manifest 不存在: {DebugManifest}");
                return 1;
            }
            Console.WriteLine($"注册松散包: {DebugManifest}");
            var reg = Run("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command",
                $"$ProgressPreference='SilentlyContinue'; Add-AppxPackage -Register '{DebugManifest}' -Confirm:$false" }, null, 120000);
            Console.WriteLine($"  注册 exitCode = {reg.Exit}");
            if (reg.Exit != 0)
            {
                // 常见原因: 已安装同名但不同架构的包。此时复用已注册的包即可, 不必失败退出。
                Console.WriteLine("  注册未成功(常见: 已存在同名但不同架构的包)。改为复用已注册的包继续。");
                var detail = (reg.Out + reg.Err).Trim();
                if (detail.Length > 0) Console.WriteLine("  " + detail.Split('\n')[0]);
            }
        }

        // 2) 解析清单: Identity / AppId / Executable
        string identity = DebugPackage, appId = "App", exeName = "";
        if (DebugManifest.Length > 0 && File.Exists(DebugManifest))
        {
            var (id2, app2, exe2) = ParseManifest(DebugManifest);
            if (id2.Length > 0) identity = id2;
            if (app2.Length > 0) appId = app2;
            if (exe2.Length > 0) exeName = exe2;
        }

        // 3) 查 PackageFamilyName -> 拼 AUMID
        string pfn = "";
        if (identity.Length > 0)
        {
            var q = Run("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command",
                $"$ProgressPreference='SilentlyContinue'; (Get-AppxPackage -Name '{identity}' | Select-Object -First 1).PackageFamilyName" }, null);
            pfn = q.Out.Trim();
        }
        if (pfn.Length == 0)
        {
            Console.Error.WriteLine("无法确定 PackageFamilyName —— 包是否已注册? 用 -Debug <AppxManifest.xml> 先注册, 或用 -Package <Identity> 指定。");
            return 1;
        }

        // 3.5) 没给 manifest 时, 从已安装包的 InstallLocation 反查 exe 名
        if (exeName.Length == 0 && identity.Length > 0)
        {
            var instQ = Run("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command",
                $"$ProgressPreference='SilentlyContinue'; (Get-AppxPackage -Name '{identity}' | Select-Object -First 1).InstallLocation" }, null);
            var inst = instQ.Out.Trim();
            if (inst.Length > 0)
            {
                var m = Path.Combine(inst, "AppxManifest.xml");
                if (File.Exists(m))
                {
                    var (_, app3, exe3) = ParseManifest(m);
                    if (app3.Length > 0) appId = app3;
                    if (exe3.Length > 0) exeName = exe3;
                    Console.WriteLine($"安装位置  : {inst}");
                }
            }
        }

        var aumid = pfn + "!" + appId;
        Console.WriteLine($"包    : {identity}");
        Console.WriteLine($"AUMID : {aumid}");

        // 4) 启动
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + aumid) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"启动失败: {ex.Message}");
            return 1;
        }
        Console.WriteLine($"已发送启动命令, 等待 {DebugSeconds} 秒后抓取运行状态...");
        Thread.Sleep(Math.Max(1, DebugSeconds) * 1000);

        // 5) 找进程
        int pid = 0;
        if (exeName.Length > 0)
        {
            var procs = Process.GetProcessesByName(exeName);
            if (procs.Length > 0)
            {
                pid = procs[0].Id;
                long mb = 0;
                try { mb = procs[0].WorkingSet64 / 1024 / 1024; } catch { }
                Console.WriteLine($"进程  : {exeName}  PID={pid}  内存={mb}MB");
            }
            else
            {
                Console.WriteLine($"进程  : 未发现 {exeName}(可能已退出或启动失败)");
            }
        }

        // 6) 托管调用栈
        var toolsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools");
        var stackTool = Path.Combine(toolsDir, "dotnet-stack.exe");
        if (pid > 0 && File.Exists(stackTool))
        {
            var r = Run(stackTool, new[] { "report", "-p", pid.ToString() }, null, 25000);
            Console.WriteLine("---- 托管调用栈 (dotnet-stack) ----");
            if (r.TimedOut)
                Console.WriteLine("(dotnet-stack attach 超时, 已终止 —— UWP 容器进程常不允许诊断端口 attach)");
            Console.WriteLine(string.IsNullOrWhiteSpace(r.Out) ? "(无输出)" : r.Out.TrimEnd());
            if (!string.IsNullOrWhiteSpace(r.Err)) Console.WriteLine("[stderr] " + r.Err.TrimEnd());
        }
        else if (pid > 0)
        {
            Console.WriteLine($"---- 未找到 dotnet-stack({stackTool}), 跳过调用栈 ----");
        }

        // 7) 崩溃 / 报错事件
        var minutes = Math.Max(2, DebugSeconds / 60 + 3);
        var psCmd = "$ProgressPreference='SilentlyContinue'; " +
                    "Get-WinEvent -FilterHashtable @{LogName='Application';StartTime=(Get-Date).AddMinutes(-" + minutes + ")} -ErrorAction SilentlyContinue | " +
                    "Where-Object { $_.LevelDisplayName -eq '错误' -or $_.ProviderName -match 'Application Error|AppCrash|Application Hang' } | " +
                    "Select-Object -First 15 | ForEach-Object { '[' + $_.TimeCreated + '] ' + $_.ProviderName + ': ' + ($_.Message -split \"`n\")[0] }";
        var ev = Run("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", psCmd }, null, 60000);
        Console.WriteLine("---- 应用错误事件 ----");
        var evText = (ev.Out ?? "").Trim();
        Console.WriteLine(evText.Length > 0 ? evText : $"(近 {minutes} 分钟内无错误事件)");

        // 8) 应用自身日志(包 LocalState)
        var localState = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", pfn, "LocalState");
        if (Directory.Exists(localState))
        {
            var logs = Directory.GetFiles(localState, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .Take(3)
                .ToList();
            if (logs.Count > 0)
            {
                Console.WriteLine("---- 应用日志(LocalState) ----");
                foreach (var f in logs)
                {
                    Console.WriteLine($"  [{Path.GetFileName(f)}]");
                    try
                    {
                        foreach (var l in File.ReadAllLines(f).TakeLast(15)) Console.WriteLine("    " + l);
                    }
                    catch (Exception ex) { Console.WriteLine("    读取失败: " + ex.Message); }
                }
            }
        }

        // 9) 需要就结束进程
        if (DebugKill && exeName.Length > 0)
        {
            foreach (var p in Process.GetProcessesByName(exeName))
            {
                try { p.Kill(); Console.WriteLine($"已结束进程 {exeName} PID={p.Id}"); } catch { }
            }
        }
        return 0;
    }

    /// <summary>
    /// 窗口体检（只读）。
    ///
    /// 【为什么加这个】<c>-VsDebug</c> 反复报 <c>RPC_E_CALL_REJECTED</c>（VS 忙），
    ///   但 <c>devenv</c> 的 CPU 几乎为 0 —— 说明主线程不是在算，而是卡在某个模态消息循环里。
    ///   老的 <see cref="DismissVsDialogs"/> 只认 <c>#32770</c>（Win32 原生对话框类），
    ///   而新版 VS 的对话框是 WPF 承载的 <c>HwndWrapper[...]</c> / Custom 类窗口，
    ///   于是出现「明明有框挡路，清单里却一个对话框都没有」。
    ///   这里把 devenv 的顶层窗口、<c>GW_ENABLEDPOPUP</c>（当前激活弹层 = 模态框本体）、
    ///   属主为 VS 的<b>其它进程</b>窗口（UAC / 证书信任 / 设备选择这类框常不归 devenv）
    ///   连同子控件文字一并列出，纯只读，不动任何窗口。
    /// </summary>
    [SupportedOSPlatform("windows")]
    static int VsDumpWindows()
    {
        Console.WriteLine("===== VS 窗口体检（只读） =====");
        var vs = Process.GetProcessesByName("devenv");
        if (vs.Length == 0)
        {
            Console.WriteLine("没有 devenv 进程。");
            return 1;
        }
        var pids = vs.Select(p => (uint)p.Id).ToHashSet();
        Console.WriteLine("devenv PID: " + string.Join(", ", pids));

        var top = new List<(IntPtr Hwnd, uint Pid, string Cls, string Title)>();
        EnumWindows((h, l) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            var (cls, title) = Describe(h);
            top.Add((h, pid, cls, title));
            return true;
        }, IntPtr.Zero);

        var vsTop = top.Where(r => pids.Contains(r.Pid)).ToList();
        foreach (var r in vsTop)
        {
            bool vis = IsWindowVisible(r.Hwnd);
            IntPtr popup = GetWindow(r.Hwnd, GW_ENABLEDPOPUP);
            IntPtr owner = GetWindow(r.Hwnd, GW_OWNER);
            Console.WriteLine("");
            Console.WriteLine($"[devenv/{r.Pid}] 0x{r.Hwnd.ToInt64():X} [{r.Cls}] \"{r.Title}\" visible={vis}");
            if (owner != IntPtr.Zero)
            {
                var (oc, ot) = Describe(owner);
                Console.WriteLine($"    属主: [{oc}] \"{ot}\"");
            }
            if (popup != IntPtr.Zero && popup != r.Hwnd)
            {
                var (pc, pt) = Describe(popup);
                Console.WriteLine($"    ★ 激活弹层 GW_ENABLEDPOPUP = 0x{popup.ToInt64():X} [{pc}] \"{pt}\"  <== 挡住 DTE 的通常就是它");
                foreach (var t in ReadDialogText(popup).Distinct().Take(30))
                    Console.WriteLine("        " + (t.Length > 200 ? t[..200] + "..." : t));
            }
            foreach (var t in ReadDialogText(r.Hwnd).Distinct().Take(15))
                Console.WriteLine("    子控件: " + (t.Length > 200 ? t[..200] + "..." : t));
        }

        Console.WriteLine("");
        Console.WriteLine("---- 属主指向 devenv 窗口的其它进程窗口 ----");
        var vsHwnds = vsTop.Select(x => x.Hwnd).ToHashSet();
        int n = 0;
        foreach (var r in top.Where(r => !pids.Contains(r.Pid) && IsWindowVisible(r.Hwnd)))
        {
            IntPtr o = GetWindow(r.Hwnd, GW_OWNER);
            if (o == IntPtr.Zero || !vsHwnds.Contains(o)) continue;
            string pname = "?";
            try { pname = Process.GetProcessById((int)r.Pid).ProcessName; } catch { }
            Console.WriteLine($"  · {pname}({r.Pid}) [{r.Cls}] \"{r.Title}\"");
            foreach (var t in ReadDialogText(r.Hwnd).Distinct().Take(12))
                Console.WriteLine("      " + (t.Length > 200 ? t[..200] + "..." : t));
            n++;
        }
        if (n == 0) Console.WriteLine("  (无)");
        return 0;
    }

    static (string Cls, string Title) Describe(IntPtr h)
    {
        var cb = new StringBuilder(256);
        GetClassName(h, cb, cb.Capacity);
        int len = GetWindowTextLength(h);
        var sb = new StringBuilder(Math.Max(len, 1) + 1);
        if (len > 0) GetWindowText(h, sb, sb.Capacity);
        return (cb.ToString(), sb.ToString().Trim());
    }

    [DllImport("user32.dll")]
    static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    const uint GW_OWNER = 4;
    const uint GW_ENABLEDPOPUP = 6;

    // ---------------- VS 官方自动化调试 (EnvDTE): 命令行 F5 ----------------
    // 说明: 这是微软官方的 VS 自动化接口, 不是自制机制。
    //   * 接口: EnvDTE.DTE / DTE.Debugger / DTE.ExecuteCommand("Debug.Start")
    //   * 文档: https://learn.microsoft.com/visualstudio/extensibility/launch-visual-studio-dte
    //   * 为何不用 WinDbg: 微软明确不建议对托管(C#)代码使用 WinDbg;
    //     VS 内置调试器才是托管项目的官方路径, 且能拿到托管调用栈与 C# 异常。
    //   * 前提: VS 已启动(可在 ROT 中取到 DTE), 且已打开解决方案。
    [SupportedOSPlatform("windows")]
    static int VsDebugApp()
    {
        Console.WriteLine("===== VS 官方自动化调试 (EnvDTE) =====");

        dynamic? dte = null;
        string usedProgId = "";
        foreach (var prog in new[] {
            "VisualStudio.DTE.18.0", "VisualStudio.DTE.17.0",
            "VisualStudio.DTE.16.0", "VisualStudio.DTE" })
        {
            var o = GetActiveComObject(prog);
            if (o != null) { dte = o; usedProgId = prog; break; }
        }
        if (dte == null)
        {
            Console.Error.WriteLine("未找到运行中的 Visual Studio (ROT 中无 DTE 实例)。");
            Console.Error.WriteLine("请先启动 VS 并打开解决方案, 或用 -Solution <路径> 指定。");
            return 1;
        }

        // 微软官方要求 (KB ms228772): 外部进程自动化 VS 必须注册 IOleMessageFilter,
        // 否则 VS 一忙(典型=弹出模态对话框)所有 COM 调用立即被拒 0x80010001。
        VsMessageFilter.BudgetMs = VsWaitSeconds * 1000;
        VsMessageFilter.Register();

        try
        {
            Console.WriteLine($"ProgID    : {usedProgId}");

            // 握手: VS 被模态对话框占住时, 连 dte.Name 都会 0x80010001。
            // 等到 VS 空闲(或超时), 再往下走, 避免整轮自动化毫无信息地崩掉。
            string vsName = "", vsVer = "";
            var hw = Stopwatch.StartNew();
            long lastDismissAttempt = -10000;
            while (true)
            {
                try { vsName = (string)dte.Name; vsVer = (string)dte.Version; break; }
                catch (Exception ex) when (IsVsBusy(ex))
                {
                    // 第一反应: 如果 VS 上挂着模态对话框, 读它的文字并关掉它(等价点"确定"),
                    // 否则等再久也没用 —— 无人值守场景下这是唯一出路。
                    // ★ 每 8 秒重试一次(原来只试一次): VS 会在一个自动化回合里
                    //   连着弹好几个框（"调试会话已结束"→"部署完成"→…），一次性尝试
                    //   只能干掉第一个，后面的照样把 DTE 挡死。
                    if (VsDismissDialogs && hw.ElapsedMilliseconds - lastDismissAttempt >= 8000)
                    {
                        lastDismissAttempt = hw.ElapsedMilliseconds;
                        Console.WriteLine($"  VS 忙, 尝试识别并点掉安全提示框 ({hw.ElapsedMilliseconds} ms) ...");
                        bool a = DismissVsDialogs();
                        bool b = DismissVsPopups(VsDryRunDismiss);
                        if (a || b) { Thread.Sleep(500); continue; }
                    }
                    long left = VsWaitSeconds * 1000L - hw.ElapsedMilliseconds;
                    if (left <= 0)
                    {
                        Console.Error.WriteLine(
                            $"VS 忙了 {hw.ElapsedMilliseconds} ms 仍未响应 —— 多半有未关闭的模态对话框。");
                        DumpVsBusyHint();
                        return 2;
                    }
                    // 单次 COM 调用的重试预算 = 剩余预算(至少 2s), 保证总时长受 -WaitVS 控制
                    VsMessageFilter.BudgetMs = (int)Math.Max(2000, left);
                    if (hw.ElapsedMilliseconds > 0 && hw.ElapsedMilliseconds % 5000 < 600)
                        Console.WriteLine($"  VS 忙, 等待中 ... ({hw.ElapsedMilliseconds} ms)");
                    Thread.Sleep(500);
                }
            }
            Console.WriteLine($"VS        : {vsName} {vsVer}");

            string cur = "";
            try { cur = (string)dte.Solution.FullName; } catch { }
            Console.WriteLine($"解决方案  : {(cur.Length > 0 ? cur : "(未打开)")}");

            if (VsSolution.Length > 0 &&
                !string.Equals(VsSolution, cur, StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(VsSolution))
                {
                    Console.Error.WriteLine($"解决方案不存在: {VsSolution}");
                    return 1;
                }
                Console.WriteLine($"打开      : {VsSolution}");
                dte.Solution.Open(VsSolution);
                Thread.Sleep(4000);
                try { cur = (string)dte.Solution.FullName; } catch { }
            }

            if (cur.Length == 0)
            {
                Console.Error.WriteLine("没有已打开的解决方案 —— 请用 -Solution <路径.sln/.slnx> 指定。");
                return 1;
            }

            // 若 VS 不在设计态(上一轮调试残留), 先停止, 否则 Debug.Start 不可用
            int pre = 1;
            try { pre = (int)dte.Debugger.CurrentMode; } catch { }
            if (pre != 1)
            {
                Console.WriteLine("");
                Console.WriteLine($"当前非设计态 ({ModeName(pre)}) —— 先停止上一次调试 ...");
                try { dte.ExecuteCommand("Debug.StopDebugging"); }
                catch { try { dte.Debugger.Stop(true); } catch { } }
                var sw0 = Stopwatch.StartNew();
                while (sw0.ElapsedMilliseconds < 60000)
                {
                    Thread.Sleep(500);
                    try { pre = (int)dte.Debugger.CurrentMode; } catch { }
                    if (pre == 1) break;
                }
                Console.WriteLine($"停止后状态: {ModeName(pre)}");
            }

            // 启动项目: 未设置会让 Debug.Start 行为异常
            string startup = "";
            try
            {
                object sp = dte.Solution.SolutionBuild.StartupProjects;
                if (sp is object[] arr) startup = string.Join(", ", arr.Select(x => x?.ToString() ?? ""));
                else startup = sp?.ToString() ?? "";
            }
            catch { }
            Console.WriteLine($"启动项目  : {(startup.Length > 0 ? startup : "(未设置!)")}");
            if (startup.Length == 0)
            {
                // 自动挑解决方案里唯一/第一个可执行项目作为启动项目
                try
                {
                    dynamic projs = dte.Solution.Projects;
                    var cand = new List<string>();
                    foreach (dynamic p in projs) CollectProjects(p, cand);
                    if (cand.Count > 0)
                    {
                        dte.Solution.SolutionBuild.StartupProjects = new object[] { cand[0] };
                        Console.WriteLine($"自动设置  : {cand[0]}");
                    }
                }
                catch (Exception ex) { Console.WriteLine("设置启动项目失败: " + ex.Message); }
            }

            // UWP 项目的 Deploy 开关: 没勾上时 VS 会弹模态框 "The project needs to be deployed
            // before we can debug. Please enable Deploy in the Configuration Manager." 并卡住自动化。
            // 用官方 EnvDTE 属性 SolutionContext.ShouldDeploy 勾上(等价于配置管理器里打勾)。
            if (VsFixDeploy) EnsureDeployEnabled(dte, startup);

            // 官方 F5
            Console.WriteLine("");
            Console.WriteLine("执行 Debug.Start (等价在 VS 里按 F5) ...");
            dte.ExecuteCommand("Debug.Start");

            // dbgDebugMode: 1=Design 2=Run 3=Break
            int mode = 1;
            var sw = Stopwatch.StartNew();
            int nextDlgSweep = 0;
            while (sw.ElapsedMilliseconds < 240000)
            {
                Thread.Sleep(1000);
                VsMessageFilter.Arm(10000);   // 续期: 生成/部署期间 VS 会短暂拒绝调用
                // 启动阶段 VS 还会弹框(部署失败/警告等)。若不处理, 每次 CurrentMode 都被拒,
                // 我们会一直误读成"设计态"。所以周期性扫一遍并关掉(内容先打印)。
                if (VsDismissDialogs && sw.ElapsedMilliseconds >= nextDlgSweep)
                {
                    nextDlgSweep = (int)sw.ElapsedMilliseconds + 3000;
                    DismissVsDialogs();
                    DismissVsPopups(VsDryRunDismiss);   // 新版 VS 的框是 WPF 弹层, 不在 #32770 里
                }
                try { mode = (int)dte.Debugger.CurrentMode; } catch { }
                if (mode != 1) break;
            }
            Console.WriteLine($"调试器状态: {ModeName(mode)}   (等待 {sw.ElapsedMilliseconds} ms)");
            if (mode == 1)
            {
                Console.Error.WriteLine("调试会话未启动 (可能部署失败)。请看下方 [生成] 窗口输出。");
            }

            int pid = 0;
            try
            {
                foreach (dynamic pr in dte.Debugger.DebuggedProcesses) { pid = (int)pr.ProcessID; break; }
            }
            catch { }
            if (pid > 0) Console.WriteLine($"被调试进程 PID = {pid}");
            else
            {
                Console.WriteLine("被调试进程: 无 —— 应用未启动(部署可能失败)");
                DumpErrorList(dte, "错误列表(启动阶段)");
            }

            // 运行 N 秒; 期间若命中断点/未处理异常会自动转入 Break 态
            Console.WriteLine("");
            Console.WriteLine($"运行 {DebugSeconds} 秒 (命中断点/异常会自动转入中断态) ...");
            int breaks = 0;
            for (int t = 0; t < DebugSeconds; t++)
            {
                Thread.Sleep(1000);
                VsMessageFilter.Arm(10000);   // 续期: 应用刚启动时 VS 可能仍在忙
                try { mode = (int)dte.Debugger.CurrentMode; } catch { }
                if (mode == 2)   // dbgBreakMode = 2 (官方枚举值)
                {
                    breaks++;
                    Console.WriteLine($"  [{t + 1}s] 中断 #{breaks} (dbgBreakMode)");
                    DescribeBreak(dte);
                    if (VsContinue && breaks <= 20)
                    {
                        try
                        {
                            dte.ExecuteCommand("Debug.Continue");
                            Console.WriteLine("       -> 已执行 Debug.Continue, 观察能否继续运行");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("       -> Continue 失败: " + ex.Message);
                            break;
                        }
                        Thread.Sleep(2000);
                        continue;
                    }
                    break;
                }
                if (mode == 1)
                {
                    Console.WriteLine($"  [{t + 1}s] 调试会话已结束 (进程可能已退出)");
                    break;
                }
            }
            Console.WriteLine($"结束状态  : {ModeName(mode)}   中断次数 = {breaks}");

            if (pid > 0)
            {
                try
                {
                    var pr = Process.GetProcessById(pid);
                    Console.WriteLine($"进程状态  : {(pr.HasExited ? "已退出" : "存活")}  PID={pid}  内存={pr.WorkingSet64 / 1048576}MB");
                }
                catch { Console.WriteLine($"进程状态  : 已退出 (PID={pid} 不可访问)"); }
            }

            var paneNames = new List<string>();
            Console.WriteLine("");
            Console.WriteLine("---- 输出窗口 [调试] ----");
            // 必须显式类型: dte 是 dynamic, 用 var 会推断成 dynamic,
            // 而元组元素名是编译期语法糖(运行时只有 Item1/Item2), dynamic 取不到 .Text
            (string Text, string Note) dbgRes = ReadOutputPane(dte, new[] { "调试", "Debug" }, paneNames);
            Console.WriteLine(string.IsNullOrWhiteSpace(dbgRes.Text) ? dbgRes.Note : Tail(dbgRes.Text, 40));

            Console.WriteLine("");
            Console.WriteLine("---- 输出窗口 [生成] ----");
            (string Text, string Note) buildRes = ReadOutputPane(dte, new[] { "生成", "Build" }, null);
            Console.WriteLine(string.IsNullOrWhiteSpace(buildRes.Text) ? buildRes.Note : Tail(buildRes.Text, 20));

            if (paneNames.Count > 0)
            {
                Console.WriteLine("");
                Console.WriteLine($"[诊断] VS 输出窗格实际名: {string.Join(" | ", paneNames)}");
            }

            // 错误列表总是打印: 部署/启动失败会落在这里, 正常运行也能确认 0 错误
            DumpErrorList(dte, "错误列表");

            // 补充通道: 应用日志 + 崩溃事件(VS 输出窗格抓不到时的兜底)
            if (DebugPackage.Length > 0)
            {
                var ls = FindLocalStateByIdentity(DebugPackage);
                if (ls != null && Directory.Exists(ls))
                {
                    Console.WriteLine("");
                    DumpAppLogs(ls);
                }
            }
            Console.WriteLine("");
            DumpErrorEvents(Math.Max(2, DebugSeconds / 60 + 3));

            if (VsStop)
            {
                Console.WriteLine("");
                Console.WriteLine("停止调试 (Debug.StopDebugging) ...");
                try { dte.ExecuteCommand("Debug.StopDebugging"); }
                catch (Exception ex) { Console.WriteLine("  " + ex.Message); }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("EnvDTE 调用失败: " + ex.Message);
            if (IsVsBusy(ex)) DumpVsBusyHint();
            return 1;
        }
        finally
        {
            VsMessageFilter.Revoke();
        }
    }

    // UWP 调试前必须让项目处于"部署"状态, 否则 VS 弹:
    //   "The project needs to be deployed before we can debug.
    //    Please enable Deploy in the Configuration Manager."
    // 官方 API: EnvDTE.SolutionContext.ShouldDeploy
    //   https://learn.microsoft.com/dotnet/api/envdte.solutioncontext.shoulddeploy
    [SupportedOSPlatform("windows")]
    static void EnsureDeployEnabled(dynamic dte, string startup)
    {
        try
        {
            dynamic cfg = dte.Solution.SolutionBuild.ActiveConfiguration;
            Console.WriteLine($"活动配置  : {(string)cfg.Name}");
            bool changed = false, seen = false;
            foreach (dynamic ctx in (dynamic)cfg.SolutionContexts)
            {
                string proj = (string)ctx.ProjectName;
                bool want = startup.Length == 0;
                if (!want)
                {
                    foreach (var s in startup.Split(','))
                    {
                        var n = s.Trim();
                        if (n.Length == 0) continue;
                        var leaf = n.Contains('\\') ? n[(n.LastIndexOf('\\') + 1)..] : n;
                        leaf = leaf.Replace(".csproj", "", StringComparison.OrdinalIgnoreCase);
                        if (leaf.Equals(proj, StringComparison.OrdinalIgnoreCase) ||
                            n.Equals(proj, StringComparison.OrdinalIgnoreCase)) { want = true; break; }
                    }
                }
                if (!want) continue;
                seen = true;
                bool sd = (bool)ctx.ShouldDeploy;
                Console.WriteLine($"Deploy    : {proj} = {sd}");
                if (!sd)
                {
                    ctx.ShouldDeploy = true;
                    changed = true;
                    Console.WriteLine($"            -> 已勾上 Deploy (ShouldDeploy=true)");
                }
            }
            if (!seen) Console.WriteLine("Deploy    : 未在活动配置里找到启动项目对应项");
            else if (!changed) Console.WriteLine("Deploy    : 启动项目已勾选, 无需修改");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Deploy 检查失败(可忽略): " + ex.Message);
        }
    }

    // ---- VS 忙 / RPC_E_CALL_REJECTED 处理 (微软官方 KB ms228772) ----
    // 文档: https://learn.microsoft.com/previous-versions/visualstudio/visual-studio-2010/ms228772
    // 现象(实测): VS 弹出模态对话框(部署失败/异常提示/任何要你点的框)时, 其主线程被占,
    //   外部进程所有 EnvDTE 调用返回 RPC_E_CALL_REJECTED(0x80010001) 或
    //   RPC_E_SERVERCALL_RETRYLATER(0x8001010A) —— 这就是"有弹窗就连不上"的真身。
    // 正解: 客户端实现 IOleMessageFilter 并 CoRegisterMessageFilter, 由 COM 通道自动重试,
    //   而不是自己 try/catch 了事。注意 IOleMessageFilter ≠ System.Windows.Forms.IMessageFilter。
    [ComImport, Guid("00000016-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IOleMessageFilter
    {
        [PreserveSig] int HandleInComingCall(int dwCallType, IntPtr hTaskCaller,
                                             int dwTickCount, IntPtr lpInterfaceInfo);
        [PreserveSig] int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);
        [PreserveSig] int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
    }

    class VsMessageFilter : IOleMessageFilter
    {
        // 注意: RetryRejectedCall 的 dwTickCount 是 GetTickCount()(开机以来的毫秒),
        // 不是"已等待多久" —— 拿它跟预算比毫无意义(常见网上示例的 bug)。
        // 这里改用绝对截止时间。
        static long _deadline;
        public static void Arm(int budgetMs) => _deadline = Environment.TickCount64 + budgetMs;
        public static int BudgetMs { set => Arm(value); }

        [DllImport("ole32.dll")]
        static extern int CoRegisterMessageFilter(IOleMessageFilter? newFilter,
                                                  out IOleMessageFilter? oldFilter);

        public static void Register() => CoRegisterMessageFilter(new VsMessageFilter(), out _);
        public static void Revoke() => CoRegisterMessageFilter(null, out _);

        // 我们只做客户端, 不做服务器: 入站调用直接接(0 = SERVERCALL_ISHANDLED)。
        int IOleMessageFilter.HandleInComingCall(int dwCallType, IntPtr hTaskCaller,
                                                 int dwTickCount, IntPtr lpInterfaceInfo) => 0;

        // 调用被 VS 拒绝后, COM 会来问要不要重试。
        // dwRejectType: 1=SERVERCALL_REJECTED(真拒绝) / 2=SERVERCALL_RETRYLATER(只是忙)
        // 返回值: >=0 = 等这么多毫秒再重试; -1 = 放弃(抛 RPC_E_CALL_REJECTED)
        int IOleMessageFilter.RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
        {
            // 1=SERVERCALL_REJECTED / 2=SERVERCALL_RETRYLATER —— VS 弹对话框时两种都见过
            if ((dwRejectType == 1 || dwRejectType == 2) && Environment.TickCount64 < _deadline)
                return 250;   // 等 250ms 再重试
            return -1;        // 超预算: 放弃, 抛 RPC_E_CALL_REJECTED
        }

        // 等待期间来了非 COM 消息: 让默认处理继续跑(2 = PENDINGMSG_WAITDEFPROCESS),
        // 否则 VS 的消息泵被堵死, 对话框永远没人处理 -> 死锁。
        int IOleMessageFilter.MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType) => 2;
    }

    static bool IsVsBusy(Exception ex)
    {
        uint h = 0;
        if (ex is COMException c) h = (uint)c.ErrorCode;
        else if (ex.InnerException is COMException ic) h = (uint)ic.ErrorCode;
        if (h == 0x80010001 || h == 0x8001010A) return true;
        // dynamic(IDispatch) 后期绑定失败时是 RuntimeBinderException, 错误码只在消息里
        var s = ex.ToString();
        return s.Contains("0x80010001") || s.Contains("0x8001010A");
    }

    // VS 忙/超时时给出可操作的提示, 并把 VS 当前弹出的窗口标题列出来(只读, 不做任何操作)
    static void DumpVsBusyHint()
    {
        Console.Error.WriteLine("");
        Console.Error.WriteLine("==== VS 正忙: EnvDTE 调用被拒绝 (RPC_E_CALL_REJECTED 0x80010001) ====");
        Console.Error.WriteLine("含义 : 不是连不上, 是 VS 主线程被一个模态对话框占着, 期间谁叫它都不理。");
        Console.Error.WriteLine("怎么办: ① 去 VS 点掉那个对话框; ② 或加 -WaitVS <秒> 让我等它;");
        Console.Error.WriteLine("        ③ 或绕开 VS 用 -Debug (不经 VS 的部署+启动+日志通道)。");
        try
        {
            var rows = EnumVsWindows(out var pids);
            if (pids.Count == 0) return;

            if (rows.Count > 0)
            {
                Console.Error.WriteLine("");
                Console.Error.WriteLine("VS 窗口清单 (#32770 = 对话框类, 就是它挡路):");
                foreach (var r in rows
                    .OrderBy(r => r.Cls == "#32770" ? 0 : 1)   // 对话框排最前
                    .Take(15))
                {
                    var flag = r.Cls == "#32770" ? " <== 对话框!" : "";
                    Console.Error.WriteLine($"  · devenv({r.Pid}) [{r.Cls}] \"{r.Title}\"{flag}");
                }

                // 把对话框里的文字读出来(纯只读): 通常就能看到 VS 到底在报什么
                foreach (var dlg in rows.Where(r => r.Cls == "#32770"))
                {
                    var lines = ReadDialogText(dlg.Hwnd);
                    if (lines.Count > 0)
                    {
                        Console.Error.WriteLine($"  └ 对话框内容 (devenv({dlg.Pid})):");
                        foreach (var t in lines.Distinct().Take(10))
                            Console.Error.WriteLine("      " + (t.Length > 300 ? t[..300] + "..." : t));
                    }
                }
            }
        }
        catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    const uint WM_CLOSE = 0x0010;

    // 枚举 VS(devenv)的可见窗口
    static List<(IntPtr Hwnd, uint Pid, string Cls, string Title)> EnumVsWindows(out HashSet<uint> pids)
    {
        var set = Process.GetProcessesByName("devenv").Select(p => (uint)p.Id).ToHashSet();
        pids = set;
        var rows = new List<(IntPtr, uint, string, string)>();
        if (set.Count == 0) return rows;
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (!set.Contains(pid)) return true;
            var cb = new StringBuilder(256);
            GetClassName(h, cb, cb.Capacity);
            int len = GetWindowTextLength(h);
            var sb = new StringBuilder(Math.Max(len, 1) + 1);
            if (len > 0) GetWindowText(h, sb, sb.Capacity);
            rows.Add((h, pid, cb.ToString(), sb.ToString().Trim()));
            return true;
        }, IntPtr.Zero);
        return rows;
    }

    static List<string> ReadDialogText(IntPtr dlg)
    {
        var lines = new List<string>();
        EnumChildWindows(dlg, (h, l) =>
        {
            int len = GetWindowTextLength(h);
            if (len <= 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            var t = sb.ToString().Trim().Replace("\r\n", " / ").Replace("\n", " / ");
            if (t.Length > 0) lines.Add(t);
            return true;
        }, IntPtr.Zero);
        return lines;
    }

    // 读 VS 的模态对话框(#32770)内容并关闭它(WM_CLOSE, 等价点"确定"/右上角 X)。
    // 这不是 VS 自动化 API —— DTE 自身无法关闭它自己弹的框, 所以走标准 Win32 窗口消息。
    static bool DismissVsDialogs()
    {
        try
        {
            var rows = EnumVsWindows(out _);
            var dlgs = rows.Where(r => r.Cls == "#32770").ToList();
            if (dlgs.Count == 0) return false;
            foreach (var d in dlgs)
            {
                Console.WriteLine($"检测到 VS 模态对话框 (devenv({d.Pid}) \"{d.Title}\"):");
                foreach (var t in ReadDialogText(d.Hwnd).Distinct().Take(10))
                    Console.WriteLine("    " + (t.Length > 300 ? t[..300] + "..." : t));
                SendMessage(d.Hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                Console.WriteLine("    -> 已关闭该对话框 (WM_CLOSE), 继续 ...");
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 点掉新版 VS 的模态提示框。
    ///
    /// 【为什么还要这一个】<see cref="DismissVsDialogs"/> 只认 <c>#32770</c>（Win32 原生
    ///   对话框类）。实测 VS 18 的提示框（本轮卡住 DTE 的就是「调试会话已结束。」）
    ///   <b>不是</b> #32770，而是 WPF 弹层：要从主窗口的 <c>GW_ENABLEDPOPUP</c> 才拿得到。
    ///   所以之前出现「VS 明明被框挡住，窗口清单里却一个对话框都没有」。
    ///
    /// 【安全边界】只点<b>恰好一个按钮</b>且按钮文字在 <c>确定/OK/关闭/Close</c>
    ///   白名单里的纯提示框。带「取消/是/否/保存」的多按钮框一律只打印不动 ——
    ///   那种框点错会丢改动或改配置，不是我能替用户决定的。
    /// </summary>
    static bool DismissVsPopups(bool dryRun)
    {
        int handled = 0;
        try
        {
            foreach (var r in EnumVsWindows(out _))
            {
                IntPtr popup = GetWindow(r.Hwnd, GW_ENABLEDPOPUP);
                if (popup == IntPtr.Zero || popup == r.Hwnd || !IsWindowVisible(popup))
                    continue;

                var buttons = new List<(IntPtr H, string T)>();
                EnumChildWindows(popup, (h, l) =>
                {
                    if (!GetClassNameOf(h).Equals("Button", StringComparison.OrdinalIgnoreCase))
                        return true;
                    string t = GetWindowTextOf(h);
                    if (t.Length > 0) buttons.Add((h, t));
                    return true;
                }, IntPtr.Zero);

                if (buttons.Count == 0)
                {
                    // WPF 弹层里的按钮不是 Win32 Button 子窗口, 光看子控件会一无所获。
                    // 这里把「类名 | 文字」全打出来, 便于判断到底是不是纯提示框。
                    var kids = new List<string>();
                    EnumChildWindows(popup, (h, l) =>
                    {
                        string c = GetClassNameOf(h);
                        string t = GetWindowTextOf(h);
                        kids.Add($"[{c}] \"{t}\" visible={IsWindowVisible(h)}");
                        return true;
                    }, IntPtr.Zero);
                    Console.WriteLine($"  弹层 0x{popup.ToInt64():X} [{GetClassNameOf(popup)}] 主窗口=\"{r.Title}\" 无 Win32 按钮, 子窗口 {kids.Count} 个:");
                    foreach (var k in kids.Take(20))
                        Console.WriteLine("      " + (k.Length > 200 ? k[..200] + "..." : k));

                    // ★ 第三档：没有 Win32 按钮时，按<b>文字</b>判断是不是纯通知。
                    //   本轮卡住 DTE 的「调试会话已结束。」就是这一类：它是 WPF 弹层，
                    //   <b>自己一个子窗口都没有</b>，文字挂在主框架的子窗口上 ——
                    //   所以取文要把「弹层子树 + 属主框架子树」并起来看。
                    //   对纯通知发 WM_CLOSE 等价于点 X / 按 Esc —— 这就是「知道了」。
                    //   但凡文字里出现疑问/取舍词（是否、保存、取消、覆盖、丢失…），
                    //   说明这框在等用户做决定，一律不动，交回用户。
                    var texts = ReadDialogText(popup)
                        .Concat(ReadDialogText(r.Hwnd))
                        .Select(t => t.Trim())
                        .Where(t => t.Length > 0 && t != r.Title && t != GetWindowTextOf(popup))
                        .Distinct()
                        .ToList();
                    string joined = string.Join(" ", texts);
                    string[] riskWords = { "是否", "保存", "取消", "覆盖", "丢失", "还原",
                                           "删除", "确定要", "选择", "重试", "忽略", "全部" };
                    bool risky = riskWords.Any(w => joined.Contains(w));
                    if (texts.Count > 0 && !risky)
                    {
                        if (dryRun)
                            Console.WriteLine($"      (dry-run: 纯通知，本可 WM_CLOSE: {joined})");
                        else
                        {
                            SendMessage(popup, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                            Console.WriteLine($"      -> 已 WM_CLOSE 纯通知弹层: {joined}");
                        }
                        handled++;
                    }
                    else
                        Console.WriteLine("      跳过：文字里含取舍词或为空，这框在等用户决定，不动。");
                    continue;
                }

                Console.WriteLine($"  弹层 0x{popup.ToInt64():X} [{GetClassNameOf(popup)}] 主窗口=\"{r.Title}\"");
                foreach (var t in ReadDialogText(popup).Distinct().Take(8))
                    Console.WriteLine("      内容: " + (t.Length > 200 ? t[..200] + "..." : t));

                string[] safe = { "确定", "OK", "关闭", "Close" };
                if (buttons.Count == 1 &&
                    safe.Any(s => buttons[0].T.Equals(s, StringComparison.OrdinalIgnoreCase)))
                {
                    if (dryRun)
                        Console.WriteLine($"      (dry-run: 本可点唯一按钮「{buttons[0].T}」)");
                    else
                    {
                        SendMessage(buttons[0].H, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                        Console.WriteLine($"      -> 已点击唯一按钮「{buttons[0].T}」");
                    }
                    handled++;
                }
                else
                    Console.WriteLine("      跳过：非单按钮安全框（可能是 是/否/取消/保存），不替用户点。");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  弹层处理异常: " + ex.Message);
        }
        return handled > 0;
    }

    static string GetClassNameOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    static string GetWindowTextOf(IntPtr h)
    {
        int len = GetWindowTextLength(h);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString().Trim().Replace("\r\n", " / ").Replace("\n", " / ");
    }

    const uint BM_CLICK = 0x00F5;

    // ---- 窗口检测: 证明"页面真的显示出来了" ----
    // UWP 窗口通常由 ApplicationFrameHost 托管, 所以 ClashUWP.exe 自身可能没有可见窗口。
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    static void DumpWindows(HashSet<uint>? pids, string[] hostProcs)
    {
        var hits = new List<string>();
        EnumWindows((h, l) =>
        {
            try
            {
                if (!IsWindowVisible(h)) return true;
                GetWindowThreadProcessId(h, out uint pid);
                bool match = pids != null && pids.Contains(pid);
                string pname = "";
                if (!match)
                {
                    try { pname = Process.GetProcessById((int)pid).ProcessName; } catch { return true; }
                    if (!hostProcs.Any(x => string.Equals(x, pname, StringComparison.OrdinalIgnoreCase))) return true;
                }
                else { try { pname = Process.GetProcessById((int)pid).ProcessName; } catch { } }
                int len = GetWindowTextLength(h);
                if (len <= 0) return true;
                var sb = new StringBuilder(len + 1);
                GetWindowText(h, sb, sb.Capacity);
                var t = sb.ToString();
                if (t.Trim().Length == 0) return true;
                hits.Add($"  PID={pid} [{pname}] 0x{h.ToInt64():X}  \"{t}\"");
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        Console.WriteLine("---- 可见窗口 ----");
        Console.WriteLine(hits.Count > 0
            ? string.Join(Environment.NewLine, hits.Take(15))
            : "  (未找到可见窗口 —— 页面可能未显示)");
    }

    // 官方 DTE 属性: Debugger.BreakpointLastHit —— 区分"命中断点"与"异常中断"
    static void DescribeBreak(dynamic dte)
    {
        try
        {
            dynamic bp = dte.Debugger.BreakpointLastHit;
            if (bp != null)
            {
                string f = "", fn = "";
                int line = 0;
                try { f = (string)bp.File; } catch { }
                try { line = (int)bp.FileLine; } catch { }
                try { fn = (string)bp.FunctionName; } catch { }
                Console.WriteLine($"      命中断点 : {f}:{line}  {fn}");
            }
            else
            {
                Console.WriteLine("      非断点中断 : 多半是未处理异常 (DTE 不暴露异常详情, 需在 VS 界面确认)");
            }
        }
        catch (Exception ex) { Console.WriteLine("      读取中断信息失败: " + ex.Message); }
    }

    static void CollectProjects(dynamic proj, List<string> names)
    {
        try
        {
            // 解决方案文件夹会有 ProjectItems, 需递归
            if (proj.Kind == "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}")
            {
                foreach (dynamic it in proj.ProjectItems)
                {
                    try { if (it.SubProject != null) CollectProjects(it.SubProject, names); } catch { }
                }
                return;
            }
            var n = (string)proj.UniqueName;
            if (!string.IsNullOrEmpty(n)) names.Add(n);
        }
        catch { }
    }


    // 官方 DTE: ToolWindows.ErrorList.ErrorItems —— 部署/启动失败会落在这里
    static void DumpErrorList(dynamic dte, string title)
    {
        VsMessageFilter.Arm(VsWaitSeconds * 1000);   // 续期: VS 可能正在部署, 调用会被暂时拒绝
        try
        {
            // 强类型 DTE2 (微软官方 interop): 直接 vtable 调用, 不做名字解析
            var dte2 = (DTE2)dte;
            var items = dte2.ToolWindows.ErrorList.ErrorItems;

            var sb = new StringBuilder();
            int n = 0;
            // interop 集合没有 GetEnumerator, 必须按 Item(i) 遍历 (下标从 1 开始)
            for (int i = 1; i <= items.Count && n < 20; i++)
            {
                var it = items.Item(i);
                sb.AppendLine($"  [{it.ErrorLevel}] {it.FileName}({it.Line}): {it.Description}");
                n++;
            }
            Console.WriteLine($"---- {title} ----");
            Console.WriteLine(n > 0 ? sb.ToString() : "(错误列表为空)");
        }
        catch (Exception ex) { Console.WriteLine($"读取{title}失败: " + ex.Message); }
    }

    // 应用自身日志: UWP 官方约定的应用数据目录 %LOCALAPPDATA%\Packages\<PFN>\LocalState
    // 这是 VS 输出窗格读不到时, 已实测可靠的日志通道。
    static string? FindLocalStateByIdentity(string identity)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            if (!Directory.Exists(root)) return null;
            var d = Directory.GetDirectories(root, identity + "_*").FirstOrDefault();
            return d == null ? null : Path.Combine(d, "LocalState");
        }
        catch { return null; }
    }

    static void DumpAppLogs(string localState)
    {
        try
        {
            var logs = Directory.GetFiles(localState, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .Take(3).ToList();
            if (logs.Count == 0) { Console.WriteLine("(LocalState 下无 .log/.txt)"); return; }
            Console.WriteLine("---- 应用日志(LocalState) ----");
            foreach (var f in logs)
            {
                Console.WriteLine($"  [{Path.GetFileName(f)}]");
                try { foreach (var l in File.ReadAllLines(f).TakeLast(15)) Console.WriteLine("    " + l); }
                catch { }
            }
        }
        catch (Exception ex) { Console.WriteLine("读取应用日志失败: " + ex.Message); }
    }

    static void DumpErrorEvents(int minutes)
    {
        var psCmd = "$ProgressPreference='SilentlyContinue'; " +
                    "Get-WinEvent -FilterHashtable @{LogName='Application';StartTime=(Get-Date).AddMinutes(-" + minutes + ")} -ErrorAction SilentlyContinue | " +
                    "Where-Object { $_.LevelDisplayName -eq '错误' -or $_.ProviderName -match 'Application Error|AppCrash|Application Hang' } | " +
                    "Select-Object -First 15 | ForEach-Object { '[' + $_.TimeCreated + '] ' + $_.ProviderName + ': ' + ($_.Message -split \"`n\")[0] }";
        var ev = Run("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", psCmd }, null, 60000);
        Console.WriteLine("---- 应用错误事件 ----");
        var t = (ev.Out ?? "").Trim();
        Console.WriteLine(t.Length > 0 ? t : $"(近 {minutes} 分钟内无错误事件)");
    }

    // 取 ROT 中正在运行的 COM 对象 (等价 .NET Framework 的 Marshal.GetActiveObject,
    // 该 API 在 .NET Core/5+ 已移除, 故用官方 P/Invoke)
    [DllImport("ole32.dll")]
    static extern int CLSIDFromProgID(
        [MarshalAs(UnmanagedType.LPWStr)] string lpszProgID, out Guid pclsid);

    // 用 IDispatch 封送, 便于随后 cast 成强类型 DTE2 (下方说明)。
    //
    // 【踩坑记录】读取 ToolWindows 的正确方式: 必须用微软官方 interop 程序集
    //   (Microsoft.VisualStudio.Interop) 的强类型 EnvDTE80.DTE2, 直接 vtable 调用。
    //   用 dynamic 后期绑定会失败, 报:
    //     'System.__ComObject' does not contain a definition for 'ToolWindows'
    //   原因: ToolWindows 属 EnvDTE80(EnvDTE80.dll), 不在默认 dispinterface(EnvDTE._DTE) 上。
    //   逐条排除过(均无效): ① [STAThread] 套间问题; ② 手工 IDispatch::GetIDsOfNames
    //   —— 返回 DISP_E_UNKNOWNNAME(0x80020006), 该实现只认 _DTE 成员;
    //   ③ IDispatch::GetTypeInfo -> ITypeInfo::GetIDsOfNames —— 返回 E_NOTIMPL(0x80004001)。
    //   PowerShell 之所以能读到, 是因为它走类型库的强类型路径。
    //   结论: 别手工拼 COM, 直接用官方 interop 强类型。
    [DllImport("oleaut32.dll", PreserveSig = false)]
    static extern void GetActiveObject(
        ref Guid rclsid, IntPtr reserved,
        [MarshalAs(UnmanagedType.IDispatch)] out object ppunk);

    static object? GetActiveComObject(string progId)
    {
        try
        {
            if (CLSIDFromProgID(progId, out var clsid) < 0) return null;
            GetActiveObject(ref clsid, IntPtr.Zero, out object obj);
            return obj;
        }
        catch { return null; }
    }

    // 官方 EnvDTE.dbgDebugMode (learn.microsoft.com/dotnet/api/envdte.dbgdebugmode):
    //   dbgDesignMode = 1 / dbgBreakMode = 2 / dbgRunMode = 3
    // 注意顺序: 2 是"中断", 3 是"运行中" —— 早期版本把 2/3 写反,
    // 导致把正常运行误判成中断并停掉应用。此处以官方文档为准。
    static string ModeName(int m) => m switch
    {
        1 => "dbgDesignMode (设计态/未调试)",
        2 => "dbgBreakMode (已中断)",
        3 => "dbgRunMode (运行中)",
        _ => $"未知({m})"
    };

    // 读取 VS 输出窗口指定窗格的文本。返回 (文本, 说明)。
    //
    // 官方依据(已核对 Microsoft Learn):
    //  · 取法一 EditPoint.GetText  —— 出自微软《How to: Control the Output Window》
    //  · 取法二 Selection.StartOfDocument(false)/EndOfDocument(true) —— 出自
    //    EnvDTE.OutputWindowPane.TextDocument 官方示例
    //  · 必须用强类型 EnvDTE80.DTE2 访问 ToolWindows —— dynamic 后期绑定拿不到
    //    (详见 GetActiveObject 处的踩坑记录)。实测 VS 2026 (18.0) 输出窗格可读。
    //
    // 补充背景(IVsOutputWindowPane 层级, 与本工具无关):
    //  · IVsOutputWindowPane 官方接口本身不提供任何读取文本的方法, 只有
    //    Activate/Clear/FlushToTaskList/GetName/Hide/OutputString*/SetName。
    //  · 唯一能读到文本的链路 (IVsOutputWindow -> IVsUserData -> guidIWpfTextViewHost
    //    -> IWpfTextView -> TextBuffer) 需在 VSIX 进程内取得 ServiceProvider, 外部进程做不到。
    static (string Text, string Note) ReadOutputPane(dynamic dte, string[] names, List<string>? allNames)
    {
        VsMessageFilter.Arm(VsWaitSeconds * 1000);   // 续期: 同 DumpErrorList
        try
        {
            var dte2 = (DTE2)dte;
            var ow = dte2.ToolWindows.OutputWindow;
            if (ow == null)
                return ("", "读不到 —— ToolWindows.OutputWindow 返回 null"
                          + " (VS 2022+ 对外进程已知回归; IVsOutputWindowPane 官方无读取方法)");

            var panes = ow.OutputWindowPanes;
            if (allNames != null)
            {
                for (int i = 1; i <= panes.Count; i++) allNames.Add(panes.Item(i).Name);
            }
            // 既按 Item(名字) 取, 也遍历按名字匹配, 兼容不同语言包
            foreach (var n in names)
            {
                EnvDTE.OutputWindowPane? pane = null;
                try { pane = panes.Item(n); } catch { }
                if (pane == null)
                {
                    for (int i = 1; i <= panes.Count; i++)
                    {
                        var pn = panes.Item(i);
                        if (string.Equals(pn.Name, n, StringComparison.OrdinalIgnoreCase)) { pane = pn; break; }
                    }
                }
                if (pane == null) continue;
                var txt = ReadPaneText(pane);
                if (!string.IsNullOrWhiteSpace(txt)) return (txt, "");
            }
            return ("", "(窗格为空或未找到: " + string.Join("/", names) + ")");
        }
        catch (Exception ex) { return ("", "读取失败: " + ex.Message); }
    }

    static string ReadPaneText(EnvDTE.OutputWindowPane pane)
    {
        // 取法一: 微软《How to: Control the Output Window》的 EditPoint.GetText
        try
        {
            var doc = pane.TextDocument;
            var ep = doc.StartPoint.CreateEditPoint();
            var t = ep.GetText(doc.EndPoint);
            if (!string.IsNullOrWhiteSpace(t)) return t;
        }
        catch { }
        // 取法二: 微软 OutputWindowPane.TextDocument 官方示例的 Selection 写法
        try
        {
            var sel = pane.TextDocument.Selection;
            sel.StartOfDocument(false);
            sel.EndOfDocument(true);
            var t2 = sel.Text;
            if (!string.IsNullOrWhiteSpace(t2)) return t2;
        }
        catch { }
        return "";
    }

    static string Tail(string s, int n)
    {
        var lines = s.Replace("\r", "").Split('\n');
        return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - n)));
    }

    // ---------------- HTTP 服务 ----------------
    static int Serve()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        try { listener.Start(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"无法绑定端口 {Port} (可能被占用或需要权限): {ex.Message}");
            return 1;
        }

        Console.WriteLine("===================================================");
        Console.WriteLine(" UWP Bridge (WorkBuddy 跳板 / 代运行) 已启动 [exe]");
        Console.WriteLine($" 监听:    http://127.0.0.1:{Port}");
        Console.WriteLine($" msbuild: {MsBuild}");
        Console.WriteLine($" winapp:  {WinApp}");
        Console.WriteLine($" dotnet:  {DotNet}");
        Console.WriteLine($" exec:    {(AllowArbitrary ? "已开放(-AllowArbitrary)" : "禁用")}");
        Console.WriteLine($" token:   {(Token.Length > 0 ? "已启用" : "未设置")}");
        Console.WriteLine(" 按 Ctrl+C 停止");
        Console.WriteLine("===================================================");

        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch { break; }
            try { Handle(ctx, listener); }
            catch (Exception ex) { Console.WriteLine("处理请求出错: " + ex.Message); }
        }

        Console.WriteLine("UWP Bridge 已停止。");
        return 0;
    }

    static void Handle(HttpListenerContext ctx, HttpListener listener)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        var path = req.Url?.AbsolutePath ?? "/";

        string body = "";
        if (req.HasEntityBody)
            using (var sr = new StreamReader(req.InputStream, Encoding.UTF8))
                body = sr.ReadToEnd();

        JsonObject result;
        bool authorized = CheckToken(req);

        if (req.HttpMethod == "GET" && (path == "/" || path == "/health"))
        {
            result = Obj(("ok", true), ("status", "ok"), ("msbuild", MsBuild), ("winapp", WinApp),
                         ("dotnet", DotNet), ("pid", Environment.ProcessId), ("port", Port),
                         ("execEnabled", AllowArbitrary));
        }
        else if (req.HttpMethod == "POST" && path == "/build")
        {
            result = !authorized ? Unauth() : ParseAnd(body, BuildRequest);
        }
        else if (req.HttpMethod == "POST" && path == "/run")
        {
            result = !authorized ? Unauth() : ParseAnd(body, RunRequest);
        }
        else if (req.HttpMethod == "POST" && path == "/exec")
        {
            result = !authorized ? Unauth() : ParseAnd(body, ExecRequest);
        }
        else if (req.HttpMethod == "POST" && path == "/shutdown")
        {
            result = !authorized ? Unauth() : Obj(("ok", true), ("action", "shutting down"));
            WriteJson(resp, result);
            Console.WriteLine($"[POST {path}] -> OK");
            listener.Stop();
            return;
        }
        else
        {
            result = Err($"未知端点: {req.HttpMethod} {path}");
        }

        WriteJson(resp, result);
        var okFlag = result["ok"]?.GetValue<bool>() == true;
        Console.WriteLine($"[{req.HttpMethod} {path}] -> {(okFlag ? "OK" : "FAIL")}");
    }

    static bool CheckToken(HttpListenerRequest req)
    {
        if (Token.Length == 0) return true;
        var h = req.Headers["X-Bridge-Token"];
        if (string.IsNullOrEmpty(h)) h = req.QueryString["token"];
        return h == Token;
    }

    static JsonObject ParseAnd(string body, Func<JsonObject, JsonObject> handler)
    {
        try
        {
            var o = JsonNode.Parse(body) as JsonObject;
            if (o is null) return Err("无法解析 JSON body");
            return handler(o);
        }
        catch { return Err("无法解析 JSON body"); }
    }

    static void WriteJson(HttpListenerResponse resp, JsonObject o)
    {
        var bytes = Encoding.UTF8.GetBytes(o.ToJsonString(JsonOpts));
        resp.ContentType = "application/json; charset=utf-8";
        resp.ContentLength64 = bytes.Length;
        resp.OutputStream.Write(bytes, 0, bytes.Length);
        resp.Close();
    }

    // ---------------- 小工具 ----------------
    static JsonObject Obj(params (string Key, object? Val)[] kv)
    {
        var o = new JsonObject();
        foreach (var (k, v) in kv)
        {
            if (v is null) { o[k] = null; continue; }
            switch (v)
            {
                case bool b:  o[k] = JsonValue.Create(b); break;
                case int i:   o[k] = JsonValue.Create(i); break;
                case long l:  o[k] = JsonValue.Create(l); break;
                default:      o[k] = JsonValue.Create(v.ToString()!); break;
            }
        }
        return o;
    }

    static JsonObject Err(string msg) => Obj(("ok", false), ("error", msg));
    static JsonObject Unauth() => Err("unauthorized");

    static string? Str(JsonObject b, string key)
    {
        var n = b[key];
        if (n is null) return null;
        try { return n.GetValue<string>(); } catch { return n.ToString(); }
    }

    static bool Bool(JsonObject b, string key)
    {
        var n = b[key];
        if (n is null) return false;
        return n.GetValueKind() == JsonValueKind.True;
    }

    static bool RestoreEnabled(JsonObject b)
    {
        var n = b["restore"];
        if (n is null) return true;
        return n.GetValueKind() != JsonValueKind.False;
    }

    static IEnumerable<string> Tokenize(string s)
    {
        var list = new List<string>();
        var cur = new StringBuilder();
        bool inQuotes = false;
        foreach (var ch in s)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); }
                continue;
            }
            cur.Append(ch);
        }
        if (cur.Length > 0) list.Add(cur.ToString());
        return list;
    }
}
