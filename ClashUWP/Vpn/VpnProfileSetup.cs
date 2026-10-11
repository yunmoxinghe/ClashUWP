using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

using Windows.Networking.Vpn;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// S1/S2 · 前台侧：建 VPN profile 并发起连接。
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
    /// </summary>
    public static class VpnProfileSetup
    {
        internal const string ProfileName = "ClashUWP";

        private static readonly VpnManagementAgent Agent = new();

        /// <summary>
        /// 建（或更新）profile。已存在同名 profile 时先删再建，
        /// 因为 <c>AddProfileFromObjectAsync</c> 遇到同名会返回失败而不是覆盖。
        /// </summary>
        public static async Task<VpnManagementErrorStatus> EnsureProfileAsync(VpnPluginConfig? config = null)
        {
            config ??= new VpnPluginConfig();

            IReadOnlyList<IVpnProfile> existing = await Agent.GetProfilesAsync();
            foreach (IVpnProfile profile in existing)
            {
                if (profile.ProfileName == ProfileName)
                    await Agent.DeleteProfileAsync(profile);
            }

            var pluginProfile = new VpnPlugInProfile
            {
                ProfileName = ProfileName,

                // ★【官方】VpnPluginPackageFamilyName："Gets or sets the package family name of
                //   the VPN plug-in to be used for this VPN plug-in profile. **This field is
                //   required for VpnPlugInProfile to be valid.**"
                VpnPluginPackageFamilyName = Windows.ApplicationModel.Package.Current.Id.FamilyName,

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
            pluginProfile.ServerUris.Add(new Uri("http://127.0.0.1:0"));

            // 【官方】AddProfileFromObjectAsync(IVpnProfile)："Creates a new VPN Profile based on
            //   the VpnProfile input object." → 返回 VpnManagementErrorStatus，Ok 才算成功。
            return await Agent.AddProfileFromObjectAsync(pluginProfile);
        }

        public static async Task<VpnManagementErrorStatus> ConnectAsync(VpnPluginConfig? config = null)
        {
            VpnManagementErrorStatus status = await EnsureProfileAsync(config);
            if (status != VpnManagementErrorStatus.Ok)
            {
                Debug.WriteLine($"VpnProfileSetup: 建 profile 失败: {status}");
                return status;
            }

            IReadOnlyList<IVpnProfile> profiles = await Agent.GetProfilesAsync();
            foreach (IVpnProfile profile in profiles)
            {
                if (profile.ProfileName != ProfileName)
                    continue;

                // 【官方】ConnectProfileAsync(IVpnProfile)："Connects to an existing VPN profile."
                return await Agent.ConnectProfileAsync(profile);
            }

            return VpnManagementErrorStatus.Other;
        }

        public static async Task<VpnManagementErrorStatus> DisconnectAsync()
        {
            IReadOnlyList<IVpnProfile> profiles = await Agent.GetProfilesAsync();
            foreach (IVpnProfile profile in profiles)
            {
                if (profile.ProfileName != ProfileName)
                    continue;

                // 【官方】DisconnectProfileAsync(IVpnProfile)："Disconnects an existing VPN profile."
                return await Agent.DisconnectProfileAsync(profile);
            }

            return VpnManagementErrorStatus.Other;
        }
    }
}
