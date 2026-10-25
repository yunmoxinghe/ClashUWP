using System;

namespace ClashUWP.Backend
{
    /// <summary>
    /// ★【1.0.123 · 更新生效阶梯】"这一版正文已经落盘，怎么让它真的生效"的判据 —— 纯函数。
    ///
    /// 【为什么要有这一份】1.0.122 把"连着 VPN 也照样更新"接通之后，剩下的是执行侧的洞：
    ///   <c>ReloadActiveConfigAsync</c> 只有两支出口 —— 内核可达就发 <c>PUT /configs?force=true</c>，
    ///   不可达就回一句「新订阅已落盘（内核未连接，下次连接自动加载）」。
    ///   后面那一句在<b>没有会话</b>时是真话，在<b>会话活着而内核不可达</b>时是谎：
    ///   那一刻平台认为隧道在跑、而数据面已经不认这份配置了，"下次连接"永远不会自己来。
    ///   ⇒ 用户点的"连接状态自动处理更新"缺的就是这一步：按连接状态< b>自己选一条能生效的路</b>，
    ///     而不是把"没生效"包装成"以后会生效"。
    ///
    /// ★★【对齐 Clash Verge 的 <c>reload_or_restart</c>（现读 dev 分支源码）】
    ///   <c>src-tauri/src/core/manager/config.rs</c>：
    ///     · <c>:359 apply_config(path)</c> → 非服务模式走 <c>:397 reload_or_restart(path)</c>；
    ///     · <c>:443-464</c> 整段就是这一支的骨架：<b>先</b> <c>reload_config(path)</c>
    ///       （<c>:466</c> = <c>mihomo().reload_config(true, path)</c>，即原地 <c>PUT /configs?force=true</c>），
    ///       <c>let Err(err) = ... else { return Ok(()) }</c> —— 原地成功就到此为止、<b>不断流</b>；
    ///       原地失败就打 <c>"Failed to apply configuration by mihomo api, restart core to apply it"</c>
    ///       并 <c>restart_core_during_config_update()</c>（<c>lifecycle.rs:785-802</c>：
    ///       受控 stop → <c>start_core_inner</c> → 复核 readiness generation → 重新套系统代理），
    ///       重启成功就把这次更新判成<b>成功</b>（<c>:457 Ok(())</c>）。
    ///     · 更新失败而正文已存时，回话固定是
    ///       <c>feat/profile.rs:234</c>「订阅已保存，但未能确认已应用到内核: …」——
    ///       本类的 <c>Unreadable</c> 两支照抄这个口径，不许写成成功。
    ///
    /// 【我们与 Verge 的一处<b>刻意</b>不同（这一处不能照抄）】
    ///   Verge 敢在"内核拒收"之后重启内核，是因为它在 apply <b>之前</b>跑了一遍
    ///   <c>CoreConfigValidator::validate_config_outcome_with(&yaml)</c>（<c>config.rs:350-355</c>）：
    ///   能走到 apply 的配置已经证明"重启后起得来"。我们没有等价校验器
    ///   （<c>SubscriptionShape.ProbeShape</c> 只验正文形态，不验 mihomo 收不收）。
    ///   ⇒ 我们把两种失败分开处理：
    ///     · <see cref="ApplyStage.Refused"/>（内核明确拒收）⇒ <b>绝不动隧道</b>。
    ///       拒收的语义是"这份配置 mihomo 不要"，重启只会把一条<b>还能用的旧隧道</b>换成
    ///       "起不来的新配置"，那是净变坏；
    ///     · <see cref="ApplyStage.Landed"/> / <see cref="ApplyStage.SilentAfterAccept"/>
    ///       （数据面已经不在/不应答）⇒ 隧道本来就已经不认这份配置了，
    ///       重拨是把状态拉回"通"的唯一动作 ⇒ 自动做一次。
    ///   另一处差别：Verge 的内核是它自己的子进程，restart = 换进程；我们的数据面是
    ///   <b>插件宿主进程内的 <c>clash_bridge.dll</c></b>（<c>Vpn\MihomoBridge.cs</c>），
    ///   前台没有任何动词能重启它，等价动作只有"把这一条会话拆掉再拨一次"
    ///   （新宿主进程在 Connect 时读 <c>config.yaml</c>，这就是我们的 restart）。
    ///
    /// 【为什么"只自动重拨一次、且只在读得出会话时才动"】
    ///   自动重拨动的是整机网络，本项目在这上面有账：Docs/55 的三段式、828 无限重拨环
    ///   （memory「Redial 828 loop」）、以及 <c>SessionStateUnknown</c> 那一族
    ///   "读数坏了 ≠ 没连着"的实测（Docs/51 §七.4：两条判据逐字成立，平台照样回
    ///   <c>AlreadyConnected</c>）。所以：
    ///     · 会话状态<b>读不出</b> ⇒ 不动隧道，如实回"未能确认已应用"；
    ///     · 台上<b>没有会话</b> ⇒ 不动（「空壳不拨号」是已验收口径，凭空拨一条是新造行为）；
    ///     · <b>切订阅正在进行</b> ⇒ 不动，那一次三段式自己会重拨；
    ///     · 只有"会话活着 + 数据面不认这份配置"才拨，且<b>一轮更新只拨一次</b>
    ///       （到点判据在 <see cref="SubscriptionAutoPolicy"/>，失败有退避，不会变成环）。
    /// </summary>
    internal static class ConfigApplyPolicy
    {
        /// <summary>这次更新落到盘之后，原地生效那一支走到了哪一步。</summary>
        internal enum ApplyStage
        {
            /// 内核 REST 不可达 ⇒ 重载<b>根本没发出去</b>（只有文件就位）。
            Landed,
            /// 重载被接受且核心随后仍在 ⇒ <b>唯一</b>不需要后续动作的成功。
            Accepted,
            /// 内核明确拒收（非 2xx + 原因）⇒ 很可能这份配置非法。
            Refused,
            /// 发了重载且拿到 2xx，随后核心无应答 ⇒ 很可能新配置把核心搞崩了。
            SilentAfterAccept,
        }

