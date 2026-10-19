// 编译桩：只为让 DnsConfig.Load / LocalSettings.Load 通过编译。
// 测试从不调用这两个方法，所以桩本身不做任何 IO。
using System;
using System.Threading.Tasks;

namespace Windows.Storage
{
    public sealed class StorageFile { }

    public sealed class StorageFolder
    {
        public FakeAsyncOperation<StorageFile> GetFileAsync(string name) => new();
    }

    public sealed class ApplicationData
    {
        public static ApplicationData Current { get; } = new ApplicationData();
        public StorageFolder LocalFolder { get; } = new StorageFolder();
    }

    public static class FileIO
    {
        public static FakeAsyncOperation<string> ReadTextAsync(StorageFile file) => new();
    }

    public sealed class FakeAsyncOperation<T>
    {
        public Task<T> AsTask() =>
            Task.FromException<T>(new NotSupportedException("DnsConfigTest stub"));
    }
}

namespace ClashUWP.Vpn
{
    // LocalSettings.Load 的日志调用；测试不触达。
    internal static class RollingLog
    {
        internal static void Info(string channel, string message) { }
        internal static void Warn(string channel, string message) { }
        internal static string Diag() => "";
    }
}
