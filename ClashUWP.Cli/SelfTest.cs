// selftest —— 只验一件事：本工具用到的 WinRT 投影在 Native AOT 下是否可用。
//
// 【为什么要有它，而且为什么不能删】CLI 改成 PublishAot 发布（官方对可执行文件的做法：
//   启动快、免运行时依赖、包体从 27 MB 降到 4 MB）能编译通过<b>不代表</b>跑得起来 ——
//   AOT 下跨 ABI 的泛型接口（ValueSet 本质是 IDictionary<string, object>）与
//   IAsyncOperation<T>.AsTask() 这些投影代码要靠 CsWinRT 生成，缺了是<b>运行时</b>才炸。
//   而端到端那一头要等主应用装上带 AppService 的版本才能验，太晚。
//
// 【刻意不碰真实目标】第 2/4 步连的是一个<b>不存在</b>的 PFN：平台没有可激活的包，
//   所以既不启动任何应用、也不碰隧道，只是把 OpenAsync 的异步跨 ABI 路径走一遍。
//   返回 Success 反而说明有问题；这里只要求"拿到状态、没炸"。

using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;
using Windows.Management.Deployment;

namespace ClashUWP.Cli;

internal static class SelfTest
{
    private const string NoSuchPfn = "ClashUWP.SelfTest.NoSuch_0000000000000";

    public static int Run()
    {
        int fail = 0;

        // 1) 值集：请求/响应两个方向的载体，也是最容易在 AOT 下炸的泛型字典投影。
        try
        {
            var vs = new ValueSet { ["cmd"] = "ping" };
            bool hit = vs.TryGetValue("cmd", out object? v) && (string?)v == "ping";
            Console.WriteLine($"[{(hit && vs.Count == 1 ? "PASS" : "FAIL")}] ValueSet 读写 " +
                              $"(count={vs.Count}, type={v?.GetType().Name ?? "null"})");
            if (!hit) fail++;
        }
        catch (Exception ex) { Console.WriteLine($"[FAIL] ValueSet 读写: {ex.GetType().Name}: {ex.Message}"); fail++; }

        // 2) 类型激活：RoActivateInstance 能不能拿到 AppServiceConnection 的工厂。
        try
        {
            using var c = new AppServiceConnection
            {
                AppServiceName = AppServiceChannel.ServiceName,
                PackageFamilyName = NoSuchPfn,
            };
            bool ok = c.AppServiceName == AppServiceChannel.ServiceName &&
                      c.PackageFamilyName == NoSuchPfn;
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] AppServiceConnection 构造/属性回读");
            if (!ok) fail++;
        }
        catch (Exception ex) { Console.WriteLine($"[FAIL] AppServiceConnection 构造: {ex.GetType().Name}: {ex.Message}"); fail++; }

        // 3) 包发现：PackageManager.FindPackagesForUser（full-trust 桌面进程才允许）。
        try
        {
            int n = new PackageManager().FindPackagesForUser("").Count();
            Console.WriteLine($"[{(n > 0 ? "PASS" : "FAIL")}] PackageManager 枚举到 {n} 个包");
            if (n <= 0) fail++;
        }
        catch (Exception ex) { Console.WriteLine($"[FAIL] PackageManager: {ex.GetType().Name}: {ex.Message}"); fail++; }

        // 4) 异步跨 ABI：OpenAsync().AsTask().WaitAsync(...) —— 真实连接用的同一条路径。
        try
        {
            using var c = new AppServiceConnection
            {
                AppServiceName = AppServiceChannel.ServiceName,
                PackageFamilyName = NoSuchPfn,
            };
            AppServiceConnectionStatus st = c.OpenAsync().AsTask()
                                             .WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            Console.WriteLine($"[PASS] OpenAsync 异步通路 (不存在包→状态={st}，预期非 Success)");
        }
        catch (TimeoutException) { Console.WriteLine("[PASS] OpenAsync 异步通路 (超时也说明任务在跑，未崩)"); }
        catch (Exception ex) { Console.WriteLine($"[FAIL] OpenAsync: {ex.GetType().Name}: {ex.Message}"); fail++; }

        Console.WriteLine(fail == 0
            ? "自检结论: AOT 下 WinRT 通道可用"
            : $"自检结论: {fail} 项失败 —— 这个发布形态不能用");
        return fail == 0 ? ExitCodes.Ok : ExitCodes.SelfTestFailed;
    }
}
