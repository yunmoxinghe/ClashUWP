using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using Windows.Storage;

using ClashUWP.Backend.Mihomo;
using ClashUWP.Vpn;

namespace ClashUWP.Backend
{
    /// <summary>
    /// 【后端 · 订阅管理器】G1 缺口的第一块落地：从 URL 拉订阅、落盘、更新、多 profile。
    ///
    /// 【★ 为什么必须做这个】
    ///   在此之前 <c>LocalState\config.yaml</c> <b>只能靠手工放进去</b>（10-16 06:00 那份就是）。
    ///   插件侧 <c>ClashVpnPlugin.LoadMihomoConfig</c> 会读它，但<b>没有任何代码路径能产生或更新它</b>
    ///   ⇒ 用户想换订阅只能连桌面手动拷文件。这是 Docs/12 §5 的 G1，第一号缺口。
    ///
    /// 【存储形态（刻意从简）】
    ///   · 订阅 URL 存在 <c>LocalState\settings.txt</c> 的 <c>subUrl=</c> 一行（与既有开关同一文件、
    ///     同一"键=值"行式格式，<see cref="AppSettings"/> 那套解析直接复用）；
    ///   · 拉下来的正文写 <c>LocalState\subscriptions\{profileId}.yaml</c>；
    ///   · <b>当前生效的那份</b>另存一份到 <c>LocalState\config.yaml</c> —— 因为插件侧
    ///     <c>LoadMihomoConfig</c> 只认这个固定文件名（见那里的注释），改它要动组件侧，
    ///     而组件侧改名涉及热重载/指纹一整套，这一轮不碰。
    ///
    /// 【★ 为什么用"复制"而不是"让插件读 subscriptions\{id}.yaml"】
    ///   单变量原则：这一轮只加"能换订阅"，不改插件侧任何行为。
    ///   插件侧读什么文件、怎么算指纹、怎么热重载，全都是已实测立住的（Docs/08/09/12），
    ///   动它就要重跑一整轮数据面验证。复制一份是零风险的接法。
    ///
    /// 【★ 拉取必须走系统代理（与 MihomoController 相反）】
    ///   <c>MihomoController</c> 那边 <c>UseProxy=false</c> 是硬约束（否则请求被自己下发的代理劫走）。
    ///   这边<b>正相反</b>：订阅 URL 是公网地址，而隧道还没建 / 或用户在墙内，
    ///   走系统代理才是能拉到的那条路。两边的差异写在各自注释里，别"统一"掉。
    ///
    /// 【★ 多 profile 是"多个文件"，不是"多个对象"】
    ///   刻意不做数据库/索引/JSON 清单：profile 数量是个位数，目录列一遍就够。
    ///   加一层持久化结构等于给"删文件"和"列表"两件事引入同步问题。
    /// </summary>
    internal static class SubscriptionManager
    {
        /// <summary>订阅正文存放目录（相对 LocalFolder）。</summary>
        internal const string DirName = "subscriptions";

        /// <summary>插件侧认的那个固定文件名（见 <c>ClashVpnPlugin.LoadMihomoConfig</c>）。</summary>
        internal const string ActiveName = "config.yaml";

        /// <summary>settings.txt 里存订阅 URL 的键。</summary>
        internal const string UrlKey = "subUrl";

        /// <summary>settings.txt 里存"当前 profile id"的键。</summary>
        internal const string ActiveKey = "subActive";

        /// <summary>
        /// settings.txt 里存订阅显示名的键（<c>subName:{id}</c>）。
        /// ★【1.0.72】名字来自拉取响应头：<c>Profile-Title</c> 或
        ///   <c>Content-Disposition</c> 的 filename（RFC 5987 的 filename*）。
        ///   实测本机 `starlink…` 机场的 filename* 解码就是「星链机场」；
        ///   之前只读正文、把名字丢了，UI 与快速设置才一直显示 URL 派生串。
        /// </summary>
        internal const string NameKey = "subName";

        internal sealed record Result(bool Ok, string Message, string? ProfileId = null);

        internal sealed record Profile(
            string Id,
            string Name,
            string Url,
            DateTime FetchedAt,
            long SizeBytes,
            int NodeCount,
            int RuleCount);

