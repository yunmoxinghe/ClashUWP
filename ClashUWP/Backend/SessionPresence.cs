namespace ClashUWP.Backend
{
    /// <summary>
    /// ★【1.0.123 · 更新生效阶梯】"台上到底有没有我们这条活会话"的三态答案（外加一态"这一支不用问"）。
    ///
    /// 【为什么单独成一个文件而不是嵌在 <c>VpnProfileSetup</c> 里】判据
    ///   <see cref="ConfigApplyPolicy"/> 要能被离线夹具 <c>Tools\YamlShapeTest</c> 直接 <c>&lt;Compile Link&gt;</c>
    ///   过去逐条钉住，而 <c>VpnProfileSetup</c> 带着 WinRT 投影（<c>Windows.Networking.Vpn</c>）
    ///   在离线夹具里根本编不动。类型放这里，读它的实现仍在 <c>VpnProfileSetup</c>，
    ///   夹具只需要这两份文件。
    ///
    /// 【为什么必须是三态而不是一个 bool】Docs/51 §七.4 装机两次（05:29:24 / 05:31:09）实测：
    ///   清单里读不出 <c>Connected</c>、默认路由在 WLAN，两条判据逐字成立，平台在
    ///   <c>ConnectProfileAsync</c> 那里照样回 <c>AlreadyConnected</c>。
    ///   ⇒ "没读到会话"和"读不出"是两件事：把后者当成前者，就会在一条<b>可能活着</b>的隧道上
    ///   凭空重拨；把前者当成后者，就把"根本没人连着"当成"要修"。
    ///   阶梯靠这两个值走两条完全不同的路（一条动整机网络，一条只写文件）。
    /// </summary>
    internal enum SessionPresence
    {
        /// <summary>调用方刻意没读 —— 不需要会话状态就能定结论的那两支
        /// （<see cref="ConfigApplyPolicy.ApplyStage.Accepted"/> /
        ///  <see cref="ConfigApplyPolicy.ApplyStage.Refused"/>）用它，
        /// 目的是别让每一次成功的原地热重载多读一遍 profile 清单
        /// （那一读自己就可能中毒或抛，见 Docs/45 的 0x80070006）。</summary>
        NotAsked,

        /// <summary>本包全部条目都读得出来，且没有一条是会话态 ⇒ 台上没有会话 ⇒ <b>不许凭空拨号</b>。</summary>
        NoSession,

        /// <summary>有一条本包条目是 <c>Connected</c>/<c>Connecting</c> ⇒ 会话活着
        /// （官方尺子，<c>GetFirstConnectedProfileAsync</c> 把这两态都算"那一条会话"）。</summary>
        LiveSession,

        /// <summary>没有会话态条目、但有条目<b>读数坏掉</b> ⇒ "有没有会话"这件事不可知 ⇒ 阶梯不动隧道，
        /// 如实报"未能确认已应用"。</summary>
        Unreadable,
    }
}
