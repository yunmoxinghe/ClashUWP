using System;
using System.Threading;
using System.Threading.Tasks;

using ClashUWP.Vpn;

namespace ClashUWP.Backend
{
    /// <summary>
    /// ★【1.0.116】系统 VPN 条目与连接的<b>唯一后端入口</b>。
    ///
    /// 前台（MainComponent）只管 UI 渲染与用户意图采集，所有后端动作调本类；
    /// CLI 只管命令行解析，通过 AppService 打到前台进程后同样走本类。
    /// 本类以下不再允许前台/CLI 直接调 VpnSession / VpnProfileSetup / SubscriptionManager。
    ///
    /// 【互斥语义】变更类动词（Reconcile / Rebuild / Connect / Disconnect / Switch）
    /// 共享一把语义级闸：一次只允许一个变更流程在跑。
    /// 只读动词（Status / ConnectedSub / ParallelConnected）不占闸。
    ///
    /// 【后续】本类将搬进 AppService 后台任务宿主，成为跨进程唯一入口；
    /// 当前先以前台进程内静态类落地，接口形状不变。
    /// </summary>
    internal static class ProfileGate
    {
        private static readonly SemaphoreSlim FlowGate = new(1, 1);
        private const int FlowTimeoutMs = 30_000;

        // ─── 变更类动词（占闸）─────────────────────────────────────────────

        /// <summary>对账：期望状态 vs 实际状态，执行最小差异。</summary>
        internal static async Task<string> ReconcileAsync(string trigger, bool allowPrune = true)
        {
            if (!await AcquireAsync(trigger))
                return $"ProfileGate=忙（{trigger} 本轮跳过）";
            try { return await SubscriptionManager.SyncSystemProfilesAsync(trigger, allowPrune); }
            finally { FlowGate.Release(); }
        }

        /// <summary>重建：删光本包条目 → 按期望状态全部新建。</summary>
        internal static async Task<string> RebuildAsync()
        {
            if (!await AcquireAsync("rebuild"))
                return "ProfileGate=忙（rebuild 本轮跳过）";
            try { return await VpnProfileSetup.RecreateProfileAsync(); }
            finally { FlowGate.Release(); }
        }

        /// <summary>连接。targetSubId 空 = 基础 profile；非空 = 订阅 profile。</summary>
        internal static async Task<string> ConnectAsync(string? targetSubId = null)
        {
            string label = $"connect:{targetSubId ?? "(base)"}";
            if (!await AcquireAsync(label))
                return $"ProfileGate=忙（{label} 本轮跳过）";
            try { return await VpnSession.ConnectAsync(targetSubId); }
            finally { FlowGate.Release(); }
        }

        /// <summary>断开当前活动会话。</summary>
        internal static async Task<string> DisconnectAsync()
        {
            if (!await AcquireAsync("disconnect"))
                return "ProfileGate=忙（disconnect 本轮跳过）";
            try { return await VpnSession.DisconnectAsync(); }
            finally { FlowGate.Release(); }
        }

        /// <summary>三段式切订阅：先断开 → 后修改 → 再重连。</summary>
        internal static async Task<(bool Ok, string Message, bool Redialed)> SwitchAsync(
            string targetSubId, Func<Task<(bool ok, string detail)>> modifyStep)
        {
            // ★ Switch 内部自己管 SwitchInProgress 标记与三段顺序，
            //   不再额外占 FlowGate（否则与内部 ConnectAsync 死锁）。
            return await VpnSession.SwitchSubscriptionAsync(targetSubId, modifyStep);
        }

        // ─── 只读动词（不占闸）─────────────────────────────────────────────

        /// <summary>当前连接状态文本。</summary>
        internal static Task<string> StatusAsync() => VpnSession.StatusAsync();

        /// <summary>当前已连接的订阅 id（空 = 没连或连的基础）。</summary>
        internal static Task<string> ConnectedSubAsync() => VpnSession.ConnectedSubAsync();

        /// <summary>诊断：有没有两条同时 Connected（正常不该出现）。</summary>
        internal static Task<string> ParallelConnectedAsync() => VpnSession.ParallelConnectedAsync();

        /// <summary>状态不可读时的占位文本（前台判"读数中毒"用）。</summary>
        internal const string StatusUnreadable = VpnProfileSetup.StatusUnreadable;

        // ─── 订阅数据（只读）──────────────────────────────────────────────

