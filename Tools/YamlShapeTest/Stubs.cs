// 编译桩：只为让产品的 KernelConfig / AppSplit / DnsConfig / MihomoController /
// KernelView / SubscriptionView 在纯 net10.0 控制台下<b>编译得过</b>。
// ★ 测试从不调用任何 IO 路径（Transform 的 WriteEffectiveFile、Load/LoadAsync 的
//   存储调用），这些桩一律抛 NotSupportedException —— 一旦有测试真的走到 IO，
//   它会立刻炸出来，而不是悄悄读到假数据。
using System;
using System.Threading.Tasks;

namespace Windows.Storage
{
    public enum CreationCollisionOption { ReplaceExisting = 0, OpenExisting = 2 }

    public sealed class BasicProperties
    {
        public ulong Size => 0;
    }

    public sealed class StorageFile
    {
        public string Path => "";
        public Task<BasicProperties> GetBasicPropertiesAsync() =>
            Task.FromResult(new BasicProperties());
    }

    public sealed class StorageFolder
    {
        public FakeOp<StorageFile> GetFileAsync(string name) => new();
        public FakeOp<StorageFile> CreateFileAsync(string name, CreationCollisionOption o) => new();
    }

    public sealed class ApplicationData
    {
        public static ApplicationData Current { get; } = new ApplicationData();
        public StorageFolder LocalFolder { get; } = new StorageFolder();
    }

    public static class FileIO
    {
        public static FakeOp<string> ReadTextAsync(StorageFile file) => new();
        public static FakeOp<string> WriteTextAsync(StorageFile file, string text) => new();
    }

    /// <summary>顶替 WinRT 的 <c>IAsyncOperation&lt;T&gt;</c>：只给 AsTask()，调了就抛。</summary>
    public sealed class FakeOp<T>
    {
        public Task<T> AsTask() =>
            Task.FromException<T>(new NotSupportedException("YamlShapeTest 编译桩：本测试不该走 IO"));
    }
}

namespace ClashUWP.Vpn
{
    internal static class RollingLog
    {
        internal static void Info(string channel, string message) { }
        internal static void Warn(string channel, string message) { }
        internal static void Error(string channel, string message) { }
        internal static void Error(string channel, Exception ex) { }
        internal static string Diag() => "";
    }
}

namespace ClashUWP.Backend
{
    internal static class LocalState
    {
        internal static Task<Windows.Storage.StorageFile?> TryGetFileAsync(string name) =>
            Task.FromResult<Windows.Storage.StorageFile?>(null);

        internal static Task<string> ReadTextAsync(Windows.Storage.StorageFile file) =>
            Task.FromException<string>(new NotSupportedException("YamlShapeTest 编译桩：本测试不该走 IO"));

        internal static Task<string> ReadIfExistsByPathAsync(string path) =>
            Task.FromResult("");
    }
}
