using System;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// 插件与前台共用的会话保险丝判据。
    ///
    /// 【为什么不再设固定时长上限】固定到点断开会把健康会话也拆掉；长期占网
    /// 由黑洞探针、异常重拨和物理网络兜底分别负责，不再依赖一个无条件计时器。
    /// </summary>
    internal static class SessionWatchdogPolicy
    {
        internal const int MaxConsecutiveProbeFailures = 2;
        internal const int MaxUnmanagedRedials = 1;
        internal const int FakeIpSettleSeconds = 30;

        internal static string? EvaluateTrip(
            double aliveSeconds,
            int tunnelProbeFailures,
            int fakeIpProbeFailures,
            int unmanagedRedials,
            string fakeProbeNote = "")
        {
            if (unmanagedRedials > MaxUnmanagedRedials)
                return $"{unmanagedRedials} 次非本次发起的 connect（阈值 {MaxUnmanagedRedials}）→ 平台在自动重拨";

            if (tunnelProbeFailures >= MaxConsecutiveProbeFailures)
                return $"连续 {tunnelProbeFailures} 轮隧道探针不可达 → 隧道黑洞";

            if (fakeIpProbeFailures >= MaxConsecutiveProbeFailures &&
                aliveSeconds > FakeIpSettleSeconds)
            {
                string note = fakeProbeNote.Length > 0 ? $"（最后一轮 {fakeProbeNote}）" : "";
                return $"连续 {fakeIpProbeFailures} 轮系统解析不再返回 fake-ip{note}" +
                       " → 隧道的 DNS 侧已断，宿主应用不再经隧道，判死";
            }

            return null;
        }
    }
}