        /// <summary>阶梯的结论：要不要自动重拨一次，以及不动手时的口径。</summary>
        internal enum NextAction
        {
            /// 什么都不做（成功、或如实失败、或交给别人的那一次重连）。
            None,
            /// 自动拆掉这条会话再拨一次，让新宿主按 <c>config.yaml</c> 起数据面。
            ReconnectOnce,
        }

        /// <summary>一次判据的结论。<c>Ok</c> 只在 <see cref="NextAction.None"/> 时有意义
        /// （要重拨的那一支，成败由重拨的结果自己说）。</summary>
        internal readonly record struct Plan(NextAction Action, bool Ok, string Why);

        /// <summary>
        /// ★【1.0.123】本轮生效阶梯<b>是否已经动过隧道</b>（进过 <c>ReconnectOnce</c> 那一级）。
        ///
        /// 【为什么需要这个标记】自动重拨的两句"没成"里，一句由 <c>AutoReconnectForApplyAsync</c>
        ///   自己回话（断开/拨号正常返回但未应答），另一句是异常从 <c>VpnSession</c> 里冒出去穿到
        ///   外层 <c>catch</c> —— 那一刻隧道可能已经被拆了而回话只写"热重载失败"，
        ///   用户读不出"我现在没网、要点连接"。外层 catch 靠这一位决定要不要附上那句出口。
        /// 【形状】单次进程内的一个标记就够：调用它的那一路（<c>ReloadActiveConfigAsync</c>）
        ///   整体由 <c>ProfileGate</c> 的闸串行，进门前归零、进门后只由重拨那一级置位。
        /// </summary>
        internal static volatile bool ReconnectMayHaveStarted;

        /// <summary>
        /// 阶梯判据。<paramref name="presence"/> 是"台上有没有我们的活会话"的<b>纯读</b>结论
        /// （<see cref="SessionPresence"/>）；<see cref="ApplyStage.Accepted"/>
        /// 与 <see cref="ApplyStage.Refused"/> 两支不读它，调用方传
        /// <see cref="SessionPresence.NotAsked"/> 即可。
        /// </summary>
        internal static Plan Decide(ApplyStage stage,
                                    SessionPresence presence,
                                    bool switchInProgress)
        {
            switch (stage)
            {
                case ApplyStage.Accepted:
                    return new Plan(NextAction.None, true,
                        "已热重载内核并确认核心在线 ⇒ 新订阅原地生效，隧道未断");

                case ApplyStage.Refused:
                    // ★ 不动隧道的理由见类注释「刻意不同」那一段。
                    //   这句只说"动作与代价"，拒收原文由调用方带在最前面，不重复一遍。
                    return new Plan(NextAction.None, false,
                        "旧隧道原样保留，不自动重拨（拿被拒收的配置重拨，只会把还能用的隧道" +
                        "换成起不来的那一条）");

                case ApplyStage.SilentAfterAccept:
                case ApplyStage.Landed:
                default:
                    string shape = stage == ApplyStage.SilentAfterAccept
                        ? "已发重载但核心随后无应答（很可能新配置导致核心崩溃）"
                        : "内核 REST 不可达（重载没发出去，只有 config.yaml 就位）";
                    if (switchInProgress)
                        return new Plan(NextAction.None, true,
                            $"{shape}；切订阅正在进行 ⇒ 不另开一次重拨，交给那一次的" +
                            "「先断开→后修改→再重连」（它拨的就是这份新配置）");
                    switch (presence)
                    {
                        case SessionPresence.LiveSession:
                            return new Plan(NextAction.ReconnectOnce, false,
                                $"{shape}，但平台侧会话是活的 ⇒ 这条隧道不会自己加载新配置，" +
                                "自动断开重拨一次（对齐 Verge 的 reload_or_restart）");
                        case SessionPresence.NoSession:
                            return new Plan(NextAction.None, true,
                                $"{shape}；台上没有会话 ⇒ 新订阅已就位，下次连接自然加载" +
                                "（不凭空拨号）");
                        case SessionPresence.Unreadable:
                        default:
                            // ★ 口径照抄 Verge feat/profile.rs:234，不写成成功。
                            return new Plan(NextAction.None, false,
                                $"{shape}；会话状态读不出 ⇒ 不自动重拨。" +
                                "订阅已保存，但未能确认已应用到内核（自己断开再连一次即可生效）");
                    }
            }
        }
    }
}
