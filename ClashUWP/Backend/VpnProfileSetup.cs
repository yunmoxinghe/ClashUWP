using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

using Windows.Networking.Vpn;

using ClashUWP.Vpn;

namespace ClashUWP.Backend
{
    /// <summary>
    /// S1/S2 · 前台侧：建VPN profile 并发起连接。
    ///
    /// 【官方】<c>VpnManagementAgent</c>：<i>"Allows the creation, deletion, edit, connect, and
    /// disconnect flows for VPN. These can be used to manage the lifecycle of a VPN profile ..."</i>
    ///
    /// 调 <c>ConnectProfileAsync</c> 之后控制权就交回平台了：平台按
    /// <c>VpnPluginPackageFamilyName</c> 找到我们的包，拉起 <c>vpnClient</c> 后台任务
    /// （<see cref="VpnBackgroundTask"/>），后面的事都在插件那边。
    ///
    /// ⚠️ 所有 <c>Windows.Networking.Vpn</c> API 都要求 <c>networkingVpnProvider</c>
    ///    受限能力（见 Package.appxmanifest）。没有它这里会直接抛 AccessDenied。
    ///
    /// ★ 为什么每个方法都返回一整句话而不是一个枚举：
    ///   这套调用有好几个会各自失败的关键步骤（查列表 / 删旧 / 建 / 回读 / 连），
    ///   之前只把最后一个枚举丢给 UI，出了 <c>CannotFindProfile</c> 完全看不出
    ///   卡在哪一步 —— 而那恰恰是定位时最需要的信息。
    ///   骨架期宁可啰嗦，也不要一个看不出所以然的状态码。
    ///
    /// ★ 为什么「重建 profile」和「连接」是两个独立动作：
    ///   官方 sample 就是两个按钮（<c>CreateAppProfile</c> / <c>ConnectOnClick</c>），
    ///   中间隔着用户操作时间。之前我们把删、建、连三步塞进一次点击，
    ///   既漏了 sample 的「先 Disconnect 再 Delete」，又让刚建出来的 profile
    ///   未必已被平台索引 —— 两条都是自己给自己挖坑。
    /// </summary>
    internal static class VpnProfileSetup
    {
        /// <summary>
        /// 主 profile 名字。<b>不在这里定义字面量</b>：真正的唯一定义在组件的
        /// <c>GracefulTeardown.ProfileName</c>，因为收尾那边也要按同一个名字认人
        /// （见 <see cref="FindOurProfileAsync"/> 的注释），两处各写一遍早晚会漂移。
        /// </summary>
        internal const string ProfileName = ClashUWP.Vpn.GracefulTeardown.ProfileName;

        /// <summary>
        /// ★【1.0.81】这里原来有一份<b>自己的</b> <c>static readonly VpnManagementAgent</c> 缓存。
        ///   现在全进程只有 <see cref="ProfileStore"/> 那一份，且它把每次调用<b>串行化</b> ——
        ///   依据与死锁纪律写在 <see cref="ProfileStore"/> 的类注释里（一句话：
        ///   "句柄无效 0x80070006"只在连接 RPC 在飞的同时枚举 profile 存储时出现，
        ///   与本类用的是哪个 agent 实例无关）。本类所有 <c>GetProfilesAsync</c>/
        ///   <c>ConnectProfileAsync</c>/… 一律走 <c>ProfileStore.*</c>。
        /// </summary>
        private static string PackageFamilyName => Windows.ApplicationModel.Package.Current.Id.FamilyName;

        /// <summary>
        /// ★【1.0.79 · Docs/30 §8-F】这条 profile 的<b>名字</b>是否属于我们的产品族。
        /// 唯一定义在 <see cref="GracefulTeardown.IsOurName"/>，这里只是转发 ——
        /// 收尾侧和管理侧必须用同一把尺子，两处各写一遍早晚会漂移。
        /// </summary>
        private static bool IsOurName(string? name) => GracefulTeardown.IsOurName(name);

        /// <summary>是不是<b>当前包家族</b>建的（官方 sample 的口径，最优先）。</summary>
        private static bool IsCurrentFamily(VpnPlugInProfile pp) =>
            pp.VpnPluginPackageFamilyName == PackageFamilyName;

        /// <summary>
        /// ★【1.0.79 · Docs/30 §8-F】"是不是我们的 profile" = <b>当前家族</b> 或 <b>我们的名字族</b> 任一命中。
        ///
        /// 【为什么不能只看家族】包家族后缀是<b>由签名证书算出来的</b>：同 CN 换一张证书，
        /// 清单身份合法、数据面上却是另一个产品 —— 老 profile 的
        /// <c>VpnPluginPackageFamilyName</c> 不再等于当前包家族，于是启动对账看不见它们、
        /// prune 也清不掉它们 ⇒ 快速设置面板里挂着上一代产品的幽灵条目，当前代又新建一份同名的，
        /// 用户看到两条都以为能连（本轮实测形状：`启动对账：订阅 0 份 + 基础 profile 1 条，新增 1`，
        /// 而日志里 `profile 数=0`）。Docs/30 §5 记的那次重签（家族变成
        /// <c>8B8CC4F4.ClashUWP_4tjvwvefxrz66</c>）就是把这条踩实的。
        ///
        /// 【为什么还要"优先当前家族"】命中旧家族是为了<b>看得见了就能收尾</b>（迁移式清理），
        /// 不是为了拿它去连接 —— 旧家族的 <c>VpnPluginPackageFamilyName</c> 指向可能已注销的包，
        /// 平台能否拉起插件没有实测背书。所以所有"取一条来用"的地方走
        /// <see cref="PickOurs"/>：先同家族，取不到才退到名字族，并把这次"退路"记进日志。
        ///
        /// ★【快速面板改造】第三条判据：<see cref="VpnPluginConfig.IsOurSchema"/> ——
        ///   订阅 profile 改叫订阅名后（不再带 <c>ClashUWP · </c> 前缀），名字族对它失效，
        ///   换证书后只剩 CustomConfiguration 的 <c>&lt;clash&gt;</c> root 能认出它们。
        ///   与前两条同属"认领/看见"，"取对象来连"仍走 <see cref="PickOurs"/> 的家族优先序。
        /// </summary>
        private static bool IsOurs(VpnPlugInProfile pp) =>
            IsCurrentFamily(pp) || IsOurName(pp.ProfileName)
            || VpnPluginConfig.IsOurSchema(pp.CustomConfiguration);

        /// <summary>
        /// 从一堆 profile 里按"先同家族、后名字族"取第一条命中 <paramref name="match"/> 的。
        /// 命中旧家族时返回它并把 <c>tookForeign=true</c> 报出来 —— 调用方要能如实说
        /// "这条不是本包装的，是上一代签名身份的残留"（能力边界要可见，口径同 Docs/12 §6）。
        /// </summary>
        private static VpnPlugInProfile? PickOurs(
            IReadOnlyList<IVpnProfile> profiles,
            Func<VpnPlugInProfile, bool> match,
            out bool tookForeign)
        {
            tookForeign = false;
            foreach (IVpnProfile p in profiles)
                if (p is VpnPlugInProfile pp && IsCurrentFamily(pp) && match(pp))
                    return pp;
            foreach (IVpnProfile p in profiles)
                if (p is VpnPlugInProfile pp && IsOurs(pp) && match(pp))
                {
                    tookForeign = true;
                    return pp;
                }
            return null;
        }

        /// <summary>
        /// ★【1.0.96 · Docs/51】"这一条的状态读不出来"的专用文本。
        /// 与 <c>HealthMonitor.StatusUnreadable</c> 同值、同用途：<b>判据不可信</b>这个事实
        /// 本身要有一个值，绝不能混进 <c>Disconnected</c> 那一档。
        /// </summary>
        internal const string StatusUnreadable = "状态读不出";

        /// <summary>
        /// ★【1.0.96 · Docs/51】读<b>一条</b> profile 的连接状态，读不出回
        /// <see cref="StatusUnreadable"/> 而不是向上抛。
        ///
        /// 【要修的真症状】本机 2026-10-23 03:04~03:29 实测：切过订阅之后，某一条 profile
        ///   的 <c>ConnectionStatus</c> 属性读自己抛 <c>COMException 0x80070006（句柄无效）</c>，
        ///   杀进程重开也不恢复。而 <see cref="PickOurs"/> 这些扫描要<b>过清单里每一条</b>，
        ///   旧写法里那一句 <c>pp.ConnectionStatus.ToString()</c> 一抛，整次扫描就废了 ⇒
        ///   「当前状态」「已连接订阅」「断开」三项<b>一起变哑</b>，哪怕另外几条读数明明是好的。
        /// 【为什么按条目吞掉才是对的修法】Docs/45 记的"本包所有进程永久抛"这个结论，
        ///   现场复核下来真正的形状是：<b>坏的是那一条的属性，哑的是整面扫描</b> ——
        ///   同一时刻基础 profile 的读数完全正常（03:29:01 <c>profile-recreate … 状态=Disconnected</c>）。
        ///   降级到"这一条不知道"，其余条目照常可读，管理面不再被一条坏条目整体接管。
        /// 【为什么这里不记日志】本方法在 3 秒轮询的热路径上，
        ///   滚动日志的行格式是看门狗的机器契约、热路径禁记；
        ///   出声由调用方按边沿负责（<c>MainComponent</c> 的 <c>lastPollError</c> 那一套）。
        /// </summary>
        internal static string StatusOf(VpnPlugInProfile pp)
        {
            try
            {
                return pp.ConnectionStatus.ToString();
            }
            catch
            {
                return StatusUnreadable;
            }
        }

        /// <summary>
        /// 找出属于我们这个包的 plugin profile。
        ///
        /// 【官方 sample 的做法】<c>GetMatchingProfilesAsync</c>：
        /// <i>"In this file, GetProfilesAsync is only called here. Everything else should use the
        /// results of this call."</i>
        /// 它按<b>包 family name</b> 过滤，而不是只比名字 ——
        /// 因为系统里可能同时存在别的 VPN app 建的同名 profile。
        /// ★【1.0.79 · Docs/30 §8-F】在此之上再加"名字族"这条退路，且<b>同家族优先</b>：
        ///   换签名证书＝换包家族哈希，只按家族匹配会让老条目彻底隐形（详见 <see cref="IsOurs"/>）。
        ///   同名冲突的风险由"同家族优先 + 必须再按名字锁定主 profile"两层挡住。
        /// </summary>
        private static async Task<VpnPlugInProfile?> FindOurProfileAsync()
        {
            IReadOnlyList<IVpnProfile> profiles = await ProfileStore.GetProfilesAsync();
            VpnPlugInProfile? ours = PickOurs(profiles, pp => pp.ProfileName == ProfileName, out bool foreign);

            // sample 原文：var pluginProfile = profile as VpnPlugInProfile;
            if (ours != null)
            {
                if (foreign)
                    RollingLog.Warn("profile-find",
                        $"主 profile 命中的是旧签名家族的残留条目 family={ours.VpnPluginPackageFamilyName}");
                // ★ 必须再按名字锁定主 profile：探测会在同一包下留下
                //   ClashUWP-Probe / ClashUWP-Dataface，只按 family 匹配会
                //   「删错对象」（2026-10-15 实测：recreate 删掉了残留 Probe，
                //   主 profile 反而没被重建）。
                return ours;
            }
            // 主 profile 不存在就返回 null，让调用方走「新建」；绝不把探测杂 profile
            // 当主 profile 交出去。
            return null;
        }

        /// <summary>
        /// 【官方 sample 的 DeleteExistingProfile】：先 Disconnect 再 Delete。
        /// 顺序反了会失败 —— 一个还连着的 profile 是删不掉的。
        ///
        /// ★★ 但"先 Disconnect"这句 sample 原文里藏着一个它没讲的坑：
        ///   <c>DisconnectProfileAsync</c> 返回 <b>Ok</b> 只代表"断开请求已受理"，
        ///   不代表平台已经把会话收干净。2026-10-16 00:21 的事故就是照字面顺序
        ///   紧接着删，结果删在平台仍记 <c>CONN=1</c> 的时候 —— OS 直接扯掉接口，
        ///   既不回调插件 Disconnect，也不清它自己下发的 NRPT 与路由，
        ///   留下一条 Namespace=<c>.</c> 的孤儿 DNS 策略 + 死接口上的幽灵路由 = 整机断网。
        ///   所以这里必须过 <see cref="GracefulTeardown.EnsureTornDownAsync"/> 这道闸：
        ///   等不到收尾就<b>不删</b>，并把原因如实回话（宁可让调用方重试，
        ///   也不再拿用户的网卡赌一次）。
        /// </summary>
        private static async Task<string> DeleteOurProfileAsync()
        {
            VpnPlugInProfile? profile = await FindOurProfileAsync();
            if (profile == null)
                return "删旧=无";

            (bool ok, string report) = await GracefulTeardown.EnsureTornDownAsync(profile);
            if (!ok)
            {
                // ★ 这是一条必须进发布版日志的告警：它意味着"我们主动放弃了删除"，
                //   而放弃删除正是防止整机断网的闸生效。只看返回值看不出来它救过一次。
                RollingLog.Warn("profile-delete-refused", $"主 profile 会话未收干净，拒绝删除 —— {report}");
                return $"删旧=拒绝（{report}）";
            }

            VpnManagementErrorStatus del = await ProfileStore.DeleteProfileAsync(profile);
            if (del != VpnManagementErrorStatus.Ok)
            {
                RollingLog.Warn("profile", $"主 profile 删除失败={del}");
                return $"删旧=失败({del})";
            }
            RollingLog.Info("profile", "主 profile 已删除（重建流程的删旧步）");
            return "删旧=完成";
        }

        /// <summary>
        /// 【官方 sample 的 CreateAppProfile】：建一个新 profile。
        /// </summary>
        private static async Task<VpnManagementErrorStatus> AddProfileAsync(VpnPluginConfig config, string serverUri)
        {
            var pluginProfile = new VpnPlugInProfile
            {
                ProfileName = ProfileName,

                // ★【官方】VpnPluginPackageFamilyName："Gets or sets the package family name of
                //   the VPN plug-in to be used for this VPN plug-in profile. **This field is
                //   required for VpnPlugInProfile to be valid.**"
                VpnPluginPackageFamilyName = PackageFamilyName,

                // 【官方】RequireVpnClientAppUI："determines whether this VPN profile appears in
                //   the system's VPN connection UI."
                //   ★【1.0.115】与订阅条目互斥：panelMultiEntry=0（默认）时基础条目可见
                //   （面板唯一一条 "ClashUWP" = 开关）；panelMultiEntry=1 时基础条目隐藏，
                //   面板只列订阅条目（旧行为）。两种模式不同时出现。
                RequireVpnClientAppUI = !AppSettings.PanelMultiEntry,

                // 【官方】CustomConfiguration："an XML string to configure the VPN plug-in. Refer
                //   to the documentation from the VPN plug-in for format and support."
                //   → 格式自定义，schema 见 VpnPluginConfig.ToXml()。
                CustomConfiguration = config.ToXml(),

                // AlwaysOn 会让系统在登录时自动连 —— 骨架期先关掉，免得调试时甩不掉。
                AlwaysOn = false,
            };

            // 【官方】ServerUris 是 IVpnProfile 的成员；sample 填一个占位 Uri。
            // 我们没有真实服务器（数据面在进程内），填本地占位。
            //
            // ⚠️ 千万别写端口 0（最早那版写的是 http://127.0.0.1:0）：0 不是有效端口。
            //   这次排查发现一个很坑的后果 —— AddProfileFromObjectAsync 照样返回 Ok、
            //   GetProfilesAsync 也照样能读到这个 profile，但 ConnectProfileAsync 会报
            //   CannotFindProfile，因为平台解析不了这个端点。
            //   也就是说：**建 profile 的返回值 Ok 不代表这个 profile 是可连的。**
            pluginProfile.ServerUris.Add(new Uri(serverUri));

            // 【官方】AddProfileFromObjectAsync(IVpnProfile)："Creates a new VPN Profile based on
            //   the VpnProfile input object."
            return await ProfileStore.AddProfileFromObjectAsync(pluginProfile);
        }

        /// <summary>
        /// 读 <c>LocalState\profile-server.txt</c> 的第一行作为 profile 的 ServerUri。
        ///
        /// ★【1.0.73 · WiFi盾标修复】默认值从 <c>http://127.0.0.1</c> 改为 <c>http://192.0.2.1</c>
        ///   （TEST-NET-1，RFC 5737 保留地址，保证静默黑洞）。
        ///
        /// 【为什么必须改】127.0.0.1 会导致 NCSI 探测包打到环回接口，NCSI 报告
        ///   <c>Capability=Local</c> 而非 <c>Internet</c>，Windows 认为 VPN 只是本地隧道，
        ///   WiFi 图标不显示蓝色盾牌。192.0.2.1 是 RFC 5737 明确保留的文档地址段，
        ///   任何路由器都不会转发它，也不会和真实网络冲突，且不会回 ICMP ——
        ///   NCSI 的被动探测（PassivePacketHops）能正确判断为"可达 Internet"。
        ///
        /// 【口径】文件里没有效行 / 文件不存在 / 不是绝对 URI ⇒ 一律回新默认值。
        ///   必须是带 scheme 的绝对地址（如 <c>http://192.168.0.1</c>）：
        ///   <c>new Uri("192.168.0.1")</c> 会当成相对地址不报错，但平台用不了，
        ///   所以这里显式要求 <c>UriKind.Absolute</c>。
        /// </summary>
        private static async Task<string> ReadProfileServerOverrideAsync()
        {
            // ★ 192.0.2.1 = TEST-NET-1（RFC 5737），保证静默黑洞，不回 ICMP、不被路由。
            const string fallback = "http://192.0.2.1";
            try
            {
                Windows.Storage.StorageFile file = await Windows.Storage.ApplicationData.Current.LocalFolder
                    .GetFileAsync("profile-server.txt");
                string text = await Windows.Storage.FileIO.ReadTextAsync(file);
                foreach (string raw in text.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#"))
                        continue;
                    return new Uri(line, UriKind.Absolute).ToString();
                }
            }
            catch
            {
                // 缺文件/读失败/非法 URI 都是正常路径，不抛给调用方
            }
            return fallback;
        }

