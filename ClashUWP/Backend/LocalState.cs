using System;
using System.Threading.Tasks;

namespace ClashUWP.Backend
{
    /// <summary>
    /// 【后端 · LocalState 读写原语】诊断文件是本项目唯一的可靠取证通道：
    /// 插件跑在后台任务上下文没有 UI，<c>LogDiagnosticMessage</c> 又常被平台日志
    /// 清空/丢失（踩过坑），所以前后台都靠共享的 <c>ApplicationData.LocalFolder</c>
    /// 交换证据。这里把那套「CreateFileAsync(ReplaceExisting) + FileIO.WriteTextAsync」
    /// 的重复写法收敛成一处，并把<b>会不会向上抛</b>区分成两个名字：
    ///
    ///   <see cref="WriteTextAsync"/>      会抛 —— 调用方自己兜（探测主体就是这么把
    ///                                     ERROR= 落到 dataface.txt 的）；
    ///   <see cref="WriteTextQuietAsync"/> 不抛 —— 「诊断写不了就写不了」的纯旁路用它，
    ///                                     绝不能因为落盘失败把主流程带崩。
    ///
    /// 文件名一律原样传，不改写、不猜路径。
    /// </summary>
    internal static class LocalState
    {
        internal static Windows.Storage.StorageFolder Folder
            => Windows.Storage.ApplicationData.Current.LocalFolder;

        /// <summary>存在才给文件对象；不存在返回 null（闸门判定就靠这个）。</summary>
        internal static async Task<Windows.Storage.StorageFile?> TryGetFileAsync(string name)
            => await Folder.TryGetItemAsync(name) as Windows.Storage.StorageFile;

        /// <summary>
        /// 闸门：文件存在即为「已经跑过」。</summary>
        internal static async Task<bool> ExistsAsync(string name)
            => await TryGetFileAsync(name) != null;

        /// <summary>
        /// ★ 按<b>相对子路径</b>取文件（可含分隔符），取不到返回 null。
        ///
        /// 【为什么不能用 <see cref="TryGetItemAsync"/> 直接传带斜杠的 name】
        ///   <c>StorageFolder.TryGetItemAsync(name)</c> 的 name <b>只接受一个文件名</b>，
        ///   含路径分隔符会抛 <c>ArgumentException</c>（hr=<c>0x80070057</c> E_INVALIDARG，
        ///   平台文案「参数错误。找不到具有指定名称(UNABLE_TO_MASK_PATH)的项目」）。
        ///   ★ 微软 Learn 那句「The name (<b>or path relative to the current folder</b>)」
        ///     指的是<b>单层</b>相对路径（<c>"logs"</c>），不是 <c>"logs/app.log"</c> ——
        ///     2026-10-18 在本机用真实 WinRT API 实测过（.local 里的复现程序）：
        ///       <c>TryGetItemAsync("logs/app.log")</c> ⇒ THROW ArgumentException 0x80070057
        ///       <c>TryGetItemAsync("app.log")</c>      ⇒ null
        ///       <c>GetFolderAsync("logs")</c> → <c>TryGetItemAsync("app.log")</c> ⇒ 拿到文件
        ///   ⇒ 所以<b>必须两段式</b>：逐段 GetFolderAsync，最后一段才 TryGetItemAsync。
        ///
        /// 【这个坑踩过两处】<c>MainComponent.ShowLogtailAsync</c> 读 <c>logs/app.log</c>、
        /// <c>MihomoController.DiagnoseKernelAsync</c> 读 <c>logs/plugin.log</c> ——
        /// 而 <c>RollingLog</c> 落盘用的是 <c>System.IO</c> 的 <c>Path.Combine(_dir, "logs", …)</c>
        /// （它建目录、建文件都不走 StorageFolder API），⇒ 写得出、读不回。
        /// </summary>
        internal static async Task<Windows.Storage.StorageFile?> TryGetFileByRelativePathAsync(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return null;

            string[] parts = relativePath
                .Replace('/', '\\')
                .Split('\\', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0) return null;

            // 最后一段是文件名，前面几段是子文件夹，逐段下钻。
            Windows.Storage.StorageFolder cur = Folder;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                Windows.Storage.StorageFolder? sub =
                    await cur.TryGetItemAsync(parts[i]) as Windows.Storage.StorageFolder;
                if (sub == null) return null;
                cur = sub;
            }

            return await cur.TryGetItemAsync(parts[^1]) as Windows.Storage.StorageFile;
        }

        /// <summary>原样读文本。</summary>
        internal static Task<string> ReadTextAsync(Windows.Storage.StorageFile file)
            => Windows.Storage.FileIO.ReadTextAsync(file).AsTask();

        /// <summary>读，缺文件回一句占位（debug-out 的 probe 命令用它，不能让调用方崩）。</summary>
        internal static async Task<string> ReadIfExistsAsync(string name)
        {
            Windows.Storage.StorageFile? f = await TryGetFileAsync(name);
            return f == null ? "(无结果文件)" : await ReadTextAsync(f);
        }

        /// <summary>
        /// ★ 按相对子路径读文本（可含分隔符），缺文件回一句占位。
        /// 走 <see cref="TryGetFileByRelativePathAsync"/> 的两段式下钻，
        /// 绕开 <c>TryGetItemAsync</c> 不接受路径分隔符的限制。
        /// </summary>
        internal static async Task<string> ReadIfExistsByPathAsync(string relativePath)
        {
            Windows.Storage.StorageFile? f = await TryGetFileByRelativePathAsync(relativePath);
            return f == null ? "(无结果文件)" : await ReadTextAsync(f);
        }

        /// <summary>整文件覆盖写（ReplaceExisting）；★ 会抛，调用方负责兜。</summary>
        internal static async Task WriteTextAsync(string name, string content)
        {
            var f = await Folder.CreateFileAsync(name,
                Windows.Storage.CreationCollisionOption.ReplaceExisting);
            await Windows.Storage.FileIO.WriteTextAsync(f, content);
        }

        /// <summary>整文件覆盖写，失败只记滚动日志 —— 旁路诊断专用，绝不带崩主流程。</summary>
        internal static async Task WriteTextQuietAsync(string name, string content)
        {
            try { await WriteTextAsync(name, content); }
            catch (Exception ex)
            {
                // ★ 记进滚动日志而不是 Debug.WriteLine：后者在发布版里根本不存在，
                //   "诊断文件写不了"这种失败如果只有 Debug 版看得见，发布版就变成
                //   既没结果文件也没日志的双盲。
                ClashUWP.Vpn.RollingLog.Error("localstate-write", name, ex);
            }
        }

        /// <summary>追加写（OpenIfExists + Append）——哨兵 health.txt 这种流水账用。</summary>
        internal static async Task AppendTextAsync(string name, string content)
        {
            var f = await Folder.CreateFileAsync(name,
                Windows.Storage.CreationCollisionOption.OpenIfExists);
            await Windows.Storage.FileIO.AppendTextAsync(f, content);
        }

        /// <summary>删除，失败静默（命令文件常被并发读占住，删不掉下一轮再处理）。</summary>
        internal static async Task DeleteQuietAsync(Windows.Storage.StorageFile file)
        {
            try { await file.DeleteAsync(); }
            catch { /* 可能被并发读占住 */ }
        }
    }
}
