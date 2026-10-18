using System;
using System.Collections.Generic;

using Windows.Storage;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// <c>LocalState\settings.txt</c> 里的插件开关（<c>key=value</c>，一行一个）。
    ///
    /// 【为什么要文件开关，而不是写死在代码里】
    ///   UWP 改一行代码就要重装一次 MSIX，而"隧道内 DNS"这类改动本身有风险
    ///   （DNS 一旦进隧道，隧道坏掉时整机解析跟着坏）。文件开关让同一次部署
    ///   能做 A/B：外部只写文件、不重装，就能对比"DNS 进隧道 / 不进隧道"两种行为，
    ///   出问题时写一行 <c>dnsInTunnel=0</c> 立刻退回旧行为。
    ///
    /// 【读取时机】每次 Connect 读一次（文件极小，同步读；Connect 本身是同步回调）。
    /// 缺失文件/缺 key 一律取默认值，解析失败不抛。
    /// </summary>
    internal static class LocalSettings
    {
        /// <summary>DNS 是否收进隧道（对齐安卓版 addDnsServer + dnsHijacking）。默认开。</summary>
        internal static bool DnsIntoTunnel = true;

        /// <summary>mihomo 内核是否跨会话常驻（对齐安卓"内核只起一次"）。默认开。</summary>
        internal static bool ResidentBridge = true;

        /// <summary>常驻时是否用 mihomo REST 控制器热重载订阅（对齐 Verge 的做法）。默认开。</summary>
        internal static bool HotReload = true;

        /// <summary>
        /// 是否把节点服务器 IP 作为 <c>/32</c> 排除路由下发。<b>默认开</b>（保持既有行为）。
        ///
        /// 【为什么留这个开关】2026-10-16 run 14 实测：隧道起来之后
        /// <c>Find-NetRoute 82.39.109.155</c>（节点 IP）落到
        /// <c>Loopback Pseudo-Interface 1 ifIdx=1</c>，而同一时刻
        /// <c>223.5.5.5</c> 落到 <c>ClashUWP ifIdx=45</c>（隧道）。
        /// 也就是说这批 /32 排除路由被平台装到了<b>环回接口</b>上，
        /// 比隧道的 0.0.0.0/0 更特定 ⇒ 节点拨号包直接进环回 ⇒
        /// <c>connectex: unreachable host</c>（run 13 内核原文）。
        /// 但「排除路由本身有害」和「排除路由被装到环回，是因为 profile 的
        /// ServerUris=127.0.0.1，平台据此选装路由的接口」是两个不同的假设，
        /// 必须能各自单变量验证。<c>nodeExclusions=0</c> 就是前者的验证臂。
        /// </summary>
        internal static bool NodeExclusions = true;

        /// <summary>
        /// 是否给 <c>StartWithTrafficFilter</c> 传一个「只把本包拆出强制隧道」的
        /// <c>VpnTrafficFilterAssignment</c>。默认 <b>0 = 不传（保持 null）</b>。
        ///
        /// 【为什么需要它 —— run 13/15/16/17 四臂排除了路由层的所有可能后剩下的唯一出口】
        ///   根因是「承载 mihomo 的进程被自己下发的 0.0.0.0/0 抓回隧道」，而这条进程内
        ///   自激在路由层<b>无法</b>解决：<c>VpnRoute</c> 只有 Address/PrefixSize，
        ///   没有 NextHop/Metric ⇒ 排除路由被平台装成环回黑洞（run 14 实测
        ///   <c>Find-NetRoute 82.39.109.155 → ifIdx=1</c>），
        ///   安卓 <c>VpnService.protect()</c> 那种「这个 socket 绕过隧道」的表达能力
        ///   在 UWP VPN 插件侧不存在。于是只剩按<b>应用</b>分流的官方原语：
        ///   <c>VpnTrafficFilter(VpnAppId(PackageFamilyName, 本包)) { SplitRouting }</c>
        ///   —— 让本包（前台 + 同 family 的 vpnClient 后台任务，mihomo 就在里面）
        ///   可以走物理网卡，其余应用继续按 include 0.0.0.0/0 进隧道。
        ///
        /// 【三臂定义（同一次部署可切，不必重装）】
        ///   0 = 传 null。docs/76 tfmode=2 实测的「全部应用进隧道」基线。
        ///   1 = 传 assignment，<c>AllowOutbound=false</c>：不匹配 filter 的流量
        ///       仍进隧道，只有本包 SplitRouting。这是期望臂。
        ///   2 = 传 assignment，<c>AllowOutbound=true</c>：按 docs/76 §5.4 的语义，
        ///       「不匹配任何 filter 的流量允许绕过 VPN 直连」⇒ 预期其它应用
        ///       <b>不</b>进隧道（tfmode=1 实测 reqSeen=0）。留着是为了把
        ///       「filter 是否真的生效」和「AllowOutbound 语义」这两件事分开判读，
        ///       并提供一个「隧道彻底失败也不黑洞全网」的兜底形态。
        ///
        /// 【风险】filter 一旦装错，最坏情况是数据面整机旁路（2 号的预期行为），
        /// 而不是当前这样只能靠 600 s 硬上限断开 —— 哨兵与 rasdial 兜底照旧。
        /// </summary>
        internal static int SelfSplitTunnel = 0;

        /// <summary>
        /// 外层 transport 的连接目标。默认 <c>auto</c> = <b>静默黑洞 <c>192.0.2.1</c></b>；
        /// <c>node</c> = 本次订阅里第一个 IPv4 节点服务器 IP（= 1.0.24 及以前的旧默认）；
        /// 拿不到节点时退回 <c>8.8.8.8</c>；写具体地址（如 <c>192.168.0.1</c>）即固定值，
        /// <c>127.0.0.1</c> 是旧的实验基线。
        ///
        /// 【为什么默认不再是 127.0.0.1 —— run 21 三臂定论】
        ///   同一个 profile、同一次部署，只换这个 socket 的已连接远端：
        ///     21-A outerTarget=127.0.0.1    → <c>Find-NetRoute 节点IP</c> = <b>Loopback
        ///            Pseudo-Interface 1 ifIdx=1</b>（比隧道的 0/0 更特定 ⇒ 拨号包进环回黑洞）
        ///     21-B outerTarget=192.168.0.1  → <b>WLAN ifIdx=6</b> nh=192.168.0.100
        ///     21-C outerTarget=82.39.109.155→ <b>WLAN ifIdx=6</b>（同上）
        ///   即平台是按<b>这个 socket 会走哪条路</b>来安装 <c>Ipv4ExclusionRoutes</c> 的接口的。
        ///   而 <c>VpnRoute</c> 没有 NextHop/Metric，路由层无法表达"装到物理网卡"，
        ///   所以这是唯一能纠正排除路由归属的输入。run 15B/20B 已证 profile 的
        ///   <c>ServerUris</c> 与此无关（换成网关、换成节点 IP 归属逐字不变）。
        ///
        /// 【为什么默认从"节点 IP"换成静默黑洞 —— docs/93 + run 25 的 828 风暴】
        ///   微软官方口径（VPN Plugin framework and detection of closed peer connection）：
        ///   <i>"The OS will detect that the outer socket has been closed and will automatically
        ///   restart (you'll get a new connect call)."</i>
        ///   平台的 keep-alive 是要真发出去的（GetKeepAlivePayload 给的是 Send 池的包），
        ///   而节点机器的 UDP 端口多半没人监听 ⇒ ICMP port unreachable ⇒ 外层 socket 被判关闭
        ///   ⇒ 平台拆通道重拨。run 25 之后事件日志的形状正是这个：RasClient 单日 dial=413、
        ///   终止原因码 828 占 411 次、相邻间隔稳定 97±2 s、发起者 SYSTEM。
        ///   黑洞 <c>192.0.2.1</c>（RFC 5737 TEST-NET-1）到网关即丢弃、无应答也无 ICMP 回程，
        ///   外层 socket 永不报错 —— 先遣项目 docs/79 实测过这个取值：
        ///   udp transport + 黑洞 ⇒ 45/90/60/60/60 s 稳定，Connect=1 / Disconnect=0。
        ///   注意 <c>auto</c> 现在是黑洞，旧行为改成显式写 <c>node</c>（反向 A/B 用）。
        ///
        /// 【为什么取公网地址而不是写死网关】网关地址随网络变（家里 192.168.0.1、
        ///   手机热点 192.168.x.x、以太网另算），写死等于换网就坏。黑洞是文档保留段，
        ///   任何网络上都不会有真实服务，却仍然是公网路由 ⇒ 走物理网卡。
        ///   UDP <c>ConnectAsync</c> 不握手不发包，对端不存在也立即成功，所以这个目标
        ///   只被平台用来选接口，不承担任何传输。
        /// </summary>
        internal static string OuterTarget = "auto";

        /// <summary>
        /// 外层 transport 的端口，配合 <see cref="OuterTarget"/> 用。UDP connect 不握手，
        /// 对端不存在也立即成功。默认 <c>9</c>（discard）：黑洞地址上本来就没有服务，
        /// 取一个"即使真被送到也只是丢弃"的端口，避免撞到真实监听（53/443）招来回程。
        /// </summary>
        internal static string OuterPort = "9";

        /// <summary>
        /// 是否把交给内核的 YAML 强制改成 <c>dns.enhanced-mode: fake-ip</c>
        /// （并按需补 <c>fake-ip-range</c>/<c>fake-ip-filter</c>、锁 <c>tun.enable: false</c>）。
        /// 默认 <b>开</b>。理由见 <see cref="KernelConfig"/> 的类注释：
        /// run 24 两臂 —— fake-ip + DNS 进隧道 ⇒ <c>www.google.com → 198.18.0.15</c> 且
        /// <c>/generate_204 = 204/2.87 s</c>；redir-host（订阅原样）⇒ 国内解析器给污染答案
        /// （<c>2001::1,104.244.42.197</c>），按域名出网必死。
        /// 留 <c>fakeIp=0</c> 是为了下次能做反向单变量（同一次部署切回 redir-host）。
        /// </summary>
        internal static bool FakeIp = true;

        /// <summary>
        /// 内核（mihomo 桥）冷启动失败时是否<b>立即放弃这次连接</b>。默认 <b>开</b>。
        ///
        /// 【为什么】run 25 实测：改写后的 <c>config.kernel.yaml</c> 有一行 YAML 语法问题
        /// → <c>Bridge=start 失败 rc=-2: parse mihomo config: yaml: line 576</c>，
        /// 但旧代码只记诊断就 <c>return</c>，隧道照装 —— 于是
        /// <c>Encapsulated=883 / Injected=0</c>：应用流量全进隧道、没人回程，
        /// 整机断网约 60~90 s 才由健康哨兵兜住。
        /// 隧道已经建起来之后才发现"内核没了"是最坏的时序：没有内核的 0.0.0.0/0 就是黑洞。
        /// 关掉它（<c>failFast=0</c>）只用于实验时"先保住连接再看诊断"的读法。
        /// </summary>
        internal static bool FailFast = true;

        /// <summary>本次读取的来源说明，写进诊断快照用。</summary>
        internal static string Source = "默认值";

        /// <summary>
        /// 进隧道的路由形态。<c>wide</c> = profile 里的 <c>0.0.0.0/0</c>；
        /// <c>narrow</c>（默认）= 只下发 fake-ip 池 + 隧道内 DNS 的 /32。
        ///
        /// 【为什么要这个开关 —— 28-A 的缺口 A】
        ///   wide 下隧道期间 <c>www.baidu.com</c> 恒为 <c>000</c>（断开即 <c>200/0.097 s</c>）：
        ///   国内真 IP 撞上 <c>0.0.0.0/0</c> 进隧道，内核想 DIRECT 出去，出站又被同一条
        ///   默认路由抓回来 —— 节点 IP 有 /32 排除兜着，"所有国内 IP"没法穷举排除，
        ///   所以只能从反方向收窄。实现见 <c>ClashVpnPlugin.NarrowInclusionRoutes</c>。
        ///
        /// ★ 1.0.15 起默认改 <b>narrow</b>，依据是 run 29 的 A/B/A 三臂（单变量、双向可切）：
        ///   29-A wide  → 隧道内 google=204，国内 baidu=<b>000 FAIL</b>，dns=000
        ///   29-B narrow → 隧道内 google=204，国内 baidu=<b>200/0.10 s</b>，dns=404（可达）
        ///   29-C wide  → baidu 又回 <b>000</b>（可重复，不是抖动）
        ///   即 narrow 只救回国内直连，<b>没有</b>牺牲头条判据（境外 204 依旧），
        ///   而 204 只在隧道内可能出现：此刻直连 google 恒为 000（23:59 复测 3/3 次超时），
        ///   所以这个判据是有判别力的，不是"本来就能通"。
        ///
        /// 【narrow 的代价，写明白】隧道外只剩 fake-ip 池与隧道 DNS 的 /32，因此
        ///   「应用硬编码境外 IP」（例如自己连 8.8.8.8、或 DoH 直连境外 IP）会绕过隧道
        ///   走物理网卡 —— 对这些应用它不是全局代理。域名类流量（浏览器、绝大多数应用）
        ///   经 fake-ip 必然进隧道，这正是"其他应用能连外网"的目标形态。
        ///   想退回全应用强行走隧道就写 <c>includeMode=wide</c>，代价是国内直连再次黑洞。
        /// </summary>
        internal static string IncludeMode = "narrow";

        /// <summary>
        /// <c>Encapsulate</c> 里从 <c>packets</c> 取走的 buffer 怎么归还平台。默认 <b>2 = Append + 每包 Flush</b>（run 35/37 实测成立的那条）。
        ///
        /// 【为什么这是数据面的生死开关 —— run 32 抓到、先遣项目 docs/105 §4.1 早就写明】
        ///   官方对 buffer 归属的口径（sample 注释引用原文）：
        ///   <i>"...must eventually be returned to the VPN platform
        ///      (i.e. via IVpnPlugIn.Encapsulate or IVpnPlugIn.Decapsulate)"</i>
        ///   而 <c>Encapsulate</c> 的入参 <c>packets</c> 是<b>借</b>给我们的：
        ///   <c>RemoveAtBegin()</c> 只是把它从列表摘出来，<b>不等于</b>归还。
        ///   我们旧代码摘出来、拷进自己的 byte[]、喂给内核，然后就再也不管这个 buffer ——
        ///   平台的 <b>send 池</b>每包被借走一个、永不回还，借空之后平台<b>不再交付任何出站包</b>，
        ///   而且<b>不报错、不回调、不断线</b>：接口还在、路由还在、NRPT 还在、
        ///   会话仍记 <c>Connected</c>，只是宿主应用的包再也进不到我们手里 = 静默黑洞。
        ///
        /// 【实测形状（五臂同一个数）】<c>Encapsulated</c> 计数冻结在<b>逐字相同的 7188</b>：
        ///   run 27、run 28、run 31（alive=208 s 起）、run 32（alive=195 s 起）、
        ///   run 33（alive=236 s 起，且这臂额外加了外部请求压力、包速约 60 pps）——
        ///   五臂时长与包速都不同却停在同一个包数 ⇒ 触发条件是<b>包数配额</b>，不是时间、不是抖动。
        ///   同轮伴随：<c>Injected</c> 还在涨（内核活着）、宿主三探全 <c>000</c>、
        ///   <c>Resolve-DnsName</c> 空、断开瞬间立刻恢复。
        ///   ★ run 33 的心跳还多给了一条决定性证据：<c>ret=7188/1 retFail=0</c> ——
        ///     每个包都 Append 归还了，<b>照样停在 7188</b> ⇒ "Append 就是归还"被实测否证。
        ///
        /// 【官方 remarks 里其实写了正确的回收入口（先遣项目 docs/65:129 逐字引 MSDN）】
        ///   <i>"The VPN framework will then send the IVpnPacketBuffer objects in
        ///      encapsulatedPackets via one of the transports specified during Connect
        ///      <b>and will clean up the remaining IVpnPacketBuffer objects in packets</b>."</i>
        ///   ⇒ 平台回收的是<b>返回时还留在 <c>packets</c> 里</b>的 buffer。docs/65 对官方
        ///     sample 的审计结论（A1）正是这一点：sample 把 <c>packets</c> 全部
        ///     <c>RemoveAtBegin()</c> 取走、既不填 <c>encapsulatedPackets</c> 也不让平台回收，
        ///     "脱离平台对缓冲池的生命周期管理"。docs/13:216 同理：
        ///     "<b>"归还"和 Append 是同一个操作</b>…攒批只能攒在 Append 到 Flush 之间"
        ///     —— 也就是 <b>Append 之后不 Flush 的 buffer 一直被通道占着</b>（docs/105 §4.4）。
        ///
        /// 【取值（0/1/2/3/4/5 全留作单变量链的证据）】
        ///   0 = 旧行为（摘出来什么都不做）。只为反向复现留档，产品不许用；
        ///   1 = 摘出来 + <c>AppendVpnSendPacketBuffer</c>、不 Flush。
        ///       ★ 已被 run 33 <b>否证</b>（ret==encap==7188 仍停摆），仅作证据保留；
        ///   2 = Append + <c>FlushVpnSendPacketBuffers</c>。
        ///       ★★ <b>当前默认，也是唯一被实测证明能持续工作的形态</b>（run 35 / 1.0.32）：
        ///         <c>Encapsulated=18202</c>（越过停摆线 7188 的 2.5 倍）、会话 497 s、
        ///         38 轮心跳每轮 <c>google=204</c> + <c>fake-ip=OK:198.18.0.10</c>、
        ///         <c>returned=flushed=18202、retFail=0</c>、国内直连全程正常、
        ///         收尾 <c>nrpt=0 幽灵路由=0 profile=0</c> 与基线逐字相同。
        ///         代价（明说）：Flush 会把每个被摘下的出站包按"封装结果"再 UDP 送一份到
        ///         外层目标（我们是静默黑洞 <c>192.0.2.1:9</c>，路由器直接丢、不泄漏内容），
        ///         所以<b>上行多付一份</b>；这是过渡形态，真正的协议封装要在 <c>encapulatedPackets</c>
        ///         里放真正的隧道报文（WireGuard 等）时才成立。
        ///   3 = 不摘列表、只读包喂内核、返回时列表留给平台 cleanup（官方 remarks 那句
        ///       "clean up the remaining IVpnPacketBuffer objects in packets"）。
        ///       ★★ run 34（1.0.31，无归因列）表现为"整条数据面从第 11 秒就死"；
        ///         run 36（1.0.20 组件，加了分支之前的入口归因列）把机制钉死了：
        ///         <c>EncapsulateCalls=219</c>、<c>EncapListItemsSeen=243</c>、<c>EncapMaxBatch=4</c>
        ///         但 <c>EncapEnumerated=0</c>，且 <c>EncapEnumFirstError=
        ///         InvalidCastException:Specified cast is not valid.</c>
        ///         ⇒ <b>平台一直在回调、一直在交包，是我们读不出来</b>：投影出来的
        ///         <c>VpnPacketBufferList</c> 只有 <c>GetEnumerator</c>（无 GetAt / 无索引器，
        ///         见 .local/ifc35.txt），而 CsWinRT 给这个列表生成的枚举器<b>一取就抛</b>
        ///         <c>InvalidCastException</c>。所以"档 3 = 用 foreach 读"这条路作废；
        ///         1.0.19 的 run 34 之所以连异常都没留下，是因为那时 foreach 没有 try 包住、
        ///         异常直接漏给平台 —— 平台此后就不再交包（数据面静默死掉），这正是
        ///         "同步回调里绝不能抛"的实证。
        ///         想验证"不摘列表、交平台回收"这个<b>想法</b>本身，得换一条读包路：
        ///         <c>RemoveAtBegin()</c> 摘出来处理完再 <c>packets.Append()</c> 塞回去
        ///         （记档为待测的"档 4"）—— 那两个 mutator 是投影里实测存在的。
        ///   4 = <c>RemoveAtBegin()</c> 摘出来 → 就地喂内核 → <c>packets.Append()</c> <b>塞回原列表</b>，
        ///       一次 Append/Flush 都不调，返回时列表非空，让平台按 remarks
        ///       "<i>will clean up the remaining IVpnPacketBuffer objects in packets</i>" 自己回收。
        ///       ★★ 1.0.34 实现（档 3 作废后，"不摘列表"这个<b>想法</b>唯一的可达形态：
        ///         投影里 <c>VpnPacketBufferList</c> 只有 Append/AddAtBegin/RemoveAtBegin/
        ///         RemoveAtEnd/Clear + Size，没有 GetAt/索引器，foreach 又抛 InvalidCastException）。
        ///       ★★ 实现要点（<b>别照着 while(packets.Size&gt;0) 抄</b>）：塞回去会让 Size 恒不为 0，
        ///         所以必须在进函数时记住"交来的时候有几条"（<c>packets.Size</c> 快照），
        ///         只处理这么多条；Append 落在队尾、RemoveAtBegin 取队首，顺序因此保持不变。
        ///         未实测 ⇒ 判据：<c>reapp</c> 与 <c>encap</c> 同步涨、且<b>长跑越过 7188 不停摆</b>
        ///         才算成立；若平台根本不回收，receive/send 池会先在那里见底。
        ///   5 = 逐包 <c>AppendVpnSendPacketBuffer</c>，整批（每次 <c>Encapsulate</c> 回调）
        ///       只 <c>FlushVpnSendPacketBuffers()</c> <b>一次</b> —— 官方 sample 的攒批形态，
        ///       也是先遣项目 docs/13:216 那句"攒批只能攒在 Append 到 Flush 之间"的正解。
        ///       与档 2 的<b>唯一</b>差别是 Flush 的调用次数（N 次 → 每批 1 次）：
        ///       归还的包数、外层 UDP 重复量都不变，省下的是每包一次 Flush 的开销。
        ///       ★ 未实测，且<b>不作为默认</b>：run 35 只证明了"每包 Flush"能持续工作，
        ///         批边界会不会让平台少回收，必须用 <c>ret/flush/left</c> 三列实测对出来。
        ///
        /// ★ 若走"摘出来"的档（0/1/2/5），归还必须对<b>每一个</b>摘出来的 buffer 执行，
        ///   包括我们刻意丢弃的组播/广播包（<c>IsNonUnicastDestination</c> 那条分支）——
        ///   旧代码在 <c>continue</c> 之前同样没归还，那才是最先开始借池子的地方。
        /// </summary>
        internal static int EncapReturn = 2;

        /// <summary>
        /// 回程注入（<c>Inject</c>）什么时候调 <c>FlushVpnReceivePacketBuffers</c>。
        /// <b>0（默认）= 每注一个包就 Flush 一次</b>；<c>N&gt;0</c> = 攒到 N 个包（或本轮取空/休眠/退出前）
        /// 才 Flush 一次。
        ///
        /// 【为什么给这个把手】send 侧的教训（run 33/35）是"Append 只是入队，不 Flush 就借空池子"，
        ///   而 receive 侧是同一条 API 家族：<c>AppendVpnReceivePacketBuffer</c> + <c>FlushVpnReceivePacketBuffers</c>。
        ///   现在的 <c>Inject</c> 逐包 Flush 一定安全，但每个回程包都付一次提交开销；
        ///   攒批是官方 sample 的形态，收益要拿实测换。
        ///
        /// ★ 默认<b>保持 0</b>：run 35/37 的结论（头条判据 PASS）是在逐包 Flush 下量出来的，
        ///   没有实测证据之前不改产品默认（口径与 <see cref="EncapReturn"/> 的档 5 一致）。
        ///   拨这个开关时判据是 <c>rxpend</c>（在借未还）不单调上涨、<c>rxflush</c> 与 <c>inj</c> 同步。
        /// </summary>
        internal static int ReceiveBatch = 0;

        /// <summary>
        /// 回程注入取 receive buffer 用哪一条官方 API。
        /// ★★ 1.0.26 起默认 <b>true = <c>GetVpnReceivePacketBuffer()</c></b>；
        ///   <c>injectGetBuffer=0</c> 回退到 <c>RequestVpnPacketBuffer(Receive)</c>（对照臂，不许删）。
        ///
        /// 【切默认为什么的正式口径（本地 26100 元数据逐字，非网页）】
        ///   · <c>GetVpnReceivePacketBuffer()</c>："Requests a VpnPacketBuffer object from the
        ///     receive packet buffer pool <b>to be used in the decapsulation or injection of a
        ///     received packet from the VPN server into the local network stack</b>"
        ///     —— 官方<b>点名</b>给 injection 场景的入口，而本品的注入正是 injection；
        ///   · <c>RequestVpnPacketBuffer(VpnDataPathType, out VpnPacketBuffer)</c>：整句开头是
        ///     "<b>Not supported.</b> Requests a **IVpnPacketBuffer** from the specified pool..."；
        ///   · 连它吃的枚举都被判了：<c>VpnDataPathType</c> =
        ///     "**VpnDataPathType** is not supported and may be altered or unavailable in the future."
        ///   ⇒ 默认留在"被标记 Not supported"的那条上，是在为一个已经量过的成功形状继续押注
        ///     一个官方声明随时会撤的入口。
        ///
        /// 【先遣项目证据（为什么这次敢切默认）】docs/76 的 <c>injectmode</c> A/B：
        ///   <c>injectmode=1</c> = 回程泵线程 + <c>GetVpnReceivePacketBuffer()</c>，
        ///   24 个包全部 <c>attempt=0</c> 一次成功，成功率没掉 —— 同一条取包路在先遣项目里
        ///   是<b>跑通过数据面的</b>，不是纸面推断。
        ///
        /// ★ 两条借的是同一个 receive 池、归还路径（Append+Flush）也完全一样，差别只在取包层，
        ///   所以判据不变：<c>rxpend</c>（在借未还）不单调上涨、<c>rxflush</c> 与 <c>inj</c> 同步、
        ///   长跑越过 7188 不停摆。
        /// ★ <b>本臂自身在本仓库仍是未实测</b>（1.0.26 这轮是纯代码轮，不碰隧道）：
        ///   切默认是"按条文 + 按先遣项目实测"的判断，不是本仓库的实测结论。
        ///   一旦实测发现 <c>Get</c> 抛/返回 null，取包失败会现形为
        ///   <c>injFail</c>/<c>takeFail</c> 计数 —— 回落写 <c>injectGetBuffer=0</c> 即可，
        ///   代码里两条入口都保留。
        /// </summary>
        internal static bool InjectGetBuffer = true;

        /// <summary>
        /// keep-alive 取 send buffer 用哪一条官方 API。
        /// ★ 1.0.27 起默认 <b>false = <c>RequestVpnPacketBuffer(Send)</c></b>（run 35/37 量通过的形状）；
        ///   <c>sendGetBuffer=1</c> 才走 <c>GetVpnSendPacketBuffer()</c>（待测臂）。
        ///   1.0.26（审计项 T2）新增时曾把默认定为 true，下一节写明为什么又翻回来。
        ///
        /// 【条文（本地 26100 元数据逐字）】
        ///   · <c>GetVpnSendPacketBuffer()</c>："Retrieves a VpnPacketBuffer object from the send
        ///     packet buffer pool to use in encapsulation and transmission of a data packet from
        ///     the client network stack to the VPN server." —— 它是官方给 send 池的<b>具名</b>入口；
        ///   · 现在这条默认路径用的 <c>RequestVpnPacketBuffer</c> 与枚举 <c>VpnDataPathType</c>
        ///     都被标了 "Not supported"（原文见 <see cref="InjectGetBuffer"/>），
        ///     而本品的 send 借包<b>只有 keep-alive 一处</b>（<c>Encapsulate</c> 的包是平台交进来的，
        ///     不是我们借的）⇒ 切换的面很小，值得按条文摆正。
        ///
        /// ★★ <b>必须先承认的未验证项</b>：先遣项目对 <c>GetVpnSendPacketBuffer</c>
        ///   <b>没有任何实测数据</b>（docs 里唯一的真实调用是 C# 兜底分支，该路径从未触发），
        ///   投影层会不会抛也<b>无记录</b>。这跟 receive 侧不一样（那边有 injectmode=1 的实测）。
        ///   所以本臂自带失败计数：<c>takeFail</c>（取包失败次数 + 首个原因进 plugin.log）。
        ///
        /// 【失败形状为什么可以接受】取不到 buffer 时本方法交 <c>keepAlivePacket=null</c> ——
        ///   这与 <c>RequestVpnPacketBuffer</c> 抛异常时走的是<b>同一条</b>既有出口
        ///   （<c>GetKeepAlivePayload</c> 的 catch 已经这么写），不是新发明的失败形态。
        ///   真出问题按 <c>ka</c>/<c>takeFail</c> 两列对账，开启写 <c>sendGetBuffer=1</c>。
        ///
        /// ★★★ 【1.0.27 为什么把默认翻回 false】上一条 T2 提交把默认定成了 true，被复核点破：
        ///   本仓库的既有口径写在 <see cref="EncapReturn"/> 那一族注释里 ——
        ///   <b>"没有实测证据前不改默认"</b>（这条口径的来源很硬：档 3 就是"看着更贴条文"的
        ///   <c>foreach</c> 形态，run 34 里以 <c>InvalidCastException</c> 死在投影层，
        ///   整条数据面从第 11 秒静默停摆）。而 send 侧的证据量比 receive 侧<b>少一档</b>：
        ///   receive 那边有先遣项目 docs/76 的 <c>injectmode=1</c>（24 包全部 <c>attempt=0</c>）
        ///   可以背书，send 这边连一条真实触发记录都没有（上面第 329-331 行就是本轮自己写的）。
        ///   ⇒ keep-alive 是热路径上唯一"由我们主动向 send 池借包"的动作，把它的默认臂换成
        ///     零实测入口，等于让用户的第一次连接去当实验。条文优先适用于<b>有对照证据</b>的臂。
        ///   ★ 代码没删、开关还在：<c>sendGetBuffer=1</c> 就是那条臂，实测授权后单变量跑
        ///     （判据见 Docs/09 §12.6 第 1 项）。
        /// </summary>
        internal static bool SendGetBuffer = false;

        /// <summary>
        /// 插件侧会话看门狗（默认<b>开</b>）。
        ///
        /// 【为什么必须放在插件里，而不是前台哨兵】run 30 的两次致命失败都出在
        ///   "看门的人先掉线"：
        ///   ① 黑洞判定与自动断开跑在<b>前台 UWP 进程</b>里，而 UWP 进程会被生命周期
        ///      挂起/节流 —— 那一轮隧道 00:12 就死了，<c>health.txt</c> 却始终没写入本轮块，
        ///      <c>debug-out.txt</c> 也没人应答，两个进程都还活着（就是挂起态的样子）；
        ///   ② 前台发现死了以后是"从外面"拆：<c>Agent.DisconnectProfileAsync</c> 紧接着
        ///      <c>DeleteProfileAsync</c>，而平台当时仍记 <c>CONN=1</c>、RasClient 里没有任何
        ///      20226 终止事件 —— profile 是在会话还被平台认为活着的时候被删的，
        ///      OS 直接扯掉接口，<b>既不回调插件的 Disconnect，也不清它自己下发的 DNS 策略</b>，
        ///      于是留下根命名空间的 NRPT 规则 + 死接口上的幽灵路由 = 整机断网。
        ///
        /// 【触发之后走哪条路 —— 逐个核对过 winrt-26100 的 VpnChannel 方法表】
        ///   <c>VpnChannel</c> <b>没有</b> <c>Disconnect()</c>，插件不能"自己把会话断了"；
        ///   <c>Stop()</c> 的官方语义是"销毁通道、解除并关闭 outerTunnelTransport"，
        ///   而 sample 只在 <c>IVpnPlugIn.Disconnect</c> 里调它（先遣项目
        ///   VpnPlugInImpl.cpp:775 同），属于"回应平台的断开请求"，不是插件发起；
        ///   <c>TerminateConnection(String)</c> 文档写的是终止<b>建连尝试</b>
        ///   （"terminate the connection attempt performed inside IVpnPlugin.Connect"，
        ///    并且 "Should not be called on reconnect cases"），拿来拆已跑通的会话是超出文档用法。
        ///   ⇒ 看门狗最终走的还是<b>用户级</b>那条：平台级断开 + <b>等会话确认收干净</b>
        ///     才删 profile（实现见 <see cref="GracefulTeardown"/>），
        ///     前台和插件共用这一份，不再有两种拆法。
        ///
        /// 【判据】每 10 s 一轮，四条并列（完整推导见 <see cref="SessionWatchdog"/> 类注释）：
        ///   ① 探<b>隧道内</b> DNS 服务器的 443（narrow 下它们有 /32 进隧道、wide 下被 0/0
        ///      覆盖，所以量到的都是<b>隧道</b>；用 IP 字面量是为了不把 DNS 故障误读成断网），
        ///      两个都失败算一轮失败，<b>连续两轮</b>才断。
        ///      ★ <c>dnsInTunnel=0</c> 且 narrow 时这些 IP 走物理网卡，量不到隧道，
        ///        此判据<b>主动弃权</b>并写进心跳 —— 宁可明说看不见，也不假装看得见。
        ///   ② ★ 1.0.17 新增：让<b>系统解析器</b>查 www.google.com，答案必须是 fake-ip
        ///      （<c>198.18.x.x</c>）。①探的那些 IP 物理网卡也到得了，run 31 里宿主全死
        ///      而①一路绿灯；②是"只有隧道活着才可能成立"的整机口径判据。
        ///      <c>dnsInTunnel=0</c> 或 <c>selfSplitTunnel!=0</c> 时同样弃权（理由见类注释），
        ///      另有 30 s 宽限期不让平台装策略的时序误判。
        ///   ③ 非本次发起的 connect &gt;1 次 = 平台在自动重拨，立刻拆干净。
        ///   ④ 硬上限 1800 s 是无条件兜底。
        ///   另有一列内核控制器 <c>/version</c> 只进日志作归因，<b>不</b>参与判死。
        ///   关它（<c>sessionWatchdog=0</c>）只用于实验：让前台那套外部拆法再复现一次残留。
        /// </summary>
        internal static bool SessionWatchdog = true;

        internal static void Load()
        {
            // 先恢复默认，避免上一次连接的值残留到本次
            DnsIntoTunnel = true;
            ResidentBridge = true;
            HotReload = true;
            NodeExclusions = true;
            SelfSplitTunnel = 0;
            OuterTarget = "auto";
            OuterPort = "9";
            IncludeMode = "narrow";
            // ★ 默认档必须与字段初始值一致（run 35 实测成立的那一档），
            //   否则 settings.txt 缺失/解析失败时会掉回另一条数据面路径。
            EncapReturn = 2;
            ReceiveBatch = 0;
            // ★ 1.0.26（T2）→1.0.27 修订：两条取包入口的默认臂<b>不对称</b>，各自写清依据 ——
            //   · receive：切到具名入口 <c>GetVpnReceivePacketBuffer()</c>，因为先遣项目
            //     docs/76 的 <c>injectmode=1</c> 有 24 包全成功的实测；
            //   · send：<b>保持</b> run 35/37 量通过的 <c>RequestVpnPacketBuffer(Send)</c>，
            //     因为 <c>GetVpnSendPacketBuffer()</c> 在本仓库与先遣仓库<b>都零实测</b>
            //     （口径："没有实测证据前不改默认"，理由全文见 <see cref="SendGetBuffer"/>）。
            //   ★ 这两行必须与字段初始值逐字一致（:408 的约定）：settings.txt 缺失时
            //     掉回另一条取包路径，比换臂更难归因。
            InjectGetBuffer = true;
            SendGetBuffer = false;
            SessionWatchdog = true;
            FakeIp = true;
            FailFast = true;
            Source = "默认值";

            try
            {
                StorageFile file = ApplicationData.Current.LocalFolder
                    .GetFileAsync("settings.txt").AsTask().GetAwaiter().GetResult();
                string text = FileIO.ReadTextAsync(file).AsTask().GetAwaiter().GetResult();
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in text.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#"))
                        continue;
                    int idx = line.IndexOf('=');
                    if (idx <= 0)
                        continue;
                    map[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
                }

                DnsIntoTunnel = ReadBool(map, "dnsInTunnel", DnsIntoTunnel);
                ResidentBridge = ReadBool(map, "residentBridge", ResidentBridge);
                HotReload = ReadBool(map, "hotReload", HotReload);
                NodeExclusions = ReadBool(map, "nodeExclusions", NodeExclusions);
                SelfSplitTunnel = ReadInt(map, "selfSplitTunnel", SelfSplitTunnel);
                OuterTarget = ReadString(map, "outerTarget", OuterTarget);
                OuterPort = ReadString(map, "outerPort", OuterPort);
                IncludeMode = ReadString(map, "includeMode", IncludeMode);
                EncapReturn = ReadInt(map, "encapReturn", EncapReturn, 0, 5);
                ReceiveBatch = ReadInt(map, "receiveBatch", ReceiveBatch, 0, 64);
                InjectGetBuffer = ReadBool(map, "injectGetBuffer", InjectGetBuffer);
                SendGetBuffer = ReadBool(map, "sendGetBuffer", SendGetBuffer);
                SessionWatchdog = ReadBool(map, "sessionWatchdog", SessionWatchdog);
                FakeIp = ReadBool(map, "fakeIp", FakeIp);
                FailFast = ReadBool(map, "failFast", FailFast);
                Source = "settings.txt";
            }
            catch
            {
                // 没有文件/读不到 → 用默认值，这是正常路径不是错误
            }

            // ★ 这一行是发布版最值钱的日志：把<b>本次真正生效</b>的开关原样打出来。
            //   之前出过一次只有这里能抓到的事故 —— ReadInt 硬编码 0..2 上界，
            //   settings.txt 写 encapReturn=4 却被静默落回 2，"实验臂看着切换了、
            //   其实没切"。光看 settings.txt 永远发现不了，必须看读到什么。
            //   同时记 log=...：日志自己丢了多少行，决定这行以下还能不能信。
            RollingLog.Info("settings",
                $"src={Source} encapReturn={EncapReturn} receiveBatch={ReceiveBatch} " +
                $"injectGetBuffer={(InjectGetBuffer ? 1 : 0)} " +
                $"sendGetBuffer={(SendGetBuffer ? 1 : 0)} " +
                $"dnsInTunnel={(DnsIntoTunnel ? 1 : 0)} " +
                $"residentBridge={(ResidentBridge ? 1 : 0)} nodeExclusions={(NodeExclusions ? 1 : 0)} " +
                $"selfSplit={SelfSplitTunnel} outer={OuterTarget}:{OuterPort} includeMode={IncludeMode} " +
                $"fakeIp={(FakeIp ? 1 : 0)} failFast={(FailFast ? 1 : 0)} " +
                $"watchdog={(SessionWatchdog ? 1 : 0)} {RollingLog.Diag()}");
        }

        /// <summary>读字符串开关（空值一律回落 fallback —— 空 HostName 会让 ConnectAsync 抛）。</summary>
        private static string ReadString(Dictionary<string, string> map, string key, string fallback)
        {
            if (!map.TryGetValue(key, out string? v) || v.Length == 0)
                return fallback;
            return v;
        }

        /// <summary>读整数开关（越界/非数字一律回落 fallback）。</summary>
        /// <summary>读整数开关。<b>区间由调用方给</b>，越界/非数字回落 fallback。</summary>
        ///
        /// ★ 这里曾经是数据面实验的一个<b>真缺陷</b>（Docs/09 C5）：老实现硬编码
        ///   <c>parsed &gt;= 0 &amp;&amp; parsed &lt;= 2</c>，而 <c>encapReturn</c> 的档 3/4/5
        ///   都 &gt; 2 —— 于是 <c>settings.txt</c> 写 <c>encapReturn=3</c> 会被静默回落成 2，
        ///   实验臂<b>看着切换了、其实没切</b>。run 36 之所以能读到档 3 的数，是因为那时
        ///   上界还没生效/版本不同，事后没法保证每次单变量都真的单变量。
        ///   ⇒ 现在每个开关自带合法区间，非法值仍然回落（保护产品默认路径），
        ///     但<b>合法区间按开关自己的取值表给</b>。
        /// </summary>
        private static int ReadInt(Dictionary<string, string> map, string key, int fallback)
            => ReadInt(map, key, fallback, 0, 2);

        private static int ReadInt(Dictionary<string, string> map, string key, int fallback,
                                   int minInclusive, int maxInclusive)
        {
            if (!map.TryGetValue(key, out string? v))
                return fallback;
            return int.TryParse(v, out int parsed) &&
                   parsed >= minInclusive && parsed <= maxInclusive
                ? parsed
                : fallback;
        }

        private static bool ReadBool(Dictionary<string, string> map, string key, bool fallback)
        {
            if (!map.TryGetValue(key, out string? v))
                return fallback;
            if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("yes", StringComparison.OrdinalIgnoreCase))
                return true;
            if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("no", StringComparison.OrdinalIgnoreCase))
                return false;
            return fallback;
        }
    }
}
