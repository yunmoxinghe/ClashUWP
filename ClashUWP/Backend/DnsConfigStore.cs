using System;
using System.Threading.Tasks;

using ClashUWP.Vpn;

namespace ClashUWP.Backend
{
    /// <summary>
    /// 【后端 · 前台侧 DNS 覆盖】<c>LocalState\dns-config.txt</c> 的读写口（1.0.40 · DNS 可配）。
    ///
    /// 【为什么不自己再写一份解析器】模型、白名单与逐条校验都在组件里
    ///   （<see cref="DnsConfig"/>，<c>ClashUWP.Vpn/DnsConfig.cs</c>）：那个文件是
    ///   <b>插件在 Connect / 热重载时读的</b>，前台与插件对同一份文本必须读出
    ///   <b>同一个结果</b>。这边只做两件事：
    ///     · 异步 IO（组件里的 <see cref="DnsConfig.Load"/> 为了在 WinRT 同步回调里用，
    ///       走的是同步等异步；前台在渲染线程上阻塞一次存储调用就是挂住 UI）；
    ///     · 把"上次连接实际应用了什么"从 <c>dataface-plugin.txt</c> 里取出来给界面看。
    ///
    /// 【为什么保存的是<b>原文</b>而不是 <see cref="DnsConfig.ToText"/>】这个文件的头部
    ///   注释就是使用说明，用户很可能在上面记自己的备注；应用分流的规则行是结构化产物，
    ///   而 DNS 这份是"一行一个键值"的配置文本 —— 原文写回、不丢注释，才是它该有的形态。
    ///   代价：保存不会帮你规范化拼写；被拒绝的行会在保存结果里逐条列出来（不静默）。
    /// </summary>
    internal static class DnsConfigStore
    {
        /// <summary>一次读的结果：编辑器原文 + 解析产物 + 插件上一次实际覆盖的结果。</summary>
        internal readonly record struct View(string Text, DnsConfig Parsed, string LastSnapshot);

        /// <summary>
        /// 读文件并解析。文件不存在 = 显示 <see cref="DnsConfig.ToText"/> 的模板正文
        /// （逐键说明都在里面），解析结果仍是空配置 —— 空配置不改内核，这是正常路径。
        /// </summary>
        internal static async Task<View> LoadAsync()
        {
            string text = "";
            try
            {
                Windows.Storage.StorageFile? file =
                    await LocalState.TryGetFileAsync(DnsConfig.FileName);
                if (file != null)
                    text = await LocalState.ReadTextAsync(file);
            }
            catch (Exception ex)
            {
                // 读失败不能装作"文件是空的"——那会让界面显示 0 项、文件里其实有内容。
                RollingLog.Error("dns", "读 dns-config.txt 失败", ex);
            }

            DnsConfig parsed = DnsConfig.Parse(text);
            if (text.Trim().Length == 0)
                text = parsed.ToText();       // 模板：头部注释 + 零条覆盖

            return new View(text, parsed, await LastSnapshotAsync());
        }

        /// <summary>
        /// 保存编辑器原文，回读并用<b>同一个</b> <see cref="DnsConfig.Parse"/> 解析。
        /// 返回的是"几项会被应用、几行被拒"的可读结果，不是一句"保存成功" ——
        /// 被挡掉的行必须当场看得见，否则就是"存下了但内核不认"的经典静默失败。
        /// </summary>
        internal static async Task<string> SaveAsync(string text)
        {
            await LocalState.WriteTextAsync(DnsConfig.FileName, text);

            Windows.Storage.StorageFile? back =
                await LocalState.TryGetFileAsync(DnsConfig.FileName);
            DnsConfig parsed = DnsConfig.Parse(
                back == null ? "" : await LocalState.ReadTextAsync(back));

            RollingLog.Info("dns",
                $"保存 dns-config：entries={parsed.Entries.Count} rejected={parsed.Rejected.Count}");

            if (parsed.Rejected.Count > 0)
                return $"已存文件，但 {parsed.Rejected.Count} 行会被忽略：" +
                       string.Join(" / ", ToList(parsed.Rejected));

            if (parsed.IsEmpty)
                return "已存文件：没有有效覆盖，内核配置不会被改动。";

            // ★ 生效时机与组件侧逐字一致：内核侧两条路都行；NRPT（nrpt-dns）随下一次
            //   StartWithTrafficFilter 下发，同样是断开再连，不需要重建 profile。
            return $"已存 {parsed.Entries.Count} 项：断开再连、或点「订阅 → 重新加载配置」热重载，" +
                   "内核侧都生效；nrpt-dns 改的是交给 Windows 的 DNS，" +
                   "要断开再连才换（另需 settings.txt: dnsInTunnel=1）。";
        }

        /// <summary>
        /// 从插件写的会话档案 <c>dataface-plugin.txt</c> 里取 <c>Dns=</c> 那一行
        /// （内核侧覆盖结果：应用几项 / 跳过什么 / 拒绝几行）。
        /// 取不到就明说，不猜 —— 与 <see cref="AppSplitStore.LastInjectionAsync"/> 同一口径。
        /// </summary>
        internal static async Task<string> LastSnapshotAsync()
        {
            try
            {
                string dump = await LocalState.ReadIfExistsAsync("dataface-plugin.txt");
                foreach (string raw in dump.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("Dns="))
                        return line["Dns=".Length..];
                }
                return "(没有记录：本机还没用这个版本连过一次)";
            }
            catch (Exception ex)
            {
                return $"(读取失败 {ex.GetType().Name})";
            }
        }

        private static System.Collections.Generic.List<string> ToList(
            System.Collections.Generic.IList<string> src)
        {
            // 显式循环而不是 LINQ：本包是 Native AOT，投影链越普通越好。
            var list = new System.Collections.Generic.List<string>(src.Count);
            foreach (string s in src) list.Add(s);
            return list;
        }
    }
}
