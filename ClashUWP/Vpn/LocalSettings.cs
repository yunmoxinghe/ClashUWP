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
        /// 而不是当前这样只能靠黑洞探针与 rasdial 兜底断开。
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
        ///       ★★ 1.0.32~1.0.86 的产品默认，也是<b>至今唯一被实测证明能持续工作</b>的形态
        ///         （run 35 / 1.0.32）：
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
        ///       ★ 1.0.34~1.0.86 期间<b>未实测、不作默认</b>；<b>1.0.87 起按用户决定抬为默认</b>，
        ///         run 35 只证明了"每包 Flush"能持续工作，批边界会不会让平台少回收，
        ///         必须用 <c>ret/flush/left</c> 三列实测对出来 —— 判据与回退条件见本字段末尾。
        ///
        /// ★【1.0.87 · 默认档 2 → 5】用户 2026-10-21 决定抬档，理由是热路径开销：
        ///   档 2 每摘一个包就 <c>FlushVpnSendPacketBuffers()</c> 一次，等于每个出站包付一次
        ///   平台提交；档 5 是同一条链只在批末提交一次，<b>归还总量与外层重复量逐字不变</b>，
        ///   唯一变量就是 Flush 的调用次数 ⇒ 它是对"卡"的最小可解释改动，也是官方 sample 的形态。
        ///   ✅ <b>1.0.87 已实测成立（Docs/37 §5.3）</b>：长会话存活 213 s、encap=21985（= 7188 的 3.06 倍）
        ///   不停摆，全程 <c>ret==encap</c>、<c>left=0</c>、<c>retFail=0</c>，拆线后 nrpt=0 / 幽灵路由=0。
        ///   ★ 同轮测出的<b>否定结论</b>（Docs/37 §5.3.1）：平台一批基本只交 1~2 个包
        ///     （<c>encap/calls ≈ 1.75~2.2</c>，<c>flush</c> 与 <c>calls</c> 逐字相等），
        ///     所以抬档只省掉 43% 的 Flush 调用（21985 包 → 12606 次提交），<b>"卡"不是 Flush 次数造成的</b> ——
        ///     别再用这个开关治卡顿，下一轮要在"内核转发 / 回程泵 / 节点 RTT"之间定位。
        ///   装机判据（任一条不成立就写回 2 并记进 Docs）：
        ///     ① <c>ret</c> 与 <c>encap</c> 同步涨、<c>flush</c> 每批 +1（不再是每包 +1）；
        ///     ② <c>left</c>（在借未还）不单调上涨；
        ///     ③ <b>长跑必须越过 7188 不停摆</b>（档 1 的死法就是批边界让平台少回收）；
        ///     ④ 收尾 <c>nrpt=0</c> 与幽灵路由=0 与基线逐字相同。
        /// ★ 若走"摘出来"的档（0/1/2/5），归还必须对<b>每一个</b>摘出来的 buffer 执行，
        ///   包括我们刻意丢弃的组播/广播包（<c>IsNonUnicastDestination</c> 那条分支）——
        ///   旧代码在 <c>continue</c> 之前同样没归还，那才是最先开始借池子的地方。
        /// </summary>
        internal static int EncapReturn = 5;

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
        ///   ④ 无固定时长上限；健康会话只由上述故障判据触发收尾。
        ///   另有一列内核控制器 <c>/version</c> 只进日志作归因，<b>不</b>参与判死。
        ///   关它（<c>sessionWatchdog=0</c>）只用于实验：让前台那套外部拆法再复现一次残留。
        /// </summary>
        internal static bool SessionWatchdog = true;

        /// <summary>
        /// ★【1.0.66】是否在通知中心常驻一条"实时上下行"状态通知。默认<b>开</b>。
        ///
        /// 【对齐的是谁】安卓那份的 <c>dynamicNotification</c>
        /// （<c>.local/ref/ServiceStore.kt:64</c>：<c>boolean(..., defaultValue = true)</c>，
        /// 关掉就退回 <c>StaticNotificationModule</c> 那条"只说在跑、不带速率"的常驻通知）。
        /// 我们这一版没有做"静态那条"的降级形态，<c>0</c> 就是<b>完全不发通知</b> ——
        /// 因为 Windows 侧这条通知的一切代价与风险都来自它本身（划掉会不会补发、
        /// 插件进程发通知算不算越界），关掉就是关掉，不保留半条。
        ///
        /// 【为什么留这个开关（而不是写死）】
        ///   ① 发通知这件事发生在<b>vpnClient 后台任务进程</b>里，
        ///      "这个进程到底有没有包身份、通知会不会被设置压掉"是<b>未实测</b>的
        ///      （<see cref="StatusToast"/> 会把 <c>CreateToastNotifier()</c> 的结果与
        ///      <c>Setting</c> 都记进 <c>channel=toast</c> 日志）。真出问题时一行
        ///      <c>statusToast=0</c> 就能退回，不用重装。
        ///   ② 它是<b>每秒一次</b>的平台调用（文案变了才真调），与数据面无关，
        ///      属于"可以随时摘掉、不影响隧道"的那一类 —— 开关要留在能摘的位置。
        ///
        /// 【关掉之后】会话结束时的 <c>Stop()</c> 会试图撤掉已经挂着的那条（若有），
        /// 不留孤儿通知。
        /// </summary>
        internal static bool StatusToast = true;

        /// <summary>
        /// ★【1.0.35 · G5】会话边界清不清内核缓存、清到哪一层。默认 <b>0 = 不清</b>。
        ///
        /// 【这条对齐的是谁】安卓 <c>ClashRuntime.kt:31-32,50-53</c> 进出各一次
        ///   <c>Clash.reset()</c> + <c>clearOverride(Session)</c>，
        ///   <c>SuspendModule.kt:17,28,34</c> 还按亮灭屏 <c>suspendCore</c>。
        ///   我们因为桥常驻（Docs/05 §2④ 为绕开"同进程第二次 Start 回不来"刻意选的），
        ///   fake-ip 表 / DNS 缓存 / 在途连接登记表<b>跨会话全留着</b>（Docs/12 §2 G5）。
        ///
        /// 【三档，分界线画在"动不动 fake-ip 池"】
        ///   0 = 什么都不清 = 本批改动之前的行为，逐字不变。
        ///   1 = <c>POST /cache/dns/flush</c> + <c>DELETE /connections</c>。
        ///       只清解析缓存与在途连接登记表，<b>不动 fake-ip 池</b>。
        ///   2 = 在 1 的两步之外再加 <c>POST /cache/fakeip/flush</c>。
        ///       ★ 这一档有自己的代价，必须写明白：mihomo 的 fake-ip 池是<b>落盘的</b>
        ///         （<c>component/fakeip/cachefile.go</c>，就在 <c>cache.db</c> 里，
        ///         而 cache.db 在 home —— home 是 G10/<see cref="KernelHome"/> 迁过来的），
        ///         跨会话复用正是"同一个域名下次还拿到同一个假 IP"的来源；
        ///         <c>Pool.FlushFakeIP()</c>（<c>component/fakeip/pool.go:110-117</c>）
        ///         是<b>整池清空 + offset 归位</b>，可 Windows 侧 DNS 缓存里那些旧
        ///         <c>198.18.x.x</c> 不会跟着失效 —— 应用拿着一个"池里已无记录"的假 IP 再连，
        ///         内核就只能按 IP 匹配规则（域名规则全部失效），行为随之改变。
        ///         ⇒ 所以 2 号不是"更彻底的 1 号"，是<b>另一件事</b>，要单独判读。
        ///
        /// ★【为什么默认 0 —— 两个都没验的前提】
        ///   ① 控制器在<b>包内</b>是否真可达仍未确认（Docs/12 §8：宿主侧 netstat 看得见
        ///      <c>127.0.0.1:9090 LISTENING</c>，但包外 curl 超时 = AppContainer 回环隔离）。
        ///      热重载已经踩在同一个前提上，可它的成败只有返回码能说明；
        ///      本开关同样只能靠返回码判断 ⇒ 在它被读回来之前不当产品默认
        ///      （口径同 <see cref="ReceiveBatch"/> 的档 0：没有实测证据之前不改产品默认）。
        ///   ② 三个端点都<b>不幂等于"无副作用"</b>：<c>DELETE /connections</c>
        ///      （<c>hub/route/connections.go:81-87</c>）是遍历 <c>statistic.DefaultManager</c>
        ///      逐个 <c>c.Close()</c> —— 在会话<b>进行中</b>调它就是把用户正在下的东西掐了。
        ///      本实现只在会话<b>边界</b>调（连接建立后、会话收尾时），但"边界"仍是时序假设。
        ///   ⇒ 拨档后判据在 plugin.log 的 <c>channel=session-reset</c> 那一行：
        ///     每个端点都要回 <c>204</c>；出现 <c>connect=异常名</c> 就是前提 ① 不成立，
        ///     这条功能整体退回"桥常驻、跨会话不清"的现状（Docs/12 G5 仍在）。
        ///
        /// 【真挂起（suspendCore）这条仍然没做，写明白】mihomo 的挂起入口是 golang bind 的
        ///   <c>Clash.suspendCore(...)</c>，我们的桥 ABI 只有
        ///   start/stop/put_packet/get_packet/last_error/bound_iface（<see cref="MihomoBridge"/>），
        ///   REST 侧也没有对应端点 —— 要么改 Go 重编 <c>clash_bridge.dll</c>（本机无 Go 工具链，
        ///   装工具链属于改用户机器，Docs/12 §5 第 12 项明确"未获同意不动"），
        ///   要么用 <c>POST /restart</c>，而那个是<b>重新拉起进程</b>：在 in-proc 形态下
        ///   等于把承载隧道的进程重启（<c>hub/route/restart.go</c> 走的是
        ///   <c>os.Exit</c> + 重新 spawn），数据面与平台通道一起没。
        ///   ★ 所以 <c>POST /restart</c> 在本项目里是<b>地雷</b>，任何代码与 debug-cmd 都不许碰。
        ///   ⇒ 本批只做"重置"这一半，"挂起"保持缺口状态。
        /// </summary>
        internal static int SessionReset = 0;

        /// <summary>
        /// ★【1.0.38 · G6】跟随物理网络变化的档位。默认 <b>1 = 只观测</b>。
        ///
        /// 【对齐的是谁】安卓 <c>NetworkObserveModule.kt:63-69</c> 监听
        ///   <c>onLinkPropertiesChanged</c>，把新链路 DNS 推给内核（<c>:119-127</c>）、
        ///   退出时清空（<c>:147-152</c>）、按 wifi&gt;eth&gt;… 挑 DNS（<c>:100-117</c>）。
        ///   我们此前 <c>NetworkInformation</c> 出现 <b>0 次</b>（Docs/12 §2 G6）：
        ///   会话中途换网络，内核的出站网卡绑定（<c>dialer.DefaultInterface</c> =
        ///   桥侧 <c>rebindInterface()</c> 在 <c>Bridge.Start</c> 里存的那个<b>网卡名</b>）
        ///   一直是连接那一刻的旧值。
        ///
        /// 【三档】
        ///   0 = 完全不订阅 = 本批改动之前逐字一致。
        ///   1 = ★ 默认。订阅 <c>NetworkStatusChanged</c>（本机 winmd 里
        ///       <c>NetworkInformation</c> 唯一的事件，见 <see cref="NetworkFollow"/> 类注释），
        ///       比对"物理网络指纹"（适配器身份 + IANA 类型 + 本机 IPv4/IPv6 地址；
        ///       ★ 不含 DNS —— <c>NetworkAdapter</c> 没有枚举 DNS 的成员，如实写在这里），
        ///       变化就作废节点路由缓存（换网后上一个网络里解析出的节点 IP 不可信）并记
        ///       <c>channel=net-follow</c>；<b>不动桥、不发 HTTP</b>。
        ///   2 = 在 1 之上，指纹真变了就<b>请求桥重绑</b>：再调一次
        ///       <c>clash_bridge_start</c>，走 Go 侧"已启动"分支
        ///       （<c>core.go:132-137</c> 就是为这条路写了 <c>rebindInterface()</c>）。
        ///
        /// ★【为什么默认不是 2】两条前提都没读回来（细节见 <see cref="NetworkFollow"/> 类注释）：
        ///   ① 这个事件在 <b>vpnClient 后台任务进程</b>里收不收得到 —— Docs/12 §6 只证明了
        ///      winmd 里有（"文档级结论，未实测"）；
        ///   ② "同进程第二次 <c>clash_bridge_start</c>"在本项目有前科（Docs/05 §2④：
        ///      桥常驻就是为了绕开它）。
        ///   ⇒ 档 1 的"证据行"正是把①读回来的手段；②被验之前不抬默认
        ///     （口径同 <see cref="EncapReturn"/> 的档 5、<see cref="SessionReset"/> 的档 1/2）。
        /// </summary>
        internal static int NetFollow = 1;

        /// <summary>
        /// ★【1.0.39 · G7】IPv6 形态。默认 <b>0 = 逐字旧行为</b>（v6 完全不碰）。
        ///
        /// 【旧行为是什么】<c>assignedClientIPv6list</c> 传 <c>null</c>、只下发 v4 路由、
        ///   内核 <c>ipv6</c>/<c>dns.ipv6</c> 由订阅原文决定。后果（Docs/05 §5 自己列为未收口）：
        ///   <b>IPv6 泄漏</b> —— 隧道里没有 v6 路由，本机所有 v6 出站照物理网卡走，
        ///   代理与规则对它完全无感（先遣项目 docs/96 判据 13 实测的正是这一形态）。
        ///
        /// 【三档】
        ///   0 = 不动。
        ///   1 = <b>堵泄漏</b>：给隧道一个 ULA v6 地址 + 下发 <c>2000::/3</c> 的
        ///       <c>Ipv6InclusionRoutes</c>，内核 <c>dns.ipv6</c> 强制 false（不答 AAAA）。
        ///       ⇒ 应用真去连 v6 字面量时进隧道；AAAA 查询拿不到 v6，普通应用不会优先 v6。
        ///       两个取值都是先遣项目实测过的（docs/27：给了 <c>ipAddressv6</c> 才有
        ///       IPv6 协议实例与 link-local；docs/96：必须 <c>2000::/3</c> 而不是
        ///       <c>::/0</c> —— 后者会让 Windows 把 VPN 也变成 IPv4 默认路由，
        ///       4/4 臂一致复现）。
        ///   2 = <b>全链路</b>：1 之外再开内核 v6 —— 顶层 <c>ipv6: true</c>、
        ///       <c>tun.inet6-address</c>、<c>dns.ipv6: true</c>、
        ///       <c>dns.fake-ip-range6</c>（AAAA 回假 IP），并把假 IP v6 池本身
        ///       也 include 进隧道。
        ///
        /// ★【为什么默认 0，而且 2 也不给默认】
        ///   ① 这台机器<b>没有全局 IPv6</b>（memory「Local network baseline」），
        ///      两档都无法在本轮验证；而这一档<b>碰隧道本体</b>（改 Start 的地址与路由表），
        ///      代价直接落在"整机能不能上网"上。
        ///   ② 我们的桥与先遣项目不同：<c>sing-tun</c> 的 gvisor 栈注册了 IPv6，
        ///      所以 2 档<b>有得做</b>；但"内核有没有 v6 出口"取决于节点与订阅，
        ///      实测之前不承诺。
        ///   ⇒ 默认停在 0，把 1/2 做成可单变量切换的臂，判据写进 Docs/22。
        ///
        /// ★【1.0.119 · 两件事同时改了】
        ///   ① <b>档 1/2 现在自带 v6 节点的宿主路由排除</b>（<c>/128</c> 进
        ///      <c>Ipv6ExclusionRoutes</c>，判据见 <see cref="RouteInclusion"/>）。
        ///      以前只往 v4 桶下发排除，而档 1 的 <c>2000::/3</c> 恰好覆盖 v6 节点地址
        ///      ⇒ 订阅里有 v6 节点时，拨它的外层包会被自家隧道抓回去。
        ///      ⇒ 这条不是"新增功能"，是<b>把档 1/2 从"抬上去会踩新坑"改成"形状自洽"</b>；
        ///        两档仍未实机验证，默认仍是 0。
        ///   ② 档 0 的快照不再只写"未下发"：会同时报本机<b>有没有</b>全局 v6 地址，
        ///      有就明说"v6 出站绕过隧道"（<see cref="RouteInclusion.LeakNote"/>）。
        ///      读不到地址（AppContainer 里能不能枚举未实测）单独一档，不冒充"没有 v6"。
        /// </summary>
        internal static int Ipv6 = 0;

        /// <summary>
        /// 档 2 的 fake-ip v6 池（写进 <c>dns.fake-ip-range6</c>，同时作为 include 路由）。
        /// 默认 <c>fdfe:dcba:9876::/64</c>：与 mihomo 的 <c>tun.inet6-address</c>
        /// 默认段同前缀但取 /64（默认的 <c>::1/126</c> 只有 4 个地址，做池子太小）。
        /// 池子从 <c>::4</c> 开始分配（<c>component/fakeip/pool.go</c> 的
        /// <c>first = gateway.Next().Next().Next()</c>），<c>::2</c> 是 DNS 劫持地址，
        /// 因此 <c>::1/126</c> 的 inet6 前缀不会与分配出来的假地址冲突。
        ///
        /// ★ 只接受 ULA（<c>fc00::/7</c>）且前缀长 48~64：这不是洁癖 ——
        ///   它同时被当作<b>路由目标</b>下发，写成 <c>2000::/3</c> 就会把全局 v6 当假池、
        ///   写成 <c>::/0</c> 就会触发上面说的 IPv4 默认路由副作用。非法值回落默认。
        /// </summary>
        internal const string DefaultIpv6FakeRange = "fdfe:dcba:9876::/64";

        /// <summary>运行值（<c>settings.txt: ipv6FakeRange</c>，非法回落默认）。</summary>
        internal static string Ipv6FakeRange = DefaultIpv6FakeRange;

        /// <summary>
        /// <c>settings.txt: ipv6ClientAddress</c> 的临时覆盖；<b>空串 = 用 profile 里的
        /// <see cref="VpnPluginConfig.ClientIpV6"/></b>。
        /// 留这个口是为了不重建系统 profile 就能换隧道 v6 地址做单变量，
        /// 与 <c>ipv6FakeRange</c> 同一个理由；产品默认不写它。
        /// </summary>
        internal static string Ipv6ClientAddress = "";

        /// <summary>
        /// ★【1.0.40 · DNS 可配】是否读取并套用 <c>LocalState\dns-config.txt</c>。默认<b>开</b>。
        ///
        /// 【为什么留总闸】这份文件里任何一条非法值都会在<b>写文件前</b>被
        ///   <see cref="DnsConfig.Validate"/> 挡掉（逐条出处见该类注释），
        ///   但"改写生成出来的 YAML 让 mihomo 解析失败"这个风险类别本身仍然存在 ——
        ///   一旦 <c>start rc=-2</c>，<see cref="FailFast"/> 会放弃这次连接。
        ///   留一行 <c>dnsConfig=0</c> 就能整条摘掉，不动别的开关。
        /// 【与 <see cref="FakeIp"/> 的耦合】<c>fakeIp=0</c> 时整份改写器都不进，
        ///   DNS 覆盖自然也不进 —— 与 <see cref="AppSplit"/> 同一句话，写在这里是为了
        ///   让它不再成为"未知原因失效"。
        /// </summary>
        internal static bool DnsConfigEnabled = true;

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
            // ★ 默认档必须与字段初始值一致（1.0.87 起为档 5，见字段注释的抬档理由与判据），
            //   否则 settings.txt 缺失/解析失败时会掉回另一条数据面路径。
            EncapReturn = 5;
            ReceiveBatch = 0;
            // ★ 1.0.52：删掉 injectGetBuffer / sendGetBuffer 两个开关及其旧臂 ——
            //   旧臂吃的 VpnDataPathType 被官方标 "not supported and may be altered or
            //   unavailable in the future"，而 GetVpnReceive/GetVpnSendPacketBuffer()
            //   是同一个池的具名入口（RequestVpnPacketBuffer 的 see-also 主动指向它们）。
            //   现在两条取包路径都只有具名入口，没有可切的臂。
            SessionWatchdog = true;
            StatusToast = true;
            FakeIp = true;
            FailFast = true;
            // ★ 默认 0 = 不清（理由见字段注释：两个未验前提）。字段初始值与这里必须一致。
            SessionReset = 0;
            NetFollow = 1;
            Ipv6 = 0;
            Ipv6FakeRange = DefaultIpv6FakeRange;
            Ipv6ClientAddress = "";
            DnsConfigEnabled = true;
            Source = "默认值";

            try
            {
                StorageFile file = ApplicationData.Current.LocalFolder
                    .GetFileAsync("settings.txt").AsTask().GetAwaiter().GetResult();
                string text = FileIO.ReadTextAsync(file).AsTask().GetAwaiter().GetResult();
                // ★【1.0.93】行格式解析收成一份（<see cref="SettingsParse"/>）：
                //   以前前台 Backend\AppSettings 里另有一份逐字相同的循环，
                //   靠注释维系口径 ⇒ 注释不能编译，分叉不会报警。
                var map = SettingsParse.Parse(text);

                DnsIntoTunnel = SettingsParse.Bool(map, "dnsInTunnel", DnsIntoTunnel);
                ResidentBridge = SettingsParse.Bool(map, "residentBridge", ResidentBridge);
                HotReload = SettingsParse.Bool(map, "hotReload", HotReload);
                NodeExclusions = SettingsParse.Bool(map, "nodeExclusions", NodeExclusions);
                SelfSplitTunnel = SettingsParse.Int(map, "selfSplitTunnel", SelfSplitTunnel);
                OuterTarget = SettingsParse.String(map, "outerTarget", OuterTarget);
                OuterPort = SettingsParse.String(map, "outerPort", OuterPort);
                IncludeMode = SettingsParse.String(map, "includeMode", IncludeMode);
                EncapReturn = SettingsParse.Int(map, "encapReturn", EncapReturn, 0, 5);
                ReceiveBatch = SettingsParse.Int(map, "receiveBatch", ReceiveBatch, 0, 64);
                SessionWatchdog = SettingsParse.Bool(map, "sessionWatchdog", SessionWatchdog);
                StatusToast = SettingsParse.Bool(map, "statusToast", StatusToast);
                FakeIp = SettingsParse.Bool(map, "fakeIp", FakeIp);
                FailFast = SettingsParse.Bool(map, "failFast", FailFast);
                SessionReset = SettingsParse.Int(map, "sessionReset", SessionReset, 0, 2);
                NetFollow = SettingsParse.Int(map, "netFollow", NetFollow, 0, 2);
                Ipv6 = SettingsParse.Int(map, "ipv6", Ipv6, 0, 2);
                Ipv6FakeRange = SettingsParse.String(map, "ipv6FakeRange", Ipv6FakeRange);
                Ipv6ClientAddress = SettingsParse.String(map, "ipv6ClientAddress", Ipv6ClientAddress);
                DnsConfigEnabled = SettingsParse.Bool(map, "dnsConfig", DnsConfigEnabled);
                Source = "settings.txt";
            }
            catch
            {
                // 没有文件/读不到 → 用默认值，这是正常路径不是错误
            }

            // ★【1.0.39 · G7】这两个值一个直接进 YAML、一个同时进路由表，
            //   写错一档的代价是"配置解析失败 / 把全局 v6 当假池"，不是"这一项没生效"。
            //   非法值一律回落默认，并把原话留在日志里（口径同其他 settings 的形状闸门）。
            if (Ipv6ClientAddress.Length > 0 && !VpnPluginConfig.IsIpv6Literal(Ipv6ClientAddress))
            {
                RollingLog.Warn("settings",
                    $"ipv6ClientAddress 不是合法 IPv6 字面量（回落 profile 值）：{Ipv6ClientAddress}");
                Ipv6ClientAddress = "";
            }
            if (!TryNormalizeV6Prefix(Ipv6FakeRange, out string normalizedFake6))
            {
                RollingLog.Warn("settings",
                    $"ipv6FakeRange 不是 ULA 的 IPv6 前缀（回落 {DefaultIpv6FakeRange}）：{Ipv6FakeRange}");
                Ipv6FakeRange = DefaultIpv6FakeRange;
            }
            else
            {
                Ipv6FakeRange = normalizedFake6;
            }

            // ★ 这一行是发布版最值钱的日志：把<b>本次真正生效</b>的开关原样打出来。
            //   之前出过一次只有这里能抓到的事故 —— ReadInt 硬编码 0..2 上界，
            //   settings.txt 写 encapReturn=4 却被静默落回 2，"实验臂看着切换了、
            //   其实没切"。光看 settings.txt 永远发现不了，必须看读到什么。
            //   同时记 log=...：日志自己丢了多少行，决定这行以下还能不能信。
            RollingLog.Info("settings",
                $"src={Source} encapReturn={EncapReturn} receiveBatch={ReceiveBatch} " +
                $"dnsInTunnel={(DnsIntoTunnel ? 1 : 0)} " +
                $"residentBridge={(ResidentBridge ? 1 : 0)} nodeExclusions={(NodeExclusions ? 1 : 0)} " +
                $"selfSplit={SelfSplitTunnel} outer={OuterTarget}:{OuterPort} includeMode={IncludeMode} " +
                $"fakeIp={(FakeIp ? 1 : 0)} failFast={(FailFast ? 1 : 0)} " +
                $"watchdog={(SessionWatchdog ? 1 : 0)} statusToast={(StatusToast ? 1 : 0)} " +
                $"sessionReset={SessionReset} " +
                $"netFollow={NetFollow} ipv6={Ipv6} " +
                $"v6addr={(Ipv6ClientAddress.Length > 0 ? Ipv6ClientAddress : "(profile)")} " +
                $"v6pool={Ipv6FakeRange} dnsConfig={(DnsConfigEnabled ? 1 : 0)} " +
                $"{RollingLog.Diag()}");
        }

        /// <summary>
        /// ★【1.0.39 · G7】把 <c>ipv6FakeRange</c> 归一成"网络地址/前缀长"，同时做形状闸门：
        ///   · 必须是 IPv6 前缀、前缀长 48~64；
        ///   · 必须是 ULA（<c>fc00::/7</c>）。
        /// 后半条不是洁癖：这个值会被同时写进 <c>dns.fake-ip-range6</c> 和
        /// <c>Ipv6InclusionRoutes</c>，写成 <c>2000::/3</c> 就把全局 v6 当假池，
        /// 写成 <c>::/0</c> 会触发 Windows 的 IPv4 默认路由副作用（先遣项目 docs/96 §6.2）。
        /// 非法返回 false，调用方回落默认并把原话记日志。
        /// </summary>
        internal static bool TryNormalizeV6Prefix(string value, out string normalized)
        {
            normalized = "";
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string[] parts = value.Split('/');
            if (parts.Length != 2 || !VpnPluginConfig.IsIpv6Literal(parts[0]))
                return false;
            if (!int.TryParse(parts[1], out int prefixLen) || prefixLen < 48 || prefixLen > 64)
                return false;
            if (!System.Net.IPAddress.TryParse(parts[0], out System.Net.IPAddress? ip))
                return false;

            byte[] bytes = ip.GetAddressBytes();
            if (bytes.Length != 16)
                return false;

            int full = prefixLen / 8;
            int rem = prefixLen % 8;
            for (int i = full; i < bytes.Length; i++)
                bytes[i] = 0;
            if (rem > 0)
                bytes[full] &= (byte)(0xFF << (8 - rem));

            // fc00::/7：首字节高 7 位为 1111110。
            if ((bytes[0] & 0xFE) != 0xFC)
                return false;

            normalized = $"{new System.Net.IPAddress(bytes)}/{prefixLen}";
            return true;
        }

        // 【1.0.93】这里原有 ReadString / ReadInt / ReadBool 三个私有解析器，
        //   与前台 Backend\AppSettings 里那份逐字相同、靠注释维系一致。
        //   现在单一实现搬到 <see cref="SettingsParse"/>（含 Docs/09 C5 那条区间教训），
        //   本类只负责"同步等异步把文件读出来 + 把值落到自己的静态缓存"。
    }
}