        // ── 拉取 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 拉一个订阅 URL，落成一份 profile，并（可选）立即设为生效。
        /// ★ 返回的 Message 是<b>给人看的原文</b>，失败时带 HTTP 状态码或异常类型 ——
        ///   UI 与 CLI 都直接显示它，不再包一层。
        /// </summary>
        internal static async Task<Result> FetchAsync(string url, bool activate)
        {
            if (string.IsNullOrWhiteSpace(url))
                return new Result(false, "订阅 URL 不能为空");

            url = url.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
                return new Result(false, $"不是合法的 http/https URL: {url}");

            string text;
            string displayName = "";
            try
            {
                using var http = new HttpClient(
                    new HttpClientHandler { UseProxy = true, AllowAutoRedirect = true });
                http.Timeout = TimeSpan.FromSeconds(30);

                // ★ 带 UA：部分机场对无 UA 的请求直接 403。
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                // ★【1.0.62 实测】部分机场还按 UA 里的产品标识返回不同格式：
                //   starlink.aliyundeepsearch.sbs 对 "ClashUWP/1.0" 返回 base64（v2ray 正文，
                //   被下面的形态校验判成"不像 Clash 订阅"），带 "Clash.meta/1.16.0" 才回 YAML；
                //   同 URL 单变量实测：ClashUWP/1.0→base64、Clash.meta/1.16.0→YAML、
                //   组合 "ClashUWP/1.0 Clash.meta/1.16.0"→YAML。故保留自身标识并附一个
                //   通用 Clash 产品标识（本内核就是 mihomo/Clash.meta 系，不算冒充）。
                req.Headers.TryAddWithoutValidation("User-Agent", "ClashUWP/1.0 Clash.meta/1.16.0");
                using var resp = await http.SendAsync(req);
                int code = (int)resp.StatusCode;
                if (!resp.IsSuccessStatusCode)
                    return new Result(false, $"拉取失败 HTTP {code}（{uri.Host}）");

                // ★ 先取显示名再读正文：机场把订阅名放在响应头里
                //   （Profile-Title / Content-Disposition），以前这里完全没看。
                displayName = SubscriptionName.FromHeaders(resp.Headers, resp.Content.Headers);

                text = await resp.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                return new Result(false, $"拉取异常: {ex.GetType().Name}: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(text))
                return new Result(false, "订阅内容为空");

            // ★ 最基本的形态校验：至少得有 proxies 或 proxies-providers 之一。
            //   不做完整 YAML 校验（那会把"机场换了格式"误判成"拉取失败"），
            //   只挡掉"拉到的是登录页/错误页"这种最常见的坑。
            if (!text.Contains("proxies") && !text.Contains("proxies-providers"))
                return new Result(false, "内容不像 Clash 订阅（找不到 proxies/proxies-providers）");

            string id = MakeId(url);
            try
            {
                await EnsureDirAsync();
                string path = PathFor(id);
                await WriteAllTextAsync(path, text);

                await SetUrlAsync(id, url);
                // ★ 响应头没给名字时保留上一次的名字，别用空串把好名字冲掉
                //   （机场偶尔会换返回形态；名字不该跟着抖动）。
                if (displayName.Length > 0)
                    await SetNameAsync(id, displayName);

                var info = SubscriptionView.Parse(text, text.Length, DateTime.Now);
                var list = await ListAsync();
                var self = list.FirstOrDefault(x => x.Id == id)
                           ?? new Profile(id,
                                  displayName.Length > 0 ? displayName : FallbackName(id, url),
                                  url, DateTime.Now, text.Length, info.NodeCount, info.RuleCount);

                // ★【1.0.62】为该订阅注册一个独立的系统 VPN profile（快速设置面板原生列出、
                //   点哪个连哪个）。无论是否立即启用都注册 —— 拉过的订阅都应在快速设置里可切。
                // ★【1.0.72】把注册结果带进返回值：以前这一步的结果只落日志，
                //   UI 永远显示"已拉取"，即使系统 profile 根本没建/没更新成功。
                // ★ 同一机场的多条链接可能给同一个名字（本机两份都是 iKuuu_V2）：
                //   快速设置靠 ProfileName 区分，重名时退回 host，保证两条都能认出来。
                var reg = await VpnProfileSetup.EnsureSubscriptionProfileResultAsync(
                    id, SystemProfileName(self, list));
                if (activate)
                {
                    // ★【1.0.79 · Docs/30 §8-D】先把正文复制到内核实际读的 config.yaml，再记生效、再热重载。
                    //   【曾经的坑】这一路只写了 subscriptions\{id}.yaml，config.yaml 是
                    //   <see cref="ActivateAsync"/> 才写的 —— 而紧接着的
                    //   <c>ReloadActiveConfigAsync</c> → <c>SubscriptionReload.RegenerateEffectiveFile()</c>
                    //   第一句就是"读 config.yaml，不存在返回 null"。
                    //   于是"拉取并启用"自己把热重载喂了个空，结果串末尾永远挂着那句
                    //   "没有可重载的生效配置：config.yaml 不存在…"，用户看到的就是
                    //   "拉下来了，但得再点一次启用才生效"（实测时间戳见 Docs/30 §8-D：
                    //   fetch 落盘 03:07:58 → config.yaml 直到 03:09:27 才出现）。
                    //   口径与 ActivateAsync 完全一致：同一份 text、同一个 ActiveName。
                    await WriteAllTextAsync(ActiveName, text);
                    await SetActiveAsync(id);
                    // ★ UX 对齐 Verge：拉取并启用后原地热重载，不必断重连。
                    var reload = await ReloadActiveConfigAsync();
                    return new Result(true,
                        $"已拉取 {info.NodeCount} 节点 / {info.RuleCount} 规则（{info.SizeBytes / 1024.0:F1} KB）　{reload.Message}　{reg.Message}",
                        id);
                }
                return new Result(true,
                    $"已拉取 {info.NodeCount} 节点 / {info.RuleCount} 规则（{info.SizeBytes / 1024.0:F1} KB）（未启用）　{reg.Message}",
                    id);
            }
            catch (Exception ex)
            {
                return new Result(false, $"落盘失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ── 切换 / 更新 ───────────────────────────────────────────────────

        /// <summary>
        /// 把某份 profile 设为生效 = 复制到 <c>config.yaml</c>。
        /// ★ 不重启内核：内核是常驻的，改了文件它不知道（这正是 1.0.49 加 <c>reload</c>
        ///   命令的原因）。所以这里只负责"文件就位"，由调用方决定要不要 reload。
        /// </summary>
        internal static async Task<Result> ActivateAsync(string id)
        {
            string src = PathFor(id);
            if (!await ExistsAsync(src))
                return new Result(false, $"profile 不存在: {id}");

            try
            {
                string text = await ReadAllTextAsync(src);
                await WriteAllTextAsync(ActiveName, text);
                await SetActiveAsync(id);
                var info = SubscriptionView.Parse(text, text.Length, DateTime.Now);
                // ★ UX 对齐 Verge：切到即原地热重载（连接中），未连接时保证文件就位、下次连接生效。
                var reload = await ReloadActiveConfigAsync();
                // ★【1.0.62】该订阅的 profile 已在 fetch 时注册；这里确保显示名与生效态一致（幂等）。
                var (_, url) = await GetActiveAsync();
                var list = await ListAsync();
                var self = list.FirstOrDefault(x => x.Id == id);
                string shown = self != null ? DisplayName(self, list) : FallbackName(id, url);
                string profName = self != null ? SystemProfileName(self, list) : FallbackName(id, url);
                var reg = await VpnProfileSetup.EnsureSubscriptionProfileAsync(id, profName);
                return new Result(true,
                    $"已切到「{shown}」：{info.NodeCount} 节点 / {info.RuleCount} 规则　{reload.Message}　{reg}", id);
            }
            catch (Exception ex)
            {
                return new Result(false, $"切换失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>按当前生效 profile 的 URL 重新拉一次（"更新订阅"）。</summary>
        internal static async Task<Result> UpdateActiveAsync()
        {
            var (id, url) = await GetActiveAsync();
            if (id.Length == 0 || url.Length == 0)
                return new Result(false, "尚未设置订阅 URL（先 fetch 一次）");
            return await FetchAsync(url, activate: true);
        }

        // ── 启动对账 / 快速面板回填（1.0.62）───────────────────────────────

        /// <summary>
        /// ★【1.0.62】给<b>已经拉过、但还没有系统 profile</b>的订阅补注册。
        ///
        /// 【为什么必须有这一步】系统 profile 只在 <see cref="FetchAsync"/>/
        /// <see cref="ActivateAsync"/> 时注册，而订阅往往是<b>升级前</b>就拉好的
        /// （1.0.59 就能 fetch）。不补这一下，升级到 1.0.62 后快速设置面板里
        /// 依然是空的 —— 功能等于没上线。
        ///
        /// ★★【1.0.72 修复 · 对账的是"全部订阅"，不是"只补缺的"】
        ///   1.0.62~1.0.71 先查有没有、有就跳过。于是 profile 的显示名 / ServerUri /
        ///   CustomConfiguration 一旦漂移（例如改了 profile-server.txt、或旧版本
        ///   把 subId 写错），启动对账<b>永远不会修</b>；而 UI 侧那次更新又走 Add
        ///   返回 Other（见 <see cref="VpnProfileSetup.EnsureSubscriptionProfileResultAsync"/>），
        ///   等于两条路都修不好。
        ///   现在每份订阅都过一遍幂等的 Ensure：一致 → 不动；不一致且已断开 → Update；
        ///   连接中 → 跳过并记日志。计数结果只落<b>一行</b>（调用方不再重复记）。
        ///
        /// ★【1.0.75 · 对账范围扩到"这个包的全部 profile"】上面那套只覆盖<b>订阅</b>，
        ///   于是留下两个系统侧现象（用户报的"订阅显示不正常"）：
        ///   ① <b>基础 profile 从不被对齐</b>：它还带着 1.0.72 之前写进去的
        ///      <c>http://127.0.0.1</c> ServerUri，而 1.0.73 那次盾标修复只改了新建路径
        ///      ⇒ 本机实测两条 profile 仍是 127.0.0.1:80，修复对它等于没发生。
        ///      现在每轮先过 <see cref="VpnProfileSetup.EnsureBaseProfileResultAsync"/>。
        ///   ② <b>幽灵条目不会被清掉</b>：删订阅时如果正连着，删除被收干净闸拒绝 ⇒
        ///      正文没了、面板那条还在，点它连不上又划不掉。
        ///      现在按本地清单 prune（<see cref="VpnProfileSetup.PruneOrphanProfilesAsync"/>，
        ///      只碰带 subId 且已断开的，删除照旧过那道闸；清单为空时不 prune）。
        /// 【不碰隧道】只写/删系统 profile 列表，不连接、不改 config.yaml。
        /// </summary>
        internal static async Task<string> SyncSystemProfilesAsync()
        {
            try
            {
                var list = await ListAsync();
                int added = 0, updated = 0, present = 0, skipped = 0, failed = 0;

                // ① 基础 profile 也进对账（它不在 list 里，以前没人管）
                var bas = await VpnProfileSetup.EnsureBaseProfileResultAsync();
                switch (bas.Action)
                {
                    case VpnProfileSetup.SubProfileAction.Added: added++; break;
                    case VpnProfileSetup.SubProfileAction.Updated: updated++; break;
                    case VpnProfileSetup.SubProfileAction.Unchanged: present++; break;
                    case VpnProfileSetup.SubProfileAction.Skipped: skipped++; break;
                    default: failed++; break;
                }

                // ② 每份订阅各过一遍幂等 Ensure
                foreach (var p in list)
                {
                    var reg = await VpnProfileSetup.EnsureSubscriptionProfileResultAsync(
                        p.Id, SystemProfileName(p, list));
                    switch (reg.Action)
                    {
                        case VpnProfileSetup.SubProfileAction.Added: added++; break;
                        case VpnProfileSetup.SubProfileAction.Updated: updated++; break;
                        case VpnProfileSetup.SubProfileAction.Unchanged: present++; break;
                        case VpnProfileSetup.SubProfileAction.Skipped: skipped++; break;
                        default: failed++; break;
                    }
                }
                // ③ 面板里"本地已经没有"的 profile 清掉（只碰已断开的，闸照过）
                string prune = await VpnProfileSetup.PruneOrphanProfilesAsync(
                    list.Select(p => p.Id).ToList());

                string msg = $"启动对账：订阅 {list.Count} 份 + 基础 profile 1 条，" +
                             $"新增 {added} / 更新 {updated} / 已就位 {present} / " +
                             $"跳过 {skipped} / 失败 {failed}；{prune}";
                RollingLog.Info("profile-sub-sync", msg);
                return msg;
            }
            catch (Exception ex)
            {
                // 对账失败不该影响启动（面板少几条，fetch/activate 时还会再补）。
                RollingLog.Warn("profile-sub-sync", $"失败: {ex.GetType().Name}: {ex.Message}");
                return $"启动对账失败: {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>
        /// ★【1.0.62】<b>快速面板切了订阅</b> → 前台把账跟上。
        ///
        /// 【触发方】MainComponent 的 3 秒轮询发现「已连接的那条 profile 带的 subId ≠ 当前生效 id」。
        /// 隧道里跑的已经是那份订阅（插件自己读的 subscriptions\{id}.yaml），
        /// 这里只补<b>书本账</b>：正文复制到 config.yaml + 记住生效 id，
        /// 否则 UI 的「生效中」「更新当前订阅」「重新加载配置」全都还冲着旧订阅。
        ///
        /// 【★ 刻意不做的事】
        ///   · 不热重载：内核跑的就是这份配置，再 PUT 一遍是无谓动作；
        ///   · 不调 EnsureSubscriptionProfileAsync：profile 此刻正连着，
        ///     对活动连接对象再 Add 一次没有实测背书，绝不在线上做。
        /// </summary>
        internal static async Task<Result> AdoptAsync(string id)
        {
            string src = PathFor(id);
            if (!await ExistsAsync(src))
                return new Result(false, $"订阅正文缺失，无法跟进: {id}");

            try
            {
                string text = await ReadAllTextAsync(src);
                var before = await GetActiveAsync();
                // ★【1.0.63】幂等短路：已生效就是这份 ⇒ 不再重复写（SetActive 是追加式，
                //   每 3 秒追一行 settings.txt 纯属噪音；且 config.yaml 被内核短暂占着时
                //   1.0.62 实测会刷 COMException 失败行）。写之前已 set 过才可能到这，
                //   所以跳过是安全的。
                if (string.Equals(before.Id, id, StringComparison.OrdinalIgnoreCase))
                    return new Result(true, $"生效订阅已是 {id}，无需回填", id);
                await WriteAllTextAsync(ActiveName, text);
                await SetActiveAsync(id);
                RollingLog.Info("profile-sub-adopt",
                    $"快速面板连接的订阅 {id} 与生效 {before.Id} 不同，已回填 config.yaml 并记为生效");
                return new Result(true, $"已跟进快速面板的切换 → {id}");
            }
            catch (Exception ex)
            {
                return new Result(false, $"回填失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ── 切/更新后原地热重载（UX 对齐 Verge）──────────────────────────

        /// <summary>
        /// 【UX 对齐 Verge】切/更新订阅后，生成内核实际读取的那份配置并热重载，
        /// 让新订阅<b>原地生效、不必断重连</b>。
        /// ★ 内核未连接时只保证文件就位（下次 Connect 自动加载），返回明确的"未连接"提示，
        ///   不会把"没热重载"伪装成失败。
        /// ★ 改写逻辑全部在 Vpn 组件内（<see cref="ClashUWP.Vpn.SubscriptionReload"/>），
        ///   本方法只负责"拿路径 → PUT /configs"，不重复实现 transform。
        /// </summary>
        internal static async Task<Result> ReloadActiveConfigAsync()
        {
            try
            {
                string? path = ClashUWP.Vpn.SubscriptionReload.RegenerateEffectiveFile();
                if (string.IsNullOrEmpty(path))
                    // ★【1.0.75】null 有两种成因，都得说给用户听：
                    //   组件侧"空壳不热重载"（改写结果零出站）也返回 null，
                    //   只报"config.yaml 不存在"会指错方向 —— 静默或指错的失败
                    //   在这个项目里一律算没修（口径同 1.0.35 的 splitNote 进日志）。
                    //   具体是哪一种看 app.log 的 config 频道：那里有原话理由。
                    return new Result(false,
                        "没有可重载的生效配置：config.yaml 不存在，或它不含任何可用出站" +
                        "（空壳不热重载，内核保持原有配置；理由见 日志 → config）");

                // 先探内核是否在跑：在跑才热重载，否则只保证文件就位（下次连接生效）。
                if (!await IsKernelUpAsync())
                    return new Result(true, "新订阅已落盘（内核未连接，下次连接自动加载）");

                string reload = await MihomoController.RunReloadAsync(path);
                return new Result(true, "已热重载内核: " + reload);
            }
            catch (Exception ex)
            {
                return new Result(false, $"热重载失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>内核 REST（127.0.0.1:9090）是否可达；2 秒超时，任何异常都算"没在跑"。</summary>
        private static async Task<bool> IsKernelUpAsync()
        {
            try
            {
                using var http = MihomoController.NewLoopbackClient(2);
                using var resp = await http.GetAsync("http://127.0.0.1:9090/version");
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>删一份 profile。★ 不许删当前生效的那份（删了 config.yaml 还在，会不同步）。</summary>
        internal static async Task<Result> DeleteAsync(string id)
        {
            var (activeId, _) = await GetActiveAsync();
            if (string.Equals(id, activeId, StringComparison.OrdinalIgnoreCase))
                return new Result(false, "不能删当前生效的订阅（先切到别的）");

            string path = PathFor(id);
            if (!await ExistsAsync(path))
                return new Result(false, $"profile 不存在: {id}");

            try
            {
                // ★★【1.0.72 修复 · 顺序反过来】先删系统 profile，成功后才删订阅正文。
                //   旧顺序（先删文件）在 1.0.62~1.0.71 留下过幽灵：会话收不干净时
                //   RemoveSubscriptionProfileAsync 拒绝删除，而文件已经没了 ——
                //   快速设置里那条 profile 还在，插件连上去却读不到 subscriptions\{id}.yaml。
                //   现在 profile 删不掉就整体判失败，正文保留，用户可重试；
                //   反过来（profile 已删、文件删除失败）重试一次即可收尾，不会留幽灵。
                var removal = await VpnProfileSetup.RemoveSubscriptionProfileAsync(id);
                if (!removal.Ok)
                    return new Result(false, $"未删除 {id}：{removal.Message}（订阅正文与生效记录保留）");

                var file = await GetFileAsync(path);
                if (file != null) await file.DeleteAsync();
                await ClearUrlAsync(id);
                return new Result(true, $"已删除 {id}（{removal.Message}）");
            }
            catch (Exception ex)
            {
                return new Result(false, $"删除失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ── 列表 ──────────────────────────────────────────────────────────

        internal sealed record ActiveInfo(string Id, string Url);

        /// <summary>当前生效的 profile id 与它的 URL。</summary>
        internal static async Task<ActiveInfo> GetActiveAsync()
        {
            var kv = await ReadSettingsAsync();
            string id = kv.TryGetValue(ActiveKey, out string? a) ? a : "";
            string url = id.Length > 0 && kv.TryGetValue(UrlKey + ":" + id, out string? u) ? u : "";
            return new ActiveInfo(id, url);
        }

        /// <summary>
        /// 列出所有 profile（按拉取时间倒序）。
        /// ★ 时间戳取<b>文件 mtime</b>，不另存元数据 —— 少一份要同步的状态。
        /// </summary>
        internal static async Task<List<Profile>> ListAsync()
        {
            var result = new List<Profile>();
            var kv = await ReadSettingsAsync();

            StorageFolder? dir = await GetFolderAsync(DirName);
            if (dir == null) return result;

            IReadOnlyList<StorageFile> files;
            try { files = await dir.GetFilesAsync(); }
            catch { return result; }

            foreach (StorageFile f in files)
            {
                string id = Path.GetFileNameWithoutExtension(f.Name);
                string url = kv.TryGetValue(UrlKey + ":" + id, out string? u) ? u : "";
                // 显示名：响应头给的名字优先；老订阅没有这个键时退回 host
                // （host 也比 id 短得多，不会再出现 `…-link-<token>` 那种长串）。
                string name = kv.TryGetValue(NameKey + ":" + id, out string? n) && !string.IsNullOrWhiteSpace(n)
                    ? n
                    : FallbackName(id, url);

                long size = 0;
                DateTime mtime = DateTime.MinValue;
                int nodes = 0, rules = 0;
                try
                {
                    var props = await f.GetBasicPropertiesAsync();
                    size = (long)props.Size;
                    mtime = props.DateModified.DateTime;

                    // ★ 元信息靠现成的 SubscriptionView，不再写第二套解析。
                    string text = await FileIO.ReadTextAsync(f);
                    var info = SubscriptionView.Parse(text, size, mtime);
                    nodes = info.NodeCount;
                    rules = info.RuleCount;
                }
                catch
                {
                    // 读不了一份也不能让整份列表失败，那份就显示成 0 节点。
                }

                result.Add(new Profile(id, name, url, mtime, size, nodes, rules));
            }

            result.Sort((a, b) => b.FetchedAt.CompareTo(a.FetchedAt));
            return result;
        }

        // ── settings.txt 读写（与 AppSettings 同一文件、同一格式）───────────

        /// <summary>
        /// 读 settings.txt 的"键=值"行。★ 只读我们这两个键，别的键原样留着 ——
        /// 这个文件同时被 <c>LocalSettings</c>（组件侧）和 <c>AppSettings</c>（前台）用，
        /// 谁都不能把它整个覆写掉。
        /// </summary>
        private static async Task<Dictionary<string, string>> ReadSettingsAsync()
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                StorageFile? f = await LocalState.TryGetFileAsync("settings.txt");
                if (f == null) return dict;
                string text = await LocalState.ReadTextAsync(f);
                foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
                {
                    string t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("#")) continue;
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    dict[t.Substring(0, eq).Trim()] = t.Substring(eq + 1).Trim();
                }
            }
            catch { /* 读不到就当没有，不影响调用方做别的事 */ }
            return dict;
        }

        /// <summary>
        /// 往 settings.txt 追一行 <c>key=value</c>。
        /// ★ 追加而不是重写：那两位（LocalSettings/AppSettings）各写各的键，
        ///   重写必然发生"后写的把先写的冲掉"。追加的代价是同键会出现多行 ——
        ///   读取时<b>后一行胜出</b>（<see cref="ReadSettingsAsync"/> 的循环天然如此），
        ///   所以语义上等价于"更新"。
        /// </summary>
        private static async Task AppendSettingAsync(string key, string value)
        {
            try
            {
                await LocalState.AppendTextAsync("settings.txt", $"{key}={value}\n");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"写 settings.txt 失败: {ex.Message}", ex);
            }
        }

        private static Task SetUrlAsync(string id, string url) => AppendSettingAsync($"{UrlKey}:{id}", url);
        private static Task SetActiveAsync(string id) => AppendSettingAsync(ActiveKey, id);
        private static Task ClearUrlAsync(string id) => Task.CompletedTask;   // URL 留着无害，删文件即可

        /// <summary>读某份 profile 的 URL（settings.txt 的 <c>subUrl:{id}</c> 行）。</summary>
        private static async Task<string> GetUrlAsync(string id)
        {
            var kv = await ReadSettingsAsync();
            return kv.TryGetValue(UrlKey + ":" + id, out string? u) ? u : "";
        }

        /// <summary>从 URL 取 host（没有响应头名字时的显示名回落）。</summary>
        private static string HostOf(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? u))
                return u.Host;
            return "";
        }

        private static Task SetNameAsync(string id, string name)
            => AppendSettingAsync($"{NameKey}:{id}", name);

        /// <summary>没有响应头名字时的回落：host → id，保证显示名永远非空。</summary>
        private static string FallbackName(string id, string url)
        {
            string host = HostOf(url);
            return host.Length > 0 ? host : id;
        }

        /// <summary>
        /// 给人看的名字（UI / CLI）。
        /// ★ 同一机场的多条链接会给出同一个名字（本机两份 iKuuu 的 filename 都是
        ///   <c>iKuuu_V2</c>）。列表里重名就追加 host，否则用户分不清点的是哪条；
        ///   唯一时不加后缀，保持「星链机场」这种干净显示。
        /// </summary>
        internal static string DisplayName(Profile p, IReadOnlyList<Profile> all)
        {
            if (!HasSameName(p, all)) return p.Name;
            string host = HostOf(p.Url);
            if (host.Length == 0) return p.Id;
            return string.Equals(host, p.Name, StringComparison.OrdinalIgnoreCase)
                ? p.Name
                : $"{p.Name}（{host}）";
        }

        /// <summary>
        /// 系统 VPN profile 的显示名（<c>ClashUWP · </c> 前缀由 VpnProfileSetup 加）。
        /// ★ 重名时退回 host：Windows 快速设置靠 ProfileName 区分条目，
        ///   两条同名 profile 既难辨认，也可能让平台拒绝新建。
        /// </summary>
        private static string SystemProfileName(Profile p, IReadOnlyList<Profile> all)
        {
            if (!HasSameName(p, all)) return p.Name;
            string host = HostOf(p.Url);
            if (host.Length == 0) return p.Id;
            // host 也撞（同 host 不同 path，MakeId 的已知边界）⇒ 用 id 保唯一。
            foreach (Profile q in all)
                if (q.Id != p.Id && string.Equals(HostOf(q.Url), host, StringComparison.OrdinalIgnoreCase))
                    return p.Id;
            return host;
        }

        private static bool HasSameName(Profile p, IReadOnlyList<Profile> all)
        {
            // O(n) 遍历，但 n 是个位数（订阅数量通常 ≤5），不需要 HashSet
            foreach (Profile q in all)
                if (q.Id != p.Id && string.Equals(q.Name, p.Name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // ── 文件小工具 ────────────────────────────────────────────────────

        /// <summary>由 URL 派生一个稳定的 profile id（同 URL 永远同 id）。</summary>
        private static string MakeId(string url)
        {
            // ★ 不用哈希：可读性优先（用户在列表里看到的是 host+短路径，不是 GUID）。
            //   冲突（同 host 同 path 不同 query）的概率低到可以忽略，且真冲突了
            //   表现为"更新了同一份"，不是数据丢失。
            string raw = "";
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? u))
            {
                raw = u.Host;
                string p = u.AbsolutePath.Trim('/');
                if (p.Length > 0) raw += "-" + p.Replace('/', '-');
            }
            // Windows 文件名禁用字符一律换成 '_'，再收敛长度。
            string clean = new string(raw.Select(ch =>
                Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray());
            if (clean.Length > 60) clean = clean.Substring(0, 60);
            return clean.Length == 0 ? "profile" : clean;
        }

        private static string PathFor(string id) => $"{DirName}/{id}.yaml";

        private static async Task EnsureDirAsync()
        {
            await ApplicationData.Current.LocalFolder
                .CreateFolderAsync(DirName, CreationCollisionOption.OpenIfExists)
                .AsTask();
        }

        private static async Task<bool> ExistsAsync(string relativePath)
            => await LocalState.TryGetFileByRelativePathAsync(relativePath) != null;

        private static async Task<StorageFile?> GetFileAsync(string relativePath)
            => await LocalState.TryGetFileByRelativePathAsync(relativePath);

        private static async Task<StorageFolder?> GetFolderAsync(string name)
            => await ApplicationData.Current.LocalFolder.TryGetItemAsync(name) as StorageFolder;

        private static async Task WriteAllTextAsync(string relativePath, string text)
        {
            StorageFolder folder = ApplicationData.Current.LocalFolder;
            if (relativePath.Contains('/'))
            {
                folder = await folder.GetFolderAsync(relativePath.Substring(0, relativePath.LastIndexOf('/')));
            }
            StorageFile file = await folder.CreateFileAsync(
                Path.GetFileName(relativePath), CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, text);
        }

        private static async Task<string> ReadAllTextAsync(string relativePath)
        {
            StorageFile? f = await LocalState.TryGetFileByRelativePathAsync(relativePath);
            return f == null ? "" : await FileIO.ReadTextAsync(f);
        }

        /// <summary>给人看的一行摘要（UI 与 debug 命令共用）。</summary>
        internal static string OneLine(List<Profile> profiles, ActiveInfo active)
        {
            if (profiles.Count == 0)
                return "（还没有订阅：用 fetch <URL> 拉一个）";

            var sb = new StringBuilder();
            sb.Append($"共 {profiles.Count} 份，生效={active.Id}({(active.Url.Length == 0 ? "无URL" : active.Url)})");
            foreach (var p in profiles)
            {
                bool isActive = string.Equals(p.Id, active.Id, StringComparison.OrdinalIgnoreCase);
                string shown = DisplayName(p, profiles);
                if (!string.Equals(shown, p.Id, StringComparison.Ordinal))
                    shown += $" [{p.Id}]";   // CLI 的 switch/delete 还要用 id
                sb.Append($"\n{(isActive ? "▶" : " ")} {shown}  {p.NodeCount}节点/{p.RuleCount}规则  " +
                          $"{p.SizeBytes / 1024.0:F1}KB  {p.FetchedAt:MM-dd HH:mm}");
            }
            return sb.ToString();
        }
    }
}
