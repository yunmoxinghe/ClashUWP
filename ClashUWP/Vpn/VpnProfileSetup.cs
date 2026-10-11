using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// 调<code>ConnectProfileAsync</code> 之后控制权就交回平台了：平台按
    /// <c>VpnPluginPackageFamilyName</c> 找到我们的包，拉起 <c>vpnClient</c> 后台任务
    /// （<see cref="VpnBackgroundTask"/>），后面的事都在插件那边。
    ///
    /// ⚠️ 所有 <c>Windows.Networking.Vpn</c> API 都要求 <c>networkingVpnProvider</c>
    ///    受限能力（见 Package.appxmanifest）。没有它这里会直接抛 AccessDenied。
    ///
    /// ★ 为什么 <see cref="ConnectAsync"/> 返回一整句话而不是一个枚举：
    ///   这套调用有四个会各自失败的关键步骤（查列表 / 建 profile / 再查 / 连），
    ///   之前只把最后一个枚举丢给 UI，出了 <c>CannotFindProfile</c> 完全看不出
    ///   卡在哪一步 —— 而那恰恰是定位这个问题时最需要的信息。
    ///   骨架期宁可啰嗦，也不要一个看不出所以然的状态码。
    /// </summary>
    public static class VpnProfileSetup
    {
        internal const string ProfileName = "ClashUWP";

        private static readonly VpnManagementAgent Agent = new();

        private static string PackageFamilyName => Windows.ApplicationModel.Package.Current.Id.FamilyName;

        /// <summary>
        /// 找出属于我们这个包的 plugin profile。
        ///
        /// 【官方 sample 的做法】<c>GetMatchingProfilesAsync</c>：
        ///   "In this file, GetProfilesAsync is only called here. Everything else should use the
        ///    results of this call."
        /// 它按<b>包 family name</b> 过滤，而不是只比名字 ——
        /// 因为系统里可能同时存在别的 VPN app 建的同名 profile。
        /// </summary>
        private static async Task<VpnPlugInProfile?> FindOurProfileAsync()
        {
            IReadOnlyList<IVpnProfile> profiles = await Agent.GetProfilesAsync();
            foreach (IVpnProfile profile in profiles)
            {
                // sample 原文：var pluginProfile = profile as VpnPlugInProfile;
                if (profile is VpnPlugInProfile pluginProfile &&
                    pluginProfile.VpnPluginPackageFamilyName == PackageFamilyName)
                {
                    return pluginProfile;
                }
            }
            return null;
        }

        /// <summary>
        /// 建 profile。仅在确实不存在同名同包的 profile 时才建。
        ///
        /// ★ 之前这里是"先删同名再新建"，每次连接都走一遍 delete + add，
        ///   然后紧接着 connect。这有两个问题：
        ///   ① sample 的 <c>DeleteExistingProfile</c> 是<b>先 Disconnect 再 Delete</b>，
        ///      我们漏了断开那一步；
        ///   ② 删/建/连三步连着做，刚建出来的 profile 未必已被平台索引到，
        ///      于是 <c>ConnectProfileAsync</c> 可能报 <c>CannotFindProfile</c>。
        ///   改成"存在就复用"之后，这条竞态路径直接不存在了。
        /// </summary>
        public static async Task<VpnManagementErrorStatus> EnsureProfileAsync(VpnPluginConfig? config = null)
        {
            config ??= new VpnPluginConfig();

            VpnPlugInProfile? existing = await FindOurProfileAsync();
            if (existing != null)
                return VpnManagementErrorStatus.Ok;

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
            // ⚠️ 别写端口 0（原来写的是 http://127.0.0.1:0）：0 不是有效端口，
            //   平台侧会把 ServerUris 当成无效配置 —— 最小成本的做法是照sample
            //   的写法只给 host，让它落到默认端口。
            pluginProfile.ServerUris.Add(new Uri("http://127.0.0.1"));

            // 【官方】AddProfileFromObjectAsync(IVpnProfile)："Creates a new VPN Profile based on
            //   the VpnProfile input object." → 返回 VpnManagementErrorStatus，Ok 才算成功。
            VpnManagementErrorStatus status = await Agent.AddProfileFromObjectAsync(pluginProfile);
            Debug.WriteLine($"VpnProfileSetup: 建 profile 返回 {status}");
            return status;
        }

        /// <summary>
        /// 分步连接，并把每一步的结果拼成一句人话返回给 UI。
        /// </summary>
        public static async Task<string> ConnectAsync(VpnPluginConfig? config = null)
        {
            var sb = new StringBuilder();

            VpnManagementErrorStatus ensure = await EnsureProfileAsync(config);
            sb.Append($"建profile={ensure}");

            if (ensure != VpnManagementErrorStatus.Ok)
                return sb.ToString();

            IReadOnlyList<IVpnProfile> all = await Agent.GetProfilesAsync();
            sb.Append($", 系统内共{all.Count}个");

            VpnPlugInProfile? target = null;
            foreach (IVpnProfile p in all)
            {
                if (p is VpnPlugInProfile pp && pp.VpnPluginPackageFamilyName == PackageFamilyName)
                {
                    target = pp;
                    break;
                }
            }

            if (target == null)
                return sb.Append(", 但找不到我们的 plugin profile").ToString();

            sb.Append($", 目标名={target.ProfileName}");
            sb.Append($", 连接前状态={target.ConnectionStatus}");

            VpnManagementErrorStatus status = await Agent.ConnectProfileAsync(target);
            sb.Append($", 连接={status}");

            Debug.WriteLine($"VpnProfileSetup.ConnectAsync: {sb}");
            return sb.ToString();
        }

        public static async Task<string> DisconnectAsync()
        {
            VpnPlugInProfile? profile = await FindOurProfileAsync();
            if (profile == null)
                return "找不到我们的 plugin profile";

            VpnManagementErrorStatus status = await Agent.DisconnectProfileAsync(profile);
            return $"断开={status}";
        }

        /// <summary>
        /// 【官方 sample 的 DeleteExistingProfile】：先 Disconnect 再 Delete。
        /// 顺序反了会失败 —— 一个还连着的 profile 是删不掉的。
        /// </summary>
        public static async Task DeleteProfileAsync()
        {
            VpnPlugInProfile? profile = await FindOurProfileAsync();
            if (profile == null)
                return;

            await Agent.DisconnectProfileAsync(profile);
            await Agent.DeleteProfileAsync(profile);
        }
    }
}