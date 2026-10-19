using System;
using System.Threading.Tasks;

using ClashUWP.Vpn;

namespace ClashUWP.Backend
{
    /// <summary>
    /// 【后端 · 前台侧应用分流名单】<c>LocalState\split-apps.txt</c> 的读写口（G9）。
    ///
    /// 【为什么不自己再写一份解析器】模型与解析都在组件里
    ///   （<see cref="AppSplit"/>，<c>ClashUWP.Vpn/AppSplit.cs</c>）：那个文件是
    ///   <b>插件在 Connect 时读的</b>，前台与插件对同一份文本必须读出<b>同一个结果</b>。
    ///   两边各写一份解析器就是给自己留第二套真值表 —— 这条纪律与
    ///   <see cref="AppSettings"/> 里"布尔口径逐字照搬"是同一个理由，
    ///   只不过这里连"照搬"都省了：直接复用组件的 public 类型。
    ///   ★ 组件是 <c>InternalsVisibleTo("ClashUWP")</c> 的
    ///     （<c>ClashUWP.Vpn/AssemblyInfo.cs:13</c>），所以插件侧那些 internal 也看得见；
    ///     但 <see cref="AppSplit"/> 本来就该是 public，走的是投影后的正常形态。
    ///
    /// 【为什么前台全异步】与 <see cref="AppSettings"/> 同一条理由：
    ///   组件里的 <see cref="AppSplit.Load"/> 为了在 WinRT 同步回调里用，
    ///   走的是同步等异步；前台在渲染线程上阻塞一次存储调用就是挂住 UI。
    ///   ⇒ 前台只用 <see cref="AppSplit.Parse"/>/<see cref="AppSplit.ToText"/>
    ///     这两个纯函数，IO 由本类用 <c>await</c> 做。
    ///
    /// 【保存形态】走 <see cref="AppSplit.ToText"/>：文件头 + 规范化后的规则行。
    ///   ★ 代价要说明白 —— <b>手写的注释会被重写掉</b>。界面是"一条一条加/删"的形态，
    ///     不做原文增量编辑；想留注释就手改文件，改完别在界面上点保存。
    /// </summary>
    internal static class AppSplitStore
    {
        /// <summary>一次读的结果：原文 + 解析产物 + 插件上一次实际注入的情况。</summary>
        internal readonly record struct View(string Text, AppSplit Split, string LastInjection);

        /// <summary>读名单并解析。文件不存在 = 空名单，这是正常路径，不记警告。</summary>
        internal static async Task<View> LoadAsync()
        {
            string text = "";
            try
            {
                // ★ 不能图省事用 <c>LocalState.ReadIfExistsAsync</c>：那个"缺文件"的回落值
                //   是字面量 <c>"(无结果文件)"</c>（<c>LocalState.cs:85</c>），
                //   把它当文件内容交给 <see cref="AppSplit.Parse"/> 会得到一条
                //   "字段数不是 3：(无结果文件)"的假拒绝行 —— 全新装包本该是干净的空名单。
                //   ⇒ 这里自己走 TryGetFile 那一步，缺文件就是<b>真正的空串</b>。
                Windows.Storage.StorageFile? file =
                    await LocalState.TryGetFileAsync(AppSplit.FileName);
                if (file != null)
                    text = await LocalState.ReadTextAsync(file);
            }
            catch (Exception ex)
            {
                // 读失败不能装作"名单是空的"——那会让界面显示 0 条、而文件里其实有内容。
                RollingLog.Error("appsplit", "读名单失败", ex);
            }

            AppSplit split = AppSplit.Parse(text);
            string injection = await LastInjectionAsync();
            return new View(text, split, injection);
        }

        /// <summary>
        /// 从插件写的会话档案 <c>dataface-plugin.txt</c> 里取 <c>AppSplit=</c> 那一行。
        /// ★ 为什么值得取：这个功能最坏的失败形态是"界面存下了、内核配置里没那几行"
        ///   （rules 块缺失 / rules 是行内写法 / fakeIp 总闸关着 —— 三条都是静默路径）。
        ///   插件把注入结果写进了 <see cref="KernelConfig.Transform"/> 的返回值，
        ///   前台原样显示出来，用户才分得清"我没写"和"写了但没进去"。
        ///   取不到（没连过、或档案是旧版本写的）就明说，不猜。
        /// </summary>
        internal static async Task<string> LastInjectionAsync()
        {
            try
            {
                string dump = await LocalState.ReadIfExistsAsync("dataface-plugin.txt");
                foreach (string raw in dump.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("AppSplit="))
                        return line["AppSplit=".Length..];
                }
                return "(没有记录：本机还没用这个版本连过一次)";
            }
            catch (Exception ex)
            {
                return $"(读取失败 {ex.GetType().Name})";
            }
        }

        /// <summary>
        /// 保存名单。★ 返回的是<b>重新解析后</b>的畸形行清单，不是"保存成功/失败"一句话 ——
        /// 被挡掉的行（去向写了组名、标识里有 <c>| , '</c>…）必须当场看得见，
        /// 否则就是"存下了但内核不认"的经典静默失败。
        /// </summary>
        internal static async Task<string> SaveAsync(AppSplit split)
        {
            string text = split.ToText();
            await LocalState.WriteTextAsync(AppSplit.FileName, text);

            // 回读用 TryGetFile 而不是 ReadIfExistsAsync，理由同 LoadAsync：
            //   那个占位字符串一旦被当成文件内容送进解析器，界面上就会长出一条假拒绝行。
            Windows.Storage.StorageFile? back =
                await LocalState.TryGetFileAsync(AppSplit.FileName);
            AppSplit readBack = AppSplit.Parse(
                back == null ? "" : await LocalState.ReadTextAsync(back));

            RollingLog.Info("appsplit",
                $"保存 rules={readBack.Rules.Count} rejected={readBack.Rejected.Count}");

            if (readBack.Rejected.Count > 0)
                return $"已存 {readBack.Rules.Count} 条，但有 {readBack.Rejected.Count} 行被忽略：" +
                       string.Join(" / ", ToList(readBack.Rejected));
            if (readBack.Rules.Count == 0)
                return "名单为空 —— 内核配置不会被改动。";
            // ★ 生效时机的口径必须与组件侧一致：名单在<b>每次连接</b>（ClashVpnPlugin 步 4.1）
            //   和<b>每次热重载</b>（SubscriptionReload.RegenerateEffectiveFile）时各重读一次，
            //   所以"必须断开重连"是错的 —— 那句话会把用户推向不必要的一次拆隧道。
            return $"已存 {readBack.Rules.Count} 条：断开再连、或点「订阅 → 重新加载配置」热重载，都生效。";
        }

        private static System.Collections.Generic.List<string> ToList(
            System.Collections.Generic.IList<string> src)
        {
            // ★ 显式循环而不是 LINQ：本包是 Native AOT，投影链越普通越好，
            //   与 MainComponent.ToList 同一口径。
            var list = new System.Collections.Generic.List<string>(src.Count);
            foreach (string s in src) list.Add(s);
            return list;
        }
    }
}
