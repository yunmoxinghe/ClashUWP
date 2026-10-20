using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

using Windows.Networking.Vpn;

namespace ClashUWP.Vpn
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

        private static readonly VpnManagementAgent Agent = new();

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
        /// </summary>
        private static bool IsOurs(VpnPlugInProfile pp) =>
            IsCurrentFamily(pp) || IsOurName(pp.ProfileName);

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
            IReadOnlyList<IVpnProfile> profiles = await Agent.GetProfilesAsync();
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

            VpnManagementErrorStatus del = await Agent.DeleteProfileAsync(profile);
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
                //   true = 出现在系统 VPN 设置里，用户可以从系统那边连/断。
                RequireVpnClientAppUI = true,

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
            return await Agent.AddProfileFromObjectAsync(pluginProfile);
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
            sb.Append($", 状态={now.ConnectionStatus}");

            RollingLog.Info("profile-recreate", sb.ToString());
            return sb.ToString();
        }

        /// <summary>
        /// 「连接 VPN」按钮 / 快速设置点某条订阅。<b>只连接，不建 profile</b> —— 与官方 sample 的分工一致。
        ///
        /// ★【1.0.62】<paramref name="subId"/> 非空时连<b>该订阅专属</b>的 profile
        ///   （<c>ClashUWP · xxx</c>，CustomConfiguration 带 subId）；为空时连基础 profile
        ///   <c>ClashUWP</c>（读 config.yaml 生效副本）。两条路都走系统
        ///   <c>ConnectProfileAsync</c> → 平台 → <c>IVpnPlugIn.Connect</c>。
        ///
        /// ★ <c>ConnectProfileAsync</c> 返回 <c>Ok</c> 只代表「平台接受了这个请求」，
        ///   不代表隧道真的建立。之前调用方把 Ok 当成已连接，于是连接失败时
        ///   健康哨兵照样开跑，量到的全是<b>直连</b>网络 —— 国内 OK、国外超时、
        ///   最后 <c>断开=NoConnection</c>，日志看着像「隧道健康但海外不通」，
        ///   实际上压根没有隧道。这种假绿灯会把整轮排障带到错误的方向上，
        ///   所以这里必须轮询到 profile 的真实状态再返回。
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

            var sb = new StringBuilder();
            sb.Append($"[{target.ProfileName}] 发起前状态={target.ConnectionStatus}, ");
            sb.Append($"连接请求={await Agent.ConnectProfileAsync(target)}");

            // 轮询真实状态，最多 20 秒。★ 这里用字符串而不是枚举成员名：
            //   CsWinRT 投影里的 VpnManagementConnectionStatus 并没有文档简名里的
            //   NotConfigured / Waiting 这些成员（实测编译报 CS0117），
            //   而调用方只关心「到底 Connected 没有」，字符串口径最稳。
            string now = target.ConnectionStatus.ToString();
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(1000);
                VpnPlugInProfile? fresh = await finder();
                if (fresh == null) { now = "Profile缺失"; break; }
                now = fresh.ConnectionStatus.ToString();
                if (now != "Connecting" && now != "Waiting") break;
            }
            sb.Append($", 实际状态={now}");

            RollingLog.Info("profile-connect", sb.ToString());
            return sb.ToString();
        }

        /// <summary>一次「把订阅 profile 对齐到应有形态」的结果分类（给启动对账计数用）。</summary>
        internal enum SubProfileAction { Unchanged, Added, Updated, Skipped, Failed }

        internal readonly record struct SubProfileResult(SubProfileAction Action, string Message);

        /// <summary>
        /// ★【1.0.62】为一条订阅注册/更新一个独立的系统 VPN profile，
        /// 让 Windows 快速设置 VPN 面板里<b>原生列出该订阅</b>、点哪个连哪个。
        ///
        /// profile 名 = <c>"ClashUWP · {displayName}"</c>，<c>CustomConfiguration</c> 带
        /// &lt;subId&gt;（插件 Connect 据此读 <c>subscriptions/{subId}.yaml</c>）。
        /// <see cref="RequireVpnClientAppUI"/>=true ⇒ 出现在系统 VPN UI（含快速设置）；
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
        ///     对活动会话改 profile 没有实测背书，且下一次启动对账会再对齐。
        /// </summary>
        public static async Task<string> EnsureSubscriptionProfileAsync(string subId, string? displayName = null)
            => (await EnsureSubscriptionProfileResultAsync(subId, displayName)).Message;

        internal static async Task<SubProfileResult> EnsureSubscriptionProfileResultAsync(
            string subId, string? displayName = null)
        {
            if (string.IsNullOrWhiteSpace(subId))
                return new SubProfileResult(SubProfileAction.Skipped,
                    "EnsureSubscriptionProfile: subId 为空，跳过");

            string name = "ClashUWP · " + (string.IsNullOrWhiteSpace(displayName) ? subId : displayName);
            string custom = new VpnPluginConfig { SubId = subId }.ToXml();

            try
            {
                string server = await ReadProfileServerOverrideAsync();
                var serverUri = new Uri(server);
                VpnPlugInProfile? existing = await FindProfileBySubIdAsync(subId);

                if (existing == null)
                {
                    var fresh = new VpnPlugInProfile
                    {
                        ProfileName = name,
                        VpnPluginPackageFamilyName = PackageFamilyName,
                        RequireVpnClientAppUI = true,
                        AlwaysOn = false,
                        CustomConfiguration = custom,
                    };
                    fresh.ServerUris.Add(serverUri);

                    VpnManagementErrorStatus add = await Agent.AddProfileFromObjectAsync(fresh);
                    string addedMsg = $"订阅profile[{name}] subId={subId} 新建={add}";
                    RollingLog.Info("profile-sub", addedMsg);
                    return new SubProfileResult(
                        add == VpnManagementErrorStatus.Ok ? SubProfileAction.Added : SubProfileAction.Failed,
                        addedMsg);
                }

                // ★ 内容比较用解析后的 subId，而不是 XML 原文：平台回读时可能重排空白/属性。
                bool sameServer = existing.ServerUris.Count == 1 &&
                                  existing.ServerUris[0].ToString() == serverUri.ToString();
                bool same = existing.ProfileName == name
                            && existing.RequireVpnClientAppUI
                            && VpnPluginConfig.Parse(existing.CustomConfiguration).SubId == subId
                            && sameServer;
                if (same)
                    return new SubProfileResult(SubProfileAction.Unchanged,
                        $"订阅profile[{name}] subId={subId} 已就位");

                string status = existing.ConnectionStatus.ToString();
                if (status != "Disconnected")
                {
                    string skipMsg =
                        $"订阅profile[{name}] subId={subId} 待更新但状态={status}，跳过（断开后再对账）";
                    RollingLog.Info("profile-sub", skipMsg);
                    return new SubProfileResult(SubProfileAction.Skipped, skipMsg);
                }

                existing.ProfileName = name;
                existing.RequireVpnClientAppUI = true;
                existing.CustomConfiguration = custom;
                existing.ServerUris.Clear();
                existing.ServerUris.Add(serverUri);

                VpnManagementErrorStatus upd = await Agent.UpdateProfileFromObjectAsync(existing);
                string updMsg = $"订阅profile[{name}] subId={subId} 更新={upd}";
                RollingLog.Info("profile-sub", updMsg);
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
                    VpnManagementErrorStatus add = await AddProfileAsync(new VpnPluginConfig(), server);
                    string addedMsg = $"基础profile[{ProfileName}] 新建={add}, server={server}";
                    RollingLog.Info("profile-base", addedMsg);
                    return new SubProfileResult(
                        add == VpnManagementErrorStatus.Ok ? SubProfileAction.Added : SubProfileAction.Failed,
                        addedMsg);
                }

                bool sameServer = existing.ServerUris.Count == 1 &&
                                  existing.ServerUris[0].ToString() == serverUri.ToString();
                // 基础 profile 的定义就是"不带 subId"（插件据此走 config.yaml）。
                // ★ 用<b>解析后</b>的 SubId 比，不比 XML 原文：平台回读会重排空白与属性。
                bool same = existing.ProfileName == ProfileName
                            && existing.RequireVpnClientAppUI
                            && VpnPluginConfig.Parse(existing.CustomConfiguration).SubId.Length == 0
                            && sameServer;
                if (same)
                    return new SubProfileResult(SubProfileAction.Unchanged,
                        $"基础profile[{ProfileName}] 已就位");

                string status = existing.ConnectionStatus.ToString();
                if (status != "Disconnected")
                {
                    string skipMsg = $"基础profile[{ProfileName}] 待更新但状态={status}，跳过（断开后再对账）";
                    RollingLog.Info("profile-base", skipMsg);
                    return new SubProfileResult(SubProfileAction.Skipped, skipMsg);
                }

                string why = !sameServer
                    ? $"ServerUri={existing.ServerUris.Count}个→1个({serverUri})"
                    : "名字/CustomConfiguration/UI可见性 对齐";
                existing.ProfileName = ProfileName;
                existing.RequireVpnClientAppUI = true;
                existing.CustomConfiguration = new VpnPluginConfig().ToXml();
                existing.ServerUris.Clear();
                existing.ServerUris.Add(serverUri);

                VpnManagementErrorStatus upd = await Agent.UpdateProfileFromObjectAsync(existing);
                string updMsg = $"基础profile[{ProfileName}] 更新={upd}（{why}）";
                RollingLog.Info("profile-base", updMsg);
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
                IReadOnlyList<IVpnProfile> all = await Agent.GetProfilesAsync();
                foreach (IVpnProfile p in all)
                {
                    if (p is not VpnPlugInProfile pp) continue;
                    // ★【1.0.79 · Docs/30 §8-F】曾经这里是 `!= PackageFamilyName → continue`，
                    //   于是换签名证书（＝换包家族哈希）之后，上一代自己建的 profile 对 prune
                    //   <b>完全隐形</b>：面板里挂着幽灵条目，当前代又新建一份同名的。
                    //   现在"名字族命中"也纳入清理（迁移式）。旧家族的条目到底删得掉删不掉
                    //   <b>未实测</b>，所以每条都在结果串里标 [旧家族]，删除返回值如实进日志。
                    if (!IsOurs(pp)) continue;
                    if (!IsCurrentFamily(pp) && !IsOurName(pp.ProfileName)) continue;
                    bool foreign = !IsCurrentFamily(pp);

                    string subId = VpnPluginConfig.Parse(pp.CustomConfiguration).SubId;
                    if (subId.Length == 0) continue;          // 基础 profile / 探测残留
                    if (keep.Contains(subId)) continue;        // 本地产物还在，留着

                    string status = pp.ConnectionStatus.ToString();
                    if (status != "Disconnected")
                    {
                        skipped++;
                        sb.Append($"\n保留[{pp.ProfileName}] 状态={status}（活动会话不拆）");
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

                    VpnManagementErrorStatus del = await Agent.DeleteProfileAsync(pp);
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
            IReadOnlyList<IVpnProfile> profiles = await Agent.GetProfilesAsync();
            VpnPlugInProfile? pp = PickOurs(profiles,
                p => VpnPluginConfig.Parse(p.CustomConfiguration).SubId == subId,
                out bool foreign);
            if (pp != null && foreign)
                RollingLog.Warn("profile-find",
                    $"订阅 subId={subId} 命中的是旧签名家族的条目 family={pp.VpnPluginPackageFamilyName}");
            return pp;
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
                del = await Agent.DeleteProfileAsync(p);
            }
            catch (Exception ex)
            {
                return (false, $"订阅profile 删除异常: {ex.GetType().Name}: {ex.Message}");
            }
            if (del != VpnManagementErrorStatus.Ok)
                return (false, $"订阅profile 删除失败={del}(subId={subId})");

            string r = $"订阅profile 已删除(subId={subId})";
            RollingLog.Info("profile-sub", r);
            return (true, r);
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
            IReadOnlyList<IVpnProfile> profiles = await Agent.GetProfilesAsync();
            bool NotProbe(VpnPlugInProfile pp) =>
                pp.ProfileName is not ("ClashUWP-Probe" or "ClashUWP-Dataface");

            VpnPlugInProfile? connected = PickOurs(profiles,
                pp => NotProbe(pp) && pp.ConnectionStatus.ToString() == "Connected", out _);
            if (connected != null) return connected;

            return PickOurs(profiles, pp => NotProbe(pp) && pp.ProfileName == ProfileName, out _);
        }

        /// <summary>我们 profile 的当前连接状态文本；profile 不存在返回 "Profile缺失"。</summary>
        public static async Task<string> OurStatusAsync()
        {
            VpnPlugInProfile? p = await FindConnectedProfileAsync();
            return p == null ? "Profile缺失" : p.ConnectionStatus.ToString();
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
            if (p == null || p.ConnectionStatus.ToString() != "Connected")
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
            IReadOnlyList<IVpnProfile> profiles = await Agent.GetProfilesAsync();
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
                          $"系统UI={pp.RequireVpnClientAppUI} | 状态={pp.ConnectionStatus}");
            }
            return n == 0 ? "本包名下没有任何 profile" : $"共 {n} 条：{sb}";
        }

        public static async Task<string> DisconnectAsync()
        {
            VpnPlugInProfile? profile = await FindConnectedProfileAsync();
            if (profile == null)
                return "找不到我们的 profile";

            // ★ 断开是"恢复网络的兜底手段"，它到底成没成必须留下可查的一行。
            //   注意 Ok 只代表请求被受理，会话收干净与否由 GracefulTeardown 那道闸判定。
            string r = $"断开={await Agent.DisconnectProfileAsync(profile)}";
            RollingLog.Info("profile-disconnect", r);
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

                VpnManagementErrorStatus add = await Agent.AddProfileFromObjectAsync(profile);

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
                        VpnManagementErrorStatus c = await Agent.ConnectProfileAsync(fresh);
                        VpnPlugInProfile? after = await FindByNameAsync(probeName);
                        conn = $"平台返回={c}, 连后回读={(after == null ? "NULL" : after.ConnectionStatus.ToString())}";
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
                        VpnManagementErrorStatus del = await Agent.DeleteProfileAsync(toClean);
                        if (del != VpnManagementErrorStatus.Ok)
                            sb.AppendLine($"  删除[{label}]={del}");
                    }
                }
            }

            return sb.ToString();
        }

        private static async Task<VpnPlugInProfile?> FindByNameAsync(string name)
        {
            IReadOnlyList<IVpnProfile> profiles = await Agent.GetProfilesAsync();
            foreach (IVpnProfile p in profiles)
            {
                if (p is VpnPlugInProfile pp && pp.ProfileName == name)
                    return pp;
            }
            return null;
        }

        private static async Task DeleteByNameAsync(string name)
        {
            VpnPlugInProfile? existing = await FindByNameAsync(name);
            if (existing == null)
                return;

            // 同一道闸：探测 profile 也可能停在 Connecting/Connected（异常收尾的会话就是这样）。
            (bool ok, _) = await GracefulTeardown.EnsureSessionTornDownAsync(name);
            if (!ok)
            {
                RollingLog.Warn("profile-delete-refused", $"{name} 会话未收干净，拒绝删除");
                return;
            }
            VpnManagementErrorStatus del = await Agent.DeleteProfileAsync(existing);
            if (del != VpnManagementErrorStatus.Ok)
                RollingLog.Warn("profile", $"探测 profile {name} 删除失败={del}（保留，用户可手工清）");
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