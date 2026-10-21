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
                //   ★【快速面板改造】基础 profile（无 subId、读 config.yaml）从这一批起
                //   <b>不再进系统 UI</b>：快速面板只列订阅条目（连哪条订阅、哪条亮），
                //   裸 ClashUWP 条目对用户是噪音，还造成"app 里连接、面板亮的是基础条目"
                //   的错位。只影响系统 UI 可见性，不影响程序侧 ConnectProfileAsync。
                RequireVpnClientAppUI = false,

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
        ///
        /// ★★【1.0.88 · Docs/38 官方方案 F1/F2/F3】本方法从此<b>不再裸拨</b>：
        ///   ① 前置条件（sample L496 只在 <c>Disconnected</c> 时连）＋ ② 返回值分支（sample L499-506）
        ///   ＋ ④ 至多一条活动会话（sample L80/L101-113）全部收进
        ///   <see cref="ConnectOnceAsync"/> 与 <see cref="SerializeBehindActiveSessionAsync"/>，
        ///   与 1.0.86 A 案（<see cref="SwitchToSubscriptionAsync"/>）<b>共用同一份实现</b> ——
        ///   两处各写一遍早晚分叉，这是本仓已经认过的账（口径同 1.0.84 对 KernelView.Find 的订正）。
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

            // ★【F3】官方按「至多一条活动会话」建模：本条之外还有 Connected ⇒ 先把它收尾，
            //   收不干净就<b>拒绝发起</b>（同一道安全闸，绝不带着未收尾会话去拨第二条）。
            (string note, bool canDial) = await SerializeBehindActiveSessionAsync(target);
            if (!canDial) return note;

            (bool ok, string msg) = await ConnectOnceAsync(target, finder, "profile-connect", "连接请求");
            _ = ok; // 回话文本本身已带终态，调用方（按钮/CLI）只展示文本
            return note.Length == 0 ? msg : note + "；" + msg;
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
        ///   ④ 受理（<c>Ok</c>）之后仍要轮询真实状态 —— <c>Ok</c> 只代表平台接受了请求，
        ///     不代表隧道起来（:343-348 那段口径不变，假绿灯会把整轮排障带偏）。
        /// 返回 <c>Ok</c> 的判据写死为「轮询末态 == <c>Connected</c>」，不让调用方去猜字符串。
        /// ★ 轮询条件从"排除 Waiting"改成"只在 <c>Connecting/Disconnecting</c> 上继续等"：
        ///   <c>VpnManagementConnectionStatus</c> 投影里只有 4 个成员
        ///   （Disconnected/Disconnecting/Connected/Connecting，元数据 :218074-218084），
        ///   旧代码那个 <c>!= "Waiting"</c> 是永不成立的死条件（Docs/38 D7）。
        /// </summary>
        /// <param name="dialLabel">回话里那次请求的名字：「连接 VPN」用 <c>连接请求</c>，
        ///   切换路径用 <c>新连接请求</c> —— 装机验收靠这两个词分辨是哪条路发起的。</param>
        private static async Task<(bool Ok, string Message)> ConnectOnceAsync(
            VpnPlugInProfile target, Func<Task<VpnPlugInProfile?>> refinder,
            string category, string dialLabel)
        {
            string st0;
            try
            {
                st0 = (target = await RefreshForDialAsync(target, refinder)).ConnectionStatus.ToString();
            }
            catch (ProfileStore.ProfileStoreBusyException exPre)
            {
                // 连"发起前状态"都读不到 ⇒ 不能凭猜测拨号（官方前置条件要求先判状态）。
                string pre = "未发起=" + exPre.Message;
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
            RollingLog.Info(category, head + $" {dialLabel}=Ok（平台已受理，继续轮询真实状态）");

            string now = st0;
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(1000);
                VpnPlugInProfile? fresh;
                try
                {
                    fresh = await refinder();
                }
                catch (ProfileStore.ProfileStoreBusyException exRead)
                {
                    // 读不到不等于没连上 —— 明写"状态不可读"，绝不猜成 Disconnected。
                    now = $"状态不可读（{exRead.Message.Split('，')[0]}）";
                    break;
                }
                if (fresh == null) { now = "Profile缺失"; break; }
                now = fresh.ConnectionStatus.ToString();
                if (now != "Connecting" && now != "Disconnecting") break;
            }
            string tail = head + $" {dialLabel}=Ok, 实际状态={now}";
            bool ok = now == "Connected";
            if (ok) RollingLog.Info(category, tail);
            else RollingLog.Warn(category, tail + "（未落到 Connected，不能对用户说已连接）");
            return (ok, tail);
        }

        /// <summary>
        /// ★【1.0.88 · Docs/38 F3】官方建模：<b>至多一条活动会话</b>
        ///   （sample L80 "There should be zero or one of these at all times" +
        ///   可连清单只收 <c>Disconnected</c> L101-113）。
        ///   拨<b>另一条</b>之前先把这一条收尾；收尾收不干净就<b>拒绝发起</b> ——
        ///   判据与安全闸同 1.0.86 A 案，共用 <see cref="GracefulTeardown.EnsureTornDownAsync"/>。
        /// 返回 (前缀回话, 能否发起)。能否发起为 <c>false</c> 时前缀就是完整回话。
        /// </summary>
        private static async Task<(string Note, bool CanDial)> SerializeBehindActiveSessionAsync(
            VpnPlugInProfile target)
        {
            VpnPlugInProfile? active;
            try
            {
                active = await FindActiveSessionAsync();
            }
            catch (ProfileStore.ProfileStoreBusyException ex)
            {
                // 读不到"当前会话在哪"⇒ 更不能裸拨（官方要求先确认至多一条）。
                string refused = "新连接=跳过（" + ex.Message + "）";
                RollingLog.Warn("profile-connect", "★拒绝发起 —— " + refused);
                return (refused, false);
            }
            if (active == null || SameProfile(active, target)) return ("", true);

            string oldName = active.ProfileName;
            RollingLog.Info("profile-connect",
                $"[{oldName}] 状态=Connected 而目标是 [{target.ProfileName}] ⇒ " +
                "按官方「至多一条活动会话」先收尾（不裸拨第二条）");

            bool torn;
            string report;
            try
            {
                (torn, report) = await GracefulTeardown.EnsureTornDownAsync(active);
            }
            catch (ProfileStore.ProfileStoreBusyException ex)
            {
                string refused = $"旧会话 [{oldName}] 收尾这一步发不出去（{ex.Message}），新连接=跳过（避免与未收尾会话并发）";
                RollingLog.Warn("profile-connect", "★拒绝发起 —— " + refused);
                return (refused, false);
            }
            if (!torn)
            {
                string refused = $"旧会话 [{oldName}] 未收干净（{report}），新连接=跳过（避免与未收尾会话并发）";
                RollingLog.Warn("profile-connect", "★拒绝发起 —— " + refused);
                return (refused, false);
            }
            return ($"已先收尾旧会话 [{oldName}]（{report}）", true);
        }

        /// <summary>
        /// ★【1.0.88】当前<b>确实处于 Connected</b> 的那条我们的 profile（排除 Probe/Dataface），
        ///   没有则 null。<b>「当前会话在哪」这一判据全仓只此一份</b>：
        ///   <see cref="FindConnectedProfileAsync"/> 没连着时会兜底回基础 profile，
        ///   而"根本没连接"绝不该被读成"有条会话要收尾"（空壳不拨号是已验收口径），
        ///   所以这里显式再要一次 <c>Connected</c>。1.0.86 A 案原先就地写这段，现收进来共用。
        /// ★ <c>internal</c>（不是 private）的理由：诊断路径的拨号点
        ///   （<see cref="ProbeConnectAsync"/>、<c>Backend.StartupProbes</c> 的 dataface probe）
        ///   也必须守官方第④条"至多一条活动会话"，它们读的是<b>同一把尺子</b>。
        /// </summary>
        internal static async Task<VpnPlugInProfile?> FindActiveSessionAsync()
        {
            VpnPlugInProfile? candidate = await FindConnectedProfileAsync();
            return candidate != null && candidate.ConnectionStatus.ToString() == "Connected"
                ? candidate
                : null;
        }

        /// <summary>两条对象是否指同一条 profile（名字 + CustomConfiguration 里的 subId 一起比）。</summary>
        private static bool SameProfile(VpnPlugInProfile a, VpnPlugInProfile b) =>
            a.ProfileName == b.ProfileName &&
            VpnPluginConfig.Parse(a.CustomConfiguration).SubId ==
            VpnPluginConfig.Parse(b.CustomConfiguration).SubId;

        /// <summary>
        /// ★【1.0.88 · F1】拨号<b>前</b>再回读一次目标：调用方手里那份对象可能是
        ///   <see cref="SerializeBehindActiveSessionAsync"/> 收尾<b>之前</b>的快照，
        ///   拿旧快照判前置条件会把"刚刚被我们断开的那条"读成还连着。
        ///   回读不到就原样交回旧对象，让下一句 <c>CannotFindProfile</c> 之类的真实返回值说话，
        ///   不在这里假装成功（口径同 :104 那条硬约束：拨号必须用新鲜回读的对象）。
        /// </summary>
        private static async Task<VpnPlugInProfile> RefreshForDialAsync(
            VpnPlugInProfile target, Func<Task<VpnPlugInProfile?>> refinder)
        {
            VpnPlugInProfile? fresh = await refinder();
            return fresh ?? target;
        }

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
                    var fresh = new VpnPlugInProfile
                    {
                        ProfileName = name,
                        VpnPluginPackageFamilyName = PackageFamilyName,
                        RequireVpnClientAppUI = true,
                        AlwaysOn = false,
                        CustomConfiguration = custom,
                    };
                    fresh.ServerUris.Add(serverUri);

                    VpnManagementErrorStatus add = await ProfileStore.AddProfileFromObjectAsync(fresh);
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

                VpnManagementErrorStatus upd = await ProfileStore.UpdateProfileFromObjectAsync(existing);
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
                // ★【快速面板改造】UI 期望值翻转为 false：老机器上已存在的基础 profile
                //   还是可见的，这里必须把它判成"待更新"才能在断开后被改成隐藏。
                bool same = existing.ProfileName == ProfileName
                            && !existing.RequireVpnClientAppUI
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
                // ★【快速面板改造】改为 false：把老机器上可见的基础条目藏出系统 UI。
                existing.RequireVpnClientAppUI = false;
                existing.CustomConfiguration = new VpnPluginConfig().ToXml();
                existing.ServerUris.Clear();
                existing.ServerUris.Add(serverUri);

                VpnManagementErrorStatus upd = await ProfileStore.UpdateProfileFromObjectAsync(existing);
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
        /// ★【1.0.86 · Docs/36 A 案】把会话从当前 profile <b>串行切</b>到目标订阅的 profile。
        ///
        /// 【为什么必须存在】App 内切订阅（<c>SubscriptionManager.ActivateAsync</c>）从 1.0.62 起
        ///   只做「写 config.yaml + 记生效 + 热重载 + 确保 profile 注册」，<b>从不 Disconnect/Connect</b>
        ///   ⇒ 会话仍属旧订阅的 profile，而插件 <c>Connect</c> 把 <c>subId</c> 钉在 profile 的
        ///   CustomConfiguration 上，<b>每次（重）拨号都按旧 subId 覆写 <c>config.kernel.yaml</c></b>，
        ///   把那次热重载的成果吃掉。本机实测（Docs/36 E6/E8）：连续 8 次会话读的全是旧订阅，
        ///   <c>config.kernel.yaml</c> 9920 行/18 name ≡ 旧订阅，而账本写着新订阅 ⇒
        ///   前台「生效中」是新订阅、面板与隧道是旧订阅。<b>这条就是把「切」这个动作补完整。</b>
        ///
        /// 【为什么是"先断干净、再连新的"，不能并行】官方 <c>VpnChannel.Start</c> 条文
        ///   "There can only be one VPN profile per process"，插件侧 <c>s_activeChannel</c> 也只有一份；
        ///   <see cref="ParallelConnectedReportAsync"/> 对并发的既有口径是<b>只观测不处置</b>。
        ///
        /// 【★ 安全闸】旧会话收不干净时<b>拒绝发起新连接</b>并原样回话，绝不带着未收尾的会话去连第二条 ——
        ///   拆不干净就动下一步正是 2026-10-16 孤儿 NRPT + 幽灵路由 = 整机断网的重演路径
        ///   （见 <see cref="GracefulTeardown"/> 类注释）。宁可让用户先手工断开，也不拿网卡赌一次。
        /// 【与删除的区别】这里<b>不删</b> profile：只断会话、留着条目，面板上那条订阅仍然可点。
        ///
        /// 【返回形状】<c>Ok</c> 的口径写死在这里，不让调用方去猜字符串：
        ///   · 本来就没有活动会话 / 会话已经在这条上 ⇒ <b>Ok=true</b>（无事可做不是失败）；
        ///   · 找不到目标 profile、安全闸拒绝、或轮询没落到 Connected ⇒ <b>Ok=false</b>。
        ///   上层（<c>ActivateAsync</c>）拿它决定"已切到"这句话能不能说。
        /// </summary>
        internal static async Task<(bool Ok, string Message)> SwitchToSubscriptionAsync(string targetSubId)
        {
            if (string.IsNullOrWhiteSpace(targetSubId))
                return (false, "切换跳过：targetSubId 为空");

            IReadOnlyList<IVpnProfile> profiles = await ProfileStore.GetProfilesAsync();

            VpnPlugInProfile? target = PickOurs(profiles,
                p => VpnPluginConfig.Parse(p.CustomConfiguration).SubId == targetSubId, out bool foreign);
            if (target == null)
                return (false, $"切换失败：找不到订阅 profile（subId={targetSubId}），先拉取并启用该订阅");

            // ★【1.0.86】"当前连着的是哪条"沿用 <see cref="FindConnectedProfileAsync"/> 这<b>同一份</b>判据
            //   （家族优先 → 名字族兜底、排除 Probe/Dataface），不在这里再写一个 foreach ——
            //   两处各判一次早晚分叉，口径同 1.0.84 对 KernelView.Find 的订正。
            //   但<b>不能</b>直接拿它的返回值当"有会话"用：它没连着时会兜底回基础 profile，
            //   而"用户根本没连接"时切订阅绝不该擅自拨一条隧道出来
            //   （空壳不拨号是已验收口径）⇒ 显式再要一次 Connected 才认。
            // ★【1.0.88】这段判据从就地写法改成共用 <see cref="FindActiveSessionAsync"/>：
            //   「连接 VPN」按钮路径（Docs/38 F3）从此和这里读<b>同一个</b>"当前会话在哪"，
            //   两处各写一遍早晚分叉 —— 本仓为这个已经认过一次账（1.0.84 对 KernelView.Find 的订正）。
            VpnPlugInProfile? current = await FindActiveSessionAsync();

            if (current == null)
                return (true, "当前无活动会话，无需切换（下次连接自然走新订阅的 profile）");
            if (VpnPluginConfig.Parse(current.CustomConfiguration).SubId == targetSubId)
                return (true, $"会话已经在 [{current.ProfileName}] 上，无需切换");

            string oldName = current.ProfileName;
            var sb = new StringBuilder();

            // ★★【1.0.86 实现复核订正】这里<b>不</b>先自己 DisconnectProfileAsync ——
            //   断开这一步交给 EnsureTornDownAsync 统一发起。理由不是"少写一行"：
            //   它进来先读一次状态，不是 Connected 就返回「无需断开」<b>直接跳过等待</b>。
            //   我们若先断一次，它几乎必然读到 Disconnected ⇒ 那条"等会话真收干净
            //   （连续两轮，最多 WaitSeconds 秒）"的闸形同虚设，旧会话还在平台手里
            //   半起半落时我们就去连第二条 —— 正是本方法注释里承诺要避免的并发。
            //   让它自己断，st0 读到的必是 Connected，等待才真的发生。
            (bool torn, string report) = await GracefulTeardown.EnsureTornDownAsync(current);
            sb.Append($"[{oldName}]→[{target.ProfileName}] 收尾={(torn ? "干净" : "未收干净")}（{report}）");
            if (!torn)
            {
                // ★ 必须出声，且是 Warn：这条意味着「切换被安全闸挡下」，
                //   只看返回值容易读成「点了没反应」。
                string refused = sb.Append("；新连接=跳过（避免与未收尾会话并发）").ToString();
                RollingLog.Warn("profile-switch",
                    $"拒绝切换（旧会话未收干净，保持现状不发起新连接）—— {refused}");
                return (false, refused);
            }

            // ★【1.0.88 · Docs/38 F1/F2】拨号这一步从此和「连接 VPN」按钮<b>共用同一份实现</b>
            //   （<see cref="ConnectOnceAsync"/>）：发起前先落一行、返回值单独一行并按枚举判、
            //   轮询只判投影里真有的那 4 个状态。原先这里自己写一遍，和本方法上面的安全闸
            //   一样是"两处各写一遍"的第二例 —— 收进来，观测缺口只补一次。
            (bool dialed, string dialMsg) = await ConnectOnceAsync(
                target, () => FindProfileBySubIdAsync(targetSubId), "profile-switch", "新连接请求");
            sb.Append("；").Append(dialMsg);
            bool ok = dialed;

            // 成功/失败都要留这一行：Docs/36 §5 第 1 条正是"会话期切订阅"缺装机实测背书，
            // 这行日志就是补齐它的第一手证据。
            string msg = sb.ToString();
            if (foreign)
                msg += $"（注意：命中的是旧签名家族的条目 family={target.VpnPluginPackageFamilyName}）";
            return (ok, msg);
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
                if (pp.ConnectionStatus.ToString() == "Connected")
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
            // ★【1.0.88 · Docs/38 F2/F4】返回值不再插值冲掉，按 <c>VpnManagementErrorStatus</c> 分支：
            //   这三项在断开语境里的处置和连接语境<b>不同</b>，所以单独写而不套连接的解释 ——
            //   <c>NoConnection</c> 官方条文是"没有可用网络连接"，但在断开这里的历史实测形状
            //   是"平台上本就没有活动会话"（<c>Docs/30</c> 那次 <c>断开=NoConnection</c>）；
            //   两种读法动作一致（都不需要再断），所以按"已是断开态"回话，同时把条文原义留在括号里不藏。
            VpnManagementErrorStatus disc = await ProfileStore.DisconnectProfileAsync(profile);
            string r = disc switch
            {
                VpnManagementErrorStatus.Ok =>
                    "断开=Ok（请求已受理；会话收干净与否由收尾闸判定，这一句不代表已断开）",
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
            VpnManagementErrorStatus del = await ProfileStore.DeleteProfileAsync(existing);
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