        /// <summary>
        /// 「建 / 重建 profile」按钮。完全照 sample 的形状：先删后建，然后<b>立刻回读验证</b>。
        ///
        /// 回读这一段是特意加的：AddProfileFromObjectAsync 返回 Ok 并不保证 profile
        /// 真的落库、真的可连。上一版就是建完直接连，结果profile 里还留着旧代码写进去的
        /// 无效 ServerUris，而由于改成了「存在就复用」，它永远修不好。
        /// </summary>
        public static async Task<string> RecreateProfileAsync(VpnPluginConfig? config = null)
        {
            config ??= new VpnPluginConfig();
            var sb = new StringBuilder();

            // ★★【1.0.101 · Docs/52】重建 = 删一条 + <b>Add 一条</b>，而"会话挂着时 Add"正是
            //   Docs/45 §八 那条最可疑的成因形状。以前这条路只在"删旧收不干净"时才被
            //   <see cref="DeleteOurProfileAsync"/> 拦住（判据只盯基础条目自己）——
            //   隧道挂在<b>别的</b>条目上时，它会照常把基础条目删掉再新建，一次都不拦。
            //   从此统一走写入闸：不干净就整轮拒绝，并给出出口（先断开再重建）。
            (bool writable, string writeProof) = await ProveStoreWriteSafeAsync();
            if (!writable)
            {
                string holdMsg = $"重建=跳过（{writeProof} ⇒ 会话期间不往 profile 存储里写条目）" +
                                 "：请先断开当前连接，再点「建/重建 profile」";
                RollingLog.Info("profile-recreate", holdMsg);
                return holdMsg;
            }

            // 先清扫探测残留（异常/中止的连接会留下 Connecting 态的 Probe/Dataface，
            // 它们与本包同 family，会干扰按 family 匹配的各处逻辑）。
            await DeleteByNameAsync("ClashUWP-Probe");
            await DeleteByNameAsync("ClashUWP-Dataface");

            sb.Append(await DeleteOurProfileAsync());

            // ★【1.0.72】删旧没完成就<b>不许新建</b>：以前无论删除结果都继续 Add，
            //   遇到会话收不干净被闸拒绝时，Add 只会撞重名（返回 Other），
            //   日志里看起来像"重建失败"，实际是同一份 profile 还在。
            //   正确做法是把原因如实回给调用方，让用户先断开再重建。
            string del = sb.ToString();
            if (del != "删旧=完成" && del != "删旧=无")
            {
                RollingLog.Warn("profile-recreate", "删旧未完成，放弃新建 —— " + del);
                return sb.Append("；新建=跳过（删旧未完成，避免重复 profile）").ToString();
            }

            // ★ server 地址可以在不重装包的前提下换（LocalState/profile-server.txt）。
            //   为什么要留这个口子：run 14 实测「节点 /32 排除路由被平台装到了环回接口
            //   ifIdx=1」，而平台要给排除路由挑接口，挑的依据最可疑的就是 profile 的
            //   ServerUris —— 我们当时填的是 http://127.0.0.1，等于告诉平台
            //   "VPN 服务器在环回上"。换掉它必须是单变量实验，所以那次只加了覆盖口子、
            //   默认值仍留 127.0.0.1，并把用到的值原样写进返回值。
            // ★【1.0.73】默认值已按盾标/NCSI 那条结论换成 <c>http://192.0.2.1</c>
            //   （见 ReadProfileServerOverrideAsync），这个口子留作单变量取证用。
            // ★【1.0.75】注意这条路径<b>只在建/重建时用得到默认值</b>：已存在的 profile
            //   不会被 RecreateProfileAsync 之外的任何路径改写，所以"改了默认值但机器上
            //   还是旧地址"要靠 <see cref="EnsureBaseProfileResultAsync"/> 的启动对账解决。
            string server = await ReadProfileServerOverrideAsync();

            VpnManagementErrorStatus add = await AddProfileAsync(config, server);
            sb.Append($", 新建={add}, server={server}");
            if (add != VpnManagementErrorStatus.Ok)
                return sb.ToString();

            VpnPlugInProfile? now = await FindOurProfileAsync();
            if (now == null)
                return sb.Append(", 回读=读不到！").ToString();

            sb.Append($", 回读=找到[{now.ProfileName}]");
            sb.Append($", ServerUris={now.ServerUris.Count}个");
            foreach (Uri uri in now.ServerUris)
                sb.Append($"({uri})");
            sb.Append($", 状态={StatusOf(now)}");

            // ★【1.0.112 · Docs/57】基础条目重建完，顺手把<b>订阅条目</b>也删旧建新一次。
            sb.Append(await RefreshSubscriptionEntriesAsync());

            RollingLog.Info("profile-recreate", sb.ToString());
            return sb.ToString();
        }