        /// <summary>订阅清单。</summary>
        internal static Task<System.Collections.Generic.List<SubscriptionManager.Profile>> ListSubscriptionsAsync()
            => SubscriptionManager.ListAsync();

        /// <summary>当前生效订阅。</summary>
        internal static Task<SubscriptionManager.ActiveInfo> GetActiveSubscriptionAsync()
            => SubscriptionManager.GetActiveAsync();

        /// <summary>订阅 id → 显示名（纯格式化，无副作用）。</summary>
        internal static string DisplayName(SubscriptionManager.Profile? p,
            System.Collections.Generic.List<SubscriptionManager.Profile> profiles)
            => SubscriptionManager.DisplayName(p, profiles);

        // ─── 订阅变更（占闸）──────────────────────────────────────────────

        /// <summary>拉取订阅并可选激活。</summary>
        internal static async Task<SubscriptionManager.Result> FetchSubscriptionAsync(string url, bool activate)
        {
            if (!await AcquireAsync("fetch"))
                return new SubscriptionManager.Result(false, "ProfileGate=忙（fetch 本轮跳过）");
            try { return await SubscriptionManager.FetchAsync(url, activate); }
            finally { FlowGate.Release(); }
        }

        /// <summary>激活指定订阅（写 config.yaml + 记生效 + 热重载 + 对账）。</summary>
        internal static async Task<SubscriptionManager.Result> ActivateSubscriptionAsync(string subId)
        {
            if (!await AcquireAsync($"activate:{subId}"))
                return new SubscriptionManager.Result(false, "ProfileGate=忙（activate 本轮跳过）");
            try { return await SubscriptionManager.ActivateAsync(subId); }
            finally { FlowGate.Release(); }
        }

        /// <summary>刷新当前生效订阅（重新拉取正文）。</summary>
        internal static async Task<SubscriptionManager.Result> UpdateActiveSubscriptionAsync()
        {
            if (!await AcquireAsync("update-active"))
                return new SubscriptionManager.Result(false, "ProfileGate=忙（update-active 本轮跳过）");
            try { return await SubscriptionManager.UpdateActiveAsync(); }
            finally { FlowGate.Release(); }
        }

        /// <summary>热重载当前生效订阅的内核配置。</summary>
        internal static async Task<SubscriptionManager.Result> ReloadActiveConfigAsync()
        {
            if (!await AcquireAsync("reload"))
                return new SubscriptionManager.Result(false, "ProfileGate=忙（reload 本轮跳过）");
            try { return await SubscriptionManager.ReloadActiveConfigAsync(); }
            finally { FlowGate.Release(); }
        }

        /// <summary>跟账：隧道实际连的订阅与账本不一致时，以隧道为准修正账本。</summary>
        internal static Task<SubscriptionManager.Result> AdoptSubscriptionAsync(string connectedSub)
            => SubscriptionManager.AdoptAsync(connectedSub);

        // ─── 欠账台账（只读 + 脏位控制）────────────────────────────────────

        /// <summary>台账是否脏（有欠账未补）。</summary>
        internal static bool OwedDirty => ReconcileOwed.Dirty;

        /// <summary>台账快照文本。</summary>
        internal static Task<string> OwedSnapshotAsync() => ReconcileOwed.SnapshotAsync();

        /// <summary>台账条目数。</summary>
        internal static Task<int> OwedCountAsync() => ReconcileOwed.CountAsync();

        /// <summary>设置台账脏位。</summary>
        internal static void OwedSetDirty(bool value) => ReconcileOwed.SetDirty(value);

        // ─── 非 async 的轻量入口 ─────────────────────────────────────────

        /// <summary>切换是否正在进行（轮询读它，非 false 就跳过回填）。</summary>
        internal static bool SwitchInProgress => VpnSession.SwitchInProgress;

        /// <summary>认领活会话（哨兵未在跑时武装）。</summary>
        internal static void AdoptLiveSession() => VpnSession.AdoptLiveSessionIfUnprotected();

        // ─── 内部 ────────────────────────────────────────────────────────

        private static async Task<bool> AcquireAsync(string trigger)
        {
            bool acquired = await FlowGate.WaitAsync(FlowTimeoutMs).ConfigureAwait(false);
            if (!acquired)
                RollingLog.Warn("profile-gate",
                    $"等上一个变更流程超过 {FlowTimeoutMs / 1000}s 仍未放行 ⇒ {trigger} 本轮放弃");
            return acquired;
        }
    }
}
