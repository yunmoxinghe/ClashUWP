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
    public static class VpnProfileSetup
    {
        internal const string ProfileName = "ClashUWP";

        private static readonly VpnManagementAgent Agent = new();

        private static string PackageFamilyName => Windows.ApplicationModel.Package.Current.Id.FamilyName;

        /// <summary>
        /// 找出属于我们这个包的 plugin profile。
        ///
        /// 【官方 sample 的做法】<c>GetMatchingProfilesAsync</c>：
        /// <i>"In this file, GetProfilesAsync is only called here. Everything else should use the
        /// results of this call."</i>
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
        /// 【官方 sample 的 DeleteExistingProfile】：先 Disconnect 再 Delete。
        /// 顺序反了会失败 —— 一个还连着的 profile 是删不掉的。
        /// </summary>
        private static async Task<VpnPlugInProfile?> DeleteOurProfileAsync()
        {
            VpnPlugInProfile? profile = await FindOurProfileAsync();
            if (profile == null)
                return null;

            await Agent.DisconnectProfileAsync(profile);
            await Agent.DeleteProfileAsync(profile);
            return profile;
        }

        /// <summary>
        /// 【官方 sample 的 CreateAppProfile】：建一个新 profile。
        /// </summary>
        private static async Task<VpnManagementErrorStatus> AddProfileAsync(VpnPluginConfig config)
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
            pluginProfile.ServerUris.Add(new Uri("http://127.0.0.1"));

            // 【官方】AddProfileFromObjectAsync(IVpnProfile)："Creates a new VPN Profile based on
            //   the VpnProfile input object."
            return await Agent.AddProfileFromObjectAsync(pluginProfile);
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

            VpnPlugInProfile? old = await DeleteOurProfileAsync();
            sb.Append(old == null ? "删旧=无" : "删旧=完成");

            VpnManagementErrorStatus add = await AddProfileAsync(config);
            sb.Append($", 新建={add}");
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

            Debug.WriteLine($"VpnProfileSetup.RecreateProfileAsync: {sb}");
            return sb.ToString();
        }

        /// <summary>
        /// 「连接 VPN」按钮。<b>只连接，不建 profile</b> —— 与官方 sample 的分工一致。
        /// </summary>
        public static async Task<string> ConnectAsync()
        {
            VpnPlugInProfile? target = await FindOurProfileAsync();
            if (target == null)
                return "找不到我们的 profile，请先点「建/重建 profile」";

            var sb = new StringBuilder();
            sb.Append($"[{target.ProfileName}] 状态={target.ConnectionStatus}, ");
            sb.Append($"连接={await Agent.ConnectProfileAsync(target)}");

            Debug.WriteLine($"VpnProfileSetup.ConnectAsync: {sb}");
            return sb.ToString();
        }

        public static async Task<string> DisconnectAsync()
        {
            VpnPlugInProfile? profile = await FindOurProfileAsync();
            if (profile == null)
                return "找不到我们的 profile";

            return $"断开={await Agent.DisconnectProfileAsync(profile)}";
        }
    }
}