        /// <summary>
        /// ★【1.0.112 · Docs/57】「建/重建 profile」从此也刷新<b>订阅条目</b>。
        ///
        /// 【为什么做这条】rasphone.pbk 里每条 profile 有一行 <c>ManagementApp=</c>，
        /// 值是<b>创建那一刻</b>的完整包名（含版本号，实测形如
        /// <c>8B8CC4F4.ClashUWP_1.0.104.0_x64__4tjvwvefxrz66</c>）。就地升级<b>不会</b>
        /// 改写它 —— 1.0.111 装机后实测：刚重建过的 [ClashUWP] 是新戳 1.0.111.0，
        /// 而 [iKuuu_V2]、[星链机场] 仍是 1.0.104.0 / 1.0.98.0（备份目录里 1.0.14/1.0.38
        /// 的老戳同构，证明这是创建时写入、永不回写）。只有<b>删掉再 Add</b> 才会换戳。
        /// 之前只有基础条目走这条路（<see cref="RecreateProfileAsync"/> 删建 ClashUWP 一条），
        /// 订阅条目没有任何出口 —— 用户在快速设置面板点另一条订阅时卡「正在验证你的登陆信息」
        /// 那一次，本包 0 条插件日志、原会话连续存活 583 s 纹丝不动，请求死在到达插件之前，
        /// 与 Docs/43「另一条占着会话时管理面没有中止原语」是同一个形状。
        ///
        /// ★【诚实边界】"面板那条字符串就是按 <c>ManagementApp</c> 解析包失败打出来的"
        ///   <b>没有证据</b>（这个键没有任何官方文档，rasdial/Get-VpnConnection 也看不见它）。
        ///   本方法修的是<b>已实测到的陈旧</b>，不是已证得的因果。改完必须重测面板切换。
        ///
        /// 【不碰的东西】只删本包名下<b>带 subId</b> 的条目（Probe/Dataface 由上游已清；
        ///   基础条目无 subId，刚在本方法里重建过）；每条删除照旧过
        ///   <see cref="GracefulTeardown.EnsureSessionTornDownAsync"/> 那道闸；
        ///   重加走 <see cref="SubscriptionManager.SyncSystemProfilesAsync"/>，
        ///   不自己拼 profile（名称规则只有 SubscriptionManager 知道）。
        /// 【调用时机】只在整轮已经过完 <see cref="ProveStoreWriteSafeAsync"/>（无会话）之后调用。
        /// 【逐条 try】读 <c>ProfileName</c>/<c>CustomConfiguration</c> 正是 Docs/45/51
        ///   那条按条目中毒会抛的位置（0x80070006），一条读不出不能把整轮带走。
        /// </summary>
        private static async Task<string> RefreshSubscriptionEntriesAsync()
        {
            var names = new List<string>();
            try
            {
                IReadOnlyList<IVpnProfile> profiles = await ProfileStore.GetProfilesAsync();
                foreach (IVpnProfile p in profiles)
                {
                    try
                    {
                        if (p is not VpnPlugInProfile pp || !IsOurs(pp))
                            continue;
                        if (pp.ProfileName is "ClashUWP-Probe" or "ClashUWP-Dataface")
                            continue;
                        if (VpnPluginConfig.Parse(pp.CustomConfiguration).SubId.Length == 0)
                            continue;
                        names.Add(pp.ProfileName);
                    }
                    catch (Exception ex)
                    {
                        RollingLog.Warn("profile-recreate",
                            $"订阅条目有一条读不出，本轮不刷新它：{ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                return $"；订阅条目刷新=跳过（清单读失败 {ex.GetType().Name}: {ex.Message}）";
            }

            if (names.Count == 0)
                return "；订阅条目刷新=无（没有带 subId 的条目）";

            var per = new List<string>();
            foreach (string name in names)
                per.Add($"[{name} {await DeleteByNameAsync(name)}]");

            // 删完立刻按本地清单补回来（同一轮内完成，不留"面板条目凭空少几条"的窗口）。
            string resync = await SubscriptionManager.SyncSystemProfilesAsync("重建");
            return $"；订阅条目刷新=删 {names.Count} 条 {string.Join(" ", per)} → {resync}";
        }

        /// <summary>
        /// 「连接 VPN」按钮 / 快速设置点某条订阅。<b>只连接，不建 profile</b> —— 与官方 sample 的分工一致。
        ///
        /// ★【1.0.62】<paramref name="subId"/> 非空时连<b>该订阅专属</b>的 profile
        ///   （<c>ClashUWP · xxx</c>，CustomConfiguration 带 subId）；为空时连基础 profile
        ///   <c>ClashUWP</c>（读 config.yaml 生效副本）。两条路都走系统
        ///   <c>ConnectProfileAsync</c> → 平台 → <c>IVpnPlugIn.Connect</c>。
        ///
        /// ★【1.0.93 · 删 hook 批】原先这里为了防"假绿灯"在 <c>Ok</c> 之后轮询真实状态，
        ///   <b>本批已删除</b>（对照 Docs/44 §一 #9）。防误读改由<b>措辞</b>承担：
        ///   回话与日志只说「受理」，绝不再出现"已连接"字样；"已连接"三个字由
        ///   <c>MainComponent</c> 的 3 秒轮询给（官方用 500 ms timer 干同一件事，sample :260/:268）。
        ///
        /// ★★【1.0.93 · 用户指令「把和官方示例不同的 hook 方法都删掉 —— 前台进程那部分」】
        ///   本方法与官方 <c>ConnectOnClick:465-518</c> 同形：取目标 profile →
        ///   只在 <c>Disconnected</c> 上发 <c>ConnectProfileAsync</c> → 按返回值回话。
        ///   <b>不</b>先收尾别条上的活动会话（<c>SerializeBehindActiveSessionAsync</c> 已删除）、
        ///   <b>不</b>拒绝发起第二条。代价写死在这里：官方建模"至多一条活动会话"靠的是
        ///   "可连清单只收 Disconnected"（sample :111）<b>加上</b>那句 <c>:87</c> 注释，
        ///   它并没有真的写闸 —— 于是 A 处于 <c>Connecting</c> 时拨 B 会进
        ///   Docs/43 clean-room 量到的那个形态（一次请求 ≥5 分 56 秒不返回、期间无出口）。
        ///   本批就是要把这个官方形状装到机上做对照，不要再拿我们的闸替平台兜。
        /// </summary>
        public static async Task<string> ConnectAsync(string? subId = null)
        {
            Func<Task<VpnPlugInProfile?>> finder = subId == null
                ? (Func<Task<VpnPlugInProfile?>>)FindOurProfileAsync
                : () => FindProfileBySubIdAsync(subId);

            VpnPlugInProfile? target = await finder();
            if (target == null)
                return subId == null
                    ? "找不到我们的 profile，请先点「建/重建 profile」"
                    : $"找不到订阅 profile（subId={subId}），请先拉取并启用该订阅";

            // ★【1.0.93 删 hook】这里原先夹着 SerializeBehindActiveSessionAsync 那道"先收尾再拨"闸，
            //   删除后本方法只剩官方那三步：取对象 → 判前置 → 发一次请求。
            (bool ok, string msg) = await ConnectWithPoisonRepairAsync(
                target, finder, "profile-connect", "连接请求");
            _ = ok; // 回话文本本身已带结论，调用方（按钮/CLI）只展示文本
            return msg;
        }

        /// <summary>
        /// ★【1.0.96 · Docs/51】拨一次；<b>只有失败在"状态读数中毒"这个形状上</b>时，
        /// 先对这条做一次复位（拆掉平台记着的那场会话），成功了才重拨。两条拨号路径
        /// （「连接 VPN」按钮与切换订阅）共用本方法，不再各自决定要不要自愈。
        /// ★★【1.0.98 建档 / 1.0.99 按实测改道 · Docs/51 §七】档位：
        ///   ①正常拨（官方前置条件）⇒ ②复位；复位没把读数治好就<b>当场收手并把出口回给用户</b>
        ///   （不再删不再建 —— 见 <see cref="RepairPoisonedProfileAsync"/> 的实测依据）
        ///   ⇒ ③复位成功但重拨时读数<b>又</b>抛，才走 <see cref="BlindDialAsync"/> 盲拨一次。
        ///   全都只在"读状态异常"这一个形状上触发，别的失败形状一律原样回话。
        ///
        /// 【为什么要专门认这个形状】<see cref="ConnectOnceAsync"/> 的官方前置条件要先读
        ///   <c>ConnectionStatus</c>；Docs/45 那条中毒让这个读<b>永久抛</b>，于是这一次拨号
        ///   根本没发出去，而用户看到的只有一句 <c>未发起=读状态异常…</c> ——
        ///   1.0.62~1.0.95 期间这条路上没有任何出口（除了只覆盖基础 profile 的「建/重建」按钮）。
        /// 【为什么只重试一次、且只在这一个形状上】其余失败（平台拒绝、被串行层拦住、
        ///   Connecting 态）都有各自的处置口径，拿动存储去赌它们等于无谓动用户的网卡。
        /// </summary>
        private static async Task<(bool Ok, string Message)> ConnectWithPoisonRepairAsync(
            VpnPlugInProfile target, Func<Task<VpnPlugInProfile?>> finder,
            string category, string dialLabel)
        {
            (bool ok, string msg) = await ConnectOnceAsync(target, category, dialLabel);
            if (ok || !IsStatusReadFailure(msg))
                return (ok, msg);

            (bool recovered, string repair) = await RepairPoisonedProfileAsync(target);
            if (!recovered)
            {
                // ★【1.0.99 · Docs/51 §七.7 实测 B】复位没把读数治好 ⇒ 这条名下平台仍记着一场会话
                //   （1.0.98 的盲拨那一档两次回 AlreadyConnected）。此时任何"删了再建"都是
                //   2026-10-16 00:21 整机断网的成因形状，而继续硬拨只会再进 Docs/43 那个 ≥5m56s 悬挂
                //   —— 所以到此为止，把出口回给用户。
                //   ★★【1.0.100 · Docs/51 §七.9】句序必须是"人话在前、判据在后"：这一串原样进
                //   主页提示行，而那条只有一两行宽 —— 05:50:01 装机实测把 `未发起=读状态异常…`
                //   顶在最前，界面画到"读数仍不可信（会话记录"就被截没了，**唯一能动作的那半句
                //   （去 设置→VPN）用户根本看不见**。`未发起=` 这个 token 仍留在串里
                //   （<see cref="IsNoDialResult"/> 用 Contains 认，位置无关），哨兵口径不变。
                return (false,
                    $"没敢硬拨：「{target.ProfileName}」这条在系统里还挂着一场旧会话，本应用不删条目、不硬拨。" +
                    "请先打开 系统设置→VPN（或 ncpa.cpl）把这条断开或重连一次，仍不行就重启系统清掉那场会话。" +
                    $"内部判据：{msg}；{repair}");
            }

            // ★ 复位之后<b>必须重新取对象</b>再去拨：官方已知硬约束（<see cref="ExplainDialError"/>
            //   的 CannotFindProfile 那条）是"自建对象拨不动"，只能拿清单里那个。
            VpnPlugInProfile? fresh = await finder();
            if (fresh == null)
                return (false, msg + $"；{repair}；重拨=未发起=复位后找不到条目（先跑一次对账）");

            (bool ok2, string msg2) = await ConnectOnceAsync(fresh, category, dialLabel);
            if (ok2 || !IsStatusReadFailure(msg2))
                return (ok2, msg + $"；{repair}；重拨={msg2}");

            // ★【1.0.98 · Docs/51 §七】走到这儿的是"断开把读数治好了、下一次读又抛"这一支 ——
            //   最后一档盲拨（前置条件没法用，改由 ProveNoLiveSessionAsync 那两条替它担保）。
            //   建档时这里曾是"删+重建后新对象照样抛"的主通道（05:15:35 / 05:29:24 / 05:31:09 三次），
            //   1.0.99 把删除那一步取消后它退化成兜底。
            (bool blindOk, string blind) = await BlindDialAsync(fresh, category, dialLabel);
            if (blindOk)
                // 拼出来的这句里<b>不许</b>出现 <c>未发起=</c>：这一枪真发出去了，
                // 前缀一出现前台就按"没拨"处理并把刚武装的健康哨兵解除（见 BlindDialAsync 注释）。
                return (true, $"首次与重拨的前置读状态都抛（0x80070006）；{repair}；{blind}");

            return (false,
                $"没敢硬拨：「{fresh.ProfileName}」这条在系统里还挂着一场旧会话，本应用不删条目、不硬拨。" +
                "请先打开 系统设置→VPN（或 ncpa.cpl）把这条断开或重连一次，仍不行就重启系统清掉那场会话。" +
                $"内部判据：{msg}；{repair}；重拨={msg2}；盲拨未成功={blind}");
        }

        /// <summary>
        /// ★【1.0.96 建档 / 1.0.99 按实测改道 · Docs/51 §七】对一条"状态读数中毒"的 profile 做<b>复位</b>：
        ///   现在这一支只做一件事 —— <b>对这条发一次平台断开，并等它的读数恢复</b>，不动存储。
        ///   返回 (读数是否恢复可信, 一句话结论)。
        ///
        /// 【为什么不再"删了重建"（建档时的设计）】装机实测（05:15:35 / 05:29:24 / 05:31:09，同一个形状三次）：
        ///   ① 删除+<c>新建=Ok</c> 之后，<b>新对象、新进程的读数照样抛</b> ⇒ 重建对坏读数没有疗效；
        ///   ② 而平台对重建后的新条目回 <c>AlreadyConnected</c> ⇒ 它<b>一直记着这条上有一场会话</b>。
        ///   合起来就是：②说"有会话"，那①里那次删除正是 2026-10-16 00:21 整机断网（孤儿 NRPT + 死接口幽灵路由）
        ///   的成因形状"删在了平台仍记着会话的那一刻" —— 一个被实测证明没用的动作，不值得再拿用户的网卡去赌。
        ///   ★ 建档时那条"03:29:01 重建后读数恢复正常"的凭据也一并作废：基础条目 <c>[ClashUWP]</c>
        ///     在 03:26:54 的 dump 里本来就读得出（它<b>没病</b>），我拿它当"疗效"是读错了对照组。
        ///
        /// 【为什么判据仍然先行】<see cref="ProveNoLiveSessionAsync"/> 那两条（清单内无 Connected/Connecting、
        ///   默认路由不在我们条目上）现在只当<b>下限</b>用：实测已经证明它们<b>不足以</b>断定"没会话"
        ///   —— 两条全成立的同时平台回 <c>AlreadyConnected</c>。所以真正确认"会话没了"的证据改成
        ///   <see cref="DisconnectAndWaitReadableAsync"/>：读数从抛变成能读。
        ///   口径不变：判据不可信（默认路由归属读不到、或别的条目正挂着会话）就一个字都不动。
        /// </summary>
        private static async Task<(bool Recovered, string Message)> RepairPoisonedProfileAsync(
            VpnPlugInProfile poisoned)
        {
            string name;
            try { name = poisoned.ProfileName; }
            catch (Exception ex)
            {
                string s = $"复位=放弃（连 ProfileName 都读不出：{ex.GetType().Name}）";
                RollingLog.Warn("profile-repair", s);
                return (false, s);
            }

            (bool clean, string proof) = await ProveNoLiveSessionAsync();
            if (!clean)
            {
                string refuse = $"复位=拒绝（{proof}）⇒ 到 系统设置→VPN（或 ncpa.cpl）把这条断开后再点连接";
                RollingLog.Warn("profile-repair", $"[{name}] {refuse}");
                return (false, refuse);
            }

            // ★★【1.0.99 · Docs/51 §七.4】先对<b>这一条</b>发断开并等读数恢复 —— 这一步同时决定了"能不能往下走"：
            //   05:29:24 / 05:31:09 两次实测里，平台对新条目回 <c>AlreadyConnected</c>，
            //   即"这条 profile 上仍记着会话"。那一刻做删除＝ 2026-10-16 那次整机断网（孤儿 NRPT + 死接口
            //   幽灵路由）的成因形状，所以<b>读数没恢复就绝口不提删除</b>；恢复了也不用删（会话已走，
            //   条目内容归 Docs/50 的对账管），复位这一支就此变成"只拆会话、不动存储"。
            (string clearLine, bool recovered) = await DisconnectAndWaitReadableAsync(poisoned);
            if (!recovered)
            {
                string refuse = $"复位=拒绝（{clearLine}；平台仍记着这条上的会话 ⇒ 此刻删除正是 2026-10-16 断网的形状，不动）" +
                                "⇒ 出口：系统设置→VPN（或 ncpa.cpl）里把这条断开/重连，或重启系统清掉该会话记录";
                RollingLog.Warn("profile-repair", $"[{name}] {refuse}");
                return (false, refuse);
            }

            string done = $"复位={clearLine}（会话记录已拆，条目不动、不删不重建）";
            RollingLog.Warn("profile-repair", $"[{name}] {done}");
            return (true, done);
        }

        /// <summary>
        /// ★【1.0.98 · Docs/51 §七】复位之后<b>读数仍然</b>抛时的最后一档：跳过官方"先读状态再拨"那句
        ///   前置条件，直接发一次 <see cref="ProfileStore.ConnectProfileAsync"/>。
        ///   ★【1.0.99】<see cref="RepairPoisonedProfileAsync"/> 现在没把读数治好就收手，所以这一档
        ///   只剩"断开治好了、下一次读又抛"这一条窄路能走到 —— 它从主出口退化成兜底。
        ///
        /// 【凭什么敢跳过前置条件】前置条件存在的唯一目的，是"别在已经有一条活会话的 profile 上硬拨"
        ///   （Docs/43 量到过这一次 await ≥5 分 56 秒不返回、期间管理面无出口）。
        ///   这里改由 <see cref="ProveNoLiveSessionAsync"/> 那两条<b>与 <c>ConnectionStatus</c> 无关</b>的
        ///   判据替它担保：清单里没有 <c>Connected/Connecting</c>、且默认路由不在我们任何一条条目上。
        ///   ★★ 但这两条已被装机实测证明<b>不足以</b>断定"没会话"（05:29:24 / 05:31:09：两条全成立，
        ///   平台照样回 <c>AlreadyConnected</c>）。所以这一档不再把判据当终点：真拿到
        ///   <c>AlreadyConnected</c> 时，改走 <see cref="DisconnectAndWaitReadableAsync"/> 把这场会话
        ///   拆掉、等读数恢复后再按官方前置条件重拨一次；拆不开就如实回话、绝不硬来。
        ///   判据不干净（别的条目挂着会话、默认路由归属读不到）就一个字都不发，且回话必须走
        ///   <c>未发起=</c> 前缀（机器契约，见 <see cref="IsNoDialResult"/>）。
        /// 【为什么这一支必须让哨兵活着】真发出去过一次请求，回话里就<b>不许</b>再出现
        ///   <c>未发起=</c> 前缀 —— <see cref="IsNoDialResult"/> 是前台"解除/保留健康哨兵"的机器契约，
        ///   留着它等于隧道起来了却没人兜底（2026-10-15 那次整机网络被占 20 分钟就是这个形状）。
        ///   组合消息因此由调用方（<see cref="ConnectWithPoisonRepairAsync"/>）按成败分别拼。
        /// </summary>
        private static async Task<(bool Ok, string Message)> BlindDialAsync(
            VpnPlugInProfile fresh, string category, string dialLabel)
        {
            (bool clean, string proof) = await ProveNoLiveSessionAsync();
            if (!clean)
            {
                string stop = $"未发起=不发：复位后判据仍不可信（{proof}）";
                RollingLog.Warn(category, $"[{fresh.ProfileName}] {stop}");
                return (false, stop);
            }

            RollingLog.Info(category,
                $"[{fresh.ProfileName}] 发起前状态=读数仍不可信 发起=盲拨（判据：{proof}；" +
                "下一行才是平台返回值；官方对该 await 没承诺时限）");
            try
            {
                VpnManagementErrorStatus conn = await ProfileStore.ConnectProfileAsync(fresh);
                if (conn == VpnManagementErrorStatus.AlreadyConnected)
                {
                    // ★★【1.0.99 · Docs/51 §七.4】装机实测两次都是这个码（05:29:24 / 05:31:09）：
                    //   平台<b>明确回答</b>"这条 profile 上还有会话"。这一句把两件事一次定死 ——
                    //   ① 中毒条目<b>确实</b>挂着会话（不是"读不出所以没会话"）；
                    //   ② <see cref="ProveNoLiveSessionAsync"/> 那两条判据<b>不足以</b>证明没会话
                    //     （清单内无 Connected/Connecting、默认路由在 WLAN，全都成立，会话照样在）。
                    //   1.0.98 在这一码之后直接把条目删了（且只对中毒的旧对象发过断开、发完 200 ms 内就删），
                    //   所以本批的处置改成：对<b>手上这个新对象</b>发断开，并<em>等</em>读数自己恢复 ——
                    //   读数恢复可读就是"这条的会话记录真的没了"唯一看得见的证据。
                    (string clearLine, bool recovered) = await DisconnectAndWaitReadableAsync(fresh);
                    if (!recovered)
                    {
                        string still = $"盲拨={conn}（★平台拒绝：{ExplainDialError(conn)}）；{clearLine}";
                        RollingLog.Warn(category, still);
                        return (false, still);
                    }
                    (bool okR, string msgR) = await ConnectOnceAsync(fresh, category, dialLabel);
                    string line = $"{clearLine}；会话记录已消失 ⇒ {dialLabel}={msgR}";
                    RollingLog.Info(category, line);
                    return (okR, line);
                }
                if (conn != VpnManagementErrorStatus.Ok)
                {
                    string bad = $"盲拨={conn}（★平台拒绝：{ExplainDialError(conn)}）";
                    RollingLog.Warn(category, bad);
                    return (false, bad);
                }
                string good = $"盲拨=受理=Ok（判据：{proof}；隧道是否起来以前台轮询为准）";
                RollingLog.Info(category, good);
                return (true, good);
            }
            catch (ProfileStore.ProfileStoreBusyException ex)
            {
                // 串行层没放行 ⇒ 这一枪根本没发出去，前缀按机器契约写成"未发起"。
                string blocked = $"未发起={ex.Message}";
                RollingLog.Warn(category, blocked);
                return (false, blocked);
            }
        }

        /// <summary>
        /// ★【1.0.99 · Docs/51 §七.4】对<b>这一条</b>发一次平台断开，然后等它的 <c>ConnectionStatus</c> 恢复可读。
        ///
        /// 【为什么"等读数恢复"就是判据】中毒的形状是"这条名下那条会话的句柄已死"（§七.4），
        ///   所以 <c>ConnectionStatus</c> 从抛变成能读，等价于"那条会话记录被拆掉了"。
        ///   这比再扫一遍清单强：清单里那条<b>正是</b>读不出的，而 <see cref="ProveNoLiveSessionAsync"/>
        ///   已经实测证明它看不见这个会话。
        /// 【只等不催】每 500 ms 读一次属性（叶子读，不进 <see cref="ProfileStore"/> 的写预算），
        ///   默认上限 15 s；超时如实回 <c>仍不可信</c>，由调用方原样把"平台仍记着会话"报给用户。
        /// 【不记轮询噪声】热路径禁记（滚动日志行格式是重拨保险丝的机器契约），只在结束时记一行结论。
        /// </summary>
        private static async Task<(string Line, bool Readable)> DisconnectAndWaitReadableAsync(
            VpnPlugInProfile p, int timeoutMs = 15000)
        {
            string ret;
            try
            {
                ret = (await ProfileStore.DisconnectProfileAsync(p)).ToString();
            }
            catch (Exception ex)
            {
                ret = $"抛 {ex.GetType().Name}";
            }

            for (int waited = 0; ; waited += 500)
            {
                if (StatusOf(p) != StatusUnreadable)
                    return ($"对这条断开={ret}，+{waited}ms 后读数恢复（{StatusOf(p)}）", true);
                if (waited >= timeoutMs)
                    return ($"对这条断开={ret}，等 {timeoutMs}ms 读数仍不可信（会话记录还在）", false);
                await Task.Delay(500);
            }
        }

        /// <summary>
        /// ★【1.0.96 · Docs/51】"我们的 profile 上有没有活着的会话"，两条<b>与 <c>ConnectionStatus</c> 无关</b>的判据。
        /// 返回 (可以安全删除, 依据一句话)。
        ///
        /// 【① 全清单扫】<see cref="StatusOf"/> 之后每条单独降级，一条坏条目不再拖死整次扫描：
        ///   任何一条（不含 Probe/Dataface 残留）报 <c>Connected</c>/<c>Connecting</c> 就拒绝。
        /// 【② 默认路由的归属】我们的 profile 是 <c>0.0.0.0/0</c> 全路由 ⇒ 会话活着时
        ///   <c>GetInternetConnectionProfile()</c> 拿到的就是这条 profile 本身。
        ///   这一步走的是网络栈，不碰 VPN 管理面那个坏句柄，正好补上判据①唯一的盲区
        ///   （中毒那一条自己读不出，谁也不能仅凭"其它条都干净"断定它没会话）。
        ///   <b>读不到归属（空串）也拒绝</b>：判据不可信就不动手，口径同 <c>PhysicalNetworkProbe</c> 的 <c>trusted</c>。
        /// 【★【1.0.101 · Docs/52】<paramref name="ownerUnknownIsDirty"/> 这个参数为什么存在】
        ///   上面那条严格性是为<b>删除</b>服务的（删错一条 = 2026-10-16 整机断网）。
        ///   同一把尺子拿去判"能不能 Add/Update"时，严格性会误伤：整机本来就没网的时候
        ///   <c>GetInternetConnectionProfile()</c> 返回 null，于是<b>没有任何会话</b>的机器上
        ///   反而不许注册 profile —— 拉完订阅连条目都建不出来。写入的blast radius 与删除不同，
        ///   所以这里开一个口子：<b>宽松模式下"读不到归属"只降级为一条附注，不是拒绝</b>；
        ///   判据①（清单里任一条 Connected/Connecting）照旧是硬闸，一条都不松。
        /// </summary>
        private static async Task<(bool Clean, string Proof)> ProveNoLiveSessionAsync(
            bool ownerUnknownIsDirty = true)
        {
            IReadOnlyList<IVpnProfile> profiles = await ProfileStore.GetProfilesAsync();
            var ours = new List<string>();
            foreach (IVpnProfile p in profiles)
            {
                if (p is not VpnPlugInProfile pp || !IsOurs(pp)) continue;
                if (pp.ProfileName is "ClashUWP-Probe" or "ClashUWP-Dataface") continue;
                ours.Add(pp.ProfileName);
                string st = StatusOf(pp);
                if (IsActiveSessionStatus(st))
                    return (false, $"本包 [{pp.ProfileName}] 状态={st}");
            }

            string owner = "";
            string ownerNote = "";
            try
            {
                owner = Windows.Networking.Connectivity.NetworkInformation
                    .GetInternetConnectionProfile()?.ProfileName ?? "";
            }
            catch (Exception ex)
            {
                ownerNote = $"读持有默认路由的链路抛 {ex.GetType().Name}";
            }

            if (owner.Length > 0)
            {
                foreach (string n in ours)
                    if (string.Equals(n, owner, StringComparison.OrdinalIgnoreCase))
                        return (false, $"默认路由就在本包条目 [{owner}] 上");
                return (true, $"清单内无 Connected/Connecting，默认路由在「{owner}」");
            }

            if (ownerUnknownIsDirty)
                return (false, ownerNote.Length > 0 ? ownerNote : "读不到持有默认路由的链路");
            return (true, "清单内无 Connected/Connecting（" +
                    (ownerNote.Length > 0 ? ownerNote + "，" : "默认路由归属读不到，") +
                    "写入不需要这条判据）");
        }

        /// <summary>
        /// ★★【1.0.101 · Docs/52】"此刻能不能往 profile 存储里写（Add / Update）"。
        ///
        /// 【为什么要单独一道闸，而不是复用删除那道】Docs/45 §八 的第一手形状是
        ///   <c>03:05:25.830 profile-sub 订阅profile[星链机场] subId=starlink… 新建=Ok</c>
        ///   —— 那一刻 <c>[iKuuu_V2]</c> 的隧道从 03:03:56 就挂着（03:05:28 的
        ///   <c>profile-sub-adopt</c> 还写着"面板连的是 hkom1… 与生效 starlink… 不同"）。
        ///   之后<b>这条新加的</b> profile 的 <c>ConnectionStatus</c> 永久抛 0x80070006，
        ///   跨 4 个进程不自愈、05:15 整条删掉重盖过一次照样抛（Docs/51 §七.3）。
        ///   因果仍未证明（n=1，只有相关性，见 Docs/51 §四），但代价不对称：
        ///   拦一次 Add 只让面板条目晚几秒出现，漏一次 Add 可能造出一条不可自愈的僵尸条目。
        /// 【两条判据，任一命中即不写】（1.0.109 前是三条，见下面的订正）
        ///   ① 任一本包条目 <c>Connected</c>/<c>Connecting</c>（逐条降级读，坏一条不拖死整轮）；
        ///   ② 默认路由就落在本包某条条目上 —— 这条走网络栈，不碰管理面那个坏句柄，
        ///      正好补上①的盲区（中毒那条自己读不出，看着像"没会话"）。
        /// 【★【1.0.109 · 订正 Docs/52】原来的判据 ③「前台有一次订阅切换在飞」已删】
        ///   它 1.0.101 立起来时是代替"真的去看有没有会话"的近似：那时切换不改旧会话，
        ///   窗口里旧隧道未必收尾，所以宁可整段不写。三段式之后这个前提反了 ——
        ///   <see cref="VpnSession.SwitchSubscriptionAsync"/> 的"后修改"恰恰跑在
        ///   <see cref="SwitchInProgress"/> 窗口<b>之内</b>，而且它开跑之前①已经
        ///   用官方那把尺子确认台上没有会话（<c>TornDown</c>）或本来就没有（<c>NoSession</c>）。
        ///   留着③ = 切换自己需要的 Add/Update 被③欠进台账，面板条目要等下一次断开才跟，
        ///   正是本批要消灭的症状复发。删掉之后守这一段的只剩①②，而它们问的正是真值本身。
        ///   <see cref="VpnSession.SwitchInProgress"/> 仍然留给轮询做让路用（回填 / 边沿 / 补账），
        ///   那条纪律与这里无关，别顺手一起删。
        /// 【与删除那道闸的唯一区别】"默认路由归属读不到"在这里只降级成附注（整机没网时
        ///   不能因此连条目都建不出来）；删除路径照旧从严。见
        ///   <see cref="ProveNoLiveSessionAsync"/> 参数注释。
        /// 【★ 一条已经中毒的旁观条目不挡写入】它的读数抛 ⇒ <see cref="StatusOf"/> 回
        ///   <c>状态读不出</c>，那不是 <see cref="IsActiveSessionStatus"/> 里的会话态。
        ///   否则本机现在就有的 <c>[星链机场]</c> 会把此后所有对账永久锁死。
        ///   ★【1.0.110 · Docs/55 §八】这条口子是<b>明知故留</b>的，别再把它读成"写入闸能看见会话"：
        ///   Docs/51 §七.4 两次实测证明"清单干净 + 默认路由在 WLAN"时平台仍可能回 <c>AlreadyConnected</c>。
        ///   切订阅那一路现在在<b>第一段</b>就把这种情形单独立成
        ///   <see cref="SwitchSeat.SessionStateUnknown"/>（照写；★ 1.0.113 起这一支会<b>先对读数坏掉的
        ///   条目逐条发断开</b>，等到任一读数恢复就改判 <c>TornDown</c> 并重拨，全不恢复才停在"不重拨"），
        ///   判据不许再用"当前无活动会话"这种肯定句式冒充。
        /// </summary>
        internal static async Task<(bool Writable, string Proof)> ProveStoreWriteSafeAsync()
        {
            return await ProveNoLiveSessionAsync(ownerUnknownIsDirty: false);
        }

        /// <summary>
        /// ★【1.0.88 · Docs/38 F1+F2】按官方 sample 的契约拨<b>一次</b>，顺序写死在这里：
        ///   ① <b>前置条件</b>：只有 <c>Disconnected</c> 才发起（sample L496）。
        ///     <c>Connected</c> ⇒ 无事可做（Ok=true，交回话说明）；
        ///     <c>Connecting/Disconnecting</c> ⇒ 平台正在这条上处理，<b>不硬拨</b>、回"稍后再试"。
        ///   ② <b>发起之前</b>先落一行 —— 这条是本症状唯一的观测出口：
        ///     <c>ConnectProfileAsync</c> 的 await 官方<b>没有任何时限承诺</b>（Docs/38 D8），
        ///     先例里它挂过 &gt;3.5 min（研究档案 docs/60 §3.1）。"拿到返回值再拼一行"的旧写法
        ///     在悬挂时那一行永远不落，日志看着就像"用户没点过"。
        ///     ★【1.0.89】这一行落在①<b>之后</b>（1.0.88 装机时误写在①之前，"已连接再点一次"会出现
        ///       相邻两行自相矛盾；订正见 Docs/38 §13.5 与 §十四）。它仍在唯一一个无界 await 之前，②的性质不丢。
        ///   ③ 拿到返回值<b>单独</b>落第二行并按 <see cref="VpnManagementErrorStatus"/> 分支：
        ///     非 <c>Ok</c> 一律按失败回话，枚举名原样带出去（历史实例：<c>Docs/30:378</c> 那次
        ///     <c>连接请求=ServerConnection</c> 就是这样被插值进一行文本里冲掉的）。
        /// ★★【1.0.93 · 删 hook 批】原来的第 ④ 步（<c>Ok</c> 之后轮询真实状态到 <c>Connected</c>
        ///   才算成，Docs/44 §一 #9「我们比官方强」那一条）<b>已删除</b>。
        ///   官方在这里就结束了：<c>ConnectOnClick:508-514</c> 只看返回值打一行，状态交给 timer
        ///   （sample :260 的 500 ms）。所以本方法的 <c>Ok</c> 从此<b>只代表平台受理</b>，
        ///   回话文本相应改成「受理=Ok（不代表隧道已建立）」—— 判据字符串变了，
        ///   任何按 <c>实际状态=Connected</c> 建的装机判据（Docs/38 §十三、Docs/44 §三 臂 B）
        ///   在本批之后读不到，改看 <c>受理=Ok</c> + 前台轮询那一行。
        /// ★ 拨号前不再回读目标对象（<c>RefreshForDialAsync</c> 已随收尾闸一起删除）：
        ///   它存在的唯一理由是"上一道闸刚断过这条、手里的快照可能过期"，闸没了这个前提就没了；
        ///   官方 <c>:506</c> 用的就是清单里那个对象本身。少一次 <c>GetProfilesAsync</c>
        ///   也少一次占住 <see cref="ProfileStore"/> 写预算的机会（Docs/38 §十一那条排队）。
        /// </summary>
        /// <param name="dialLabel">回话里那次请求的名字：「连接 VPN」用 <c>连接请求</c>，
        ///   切换路径用 <c>新连接请求</c> —— 装机验收靠这两个词分辨是哪条路发起的。</param>
        private static async Task<(bool Ok, string Message)> ConnectOnceAsync(
            VpnPlugInProfile target, string category, string dialLabel)
        {
            string st0;
            try
            {
                st0 = target.ConnectionStatus.ToString();
            }
            catch (Exception exRead)
            {
                // ★【1.0.93】改判异常类型：这个 try 里的 RPC 没了，剩下的唯一失败形状就是
                //   <b>属性读本身抛</b>（Docs/45 那条 0x80070006 读数中毒的现场形状）。
                //   读不到状态就不能凭猜测拨号（官方 <c>:506</c> 的前置条件要求先判状态），
                //   走"未发起="前缀让 <see cref="VpnSession"/> 认出这一句根本没拨。
                string pre = $"未发起=读状态异常: {exRead.GetType().Name}: {exRead.Message}";
                RollingLog.Warn(category, pre);
                return (false, pre);
            }
            string head = $"[{target.ProfileName}] 发起前状态={st0}";

            // ② 前置条件按官方 sample：只有 <c>Disconnected</c> 才允许硬拨。
            switch (st0)
            {
                case "Connected":
                    string already = head + " 未发起=平台报告这条已经 Connected（会话已在，不再拨第二次）";
                    RollingLog.Info(category, already);
                    return (true, already);
                case "Connecting":
                case "Disconnecting":
                    string busy = head + $" 未发起=平台正在这条 profile 上处理（{st0}），稍后再试";
                    RollingLog.Warn(category, busy);
                    return (false, busy);
            }

            // ③ 发起前留痕 —— 必须落在这一次 await <b>之前</b>。
            //   ★【1.0.89 · Docs/38 §13.5 措辞债订正】这一行原来写在上面那个 switch <b>之前</b>，
            //     于是"已连接时再点一次"会在日志里留下相邻两行自相矛盾：
            //     「发起=即将发出连接请求」紧跟「未发起=平台报告这条已经 Connected」（1.0.88 装机实测 B2/B5/B6 各一次）。
            //     挪到 switch 之后不丢 F1 的性质：它仍然在<b>本方法唯一一个无界 await</b>
            //     （<see cref="ProfileStore.ConnectProfileAsync"/>，下面那一句）之前，
            //     悬挂时照样看得见这条发起行；而"根本没拨"的那些分支从此只有一行、不再谎报"即将发出"。
            RollingLog.Info(category, head + " 发起=即将发出连接请求（下一行才是平台返回值；官方对该 await 没承诺时限）");

            VpnManagementErrorStatus conn;
            try
            {
                conn = await ProfileStore.ConnectProfileAsync(target);
            }
            catch (ProfileStore.ProfileStoreBusyException ex)
            {
                // ★【1.0.88 · Docs/38 §十一】串行层拒绝发这一条（上一条 profile 存储调用至今未返回）。
                //   走"未发起="那个前缀，是为了让 <see cref="VpnSession"/> 认出"这一句根本没拨"，
                //   从而按有无既有哨兵分别处置（见 IsNoDialResult 的注释）。
                string blocked = head + " 未发起=" + ex.Message;
                RollingLog.Warn(category, blocked);
                return (false, blocked);
            }
            if (conn != VpnManagementErrorStatus.Ok)
            {
                string bad = head + $" {dialLabel}={conn}（★平台拒绝：{ExplainDialError(conn)}）";
                RollingLog.Warn(category, bad);
                return (false, bad);
            }
            // ★【1.0.93 删 hook】官方到此为止（sample :508-514 只看返回值），
            //   下面这一段"受理后再轮询 20 次拿真实状态"是我们自己加的，已随本批删除。
            RollingLog.Info(category, head + $" {dialLabel}=受理=Ok（平台已接受请求；★这一句不代表隧道已建立）");
            return (true, head + $" {dialLabel}=受理=Ok（平台已接受请求，隧道是否起来以前台轮询为准）");
        }

        // ★★★【1.0.93 · 本批删除】SerializeBehindActiveSessionAsync（1.0.88 的 Docs/38 F3 闸）
        //   —— 「拨另一条之前先把当前活动会话收尾，收不干净就拒绝发起」。
        //   删除理由见 ConnectAsync 的方法注释：官方没有这道闸，它只有 sample :87 那句
        //   "There should be zero or one of these at all times" 注释 + :111 的 Disconnected 清单，
        //   Docs/43 的 clean-room 已证明"注释写了、闸没写"在官方原样代码里就足以卡死。
        //   本批按用户指令把前台这类自有拦截全部去掉，装成与官方同形的对照组。
        //   ⚠️ 随之消失的回话词：<c>新连接=跳过</c>（<see cref="IsNoDialResult"/> 认的两个前缀里
        //   现在只剩 <c>未发起=</c> 还会产生；前缀识别保留不动，它是 VpnSession 的机器契约）。

        /// <summary>
        /// ★ 当前<b>占用着活动会话</b>的那条我们的 profile（排除 Probe/Dataface），没有则 null。
        ///   <b>「当前会话在哪」这一判据全仓只此一份</b>：
        ///   <see cref="FindConnectedProfileAsync"/> 没连着时会兜底回基础 profile，
        ///   而"根本没连接"绝不该被读成"有条会话要收尾"（空壳不拨号是已验收口径），
        ///   所以这里不套它，自己扫一遍状态。1.0.86 A 案原先就地写这段，现收进来共用。
        /// ★ <c>internal</c>（不是 private）的理由：诊断路径的拨号点
        ///   （<see cref="ProbeConnectAsync"/>、<c>Backend.StartupProbes</c> 的 dataface probe）
        ///   也读这<b>同一把尺子</b>决定"要不要跳过这次探测"。
        /// ★★【1.0.92 · Docs/44 对齐官方尺子】判据从「只认 <c>Connected</c>」改成
        ///   「<c>Connected</c> ∪ <c>Connecting</c>」—— 官方 sample 的
        ///   <c>GetFirstConnectedProfileAsync</c>（<c>MainPage.xaml.cs:92-93</c>）把这两态<b>都</b>算
        ///   "zero or one of these at all times" 里的那个 one。
        ///   ★【1.0.93 · 删 hook 批】这把<b>尺子</b>保留（它是官方的），但用它<b>拦截发起</b>的那两道闸
        ///   已删除 ⇒ 现在它只回答两件事：「断开该发在谁身上」（<see cref="DisconnectAsync"/>，
        ///   对官方 <c>:528-532</c>）与「当前会话在哪」（切换路径的前置判断、诊断拨号点）。
        ///   1.0.92 记的那笔代价仍然作数：Connecting 不算会话时，"A 未收尾就拨 B"拦不住，
        ///   而 Docs/43 实测那次 <c>ConnectProfileAsync</c> <b>≥5 分 56 秒没返回</b>、期间没有出口。
        ///   本批不是否认这条结论，而是把它<b>装成对照组</b>去看平台自己怎么回答（判读口径见 Docs/46）。
        /// 与 <see cref="GracefulTeardown.EnsureTornDownAsync"/> 的分工（删除/重建路径上仍是两道不同的闸）：
        ///   那边回答「这条上有没有需要拆的隧道」（Connecting 尚无接口 ⇒ 无需断开，对删除/重建是对的），
        ///   这边回答「平台现在把会话记在哪条上」。同一个状态、两个问题，不能互相代。
        /// </summary>
        internal static async Task<VpnPlugInProfile?> FindActiveSessionAsync()
        {
            IReadOnlyList<IVpnProfile> profiles = await ProfileStore.GetProfilesAsync();
            bool NotProbe(VpnPlugInProfile pp) =>
                pp.ProfileName is not ("ClashUWP-Probe" or "ClashUWP-Dataface");

            return PickOurs(profiles, pp =>
                NotProbe(pp) && IsActiveSessionStatus(StatusOf(pp)), out _);
        }

        /// <summary>
        /// ★【1.0.123】<see cref="ClashUWP.Backend.SessionPresence"/> 的<b>纯读</b>实现：只扫一遍清单，
        /// 不发断开、不修中毒条目、不删建（那些动作各有自己的门与账，见
        /// <see cref="TearDownSessionForSwitchAsync"/>；本方法被"更新生效阶梯"调用，
        /// 它一旦顺手发断开，"自动处理"就变成了第二次未授权的网络动作）。
        ///
        /// 【清单本身读不出来时】回 <see cref="SessionPresence.Unreadable"/>：这就是"读不出"的字面情形，
        /// 阶梯据此不动隧道并如实报"未能确认已应用"。不向上抛是为了不把一次成功的落盘
        /// 伪装成拉取失败（正文已经写完了）。
        /// 【为什么共用现成的尺子】会话态的判定走 <see cref="IsActiveSessionStatus"/>
        ///   （<c>Connected ∪ Connecting</c>，官方 sample 那把），探测条目按
        ///   <see cref="FindActiveSessionAsync"/> 的同一条规则排除 ⇒ 全项目对"有没有会话"
        ///   仍然只有一个词表，阶梯不另立判据。
        /// </summary>
        internal static async Task<SessionPresence> ReadSessionPresenceAsync()
        {
            IReadOnlyList<IVpnProfile> profiles;
            try
            {
                profiles = await ProfileStore.GetProfilesAsync();
            }
            catch (Exception ex)
            {
                RollingLog.Warn("sub-apply",
                    $"读会话状态：profile 清单本身读不出（{ex.GetType().Name}）⇒ 按不可知处理，不动隧道");
                return SessionPresence.Unreadable;
            }

            bool anyUnreadable = false;
            foreach (IVpnProfile p in profiles)
            {
                if (p is not VpnPlugInProfile pp || !IsOurs(pp)) continue;
                if (pp.ProfileName is "ClashUWP-Probe" or "ClashUWP-Dataface") continue;
                string st = StatusOf(pp);
                if (IsActiveSessionStatus(st))
                {
                    RollingLog.Info("sub-apply",
                        $"读会话状态：[{pp.ProfileName}] 状态={st} ⇒ 会话活着");
                    return SessionPresence.LiveSession;
                }
                if (st == StatusUnreadable) anyUnreadable = true;
            }

            string verdict = anyUnreadable
                ? "有条目读数坏掉 ⇒ 不可知" : "全部读得出来且都不是会话态 ⇒ 无会话";
            RollingLog.Info("sub-apply", $"读会话状态：{verdict}");
            return anyUnreadable ? SessionPresence.Unreadable : SessionPresence.NoSession;
        }

        /// <summary>
        /// ★【1.0.92 · Docs/44】官方"活动会话"的两个态（sample :92-93）。
        ///   写成一处具名判据，是为了让所有读"当前会话在哪"的调用点共用<b>同一个</b>词表。
        /// ★【1.0.93 · 删 hook 批】原先共用它的两个拒绝点（按钮路径 <c>SerializeBehindActiveSessionAsync</c>
        ///   与切换路径的 <c>Connecting</c> 拒绝分支）都删了；现在读它的是
        ///   <see cref="DisconnectAsync"/>（对官方 <c>GetFirstConnectedProfileAsync</c> + <c>:532</c>）、
        ///   <see cref="TearDownSessionForSwitchAsync"/>（切换第一段：判"当前有没有会话、在哪条上"）
        ///   与两个诊断拨号点。
        ///   <b>尺子保留、闸删掉</b> —— 这把尺子本身是官方的，不是我们的 hook。
        /// </summary>
        private static bool IsActiveSessionStatus(string status) =>
            status is "Connected" or "Connecting";

        // ★★★【1.0.93 · 本批删除】ConnectingRefusalPrefix / SameProfile / RefreshForDialAsync。
        //   三个都是上面那道闸的配件：Connecting 态的拒绝措辞、"目标就是当前会话就不用收尾"的比对、
        //   收尾之后回读目标对象拿新鲜快照。闸拆了，配件没有别的调用方，一并删掉不留死代码。

        /// <summary>
        /// ★【1.0.88 · Docs/38 F2】把 <see cref="VpnManagementErrorStatus"/> 翻成一句人能照着做的话。
        ///   成员与官方语义按 26100 元数据现读逐条对照（:218088-218140，共 19 项：
        ///   <c>Ok=0 … InvalidIP=18</c>；其中 13 项是 1607 / SDK 14393 引入，
        ///   我们最低支持 17763 ⇒ 全部可用，没有兼容性退路）。
        ///   <c>AlreadyConnected</c> / <c>AlreadyDisconnecting</c> 这两项**明令不得重试**：
        ///   先例里悬挂之后重试一律回 <c>AlreadyConnected</c>，等于把僵尸会话坐实
        ///   （研究档案 docs/60:140、docs/62:65）。
        /// </summary>
        private static string ExplainDialError(VpnManagementErrorStatus s) => s switch
        {
            VpnManagementErrorStatus.Ok => "无错误",
            VpnManagementErrorStatus.Other => "平台只给了 Other，无进一步细节（不重试，先看这条 profile 的对账结果）",
            VpnManagementErrorStatus.InvalidXmlSyntax => "CustomConfiguration 的 XML 语法非法（改 profile 内容，别重试）",
            VpnManagementErrorStatus.ProfileNameTooLong => "profile 名过长（订阅显示名要截短）",
            VpnManagementErrorStatus.ProfileInvalidAppId => "profile 绑定的 app id 非法（多半是 ManagementApp/包家族对不上，需重建 profile）",
            VpnManagementErrorStatus.AccessDenied => "拒绝访问（权限或包身份不匹配）",
            VpnManagementErrorStatus.CannotFindProfile => "找不到指定的 profile（★ 已知硬约束：必须用 GetProfilesAsync 新鲜回读的对象去拨，自建对象必吃这个码）",
            VpnManagementErrorStatus.AlreadyDisconnecting => "这条正在断开中 ⇒ 等收尾，不要重拨",
            VpnManagementErrorStatus.AlreadyConnected => "会话已存在 ⇒ 不要重拨（先例：悬挂后重拨只会一直吃这个码）",
            VpnManagementErrorStatus.GeneralAuthenticationFailure => "认证失败（平台没给细节）",
            VpnManagementErrorStatus.EapFailure => "EAP 认证失败",
            VpnManagementErrorStatus.SmartCardFailure => "智能卡认证失败",
            VpnManagementErrorStatus.CertificateFailure => "证书认证失败",
            VpnManagementErrorStatus.ServerConfiguration => "服务器配置非法（查 ServerUris）",
            VpnManagementErrorStatus.NoConnection => "没有可用网络连接（外层物理链路不通）",
            VpnManagementErrorStatus.ServerConnection => "连不上外层服务器（Docs/30:378 那次就是这个码）",
            VpnManagementErrorStatus.UserNamePassword => "用户名/密码认证失败",
            VpnManagementErrorStatus.DnsNotResolvable => "ServerUris 的域名解析不了",
            VpnManagementErrorStatus.InvalidIP => "给定的 IP 非法",
            _ => $"未识别的返回值={(int)s}（按失败处理，原样上报）",
        };

        /// <summary>
        /// ★【1.0.88 · Docs/38 F1】这一句回话代表<b>我们根本没有发起拨号</b>
        ///   （被官方前置条件拦住，或被"至多一条活动会话"的安全闸拒绝）。
        ///   存在的理由只有一条：<see cref="Backend.VpnSession.ConnectAsync"/> 是<b>先武装哨兵再连接</b>的，
        ///   而这次没拨 ⇒ 哨兵没有要保护的拨号，留着它到点会把<b>平台自己正在处理</b>的那条连接
        ///   当成"隧道没起来"断掉（HealthMonitor 的非终态出口就是这么写的）。
        ///   口径靠固定前缀而不是枚举传递，是为了不把 <c>ConnectAsync</c> 的返回形状改成
        ///   结构体后连带 CLI/调试回路的文本出口一起动 —— 这两个前缀就是上面两处拒绝文案，改文案要同步改这里。
        /// </summary>
        internal static bool IsNoDialResult(string message) =>
            message.Contains("未发起=", StringComparison.Ordinal) ||
            message.Contains("新连接=跳过", StringComparison.Ordinal);

        /// <summary>
        /// ★【1.0.96 · Docs/51】"没拨出去"里面<b>专属于读数中毒</b>的那一支：
        ///   <see cref="ConnectOnceAsync"/> 的前置条件读 <c>ConnectionStatus</c> 自己抛
        ///   （0x80070006 那句，见其 catch）。
        ///   为什么单认这一句：<c>RepairPoisonedProfileAsync</c> 要做的是"对这条发一次平台断开、
        ///   等读数恢复"（1.0.99 起不再动存储），
        ///   只有这种"平台侧条目坏了、我们连状态都拿不到"的情形才值得动它；
        ///   平台明确拒绝（<c>Connecting</c> 态、串行层拦住、<c>AlreadyConnected</c>…）都各有处置，不许拿删除去赌。
        ///   认字符串而不是加返回码，口径与 <see cref="IsNoDialResult"/> 相同 ——
        ///   <b>改 <see cref="ConnectOnceAsync"/> 那句文案必须同步改这里</b>。
        /// </summary>
        internal static bool IsStatusReadFailure(string message) =>
            message.Contains("未发起=读状态异常", StringComparison.Ordinal);


        /// <summary>一次「把订阅 profile 对齐到应有形态」的结果分类（给启动对账计数用）。</summary>
        internal enum SubProfileAction { Unchanged, Added, Updated, Skipped, Failed }

        internal readonly record struct SubProfileResult(SubProfileAction Action, string Message);

        /// <summary>
        /// ★【1.0.62】为一条订阅注册/更新一个独立的系统 VPN profile，
        /// 让 Windows 快速设置 VPN 面板里<b>原生列出该订阅</b>、点哪个连哪个。
        ///
        /// ★【快速面板改造】profile 名 = <b>订阅显示名本身</b>（如 <c>iKuuu_V2</c>），
        /// 不再带 <c>ClashUWP · </c> 前缀 —— 用户在快速面板里只想看到订阅叫什么，
        /// 产品名前缀属于噪音。"按名字族认领"由此出现的缺口，由
        /// <see cref="VpnPluginConfig.IsOurSchema"/> 的 CustomConfiguration 兜底补上；
        /// 旧前缀条目（仍被 <c>GracefulTeardown.IsOurName</c> 认得）在下次对账走到这里时
        /// 因 <c>ProfileName != name</c> 被判"待更新"，断开状态下自动改名迁移。
        /// <c>CustomConfiguration</c> 带
        /// &lt;subId&gt;（插件 Connect 据此读 <c>subscriptions/{subId}.yaml</c>）。
        /// <see cref="RequireVpnClientAppUI"/> 由 <see cref="AppSettings.PanelMultiEntry"/> 决定：
        /// 默认 false（订阅条目不进系统 UI，面板里只留基础 "ClashUWP" 做开关）；
        /// 开启后 = true（每条订阅各占一行，但受平台限制：A 连着时点 B 会卡死，
        /// 必须先手动断开，见 Docs/43、Docs/57 §一）。
        /// <see cref="AlwaysOn"/>=false ⇒ 不自动连，避免调试时甩不掉。
        ///
        /// ★★【1.0.72 修复 · 已存在的 profile 必须走 Update，不能走 Add】
        ///   1.0.62~1.0.71 对已存在的 profile 也调 <c>AddProfileFromObjectAsync</c>，
        ///   实测（本机 app.log）每次返回 <c>Other</c>，于是「更新 CustomConfiguration /
        ///   显示名 / ServerUri」这条路径<b>从来没有生效过</b>：同名 profile 一遍遍
        ///   Add 失败，日志刷 <c>注册=Other</c>，而系统里的旧 profile 原样躺着。
        ///   官方 API 的分工是明确的：Add = 新建，Update = 改已存在的
        ///   （<c>VpnManagementAgent.UpdateProfileFromObjectAsync</c>，26100 winmd 已核实）。
        ///   ⇒ 找到同 subId 的 profile 后：内容一致就<b>不动</b>（返回 Unchanged）；
        ///     不一致且当前是 Disconnected 才 Update；正在连接/断开则<b>整轮跳过</b> ——
        ///     对活动会话改 profile 没有实测背书。
        ///   ★【1.0.95 · Docs/50】"跳过（断开后再对账）"这句从今<b>有执行者了</b>：前台轮询在
        ///     会话边沿（连→断、A→B）再推一轮对账，那一刻这条正好已经回到 Disconnected。
        /// </summary>
        internal static async Task<SubProfileResult> EnsureSubscriptionProfileResultAsync(
            string subId, string? displayName = null)
        {
            if (string.IsNullOrWhiteSpace(subId))
                return new SubProfileResult(SubProfileAction.Skipped,
                    "EnsureSubscriptionProfile: subId 为空，跳过");

            // ★【快速面板改造】不再拼 "ClashUWP · " 前缀（见方法头注释）。
            string name = string.IsNullOrWhiteSpace(displayName) ? subId : displayName;
            // ★【1.0.81】把显示名也写进 CustomConfiguration，插件侧用它来在通知里展示友好名称
            string custom = new VpnPluginConfig { SubId = subId, SubDisplayName = displayName ?? "" }.ToXml();

            try
            {
                string server = await ReadProfileServerOverrideAsync();
                var serverUri = new Uri(server);
                VpnPlugInProfile? existing = await FindProfileBySubIdAsync(subId);

                if (existing == null)
                {
                    // ★★【1.0.101 · Docs/52】会话挂着的时候不往存储里加条目 ——
                    //   拦的就是 Docs/45 §八 那一次 <c>新建=Ok</c>（详见 ProveStoreWriteSafeAsync）。
                    //   欠下来的这一条记进台账，由"断开之后"的那一轮对账补（边沿 / 挂载）。
                    (bool writable, string proof) = await ProveStoreWriteSafeAsync();
                    if (!writable)
                    {
                        string holdMsg =
                            $"订阅profile[{name}] subId={subId} 待新建但{proof}，跳过（会话期间不写存储，断开后自动补）";
                        RollingLog.Info("profile-sub", holdMsg);
                        await ReconcileOwed.MarkAsync(subId, $"新建：{proof}");
                        return new SubProfileResult(SubProfileAction.Skipped, holdMsg);
                    }

                    var fresh = new VpnPlugInProfile
                    {
                        ProfileName = name,
                        VpnPluginPackageFamilyName = PackageFamilyName,
                        // ★【1.0.115】默认隐藏：panelMultiEntry=0 时订阅条目不进系统 UI，
                        //   面板里只留基础 "ClashUWP" 一条做开关。开启后恢复旧行为（每条订阅各占一行），
                        //   但受平台限制：A 连着时点 B 会卡死（Docs/43、Docs/57），须先手动断开。
                        RequireVpnClientAppUI = AppSettings.PanelMultiEntry,
                        AlwaysOn = false,
                        CustomConfiguration = custom,
                    };
                    fresh.ServerUris.Add(serverUri);

                    VpnManagementErrorStatus add = await ProfileStore.AddProfileFromObjectAsync(fresh);
                    string addedMsg = $"订阅profile[{name}] subId={subId} 新建={add}";
                    RollingLog.Info("profile-sub", addedMsg);
                    if (add == VpnManagementErrorStatus.Ok)
                        await ReconcileOwed.ClearAsync(subId);
                    return new SubProfileResult(
                        add == VpnManagementErrorStatus.Ok ? SubProfileAction.Added : SubProfileAction.Failed,
                        addedMsg);
                }

                // ★ 内容比较用解析后的 subId，而不是 XML 原文：平台回读时可能重排空白/属性。
                bool sameServer = existing.ServerUris.Count == 1 &&
                                  existing.ServerUris[0].ToString() == serverUri.ToString();
                bool same = existing.ProfileName == name
                            && existing.RequireVpnClientAppUI == AppSettings.PanelMultiEntry
                            && VpnPluginConfig.Parse(existing.CustomConfiguration).SubId == subId
                            && sameServer;
                if (same)
                {
                    // 已就位 ⇒ 这条不再欠着（上轮欠的账在这一刻其实被"本来就一致"销掉了）。
                    await ReconcileOwed.ClearAsync(subId);
                    return new SubProfileResult(SubProfileAction.Unchanged,
                        $"订阅profile[{name}] subId={subId} 已就位");
                }

                // ★★【1.0.101 · Docs/52】改一个<b>已经存在</b>的条目同样算"会话期间写存储"，
                //   所以过的是同一道全局闸（不是只看这一条自己的状态）。
                //   【代价要说清】Docs/50 那句"换了订阅，面板条目立刻跟着动"从今起
                //   改成"断开之后跟着动"：改名/补齐被闸欠下来，由会话边沿那一轮补。
                //   【为什么仍然保留整份清单的推送】全局判据"一条会话挂着"通常只在使用者
                //   在线时成立，而改名恰恰多发生在切订阅那一刻 —— 边沿对账（MainComponent）
                //   紧接着就会补推，用户看到的仍然是"这一次动作内闭合"，只是排在断开之后。
                (bool updWritable, string updProof) = await ProveStoreWriteSafeAsync();
                if (!updWritable)
                {
                    string holdUpd =
                        $"订阅profile[{name}] subId={subId} 待更新但{updProof}，跳过（会话期间不写存储，断开后自动补）";
                    RollingLog.Info("profile-sub", holdUpd);
                    await ReconcileOwed.MarkAsync(subId, $"更新：{updProof}");
                    return new SubProfileResult(SubProfileAction.Skipped, holdUpd);
                }

                string status = StatusOf(existing);
                if (status != "Disconnected")
                {
                    string skipMsg =
                        $"订阅profile[{name}] subId={subId} 待更新但状态={status}，跳过（断开后再对账）";
                    RollingLog.Info("profile-sub", skipMsg);
                    await ReconcileOwed.MarkAsync(subId, $"状态={status}");
                    return new SubProfileResult(SubProfileAction.Skipped, skipMsg);
                }

                existing.ProfileName = name;
                existing.RequireVpnClientAppUI = AppSettings.PanelMultiEntry;
                existing.CustomConfiguration = custom;
                existing.ServerUris.Clear();
                existing.ServerUris.Add(serverUri);

                VpnManagementErrorStatus upd = await ProfileStore.UpdateProfileFromObjectAsync(existing);
                string updMsg = $"订阅profile[{name}] subId={subId} 更新={upd}";
                RollingLog.Info("profile-sub", updMsg);
                if (upd == VpnManagementErrorStatus.Ok)
                    await ReconcileOwed.ClearAsync(subId);
                return new SubProfileResult(
                    upd == VpnManagementErrorStatus.Ok ? SubProfileAction.Updated : SubProfileAction.Failed,
                    updMsg);
            }
            catch (Exception ex)
            {
                // ★ 单个 profile 的失败不能把整次「拉取订阅」打成「落盘失败」：
                //   正文已经写好了，面板少一条系统 profile 是次要问题。
                string failMsg = $"订阅profile subId={subId} 同步异常: {ex.GetType().Name}: {ex.Message}";
                RollingLog.Warn("profile-sub", failMsg);
                return new SubProfileResult(SubProfileAction.Failed, failMsg);
            }
        }

        /// <summary>
        /// ★【1.0.75】把<b>基础 profile</b>（<c>ClashUWP</c>，不带 subId、读 <c>config.yaml</c>）
        /// 也纳入启动对账 —— 判据与 <see cref="EnsureSubscriptionProfileResultAsync"/> 逐字同一套。
        ///
        /// 【为什么这条必须存在】1.0.62 起的对账<b>只遍历订阅清单</b>
        /// （<c>SubscriptionManager.ListAsync()</c>），基础 profile 从来不在名单里。
        /// 后果实测在本机：1.0.73 把"新建 profile 的 ServerUri"从 <c>http://127.0.0.1</c>
        /// 换成 <c>http://192.0.2.1</c>（NCSI/盾标那条修复），改的只是<b>新建</b>路径 ——
        /// 已存在的那条没人碰，profile-dump 里两条 profile 的 ServerUris 仍是
        /// <c>http://127.0.0.1</c> Port=80。也就是"代码改了、机器上还是旧的"，
        /// 而前台所有连接走的恰恰就是这条基础 profile ⇒ 那次修复对它<b>等于没发生</b>。
        /// 【为什么只在 Disconnected 才改】同订阅 profile 那条纪律：对活动会话改 profile
        ///   没有实测背书，不在在线路径上做；断开后下一次启动对账自然对齐。
        /// </summary>
        internal static async Task<SubProfileResult> EnsureBaseProfileResultAsync()
        {
            try
            {
                string server = await ReadProfileServerOverrideAsync();
                var serverUri = new Uri(server);
                VpnPlugInProfile? existing = await FindOurProfileAsync();

                if (existing == null)
                {
                    // ★【1.0.101 · Docs/52】同订阅条目那条纪律：会话挂着的时候不新建。
                    (bool writable, string proof) = await ProveStoreWriteSafeAsync();
                    if (!writable)
                    {
                        string holdMsg = $"基础profile[{ProfileName}] 待新建但{proof}，跳过（会话期间不写存储，断开后自动补）";
                        RollingLog.Info("profile-base", holdMsg);
                        await ReconcileOwed.MarkAsync(ReconcileOwed.BaseKey, $"新建：{proof}");
                        return new SubProfileResult(SubProfileAction.Skipped, holdMsg);
                    }

                    VpnManagementErrorStatus add = await AddProfileAsync(new VpnPluginConfig(), server);
                    string addedMsg = $"基础profile[{ProfileName}] 新建={add}, server={server}";
                    RollingLog.Info("profile-base", addedMsg);
                    if (add == VpnManagementErrorStatus.Ok)
                        await ReconcileOwed.ClearAsync(ReconcileOwed.BaseKey);
                    return new SubProfileResult(
                        add == VpnManagementErrorStatus.Ok ? SubProfileAction.Added : SubProfileAction.Failed,
                        addedMsg);
                }

                bool sameServer = existing.ServerUris.Count == 1 &&
                                  existing.ServerUris[0].ToString() == serverUri.ToString();
                // 基础 profile 的定义就是"不带 subId"（插件据此走 config.yaml）。
                // ★ 用<b>解析后</b>的 SubId 比，不比 XML 原文：平台回读会重排空白与属性。
                // ★【1.0.115】UI 期望值与订阅条目互斥：panelMultiEntry=0 → 基础可见，=1 → 隐藏。
                bool same = existing.ProfileName == ProfileName
                            && existing.RequireVpnClientAppUI == !AppSettings.PanelMultiEntry
                            && VpnPluginConfig.Parse(existing.CustomConfiguration).SubId.Length == 0
                            && sameServer;
                if (same)
                {
                    await ReconcileOwed.ClearAsync(ReconcileOwed.BaseKey);
                    return new SubProfileResult(SubProfileAction.Unchanged,
                        $"基础profile[{ProfileName}] 已就位");
                }

                // ★【1.0.101 · Docs/52】全局"没有活会话"判据先过，再看这条自己的状态。
                (bool bWritable, string bProof) = await ProveStoreWriteSafeAsync();
                if (!bWritable)
                {
                    string holdUpd = $"基础profile[{ProfileName}] 待更新但{bProof}，跳过（会话期间不写存储，断开后自动补）";
                    RollingLog.Info("profile-base", holdUpd);
                    await ReconcileOwed.MarkAsync(ReconcileOwed.BaseKey, $"更新：{bProof}");
                    return new SubProfileResult(SubProfileAction.Skipped, holdUpd);
                }

                string status = StatusOf(existing);
                if (status != "Disconnected")
                {
                    string skipMsg = $"基础profile[{ProfileName}] 待更新但状态={status}，跳过（断开后再对账）";
                    RollingLog.Info("profile-base", skipMsg);
                    await ReconcileOwed.MarkAsync(ReconcileOwed.BaseKey, $"状态={status}");
                    return new SubProfileResult(SubProfileAction.Skipped, skipMsg);
                }

                string why = !sameServer
                    ? $"ServerUri={existing.ServerUris.Count}个→1个({serverUri})"
                    : "名字/CustomConfiguration/UI可见性 对齐";
                existing.ProfileName = ProfileName;
                // ★【1.0.115】与订阅条目互斥：panelMultiEntry=0 → 基础可见（开关），=1 → 隐藏。
                existing.RequireVpnClientAppUI = !AppSettings.PanelMultiEntry;
                existing.CustomConfiguration = new VpnPluginConfig().ToXml();
                existing.ServerUris.Clear();
                existing.ServerUris.Add(serverUri);

                VpnManagementErrorStatus upd = await ProfileStore.UpdateProfileFromObjectAsync(existing);
                string updMsg = $"基础profile[{ProfileName}] 更新={upd}（{why}）";
                RollingLog.Info("profile-base", updMsg);
                if (upd == VpnManagementErrorStatus.Ok)
                    await ReconcileOwed.ClearAsync(ReconcileOwed.BaseKey);
                return new SubProfileResult(
                    upd == VpnManagementErrorStatus.Ok ? SubProfileAction.Updated : SubProfileAction.Failed,
                    updMsg);
            }
            catch (Exception ex)
            {
                string failMsg = $"基础profile 同步异常: {ex.GetType().Name}: {ex.Message}";
                RollingLog.Warn("profile-base", failMsg);
                return new SubProfileResult(SubProfileAction.Failed, failMsg);
            }
        }

        /// <summary>
        /// ★【1.0.75】清掉<b>系统里有、本地订阅清单里没有</b>的 profile（幽灵条目）。
        ///
        /// 【为什么要】快速设置面板列的是<b>系统 profile 存储</b>里的东西，不是我们的
        ///   <c>LocalState\subscriptions\</c>。两边只在"增/删/切"那一刻对齐过一次：
        ///   删订阅时插件正连着 ⇒ <see cref="RemoveSubscriptionProfileAsync"/> 被
        ///   收干净闸拒绝 ⇒ 正文没了、面板那条还在；换机/重装后 LocalState 空了、
        ///   profile 还在（同一包族）。用户看到的"订阅显示不正常"就是这个 ——
        ///   点它连不上（CustomConfiguration 指向不存在的文件），又划不掉。
        /// 【纪律】
        ///   · <b>只碰带 subId 的</b>：基础 profile（subId 空）与探测残留不在此列；
        ///   · <b>只碰非活动</b>的：状态不是 Disconnected 一律跳过，绝不拆正在跑的隧道；
        ///   · 删除必须过 <see cref="GracefulTeardown.EnsureTornDownAsync"/> 那道闸
        ///     （与 <see cref="RemoveSubscriptionProfileAsync"/> 同一条纪律，
        ///     拆不干净会留孤儿 NRPT + 幽灵路由 = 整机断网）；
        ///   · keep 清单为空时<b>不 prune</b>：那更可能是"订阅还没拉/文件读失败"，
        ///     此时把面板清空属于二次事故。
        /// </summary>
        internal static async Task<string> PruneOrphanProfilesAsync(
            System.Collections.Generic.IReadOnlyList<string> keepSubIds)
        {
            if (keepSubIds == null || keepSubIds.Count == 0)
                return "无订阅清单，prune 跳过（不误空面板）";

            var keep = new HashSet<string>(keepSubIds, StringComparer.OrdinalIgnoreCase);
            int removed = 0, skipped = 0;
            var sb = new StringBuilder();

            try
            {
                IReadOnlyList<IVpnProfile> all = await ProfileStore.GetProfilesAsync();
                foreach (IVpnProfile p in all)
                {
                    if (p is not VpnPlugInProfile pp) continue;
                    // ★【1.0.79 · Docs/30 §8-F】曾经这里是 `!= PackageFamilyName → continue`，
                    //   于是换签名证书（＝换包家族哈希）之后，上一代自己建的 profile 对 prune
                    //   <b>完全隐形</b>：面板里挂着幽灵条目，当前代又新建一份同名的。
                    //   现在"名字族命中"也纳入清理（迁移式）。旧家族的条目到底删得掉删不掉
                    //   <b>未实测</b>，所以每条都在结果串里标 [旧家族]，删除返回值如实进日志。
                    // ★【快速面板改造】这里曾有一句 `!IsOurName → continue` 的复核（1.0.79 加的
                    //   双保险），在订阅 profile 改叫订阅名后它会<b>误杀新判据</b>：改名条目
                    //   名字不再命中 IsOurName，但 IsOurs 已靠 CustomConfiguration 认出它。
                    //   复核与 IsOurs 本就是同一把尺子的两个刻度，撤掉复核，尺子归一。
                    if (!IsOurs(pp)) continue;
                    bool foreign = !IsCurrentFamily(pp);

                    string subId = VpnPluginConfig.Parse(pp.CustomConfiguration).SubId;
                    if (subId.Length == 0) continue;          // 基础 profile / 探测残留
                    if (keep.Contains(subId)) continue;        // 本地产物还在，留着

                    string status = StatusOf(pp);
                    if (status != "Disconnected")
                    {
                        // ★【1.0.96 · Docs/51】这一句从此也挡住了"读数中毒"那条：
                        //   读不出 = 不知道 = <b>不许按"没连着"放行删除</b>（Docs/45 修法第 6 条）。
                        //   为什么这条纪律值这条保留：2026-10-16 的孤儿 NRPT + 幽灵路由 = 整机断网，
                        //   成因正是在平台仍记着会话时删 profile。
                        skipped++;
                        sb.Append($"\n保留[{pp.ProfileName}] 状态={status}（活动会话或读数不可信 ⇒ 不拆）");
                        continue;
                    }

                    (bool ok, string report) = await GracefulTeardown.EnsureTornDownAsync(pp);
                    if (!ok)
                    {
                        skipped++;
                        sb.Append($"\n保留[{pp.ProfileName}] 会话未收干净（{report}）");
                        RollingLog.Warn("profile-prune",
                            $"幽灵 profile 拒绝删除 —— {pp.ProfileName}: {report}");
                        continue;
                    }

                    VpnManagementErrorStatus del = await ProfileStore.DeleteProfileAsync(pp);
                    if (del == VpnManagementErrorStatus.Ok)
                    {
                        removed++;
                        sb.Append($"\n删除[{pp.ProfileName}] subId={subId}" +
                                  (foreign ? " [旧家族]" : ""));
                    }
                    else
                    {
                        skipped++;
                        sb.Append($"\n删除失败[{pp.ProfileName}] ={del}");
                    }
                }
            }
            catch (Exception ex)
            {
                // prune 是清理动作，失败不影响对账其余部分（下次启动还会再试）。
                RollingLog.Warn("profile-prune", $"异常: {ex.GetType().Name}: {ex.Message}");
                return $"prune 异常: {ex.GetType().Name}: {ex.Message}";
            }

            string msg = $"prune 幽灵 profile：删 {removed} / 留 {skipped}";
            RollingLog.Info("profile-prune", msg + sb.ToString());
            return msg + (removed + skipped == 0 ? "" : sb.ToString());
        }

        /// <summary>
        /// ★【1.0.62】按 subId 找我们的订阅 profile：遍历同 PFN 的 profile，解析 CustomConfiguration 里的 subId。
        /// ★【1.0.79 · Docs/30 §8-F】口径改为 <see cref="IsOurs"/>（同家族优先，名字族兜底）：
        ///   只按家族匹配时，换证书之后这条会"找不到 profile"，而面板里明明挂着那条订阅。
        /// </summary>
        private static async Task<VpnPlugInProfile?> FindProfileBySubIdAsync(string subId)
        {
            IReadOnlyList<IVpnProfile> profiles = await ProfileStore.GetProfilesAsync();
            VpnPlugInProfile? pp = PickOurs(profiles,
                p => VpnPluginConfig.Parse(p.CustomConfiguration).SubId == subId,
                out bool foreign);
            if (pp != null && foreign)
                RollingLog.Warn("profile-find",
                    $"订阅 subId={subId} 命中的是旧签名家族的条目 family={pp.VpnPluginPackageFamilyName}");
            return pp;
        }

        /// <summary>
        /// ★【1.0.109 · 用户指令「更换订阅遵循先断开、后修改、再重连」】切换第一相位的五种结果。
        /// 写成枚举而不是让调用方嗅探回话字符串：调用方要靠它决定"改完之后要不要重连"，
        /// 而嗅探文本正是 Docs/45 §八.5 那条"声称改了盘、只看自己写的日志行"的同族坑。
        /// </summary>
        internal enum SwitchSeat
        {
            /// 本来就没有活动会话 ⇒ 没有要重连的东西（「空壳不拨号」是已验收口径）。
            /// ★【1.0.110】这一档现在<b>只</b>在"清单里每条本包条目都读得出来、且都不是会话态"时给出；
            ///   读数坏掉的条目改由 <see cref="SwitchSeat.SessionStateUnknown"/> 如实回话。
            NoSession,
            /// 会话已经在这条目标订阅上 ⇒ 不拆，改完也不重拨。
            AlreadyTarget,
            /// 旧会话确认收干净 ⇒ 修改可以落，且改完<b>必须</b>重连（不能把用户留在断的上面）。
            TornDown,
            /// ★【1.0.110】台上<b>有没有会话这件事本身不可知</b>（有条目的 <c>ConnectionStatus</c> 读不出，
            ///   而网络栈那两条判据已被实测证明不足以断定"没会话"，见 <see cref="ProveNoLiveSessionAsync"/>
            ///   与 Docs/51 §七.4）。处置：<b>不重拨</b>，但修改照走 ——
            ///   它既不制造新会话，也不比 1.0.108 之前多写一笔。
            ///   ★【1.0.113 · Docs/58】到达这一档之前会先<b>逐条发断开并等读数恢复</b>；
            ///   任一条恢复就升格成 <see cref="TornDown"/>（改完重拨），只有全都不恢复才停在这里。
            ///   ★【1.0.114 · Docs/59】再往下还有一档：<b>整组删旧建新</b>；新清单复核后全干净 ⇒ 升格
            ///   <see cref="TornDown"/>，仍读不出才停在 <see cref="SessionStateUnknown"/>（回话带"删建已试过"）。
            SessionStateUnknown,
            /// 拆不干净 ⇒ 调用方必须停在「未修改」，一个字都不动。
            /// ★【1.0.114 · Docs/59】新增一种成因：删旧建新<b>之后</b>新条目显形成
            ///   <c>Connected</c>/<c>Connecting</c>（"会话其实活着"这件事被重建后的干净读数证实了）
            ///   ⇒ 这一刻绝不能进"后修改"，整段停在未修改。
            Refused,
        }

        /// <summary>
        /// ★★【1.0.109 · 用户指令】切订阅的<b>第一步</b>：把当前会话收干净，给后面的"修改"腾出
        /// 一个没有会话的时刻。只拆会话，<b>绝不删条目</b>（面板上那条订阅还得可点）。
        ///
        /// 【这是对 1.0.93 那一批的反向修订】1.0.93 为了对齐官方 sample，把切换改成
        ///   "旧会话完全交给平台、直接对目标发一次连接请求"（见本篇 §八.5 第 7 行与 Docs/46 #3）。
        ///   代价有两笔实测：① 切换那几秒里对 profile 存储的 Add/Update 全被写入闸欠下来，
        ///   面板条目名要等会话边沿才跟（Docs/52 §五）；② Docs/45 §八.3 那条最可疑的成因形状
        ///   ——「活动会话期间往 profile 存储里写条目」正是发生在这几秒里（n=1，仍未证）。
        ///   官方文档侧是同一口径：Intune/VPNv2 已知问题「会话期间删除或并发改活动 profile ⇒ 掉线」、
        ///   Windows 升级指引「先断开 VPN 再动系统」、KB2826164「断开失败的会话只能重启」。
        ///   ⇒ 本批改回「先断开 → 后修改 → 再重连」：写入与拨号都落在<b>没有会话</b>的时刻。
        ///
        /// 【<see cref="SwitchSeat.Refused"/> 只有五种成因，一律如实停手】
        ///   · <c>Connecting</c> —— 官方 <c>DisconnectOnClick:532</c> 只在 <c>Connected</c> 上发断开，
        ///     前台也没有取消建连的动词（<c>TerminateConnection</c> 在插件侧，见 memory）；
        ///   · <c>Disconnecting</c> 等满 <see cref="GracefulTeardown.WaitSeconds"/> 仍没落地；
        ///   · 读数不可信 —— <see cref="GracefulTeardown.EnsureTornDownAsync"/> 从 1.0.96 起
        ///     不把"失败的读数"当"没连着"（Docs/45 修法第 6 条）；
        ///   · ★【1.0.110】条目读数不可信<b>但默认路由就在它身上</b>（网络栈正面指认有会话），
        ///     对它发断开后 15 s 内读数仍不恢复 ⇒ 会话记录还在，见
        ///     <see cref="ClassifyNoReadableSessionAsync"/>。
        ///   · ★【1.0.114 · Docs/59】断开发完仍读不出 ⇒ 照指令整组删旧建新，<b>新条目里有一条显形成
        ///     会话态</b>（重建把"确实有会话"这件事证实了）⇒ 停在未修改，先让用户断开。
        /// 【为什么拆不干净就一个字都不改】此刻写存储正是本批要消灭的形状；而先改账本会把
        ///   「生效订阅」写成隧道根本没在跑的那份 —— 就是 Docs/36 §3.1 那个谎报的复发形状。
        ///   隧道与账本都留在用户点之前 = 没有净变坏，出口交回给用户。
        /// </summary>
        internal static async Task<(SwitchSeat Seat, string Line)> TearDownSessionForSwitchAsync(
            string targetSubId)
        {
            // ★ 与断开按钮、写入闸共用同一把尺子（FindActiveSessionAsync），不再各处各判一次。
            VpnPlugInProfile? current = await FindActiveSessionAsync();
            if (current == null)
                // ★【1.0.110 · Docs/55 §八】这里的 null 有两种来路，必须分开回话：
                //   "每条都读得出来且都不是会话态"（真没会话）与"有条目根本读不出来"（不知道）。
                return await ClassifyNoReadableSessionAsync();

            string name = current.ProfileName;
            if (VpnPluginConfig.Parse(current.CustomConfiguration).SubId == targetSubId)
            {
                string same = $"先断开=会话已经在 [{name}] 上，无需拆";
                RollingLog.Info("profile-switch", same);
                return (SwitchSeat.AlreadyTarget, same);
            }

            string st = StatusOf(current);
            bool down;
            string detail;
            if (st == "Connected" || st == "Disconnecting")
            {
                // Connected ⇒ 由 GracefulTeardown 发断开并<b>等</b>收干净；
                // Disconnecting ⇒ 平台已经在断这条，按官方口径不再发第二次断开，只等它落地。
                (down, detail) = st == "Connected"
                    ? await GracefulTeardown.EnsureTornDownAsync(current)
                    : await GracefulTeardown.WaitSessionDownAsync(name);
                if (st == "Disconnecting") detail = "不发断开（平台已在断这条） " + detail;
            }
            else
            {
                // 只剩 Connecting（前台没有官方取消动词）。读数不可信的条目走不到这里，
                // 它在 <see cref="ClassifyNoReadableSessionAsync"/> 那一侧分流 —— 那边才是本方法的出口。
                down = false;
                detail = $"状态={st}，前台没有官方动词能取消它";
            }

            string line = $"先断开 [{name}]：{(down ? "收尾=干净 " : "收尾=未确认 ")}{detail}";
            if (down) RollingLog.Info("profile-switch", line);
            else RollingLog.Warn("profile-switch", line + " ⇒ 本次切换停在未修改");
            return (down ? SwitchSeat.TornDown : SwitchSeat.Refused, line);
        }

        /// <summary>
        /// ★★【1.0.110 · Docs/55 §八】<see cref="FindActiveSessionAsync"/> 回 null <b>不等于</b>"台上没有会话"。
        /// 这个区别必须在这一段就判掉，因为调用方拿它决定两件事：①"后修改"敢不敢写 profile 存储，
        /// ②"再重连"要不要拨。把"读不出"报成"没会话"，等于在一条<b>可能正活着</b>的隧道期间写存储 ——
        /// 而那正是本批立起来要消灭的形状（Docs/51 §四 的嫌疑因果，n=1）。
        ///
        /// 【为什么不能拿现有两条判据替它担保】Docs/51 §七.4 装机两次（05:29:24 / 05:31:09）：
        ///   清单内无 <c>Connected/Connecting</c>、默认路由在 WLAN，两条判据<b>逐字成立</b>，
        ///   平台在 <c>ConnectProfileAsync</c> 那里照样回 <c>AlreadyConnected</c>
        ///   —— 中毒条目名下确实记着会话，只是它的 <c>ConnectionStatus</c> 抛。
        ///   ⇒ "默认路由不在本包条目上"是<b>不充分</b>的，不许拿它换一句"当前无活动会话"。
        ///
        /// 【三档出口，各自动作不同】
        ///   · 清单里没有读数坏的条目 ⇒ <see cref="SwitchSeat.NoSession"/>，回话与 1.0.109 逐字相同
        ///     （Docs/55 §四 判据 A 抄的就是这一句，不许顺手美化）；
        ///   · 有读数坏的条目、<b>且默认路由正好落在它身上</b> ⇒ 网络栈给出了正面证据：这条上就是活会话。
        ///     按第一段的本义处理：走 <see cref="DisconnectAndWaitReadableAsync"/> 拆它，
        ///     读数恢复 = 会话记录真没了 ⇒ <see cref="SwitchSeat.TornDown"/>（改完会重拨）；
        ///     拆不开 ⇒ <see cref="SwitchSeat.Refused"/>（一个字都不改）。
        ///   · 其余（读不到归属 / 归属在别的链路上）⇒ ★【1.0.113 · Docs/58 改】这一支<b>不再原地停手</b>：
        ///     照旧"不凭空拨"，但先<b>对每一条读数坏掉的条目各发一次断开并等读数恢复</b>
        ///     （用户口径："如果是连接状态先断旧的连接，再连最后选的订阅"；1.0.110~1.0.112 这一支
        ///     什么都不发，于是连着时点切换=没反应，而②还把账本改了）。任一条恢复
        ///     ⇒ <see cref="SwitchSeat.TornDown"/>（改完重拨）；全不恢复 ⇒ <see cref="SwitchSeat.SessionStateUnknown"/>
        ///     （不重拨，"后修改"照走，回话里逐条带上断开结果）。
        ///     ★ 1.0.110 当年那句"可用性换不到任何已证的收益，不换"到此作废：它权衡的是"要不要为此
        ///       放宽判据"，而漏掉这一支在<b>连着</b>时会让整次点击变成空转 —— 那是实测到的坏形状，
        ///       本机 <c>[星链机场]</c> 的读数除了重启不会恢复（KB2826164，杀宿主与 <c>rasdial /d</c>
        ///       均已实测无效）正是它的来源。
        ///     ★【1.0.114 · Docs/59 · 用户指令"读不出来就都删了重新创建"】断开全没治好时<b>再加一档</b>：
        ///       整组删旧建新（<see cref="RecreateProfileAsync"/>，与界面那颗按钮同一条路），
        ///       建完按 <see cref="RepairVerdictAsync"/> 复核新清单 ⇒ 全干净 <see cref="SwitchSeat.TornDown"/>（重拨）/
        ///       显形出会话 <see cref="SwitchSeat.Refused"/>（整段停在未修改）/ 仍读不出
        ///       <see cref="SwitchSeat.SessionStateUnknown"/>（不重拨，出口仍是重启）。
        /// 【为什么这里肯发断开、别处不肯】断开是<b>往"没网"的方向</b>动，与
        ///   <see cref="GracefulTeardown"/> 类注释里 2026-10-16 那次拆错东西导致整机断网不同向；
        ///   而且发的是这条<b>新鲜回读</b>对象（官方 <c>CannotFindProfile</c> 那条硬约束）。
        /// 【为什么删条目也不算"拿网卡赌一次"】删+Add 比断开更重，所以它<b>不自己发明判据</b>：
        ///   <see cref="RecreateProfileAsync"/> 第一步就是 <see cref="ProveStoreWriteSafeAsync"/>，
        ///   默认路由落在本包任一条目上、或任一条读得出且是会话态 ⇒ 整轮拒绝并原句带回
        ///   （回话里那句 <c>重建=跳过（…）</c> 就是它）。真把活隧道名下的条目删掉这件事没有发生在这条路上。
        /// </summary>
        private static async Task<(SwitchSeat Seat, string Line)> ClassifyNoReadableSessionAsync()
        {
            IReadOnlyList<IVpnProfile> profiles = await ProfileStore.GetProfilesAsync();
            var unreadable = new List<VpnPlugInProfile>();
            foreach (IVpnProfile p in profiles)
            {
                if (p is not VpnPlugInProfile pp || !IsOurs(pp)) continue;
                if (pp.ProfileName is "ClashUWP-Probe" or "ClashUWP-Dataface") continue;
                if (StatusOf(pp) == StatusUnreadable) unreadable.Add(pp);
            }

            if (unreadable.Count == 0)
            {
                const string none = "先断开=当前无活动会话（下次连接自然走新订阅的 profile）";
                RollingLog.Info("profile-switch", none);
                return (SwitchSeat.NoSession, none);
            }

            var sb = new StringBuilder();
            foreach (VpnPlugInProfile u in unreadable)
                sb.Append(sb.Length == 0 ? "" : "、").Append('[').Append(u.ProfileName).Append(']');
            string names = sb.ToString();

            string owner = "";
            string ownerNote = "";
            try
            {
                owner = Windows.Networking.Connectivity.NetworkInformation
                    .GetInternetConnectionProfile()?.ProfileName ?? "";
            }
            catch (Exception ex)
            {
                ownerNote = $"读默认路由归属抛 {ex.GetType().Name}";
            }

            VpnPlugInProfile? holder = null;
            if (owner.Length > 0)
                foreach (VpnPlugInProfile u in unreadable)
                    if (string.Equals(u.ProfileName, owner, StringComparison.OrdinalIgnoreCase))
                        holder = u;

            if (holder != null)
            {
                (string clearLine, bool recovered) = await DisconnectAndWaitReadableAsync(holder);
                string head = $"先断开 [{holder.ProfileName}]：状态读不出但默认路由在它身上 ⇒ " +
                    $"{(recovered ? "收尾=干净 " : "收尾=未确认 ")}{clearLine}";
                if (recovered)
                {
                    RollingLog.Info("profile-switch", head);
                    return (SwitchSeat.TornDown, head);
                }
                RollingLog.Warn("profile-switch", head + " ⇒ 本次切换停在未修改");
                return (SwitchSeat.Refused, head);
            }

            string why = ownerNote.Length > 0
                ? ownerNote
                : (owner.Length > 0 ? $"默认路由在「{owner}」上（不是本包条目）" : "读不到默认路由归属");

            // ★★【1.0.113 · Docs/58 · 用户指令"断开系统侧连接再重建"】
            //   归属指认不出来的这一支，从前直接停在"不拆不拨"，于是<b>连着的时候点切换 = 什么都没发生</b>
            //   （旧隧道继续跑，②却已经把账本改了）。现在按第一段的本义推到底：
            //   对<b>每一条读数坏掉的条目</b>各发一次平台断开、等它的读数恢复（恢复=那条会话记录真没了）。
            //   【为什么敢发】断开是往"没网"的方向动，与 2026-10-16 那次拆错东西导致整机断网不同向
            //     （同一理由见本方法头注释「为什么这里肯发断开、别处不肯」）。
            //   【代价，明写】每条最多 15 s（<see cref="DisconnectAndWaitReadableAsync"/> 默认上限），
            //     全不恢复时最坏 条目数×15 s；每次断开都过 <see cref="ProfileStore"/> 的串行写闸，
            //     平台不答时占用一次写预算 —— 这是本批换来的可用性，装机要盯的就是这两行。
            var tried = new List<string>();
            foreach (VpnPlugInProfile u in unreadable)
            {
                (string clearLine, bool recovered) = await DisconnectAndWaitReadableAsync(u);
                tried.Add($"[{u.ProfileName}] {clearLine}");
                if (!recovered) continue;

                string line = $"先断开 [{u.ProfileName}]：状态读不出且归属指认不出（{why}）⇒ " +
                    $"照发断开，读数已恢复（{clearLine}）⇒ 按已拆处理，随后重拨最后选的订阅";
                RollingLog.Info("profile-switch", line);
                return (SwitchSeat.TornDown, line);
            }

            string unknownHead = $"会话状态不可信：{names} 的状态读不出，{why}；断开已逐条发过：{string.Join("；", tried)}";

            // ★★★【1.0.114 · Docs/59 · 用户指令"读不出来就都删了重新创建"】
            //   1.0.113 的"逐条发断开"只治得住<b>会话记录还在</b>那一类（断开=把那条 RAS 会话拆掉，
            //   会话没了读数自然恢复）。治不住的是 Docs/45/51 那一类：条目自己的读数句柄坏了
            //   （KB2826164 的僵尸 transport），断开对它等于空转 —— 那一版的出口只有"重启"。
            //   本支改成把<b>整组条目删掉重盖</b>（= 界面上那颗「建/重建 profile」按钮同一条路，
            //   见 <see cref="RecreateProfileAsync"/>：清探测残留 → 删基础条目 → Add → 回读 →
            //   <see cref="RefreshSubscriptionEntriesAsync"/> 删两条订阅再按清单补回）。
            //   【为什么值得一试】删除会把这条 profile 在 RAS 层的<b>记录整段抹掉</b>，新 Add 出来的
            //     是一条干净条目（顺带把 Docs/57 那条陈旧的 <c>ManagementApp</c> 戳也钉到当前版）；
            //     复核用<b>新清单</b>，能把被"读不出"盖住的<b>真活会话</b>挖成 <see cref="SwitchSeat.Refused"/>。
            //   ★【已有的相反一手，不许藏】Docs/51 §七.3 量到过一次"<b>整条删掉重盖照样抛</b>"（10-23 05:15），
            //     而且<b>那一次就是干净窗口</b>：前置是 <c>05:15:16.577 当前无活动会话，无需切换</c>，
            //     删建那行的判据原文是 <c>清单内无 Connected/Connecting，默认路由在「TP-LINK_81D1」</c>，
            //     <c>05:15:35.144 新建=Ok</c> 之后 <c>05:15:35.188</c> 用重新扫出来的<b>新对象</b>照抛，
            //     杀进程换 pid 15096 于 <c>05:17:06</c> 读它仍是"状态读不出"。
            //     ⇒ 本支的进入条件（无活会话 + 写入闸放行）<b>与那次相同</b>，按 §七.4 的机制
            //       （"删除+新建只重排 <c>rasphone.pbk</c> 里的条目记录，碰不到那个死句柄"）<b>预期无效</b>。
            //       未测的那一手是：那次只删重盖<b>单条</b>，本支是<b>整组一起换血</b>（基础条目 + 订阅条目）
            //       —— 判据 Docs/59 §五 #1 采到什么算什么，采到"仍读不出"就是这条线第二次被证伪，撤掉这一档。
            //   【代价，明写】在 1.0.113 那 条目数×15 s 之上再加一轮 删+Add（每条一次 <see cref="ProfileStore"/>
            //     写调用，平台不答时各占 45 s 预算）⇒ 切换第一段最坏从"几十秒"变"分钟级"。
            //     所以这一支只在<b>断开全没治好</b>时才走，不在正常切换里出现。
            string rebuild = await RecreateProfileAsync();
            string seatRebuild = $"先断开=没拆（{unknownHead}）⇒ 照指令整组删旧建新：{rebuild}";

            string verdict = await RepairVerdictAsync();
            if (verdict.Length > 0)
            {
                RollingLog.Warn("profile-switch", seatRebuild + $" ⇒ {verdict}");
                return (verdict.StartsWith(RefusedMarker) ? SwitchSeat.Refused : SwitchSeat.SessionStateUnknown,
                    seatRebuild + $" ⇒ {verdict}");
            }

            string done = seatRebuild + " ⇒ 删建后每条读数都恢复且都不是会话态 ⇒ 按已拆处理，随后重拨最后选的订阅";
            RollingLog.Info("profile-switch", done);
            return (SwitchSeat.TornDown, done);
        }

        /// <summary>回话里"这条要停在未修改"的标记（<see cref="SwitchSeat.Refused"/> 的判据字面量）。</summary>
        private const string RefusedMarker = "重建后读出会话";

        /// <summary>
        /// ★【1.0.114 · Docs/59】整组删旧建新<b>之后</b>的复核，只回一句判词（空串=可以按已拆处理）。
        ///   · 仍有一条读不出 ⇒ 回"删建后仍读不出（Docs/51 §七.3 同形状，出口=重启）…"，停在
        ///     <see cref="SwitchSeat.SessionStateUnknown"/>（照改账本、<b>不重拨</b>）；
        ///   · 有条目显形成 <c>Connected</c>/<c>Connecting</c> ⇒ 回 <see cref="RefusedMarker"/> 开头的一句，
        ///     整段停在<b>未修改</b> —— 这一刻台上确实有会话，"后修改"往 profile 存储里写正是
        ///     Docs/52 要拦的形状，绝不能因为"刚重建过"就冒充没有会话；
        ///   · 全清单可读且非会话态 ⇒ 回空串（调用方据此升格 <see cref="SwitchSeat.TornDown"/>，重拨最后选的订阅）。
        /// ★ 这里读的是<b>新清单</b>（重新 <c>GetProfilesAsync</c>），不拿删建之前的旧对象 —— 旧对象那些
        ///   句柄正是坏掉的来源。
        /// </summary>
        private static async Task<string> RepairVerdictAsync()
        {
            IReadOnlyList<IVpnProfile> profiles;
            try
            {
                profiles = await ProfileStore.GetProfilesAsync();
            }
            catch (Exception ex)
            {
                return $"删建后清单读不出（{ex.GetType().Name}）⇒ 不重拨";
            }

            var still = new List<string>();
            var active = new List<string>();
            foreach (IVpnProfile p in profiles)
            {
                if (p is not VpnPlugInProfile pp || !IsOurs(pp)) continue;
                if (pp.ProfileName is "ClashUWP-Probe" or "ClashUWP-Dataface") continue;
                string st = StatusOf(pp);
                if (st == StatusUnreadable) still.Add($"[{pp.ProfileName}]");
                else if (IsActiveSessionStatus(st)) active.Add($"[{pp.ProfileName}]={st}");
            }

            if (active.Count > 0)
                return $"{RefusedMarker}：{string.Join("、", active)} ⇒ 台上确实有会话，本次切换整段停在未修改" +
                    "（请先断开再点）";
            if (still.Count > 0)
                return $"删建后仍读不出：{string.Join("、", still)}（Docs/51 §七.3 那次的形状，除重启没有出口）⇒ 不重拨";
            return "";
        }

        /// <summary>
        /// ★【1.0.62】删一条订阅的 profile（订阅被删除时清理，避免快速设置里留幽灵条目）。
        /// 走 <see cref="GracefulTeardown"/> 那道断网闸：会话收不干净就<b>拒绝删除</b>，
        /// 绝不拿网卡赌一次（与 DeleteOurProfileAsync 同一纪律）。
        ///
        /// ★【1.0.72】返回结构化结果：调用方（<c>SubscriptionManager.DeleteAsync</c>）
        ///   必须知道删除到底成没成 —— 1.0.71 之前忽略 <c>DeleteProfileAsync</c> 的返回值，
        ///   调用方已经把订阅正文删了，系统里却留下一条指向不存在文件的幽灵 profile。
        /// </summary>
        internal static async Task<(bool Ok, string Message)> RemoveSubscriptionProfileAsync(string subId)
        {
            VpnPlugInProfile? p = await FindProfileBySubIdAsync(subId);
            if (p == null)
                return (true, $"订阅profile 不存在(subId={subId})，无需删除");

            (bool ok, string report) = await GracefulTeardown.EnsureTornDownAsync(p);
            if (!ok)
                return (false, $"订阅profile 会话未收干净，拒绝删除（{report}）");

            VpnManagementErrorStatus del;
            try
            {
                del = await ProfileStore.DeleteProfileAsync(p);
            }
            catch (Exception ex)
            {
                return (false, $"订阅profile 删除异常: {ex.GetType().Name}: {ex.Message}");
            }
            if (del != VpnManagementErrorStatus.Ok)
                return (false, $"订阅profile 删除失败={del}(subId={subId})");

            string r = $"订阅profile 已删除(subId={subId})";
            RollingLog.Info("profile-sub", r);
            // ★【1.0.101 · Docs/52】条目都没了，欠账跟着销 —— 否则台账里留一条指向
            //   已删订阅的欠账，下下轮对账再也不会碰它（清单里已经没有这个 key）。
            await ReconcileOwed.ClearAsync(subId);
            return (true, r);
        }

        /// <summary>
        /// ★【快速面板改造】同时处于 Connected 的我们的 profile（排除探测残留）清单。
        /// 空串 = 正常（0 或 1 条）；≥2 条返回名字列表。
        ///
        /// 【为什么只观测、不处置】"A 连着时在快速面板直接点 B"是切换订阅的常规操作，
        ///   但平台对同一插件的两条 profile 能否并行没有实测背书（官方 VpnChannel.Start 条文
        ///   "There can only be one VPN profile per process"，插件侧 <c>s_activeChannel</c>
        ///   也只有一份）。真出现并发时，这一行日志是定位的第一手证据；
        ///   <b>绝不</b>在这里自动断开谁 —— 拆会话的时机错了就是整机断网事故重演
        ///   （见 <see cref="GracefulTeardown"/> 类注释的 2026-10-16 案例），
        ///   处置方案等实测形状出来再定。
        /// </summary>
        internal static async Task<string> ParallelConnectedReportAsync()
        {
            IReadOnlyList<IVpnProfile> profiles = await ProfileStore.GetProfilesAsync();
            var names = new List<string>();
            foreach (IVpnProfile p in profiles)
            {
                if (p is not VpnPlugInProfile pp || !IsOurs(pp)) continue;
                if (pp.ProfileName is "ClashUWP-Probe" or "ClashUWP-Dataface") continue;
                if (StatusOf(pp) == "Connected")
                    names.Add(pp.ProfileName);
            }
            return names.Count >= 2 ? string.Join(" / ", names) : "";
        }

        /// <summary>
        /// ★【1.0.62】找"当前已连接"的那条我们的 profile（基础或某条订阅），
        /// 供断开/状态兜底用。排除探测 profile（ClashUWP-Probe / ClashUWP-Dataface）。
        /// 没连着的就回基础 profile。
        /// ★【1.0.79 · Docs/30 §8-F】筛选从"只看包家族"改成 <see cref="PickOurs"/>
        ///   （同家族优先 → 名字族兜底）：换签名证书后，正在连的那条很可能是<b>旧家族</b>的，
        ///   只按家族匹配会让前台读成 <c>Profile缺失</c>（状态、断开、已连接订阅三项一起骗人）。
        ///   这条被 3 秒轮询调用，所以<b>不</b>在这里记日志（噪音），旧家族条目由
        ///   <see cref="ListOurProfilesAsync"/> 打标、由 <see cref="PruneOrphanProfilesAsync"/> 清理。
        /// </summary>
        private static async Task<VpnPlugInProfile?> FindConnectedProfileAsync()
        {
            IReadOnlyList<IVpnProfile> profiles = await ProfileStore.GetProfilesAsync();
            bool NotProbe(VpnPlugInProfile pp) =>
                pp.ProfileName is not ("ClashUWP-Probe" or "ClashUWP-Dataface");

            VpnPlugInProfile? connected = PickOurs(profiles,
                pp => NotProbe(pp) && StatusOf(pp) == "Connected", out _);
            if (connected != null) return connected;

            return PickOurs(profiles, pp => NotProbe(pp) && pp.ProfileName == ProfileName, out _);
        }

        /// <summary>我们 profile 的当前连接状态文本；profile 不存在返回 "Profile缺失"。</summary>
        public static async Task<string> OurStatusAsync()
        {
            VpnPlugInProfile? p = await FindConnectedProfileAsync();
            // ★【1.0.96 · Docs/51】最后一句也从"裸读属性会抛"换成 <see cref="StatusOf"/>：
            //   本方法被 3 秒轮询与哨兵调用，它一抛，前台的「当前状态」就永久停在
            //   上一次写进那个槽的文本上（本轮实测停的就是「未发起=读状态异常…」那句）。
            //   现在最坏读到 <see cref="StatusUnreadable"/>，界面照样是一句人话。
            return p == null ? "Profile缺失" : StatusOf(p);
        }

        /// <summary>
        /// ★【1.0.62】当前<b>已连接</b>的那条 profile 携带的订阅 id。
        /// 空串 = 没连着、或连的是基础 profile（ClashUWP，读 config.yaml）。
        ///
        /// 【为什么前台需要它】快速设置里点的连接不经我们的按钮，前台没有任何回调；
        /// 3 秒轮询拿到的除了状态还得有<b>连的是哪条订阅</b>，否则用户在快速面板切了订阅，
        /// app 的「生效中 / 更新当前订阅」还停在旧那份（bookkeeping 与隧道分叉）。
        /// 【刻意只在 Connected 时给值】Connecting/Waiting 期间就改生效态，
        /// 会把"尝试连接"当成"已切换"。
        /// </summary>
        public static async Task<string> ConnectedSubIdAsync()
        {
            VpnPlugInProfile? p = await FindConnectedProfileAsync();
            if (p == null || StatusOf(p) != "Connected")
                return "";
            return VpnPluginConfig.Parse(p.CustomConfiguration).SubId;
        }

        /// <summary>
        /// ★【1.0.62】列出<b>我们这个包</b>的全部 VPN profile（名称 / 订阅 id / 是否进系统 UI / 状态）。
        /// 纯读，用于核验「快速设置 VPN 面板里会出现哪几条」——
        /// <see cref="VpnPlugInProfile.RequireVpnClientAppUI"/>=True 就是官方说的
        /// "appears in the system's VPN connection UI"，这是不点 UI 也能拿到的判据。
        /// 探测残留（Probe/Dataface）一并列出但打标记 —— 它们同属本包，藏起来反而骗自己。
        /// </summary>
        public static async Task<string> ListOurProfilesAsync()
        {
            IReadOnlyList<IVpnProfile> profiles = await ProfileStore.GetProfilesAsync();
            var sb = new StringBuilder();
            int n = 0;
            foreach (IVpnProfile p in profiles)
            {
                if (p is not VpnPlugInProfile pp || !IsOurs(pp))
                    continue;
                n++;
                string subId = VpnPluginConfig.Parse(pp.CustomConfiguration).SubId;
                bool probe = pp.ProfileName is "ClashUWP-Probe" or "ClashUWP-Dataface";
                // ★【1.0.79 · Docs/30 §8-F】旧签名家族的条目也要看得见（只按家族匹配时它隐形，
                //   而面板里那条幽灵条目就是它）—— 打标不隐藏。
                string tag = (probe ? "[探测残留] " : "") +
                             (IsCurrentFamily(pp) ? "" : "[旧家族] ");
                sb.Append($"\n{tag}{pp.ProfileName} | " +
                          $"subId={(subId.Length == 0 ? "(基础)" : subId)} | " +
                          $"系统UI={pp.RequireVpnClientAppUI} | 状态={StatusOf(pp)}");
            }
            return n == 0 ? "本包名下没有任何 profile" : $"共 {n} 条：{sb}";
        }

        public static async Task<string> DisconnectAsync()
        {
            // ★★【1.0.93 · 删 hook 批】本方法改成逐字对齐官方 <c>DisconnectOnClick:525-540</c> 的两步：
            //   ① 用"Connecting 也算"的那把尺子取会话（sample <c>GetFirstConnectedProfileAsync:87-103</c>
            //      ⇒ 本仓 <see cref="FindActiveSessionAsync"/>，1.0.92 刚对齐、排除 Probe/Dataface）；
            //   ② <b>只在 == Connected 时</b>发 <c>DisconnectProfileAsync</c>，其余一个字节都不发。
            //   删掉的两处自有逻辑：
            //     · 用 <c>FindConnectedProfileAsync</c>（没有 Connected 时兜底回基础 profile）当①，
            //       于是对着 Disconnected 也发一次 RPC、靠 NoConnection 分支兜；
            //     · 1.0.92 · Docs/44 §二 明写「故意偏离 · 不藏」的那一支 —— 对 Connecting <b>仍然试断开</b>。
            //   ⚠️ 代价当场生效，写在这儿：Docs/44 §四预告过的那句话成真了 ——
            //     <c>Backend/HealthMonitor.cs:311</c> 那条"卡在非终态 ⇒ 把网络交回直连"从此只会拿到
            //     <c>不发=状态=Connecting</c> 这一句，前台在 Connecting 悬挂时<b>再没有任何出口</b>
            //     （这与官方一致 —— 官方本来也没有；Docs/43 §三.3 实测连杀插件宿主进程都不解）。
            //     出口剩下的两处都在插件侧/系统侧：插件的 <c>TerminateConnection</c>（本批未动）
            //     与 系统设置→VPN。
            VpnPlugInProfile? profile = await FindActiveSessionAsync();
            if (profile == null)
            {
                // ★ 这一行必须出声（官方 :534-536 的 else 分支同样 Log 一句 no such profile）。
                //   ★【1.0.96 · Docs/51 订正】它<b>不再</b>兼 Docs/45 那条"读数活没活"的探针：
                //   属性读改成按条目降级（<see cref="StatusOf"/>）之后，坏条目在这里只算
                //   "不是活动会话"，这一行照样落 —— 于是"有没有这一行"分不出读数死活。
                //   探针换成 per-entry 面：对账汇总与 profile 清点里每条自带的 <c>状态=</c>，
                //   出现 <see cref="StatusUnreadable"/> = 那一条读不出。别把这里改回静默返回。
                string none = "不发=平台没有我们的活动会话（Connected/Connecting 都没有），按已断开处理";
                RollingLog.Info("profile-disconnect", none);
                return none;
            }

            string st = StatusOf(profile);
            if (st != "Connected")
            {
                // ★ 官方 :532 —— Connecting / Disconnecting 这些态它<b>不发</b>断开请求。
                string skip = $"[{profile.ProfileName}] 不发=状态={st}（官方只在 Connected 上发断开：sample :532）";
                RollingLog.Warn("profile-disconnect", skip);
                return skip;
            }

            // ★ 断开是"恢复网络的兜底手段"，它到底成没成必须留下可查的一行。
            //   注意 Ok 只代表请求被受理，会话收干净与否由 GracefulTeardown 那道闸判定。
            // ★【1.0.88 · Docs/38 F2/F4】返回值不再插值冲掉，按 <c>VpnManagementErrorStatus</c> 分支：
            //   这三项在断开语境里的处置和连接语境<b>不同</b>，所以单独写而不套连接的解释 ——
            //   <c>NoConnection</c> 官方条文是"没有可用网络连接"，但在断开这里的历史实测形状
            //   是"平台上本就没有活动会话"（<c>Docs/30</c> 那次 <c>断开=NoConnection</c>）；
            //   两种读法动作一致（都不需要再断），所以按"已是断开态"回话，同时把条文原义留在括号里不藏。
            VpnManagementErrorStatus disc = await ProfileStore.DisconnectProfileAsync(profile);
            string r = disc switch
            {
                VpnManagementErrorStatus.Ok =>
                    "断开=Ok（请求已受理；★1.0.93 起前台不等收尾，会话收干净与否以前台轮询为准，这一句不代表已断开）",
                VpnManagementErrorStatus.NoConnection =>
                    "断开=NoConnection（平台侧本就没有活动会话 ⇒ 按已断开处理；官方对该成员的条文写的是\u201c没有可用网络连接\u201d）",
                VpnManagementErrorStatus.AlreadyDisconnecting =>
                    "断开=AlreadyDisconnecting（平台已经在断这条 ⇒ 等收尾，不再重发断开请求）",
                VpnManagementErrorStatus.CannotFindProfile =>
                    "断开=CannotFindProfile（★profile 在存储里找不到，条目多半已消失 ⇒ 走启动对账重建）",
                _ => $"断开={disc}（★平台未受理断开：{ExplainDialError(disc)}）",
            };
            bool discAccepted = disc is VpnManagementErrorStatus.Ok
                or VpnManagementErrorStatus.NoConnection
                or VpnManagementErrorStatus.AlreadyDisconnecting;
            if (discAccepted) RollingLog.Info("profile-disconnect", r);
            else RollingLog.Warn("profile-disconnect", r);
            return r;
        }

        // ---------------- 安全连通性探测 ----------------

        /// <summary>
        /// 探测 profile 的 ServerUris 到底能不能被平台接受。
        ///
        /// ★ 【为什么敢在别人不在时跑 —— 网络安全性】
        ///   这个临时 profile 的 CustomConfiguration 里<b>只</b>写
        ///   <c>&lt;route&gt;203.0.113.0/24&lt;/route&gt;</c>（RFC 5737 文档保留段，
        ///   没有任何真实流量会命中它），且**不写 dns**。
        ///   对照主 profile 的 <c>0.0.0.0/0</c>：主 profile 一旦连上会劫持全部流量
        ///   （插件目前只回 ICMP，TCP/UDP 会全断）—— 那才是危险动作，这里刻意避开。
        ///   所以即使探测连接成功，本机既有路由也不受影响。
        ///
        /// ★ 【要回答的问题】
        ///   已经确认 profile 本身是好的（LocalState/rasphone.pbk 里
        ///   ServerListServerName=127.0.0.1、ManagementApp 与当前包全名一致，
        ///   端口 0 的旧假设不成立）。剩下最可疑的就是「回环地址不被接受」——
        ///   官方 sample 用的是真实 IPv4(10.137.192.135)。
        ///   用同一份安全配置、只换 ServerUris 各连一次，就能单变量隔离出答案。
        ///
        /// 跑完自删，不留残留 profile。
        /// </summary>
        public static async Task<string> ProbeConnectAsync()
        {
            const string probeName = "ClashUWP-Probe";
            var sb = new StringBuilder();

            // 只放一条文档保留段路由，绝不写 0.0.0.0/0，也不写 dns。
            const string safeConfig =
                "<clash><clientIpV4>10.7.0.2</clientIpV4><mtu>1400</mtu>" +
                "<maxFrame>1500</maxFrame><route>203.0.113.0/24</route></clash>";

            var cases = new (string Label, string Server, bool Ui)[]
            {
                // 单变量隔离，每次只动一个条件：
                //   ① 基线：新默认黑洞地址 + 无系统UI + 立即连（复现用户点「连接」的路径）
                //   ② 只换地址 → 证明「192.0.2.1 比 127.0.0.1 更适合 NCSI」是否成立
                //   ③ 只换成 RequireVpnClientAppUI=true（主 profile 就是 true）
                ("blackhole", "http://192.0.2.1",      false),
                ("loopback",  "http://127.0.0.1",      false),
                ("ui-true",   "http://192.0.2.1",      true),
            };

            foreach ((string label, string server, bool requireUi) in cases)
            {
                // ★【1.0.88 · Docs/38 官方第④条】探测<b>也是拨号</b>：官方按"至多一条活动会话"建模
                //   （sample L80 "zero or one of these at all times"），插件侧 <c>s_activeChannel</c>
                //   同样只有一份。已有活动会话时再连一条 probe，就是在制造本篇要避免的那个形态 ——
                //   而这条路径的"只观测不处置"口径（<see cref="ParallelConnectedReportAsync"/>）
                //   从此不再由探测自己破坏。探测是诊断，跳过它不影响任何用户功能。
                VpnPlugInProfile? live = await FindActiveSessionAsync();
                if (live != null)
                {
                    string skip = $"跳过=已有活动会话 [{live.ProfileName}]（官方建模至多一条，探测不得并行拨号）";
                    RollingLog.Warn("probe-connect", skip);
                    sb.AppendLine($"[{label}] {skip}");
                    continue;
                }

                // 先删干净，保证每次都是新建（新建 vs 复用正是之前踩过的坑）。
                await DeleteByNameAsync(probeName);

                var profile = new VpnPlugInProfile
                {
                    ProfileName = probeName,
                    VpnPluginPackageFamilyName = PackageFamilyName,
                    RequireVpnClientAppUI = requireUi,
                    CustomConfiguration = safeConfig,
                    AlwaysOn = false,
                };
                profile.ServerUris.Add(new Uri(server));

                VpnManagementErrorStatus add = await ProfileStore.AddProfileFromObjectAsync(profile);

                // ★ 关键：把「回读失败」和「平台连不上」分成两个不同的字符串。
                //   上一版两者都输出 CannotFindProfile，等于把自己赋的值和平台返回的值
                //   混在一起，根本分不清是「Add 完 GetProfilesAsync 读不到」还是
                //   「读到了但平台连不上」—— 而这两种是完全不同的病。
                string conn;
                if (add != VpnManagementErrorStatus.Ok)
                {
                    conn = $"Add失败={add}";
                }
                else
                {
                    VpnPlugInProfile? fresh = await FindByNameAsync(probeName);
                    if (fresh == null)
                    {
                        conn = "回读=NULL(Add返回Ok但GetProfilesAsync读不到)";
                    }
                    else
                    {
                        VpnManagementErrorStatus c = await ProfileStore.ConnectProfileAsync(fresh);
                        VpnPlugInProfile? after = await FindByNameAsync(probeName);
                        conn = $"平台返回={c}, 连后回读={(after == null ? "NULL" : StatusOf(after))}";
                    }
                }

                sb.AppendLine($"[{label}] ui={requireUi} server={server} 建={add} 连={conn}");

                // 无论成败都收尾：断开 + 删掉，避免留下脏 profile。
                // ★★ 但"断开 + 删掉"不能连着两句写 —— 2026-10-16 00:21 的整机断网就是这么来的：
                //   <c>DisconnectProfileAsync</c> 返回 <c>Ok</c> 只代表"请求已受理"，
                //   紧接着删 = 在平台仍记 <c>CONN=1</c> 的时候删 profile ⇒ OS 直接扯掉接口，
                //   既不回调插件 Disconnect，也不清它自己下发的 NRPT 与路由，
                //   留下根 Namespace 的孤儿 DNS 策略 + 死接口上的幽灵路由。
                //   ⇒ 与 <see cref="DeleteOurProfileAsync"/>/<see cref="DeleteByNameAsync"/> 同一条闸：
                //     过 <c>GracefulTeardown.EnsureTornDownAsync</c>，等不到收尾就<b>不删</b>，
                //     宁可留一个残留 profile（用户可手工清），也不拿网卡赌一次。
                VpnPlugInProfile? toClean = await FindByNameAsync(probeName);
                if (toClean != null)
                {
                    (bool torn, string teardownReport) =
                        await GracefulTeardown.EnsureTornDownAsync(toClean);
                    sb.AppendLine($"  收尾[{label}]={(torn ? "可删" : "拒绝删")} {teardownReport}");
                    if (torn)
                    {
                        VpnManagementErrorStatus del = await ProfileStore.DeleteProfileAsync(toClean);
                        if (del != VpnManagementErrorStatus.Ok)
                            sb.AppendLine($"  删除[{label}]={del}");
                    }
                }
            }

            return sb.ToString();
        }

        private static async Task<VpnPlugInProfile?> FindByNameAsync(string name)
        {
            IReadOnlyList<IVpnProfile> profiles = await ProfileStore.GetProfilesAsync();
            foreach (IVpnProfile p in profiles)
            {
                if (p is VpnPlugInProfile pp && pp.ProfileName == name)
                    return pp;
            }
            return null;
        }

        /// <summary>删除一条本包 profile，回一句短状态供调用方拼日志（不抛）。
        /// ★【1.0.112 · Docs/57】返回值是新加的：订阅条目刷新需要逐条知道"到底删掉了没"——
        ///   没删掉时随后的 Ensure 只会读到旧条目回"已就位"，那条目的 ManagementApp 版本戳
        ///   根本没换，日志不能骗人。探测清理那两处照旧忽略返回值。</summary>
        private static async Task<string> DeleteByNameAsync(string name)
        {
            VpnPlugInProfile? existing = await FindByNameAsync(name);
            if (existing == null)
                return "删=无";

            // 同一道闸：探测 profile 也可能停在 Connecting/Connected（异常收尾的会话就是这样）。
            (bool ok, _) = await GracefulTeardown.EnsureSessionTornDownAsync(name);
            if (!ok)
            {
                RollingLog.Warn("profile-delete-refused", $"{name} 会话未收干净，拒绝删除");
                return "删=拒绝(会话未收干净)";
            }
            VpnManagementErrorStatus del = await ProfileStore.DeleteProfileAsync(existing);
            // ★【1.0.112】措辞去掉"探测"：本方法现在也用于订阅条目，按原样记会把
            //   订阅条目的删除失败写成"探测 profile …"，读日志的人会去找 Probe 那条。
            if (del != VpnManagementErrorStatus.Ok)
            {
                RollingLog.Warn("profile", $"profile {name} 删除失败={del}（保留，用户可手工清）");
                return $"删=失败({del})";
            }
            return "删=完成";
        }

        /// <summary>
        /// 【防护措施：拆掉自动重拨的燃料】断开并删除 ClashUWP profile。
        ///
        /// 为什么要删 profile 而不是只断开：run 25 之后从事件日志（Application 里的
        /// RasClient 源）查明，异常收尾的连接会被 RasMan 无条件重拨 ——
        /// 单日 dial=413 / connected=413 / terminated=414，终止原因码 828 占 411 次，
        /// 相邻 dial 间隔稳定 97±2 秒，发起者是 SYSTEM。只要 profile 还在，
        /// 这条循环就不会停，用户的网络会被长期占住（本次只能靠系统"网络重置"打断）。
        /// 断开只是停当下一次；删除 profile 才是让 RasMan 再也找不到可拨的对象。
        ///
        /// ★★ 但"断完立刻删"是 2026-10-16 炸网卡的那一手（详见
        ///   <see cref="GracefulTeardown"/> 的类注释）：删除发生在平台仍记 <c>CONN=1</c>
        ///   的时候，OS 直接扯接口 ⇒ 孤儿根 NRPT + 幽灵路由 = 整机断网。
        ///   所以本方法现在只是<b>转发</b>到组件里那一份实现：
        ///   平台级断开 → 轮询等会话真的离开 Connected（最多 20 s）→ 才删 → 残留自检。
        ///   等不到就<b>拒绝删除</b>并把原因回出来，绝不为了"防止重拨"去换"整机断网"。
        ///
        /// 本方法只在"这一轮已经判定不健康"的收尾路径上调用，绝不用于正常连接流程。
        /// </summary>
        public static async Task<string> DropProfileAsync() =>
            await GracefulTeardown.DisconnectThenDeleteAsync("前台请求（VpnProfileSetup.DropProfileAsync）");
    }
}