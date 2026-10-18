# 09 · VPN Plugin API 调用正确性逐条审计

> 对象：`ClashUWP.Vpn/ClashVpnPlugin.cs`（1694 行）、`VpnBackgroundTask.cs`、`SessionWatchdog.cs`、`GracefulTeardown.cs`、`VpnPluginConfig.cs`；profile 侧 `ClashUWP/Vpn/VpnProfileSetup.cs`、`ClashUWP/MainComponent.cs`、`ClashUWP/App.cs`
> 判据：Microsoft Learn `windows.networking.vpn` **winrt-26100** moniker 原文（2026-10-17 逐页抓取）+ 本地官方 sample `D:/fluentapps/repos/UwpVpnPluginSample` + 先遣项目 `clashUWP研究/docs/65`
> 版本：安装包 1.0.33，`EncapReturn` 默认 **2**（`LocalSettings.cs:245,304`）
> 性质：**纯静态审计**（读代码 + 读官方条文）。本轮没有发起任何连接，所有"实测成立/否证"一律引用 run 33/35/36/37 的既有日志。

---

## 0. 一句话结论

**五回调 + VpnChannel 全部 13 个调用点里，没有任何一处会让我改掉它的调用参数顺序或对象构造；真正的问题集中在三处语义层偏离（A1/A2/A3）与四处注释撒谎（C1–C4），外加 profile 侧两条绕过收尾闸的隐患（P1/P2）。**

计数：合规 10 项 · A 级偏离 3 项 · B 级风险 4 项 · 注释与代码不符 6 项 · 观测性缺口 4 项 · profile 侧隐患 3 项。
另有 **1 条要先遣项目的结论让路**：docs/65 把 `vpnInterfaceId=null` 判成 A5 硬违规，核对官方定义后**这条不成立**（见 B4）。

---

## 1. 判据原文（逐页抓取，winrt-26100）

| API | 条文里对本项目有约束力的那句（原文） |
|---|---|
| `IVpnPlugIn.Connect` | "instructs the VPN plug-in to connect to the VPN server and establish the VPN channel." —— **该页没有 Remarks**，即平台对 Connect 内部只有一条总述，细节全在被调 API 自己的条文里 |
| `IVpnPlugIn.Encapsulate` | "It is required that any **IVpnPacketBuffer** object, either passed to the method in the *packets* parameter or requested by the plugin, **be returned to the VPN framework**. The VPN framework will then send the **IVpnPacketBuffer** objects in *encapsulatedPackets* via one of the transports specified during Connect and **will clean up the remaining IVpnPacketBuffer objects in *packets***." |
| `IVpnPlugIn.Decapsulate` | "It is required that any **IVpnPacketBuffer** objects requested by the plugin be returned to the VPN framework **via either the *decapsulatedPackets* chain** … **or the *controlPacketsToSend* chain**." + "The VPN framework will send all the VpnPacketBufferList chained on *decapsulatedPackets* as unique IP packets to the VPN interface." |
| `IVpnPlugIn.GetKeepAlivePayload` | "This method is **only called at a time determined by the VPN infrastructure** … If an error occurs the VPN infrastructure will **skip sending** the keep alive payload." |
| `IVpnPlugIn.Disconnect` | "instructs the VPN plug-in to disconnect from the VPN server and destroy the VPN channel." |
| `VpnChannel.AssociateTransport` | "**This call must be performed before any other operations related to the VPN framework APIs.**" + "The socket **must have not been connected** at the time of this call, otherwise the call will fail with an invalid parameter error." |
| `VpnChannel.StartWithTrafficFilter`（我们绑的 11 参重载） | `mainOuterTunnelTransport`："**The socket must be unconnected at the point of the call.**" · `mtuSize`："**should be configured to be at most 1400**" · `maxFrameSize`："… **should be <=1500**" · 第 8 参名字就是 **`reserved`**，说明只有 "**Reserved.**" · `vpnInterfaceId`："A **VpnInterfaceId** object." |
| `VpnChannel.Start`（V1，已退役） | "On success **the ownership of outertTunnelTransport is transferred** by the VPN framework. Calling this method multiple times with the same parameters will have no effect." · "There can only be **one VPN channel per process** … The ***mtuSize* must always be smaller than the *maxFrameSize*.**" —— 注意 V1 的同位参数叫 `optimizeForLowCostNetwork` 且有真实语义 |
| `VpnChannel.RequestVpnPacketBuffer` | "Any **IVpnPacketBuffer** object requested by the plugin **must eventually be returned to the VPN platform** (i.e. via *IVpnPlugIn.Encapsulate* or *IVpnPlugin.Decapsulate*). Otherwise the plugin **may be unable to request new buffers** until the outstanding buffers have been returned." |
| `VpnChannel.GetVpnReceivePacketBuffer` | "Requests a VpnPacketBuffer object from the receive packet buffer pool to be used in the decapsulation **or injection** of a received packet from the VPN server into the local network stack" —— **injection 是官方词**（A2 的依据） |
| `VpnChannel.AppendVpnSendPacketBuffer` / `FlushVpnSendPacketBuffers` | 形参名就叫 `encapsulatedPacketBuffer`；Flush = "Flushes any appended send packet buffers."（Append 的 See also 直接指向 Flush —— 两者成对） |
| `VpnChannel.Stop` | "Destroys a previously established VPN channel object. Disassociates and closes the *outerTunnelTransport* connection to the VPN server." |
| `VpnChannel.TerminateConnection` | "Allows a VPN plugin to indicate an error message and **terminate the connection attempt** performed inside the *IVpnPlugin.Connect* method. **Should not be called on reconnect cases.**" |
| `VpnChannel.ProcessEventAsync` | 签名 **`public static void ProcessEventAsync(object, object)`** —— 名字带 Async 但返回 void，不 await 不构成违规（与 docs/65 的修正记录一致） |
| `VpnPacketBufferList` | 文档面：属性只有 `Size`/`Status`，方法只有 `Append`/`AddAtBegin`/`RemoveAtBegin`/`RemoveAtEnd`/`Clear`/`First`；它是 `IIterable<VpnPacketBuffer>`，**没有 GetAt、没有索引器** |
| `VpnInterfaceId` | "Represents an interface ID used in **the construction and assignment of an IPv6 address to the VPN interface**. The ID contained in this class **fills the interface ID portion of the IPv6 address**." 构造器 `VpnInterfaceId(Byte[])` |

---

## 2. 逐调用点判定（插件进程内）

| # | API 调用点 | 位置 | 判定 |
|---|---|---|---|
| 1 | `IBackgroundTask` 取 deferral → 复用插件实例 → `VpnChannel.ProcessEventAsync` → `finally Complete()` | `VpnBackgroundTask.cs:48/60/83/100` | ✅ 与官方 sample 逐字同构；`ProcessEventAsync` 返回 void，不 await 合规 |
| 2 | `channel.LogDiagnosticMessage` ×25 | 全文件 | ✅ 插件唯一官方日志出口；从泵线程/看门狗线程调用合法（`VpnChannel` 标 Agile+MTA） |
| 3 | `channel.Id` / `channel.Configuration.CustomField` | `:275/285/295` | ⚠️ B3：严格读 AssociateTransport 的 "before any other operations" 时，这两个读操作排在 AssociateTransport（:335）之前。sample 同序、实测无 `E_INVALIDARG` ⇒ 判字面严读风险，不判违规 |
| 4 | `channel.AssociateTransport(new DatagramSocket(), null)` | `:334-335` | ✅ socket 全新未连接，命中官方 "must have not been connected"，比 sample 更明确 |
| 5 | `_transport.ConnectAsync(...).GetAwaiter().GetResult()` | `:348` | ⚠️ B2：Connect 内同步等待；且使随后传入 Start 的 socket 处于**已连接**态 → A 级偏离 A1′（见 §3.1） |
| 6 | `BuildRoutes()` → `VpnRouteAssignment.Ipv4InclusionRoutes/Ipv4ExclusionRoutes.Add(new VpnRoute(host, prefix))` | `:366/1493-1547` | ✅ 与文档语义一致（"routes which should be associated / not associated with the VPN interface"）；节点 `/32` 排除对齐安卓 `protect()` |
| 7 | `BuildDomainNames()` → `VpnDomainNameInfo(".", Suffix, dnsServers, null)` + `VpnDomainNameAssignment` | `:367/1652-1670` | ✅ 走的是 V2 的 domain-name 一路；V1 的 `VpnNamespaceAssignment/VpnNamespaceInfo`（官方标 "Not supported"）已彻底退役 |
| 8 | `channel.StartWithTrafficFilter(ipv4, null, **null**, routes, domains ?? new(), 1400, 1500, **false**, transport, null, selfFilter)` | `:444-455` | ✅ 六条约束里四条硬命中：mtu≤1400、maxFrame≤1500、mtu<maxFrame（`VpnPluginConfig.cs` 显式 `Mtu = MaxFrame - 1` 兜住）、`reserved` 位传 false（sample 传 true = docs/65 B2）。⚠️ 第 3 参 null 合规（见 B4）。⚠️ 第 9 参已连接 socket = §3.1 |
| 9 | `Encapsulate`：`while (packets.Size>0) RemoveAtBegin()` → `ToArray()` → 计数 → `TryReturnSendBuffer` → 逐包处置 | `:638-659` | ✅ 回调内同步处理、异常只计数不外抛、拷贝后才动 buffer。⚠️ 出参 `encapulatedPackets` 全程为空 = A1（§3.1）；每包一次 Flush = B1 |
| 10 | `AppendVpnSendPacketBuffer` + `FlushVpnSendPacketBuffers` | `:793/798` | ⚠️ A1 的执行体。run 33 实测：**只 Append 不 Flush ⇒ 永久停在 7188 包**，Flush 才是归还动作 |
| 11 | `Decapsulate`（空实现） | `:829-835` | ✅ 数据面不经外层 transport，平台无包可交；⚠️ O1：没有调用计数，"从未被回调"目前只是推断 |
| 12 | `GetKeepAlivePayload`：`RequestVpnPacketBuffer(Send)` → `Length=1` → `AsStream` 写 `3` → out 交回；全 try/catch，失败回 null | `:842-890` | ✅ 与 sample `VpnPlugin.cs:879-886` 同构，且比 sample 多了异常兜底。⚠️ O4：多臂实测 `ka=0`，平台从未调用 ⇒ 这条路**写了但没被检验** |
| 13 | `Disconnect`：停泵/停看门狗 → `channel.Stop()` → 清引用 → 异步落快照 | `:923-1010` | ✅ `Stop()` 只在平台请求断开时调，符合其"回应断开"的语义；不 Dispose 已转移所有权的 socket（sample 的 B5 没抄） |
| 14 | 回程泵 `Inject`：`RequestVpnPacketBuffer(Receive)` → 设 `Length` → 写 → `AppendVpnReceivePacketBuffer` + `FlushVpnReceivePacketBuffers`；超长支 `Length=0` 仍归还 | `:1377-1421` | ✅ 借到的每条 receive buffer 都被归还（含之前会漏的超长支）。⚠️ 脱离 `Decapsulate` 时序注入 = A2（§3.2）；每包一次 Flush = B1 |
| 15 | `StartMihomoBridge` 的 `FailFast` → `throw InvalidOperationException` | `:1145` 附近，被 Connect 的 catch（`:516-526`）rethrow | ❌ A3（§3.3）：Start 已成功之后往外抛异常，绕开了官方给的唯一失败出口 |

---

## 3. A 级：三条语义偏离

### 3.1 A1 —— 出站包从未进 `encapulatedPackets`，且顺带把每份出站流量打到外层地址

官方对 `Encapsulate` 的契约是"把封装结果放进 *encapsulatedPackets*，剩下留在 *packets* 里的平台自己回收"。档 2（=产品默认）的实际形态是：**把 `packets` 全摘空 → 裸 L3 包喂进程内 mihomo → 原 buffer 经 `AppendVpnSendPacketBuffer` + `FlushVpnSendPacketBuffers` 交回**。这正是 docs/65 判官方 sample 的 A1 同一类（"既不填输出链，也不让平台回收 packets"）。

两条后果，第二条是这轮审计新落到纸面的：

1. **buffer 生命周期脱离回调上下文**。这条已经被 run 33 用数字钉死：`Encapsulated=7188 / SendBuffersReturned=7188 / SendReturnFailures=0 / SendBuffersFlushed=0` 却照旧停在 7188 ⇒ `Append` 只是入队，`Flush` 才真正回池。所以"脱离契约"不是纯洁癖问题，它就是个会黑的坑，而我们的解法（每包 Flush）是**在偏离契约的路线上找补**，不是回到契约。
2. **`FlushVpnSendPacketBuffers` 的语义是"把已 Append 的 send buffer 提交并发往 transport"**（官方 Append 的 See also 就是它；形参名 `encapsulatedPacketBuffer`）。于是每一个出站包都额外被 UDP 发往外层目标。外层目标现在刻意是 TEST-NET 黑洞 `192.0.2.1`（run 34 之前的活服务器会回 ICMP、直接触发 828 拆通道，这条已经实测过），路由器丢包 ⇒ 表现是**本机把全部出站负载（含明文非 TLS 部分）重复广播到局域网一次，并占用等量上行**。这条与"能不能连通"无关，所以之前七轮验收都没把它列进判据。

为什么不立刻改掉：档 2 是**目前唯一实测跑通数据面**的形态（run 35 到 18202 包 / 38 轮 204 无中断，run 37 头条判据 PASS）。两条更干净的路线都还没实测：

* **档 4（新增，成本最低，先做这个）**：`RemoveAtBegin()` → 就地处理 → `packets.Append(buffer)` 塞回原列表，让平台按 remarks "will clean up the remaining IVpnPacketBuffer objects in *packets*" 自己回收 —— 一次 Flush 都不调，也就不往 transport 发任何东西。如果它同样能跑长跑，A1 的两条后果一起消失。注意 `packets` 是 `IIterable` 投影、没有索引器，但 `Append`/`AddAtBegin` 在文档面存在，能不能在这个投影上调通**必须实测**（档 3 就是死在投影的枚举器上：`InvalidCastException`）。
* **真封装进 `encapulatedPackets`**：需要一个真会应答的外层服务器，与"外层必须静默黑洞"的实测结论正面冲突 ⇒ 短期不做。

### 3.2 A2 —— 回程注入不经 `Decapsulate`

官方 `RequestVpnPacketBuffer` 的归还路径写死两条链（"via *IVpnPlugIn.Encapsulate* or *IVpnPlugin.Decapsulate*"），而我们的注入发生在**回程泵线程**上。方向本身官方是认的：`GetVpnReceivePacketBuffer` 的 summary 明写 "to be used in the decapsulation **or injection** of a received packet … into the local network stack" —— "injection" 是官方词，`AppendVpnReceivePacketBuffer`+`FlushVpnReceivePacketBuffers` 也是官方给的注入出口。

要收口的只有两点：**(a)** 取包改用 `GetVpnReceivePacketBuffer()`（它就是为 injection 设计的，语义上不再和"必须经 Decapsulate 归还"那句打架）；**(b)** 泵里改成批量 —— 攒 N 包一次 Append + 一次 Flush，替掉现在的每包一次 Flush（B1）。这两条都不改数据面拓扑，属于低风险改动，但 (a) 换 API 需要重装后跑一轮长跑确认注入成功率不掉。

### 3.3 A3 —— 建连失败从 `Connect` 抛异常，而不是用 `TerminateConnection`

`StartMihomoBridge`（`:508`）排在 `StartWithTrafficFilter`（`:444`）**之后**。桥启动失败且 `FailFast=true`（默认，`LocalSettings.cs:144/307`）时，我们从 `Connect` 往外抛 `InvalidOperationException`。此时**平台的 L3 接口、NRPT、路由都已经装上了**。

官方对"插件自认建连失败"给的出口只有一个：`TerminateConnection(message)` —— "indicate an error message and **terminate the connection attempt** performed inside the *IVpnPlugin.Connect* method. **Should not be called on reconnect cases.**" 抛异常会发生什么，**文档没有任何承诺**。而"平台拆不干净"这个病灶我们有血的教训：2026-10-16 00:21 那次就是拆在不该拆的时候，留下根 Namespace 的孤儿 NRPT + 死接口幽灵路由 ⇒ 整机断网，用户手动重置网络才恢复。所以这条必须按 A 级对待。

当初为什么没用 `TerminateConnection`：它带**重连场景禁用**的前置条件，而我们没有可靠的重连判据 —— `channel.Id` 每次都是新的，`s_connectCalls` 只是进程级次数，RasMan 自动重拨与用户手动连接在插件侧看不出区别。改法（建议，需实测）：只在"本进程首次 Connect"（`s_connectCalls==1`）且桥启动失败时 `TerminateConnection`，其余场景一律走已经验证过的 `GracefulTeardown`（用户级 `DisconnectProfileAsync` + 等终态），并把 `FailFast` 的"抛"降级为"不抛、只标记本轮不健康"。

---

## 4. B 级：四条风险

**B1 每包一次 Flush**（`:798` 发送侧、`:1401/1413` 接收侧）。docs/65 判 sample 的 B4 同一形态（人家是 `ShouldFlushPacketBuffer()` 恒 true）。合法，但系统调用次数 = 包数，吞吐天花板在这里。批量化（一次回调内 Append 多条、末尾 Flush 一次）是纯收益，且和 A1 的档 4 实验可以同臂做。

**B2 `Connect` 内同步阻塞累计可达数秒**。链上三段：`NodeRoutes.BuildExclusionCidrs`（`:329`，含域名解析）、`transport.ConnectAsync(...).GetResult()`（`:348`）、`MihomoBridge.Start`（`:508`，注释自陈"桥启动阻塞数秒"）。`IVpnPlugIn.Connect` 返回 void、平台同步等它，官方**没写超时值** ⇒ 不能判违规，但它是"连接尝试卡住/被判失败"的直接风险位，与 #20（隧道 ~70 s 单次 828 + 平台自动重拨）的归因相关。改法：把非必需项挪出关键路径或各自加硬超时。

**B3 AssociateTransport 之前先读了 `Id` 与 `Configuration`**（`:275/285/295` vs `:335`）。官方 "This call must be performed before any other operations related to the VPN framework APIs." 的**严读**会把读属性算成 operation。sample 同序，且我们实测 Start 从未因顺序报 `E_INVALIDARG` ⇒ 判字面风险。最低成本处置：把这两次读挪到 `AssociateTransport` 之后，不改任何逻辑。

**B4 `vpnInterfaceId` 传 null —— 合规，且要更正先遣项目的判词。** docs/65 把这条列为 A5，理由写的是"放弃接口标识 = 放弃在 WFP / 接口层精确定位本插件接口的能力"。对照官方定义：`VpnInterfaceId` "Represents an interface ID used in **the construction and assignment of an IPv6 address to the VPN interface**. The ID contained in this class fills the interface ID portion of the IPv6 address." —— 它是**IPv6 接口标识符（EUI-64 那半截）**，不是 WFP/接口归因句柄。我们 `assignedClientIpv6List` 传 null、本机也无 IPv6 出口（记忆里已记的基线），构造这个对象没有对象可填 ⇒ **null 是正确用法，docs/65 的 A5 不成立**（它的"sample 自己也没传"这个观察是对的，推断的后果是错的）。

---

## 5. C 级：六处注释与代码不符（会毒害下一个读代码的人）

| # | 位置 | 注释在说什么 | 代码实际是什么 | 处置 |
|---|---|---|---|---|
| C1 | `:773`、`:779-780` | "⇒ 默认档改成 3 = 压根不摘列表"、"档 2 … 所以它只作判别臂，**不作产品形态**" | `LocalSettings.EncapReturn` 默认 **2**（`:245/304`），run 35/37 已把档 2 定为产品形态；档 3 死于投影层 `InvalidCastException` | 改字；并把"档 3 作废、档 4 待测"写进同一段 |
| C2 | `:452`、`:393` | 第 8 参注释 `optimizeForLowCostNetwork`，并抄了它的语义描述 | 我们绑的是 V2 重载，该位在官方文档里就叫 `reserved`/"Reserved." | 改成 `reserved`，并注明 `optimizeForLowCostNetwork` 是 V1 同位参数 |
| C3 | `:449` | "namespaceScope：**null 对象**=不发 NRPT" | 传的是 `domains ?? new VpnDomainNameAssignment()` —— **一个空 assignment 对象，不是 null** | 要么改注释，要么真传 null；两者语义差别在 trafficFilter 上已被实测过（空对象=默认 deny），**这一条属未验证项** |
| C4 | `:526` | catch 统一记 `"VpnChannel.Start 异常"` | try 块含 `StartWithTrafficFilter`、`StartMihomoBridge`、`SessionWatchdog.Start` 三处，桥的 FailFast 会以"Start 异常"面目出现 | 分段 try 或在消息里带真实阶段 |
| C5 | `:1366-1370` | 称 `GetVpnReceivePacketBuffer` 官方"明确支持 injection" | 已核对原文，成立 | 保留，补一句"本项目尚未使用" |
| C6 | `:1373-1375` | 称旧实现超长支"直接 return、buffer 丢失" | 现已归还（`:1395-1403`） | 补"该分支实测从未触发（`big=0`）"，别让人以为它被验证过 |

---

## 6. 观测性缺口（不是违规，但决定下一次归因能不能落地）

**O1** `Decapsulate` 无调用计数 —— "平台从未回调它"目前是**推断**（依据：外层无真服务器）。加 `decapCalls`，一行。
**O2** receive 侧没有"在借未还"计数。send 侧刚从 7188 学到 `ret` 必须与 `encap` 同步这条判据，回程是同构问题却只记了 `injected`/`injectFailures`，没有 `requested - returned`。撞上一次就是又一轮长跑。
**O3** `_encapsulatedPackets++` 等 `uint` 自增非原子，而同一批字段被泵线程与看门狗读；文件里别处已用 `Interlocked`，口径不统一。
**O4** `ka=0` 跨臂复现 ⇒ `GetKeepAlivePayload` 从未被平台调用。**这条可以直接从 #20 的嫌疑名单上划掉"平台周期性 keep-alive 失败"这一项**。

---

## 7. profile 侧（`VpnManagementAgent`，前台进程）—— 两条真隐患

先记合规的：`VpnPluginPackageFamilyName` 必填、连接对象必须来自 `GetProfilesAsync` 新鲜回读（自建对象会 `CannotFindProfile`）、`ServerUris` 不写端口 0、`FindProfileAsync` 同时按 **名字 + 包 family name** 匹配且拿不到 PFN 就一个都不认、`AlwaysOn=false` —— 这几条都对，且都是拿事故换来的（`:58-77`、`VpnProfileSetup.cs:114-152`、`GracefulTeardown.cs:FindProfileAsync`）。

**P1 ★★ 数据面探测在 App 启动时自动拨一次 VPN，且收尾绕过了闸。**
`MainComponent.cs:47` 的 `_ = RunProbeOnceAsync().ContinueWith(_ => RunDatafaceProbeOnceAsync())`，而 `RunDatafaceProbeOnceAsync`（`:183-357`）会建 `ClashUWP-Dataface` → `ConnectProfileAsync` → 打流量 → `DisconnectProfileAsync`（`:325`）→ **固定 `Task.Delay(2000)`** → `DeleteProfileAsync`（`:342`）。闸门（`GracefulTeardown.EnsureTornDownAsync` = 轮询到连续两轮非 `Connected` 才允许删）**没有参与这条路径**，用的正是 00:21 整机断网那个动作形状："Disconnect 返回 Ok" ≠ "平台收干净了"。它只在 `dataface-v4.txt` 不存在时跑（`:190-191`），但那是**装包后第一次启动就必然满足**的条件。
两个独立问题要一起收：① 收尾换成 `GracefulTeardown.DisconnectThenDeleteAsync` 的按名版本（`EnsureSessionTornDownAsync("ClashUWP-Dataface")` 已有，缺"等完再删 + 残留自检"）；② "开机自动连接"应当受开关门控 —— 它与本项目一直执行的"实测不得占整机网络 / 收工前确认没有自动重拨"口径正面冲突。

**P2 自测循环里还留着第二种拆法。** `VpnProfileSetup.cs:376-381`：`DisconnectProfileAsync(toClean)` 紧接 `DeleteProfileAsync(toClean)`，中间不等；而同文件 `DeleteByNameAsync`（`:405-411`）已经上了闸。**同一份代码里两种拆法并存**，就意味着哪条路被走到全看按钮 —— 这正是上次事故的形状。收敛到 `GracefulTeardown` 一处。

**P3 `App.Main` 里的启动探针在主线程同步等 WinRT 异步。** `App.cs:56` `agent.GetProfilesAsync().AsTask().GetAwaiter().GetResult()`，`:96-99` 同样形态等 `CreateFileAsync`/`WriteTextAsync`。STA 线程阻塞等待需要回到本上下文的续接是标准死锁形状；现在能稳定出 `probe.txt` 只能说明这条路径恰好没踩到，不能当验证结论。改成不阻塞（把探针丢到线程池或直接异步化）。

> **【1.0.35 追记】** P1/P3 的位置已变：前台按 `Frontend/` + `Backend/` 分层重排，
> 上述行号对应的代码现在在 `ClashUWP/Backend/StartupProbes.cs`、`StartupProbe.cs`、
> `Diagnostics.cs`（P3 已修：探针丢线程池，不再阻塞 STA）。映射表与等价性清单见
> [`Docs/10-前端抽离与前后端分层.md`](10-前端抽离与前后端分层.md)。

---

## 8. 修复清单（按"代价 / 是否需要实测"排）

| 项 | 动作 | 代价 | 是否要重装实测 |
|---|---|---|---|
| C1–C4、O1、O2、O3 | 改注释、补计数器、计数改 `Interlocked` | 一次编译 | 要（否则快照里看不到新列） |
| P2、P3 | 自测路径与探针路径上闸 / 去阻塞 | 小 | 不碰隧道，纯前台 |
| P1 | 探测收尾走 `GracefulTeardown`，并给"启动自动探测"加门控 | 中 | **要**，且属会碰隧道的动作 ⇒ 需明确授权 |
| B1、B3 | Flush 批量化；AssociateTransport 前置读挪后 | 中 | 要长跑复验（判据同 run 35/37） |
| **A1 档 4** | `RemoveAtBegin` + 处理 + `packets.Append` 塞回，不调任何 Flush | **单变量一臂** | **要** —— 先验证投影能不能 `Append` |
| A2 | 取包换 `GetVpnReceivePacketBuffer()` + 批量 Flush | 中 | 要 |
| A3 | 首次连接失败走 `TerminateConnection`，其余走 `GracefulTeardown`；FailFast 不再抛 | 中 | 要 |

**执行顺序建议**：先做 C+O（不改变行为，只为下一轮取证把口径钉准），再做 P1/P2/P3（这三条是**用户可用性问题**，比数据面纯洁癖更值钱），然后单变量做 A1 档 4 一臂（它同时消掉"偏离契约"和"重复发包上局域网"两条），A2/A3 押后。

---

## 9. 本轮审计未覆盖 / 无法验证

* 全部结论来自静态阅读 + 官方条文，**本轮没有发起任何连接**，因此不对"改完还能不能连通"下任何结论。
* C3（空 assignment vs null）、A1 档 4 的投影可用性、`TerminateConnection` 在我们这个会话形态下的实际清理效果 —— 三条都是**未验证**，文档里已标出，不允许当成已知。
* `VpnPacketBufferList.Status` 出现在文档面属性表里，但本项目 TFM 10.0.26100 的 CsWinRT 投影清单（`.local/ifc35.txt`）没有它，也没有 `GetAt`；档 3 死在枚举器 `InvalidCastException` 上。⇒ 文档面与投影面存在这一处已知差异，任何"照文档写"的改动都必须先过编译与实测。

---

## 10. 1.0.34 / 1.0.22 已实施（对齐官方 + 后端健壮化）

> 版本：App `1.0.34.0`（`Package.appxmanifest`）、组件包 `ClashUWP.Vpn 1.0.22`、`PackageReference` 已指向 1.0.22。
> 编译：插件 0 警告 0 错误；App 仅剩一处既有 CS8600（`MainComponent.cs:818`）。
> ★ **本轮没有发起任何连接**。下面每一条都只是"代码与官方条文对齐 + 可编译 + 默认行为不变"，连通性一律待实测。

### 10.1 总原则：默认不改

审计里所有"官方更正确"的形态，全部做成 `LocalSettings` 里的**可切换开关**，产品默认仍是 run 35/37 实测成立的那套：

| 开关 | 默认 | 含义 |
|---|---|---|
| `encapReturn` | **2**（Append + 每包 Flush） | 取值扩到 0–5；3 已死（投影 `InvalidCastException`），4 = 摘出→处理→`packets.Append` 塞回零 Flush，5 = 逐包 Append + 每批一次 Flush |
| `receiveBatch` | **0**（每注一个包就 Flush） | N>0 = 回程攒批，最多 N 包一次 `FlushVpnReceivePacketBuffers` |
| `injectGetBuffer` | **false**（`RequestVpnPacketBuffer(Receive)`） | true = 官方在 injection 条文里点名的 `GetVpnReceivePacketBuffer()` |

顺带修掉一个会**静默作废全部未来单变量实验**的度量缺陷：`ReadInt` 里硬编码 `0..2` 上界，导致 `encapReturn=3/4/5` 落回 2 —— "实验臂看着切了，其实没切"。现在 `ReadInt` 有显式区间重载（`LocalSettings.cs:421,424`），三个开关各自带界校验（`:388-390`）。

### 10.2 逐条对账

| 审计项 | 改了什么 | 位置 |
|---|---|---|
| **A1**（借而不还） | 档 4/5 落地为可选；归还动作独立成 `TryReturnSendBuffer`，**先还 buffer 再处理包**，摘出与归还不再隔一个可能抛异常的处理段 | `ClashVpnPlugin.cs:837-862, 1009-1070` |
| **A2**（injection 取包口径） | `GetVpnReceivePacketBuffer()` 路径实现并做成 `injectGetBuffer` 开关；`Inject` 拆成 `InjectAppend` + `FlushReceive` | `:1792-1830` |
| **A3**（建连失败无官方出口） | 新增 `AbortConnectionAttempt` → `channel.TerminateConnection(reason)`；Start **之前**的三类失败（AssociateTransport / 读 CustomField / 其它）全部走它；Start **之后**（桥 FailFast）刻意不调 —— 官方条文限定"不应在重连场景调用" | `:328, 372, 664, 687` |
| **B1**（每包一次 Flush） | 档 5 与 `receiveBatch` 两条批量化路径，且泵在**失败、空闲、异常、退出**四个口都保证把已借的 receive buffer 冲出去，绝不带着未提交 buffer 睡眠或返回 | `:1663-1722` |
| **B3**（Start 前做平台读取） | `AssociateTransport` 前置为**第一个**平台 API 调用（原来在第 4 步，前面还夹着 `LogDiagnosticMessage` / `channel.Id` / `channel.Configuration`）；顺序仍是 AssociateTransport → ConnectAsync → Start，与官方 sample 一致 | `:301-328, 380-382` |
| **C1–C4**（注释撒谎） | 相关注释逐条改回官方原文口径，`EncapReturn` 的文档头从"默认 1 = Append 不 Flush"纠正为"默认 2 = Append + 每包 Flush（run 35/37 实测成立）" | `LocalSettings.cs:245-300` |
| **O1**（`Decapsulate` 无计数） | 不再空体：`_decapsulateCalls` + 首包信息（长度/outer/control）+ 一次性诊断，全部 try 包裹。"平台从未回调它"从此**可证伪** | `:186, 1112-1120` |
| **O2**（receive 在借未还） | `s_receiveBuffersPending`（Append 成功 +1，**只有 Flush 成功才销账**）/ `s_receiveBuffersFlushed`；失败不销账，让 `rxpend` 诚实 | `:195, 1237-1238, 1815+` |
| **O3**（计数口径不统一） | 新增跨线程计数器一律 `Interlocked`；`GetKeepAlivePayload` 归还的孤儿 buffer 单独计 `_keepAliveOrphanBuffers` 并回收（`Length=0` → Append → Flush） | `:201, 1197-1206` |
| **P1**（启动探测绕过闸） | `RunDatafaceProbeOnceAsync` 收尾换成 `GracefulTeardown.EnsureTornDownAsync`，闸门不过就**拒绝删 profile**，替换原"Disconnect 返回 Ok + 固定 2 s 延时 + 照删" | `MainComponent.cs:336-337` |
| **P2**（自测循环第二种拆法） | 循环里的 `Disconnect → 立刻 Delete` 改走过闸的 `EnsureTornDownAsync`，只在 `torn` 为真时删 | `VpnProfileSetup.cs:382-390` |
| 额外 | 删掉死字段 `_bridgeRunning`（实例态存进程级桥就是文档里点名的反模式）；`Encapsulate` / `Disconnect` 变成不会外抛的包装层，`Disconnect` 的 `channel.Stop()` + 快照写盘移到 `finally` | `:720, 1264, 1297` |

仓库内 4 处 `DeleteProfileAsync` 调用点全部确认在闸后面。

### 10.3 下一轮实测的判据（先把口径钉死，免得像 7188 那样猜三轮）

快照新增列：`flush=` `reapp=` `err=` `decap=` `rxpend=` `rxflush=` `kaOrphan=`（`DatafaceCounters()`，同时进 `dataface-plugin.txt` 与 `LogSession`）。

* **档 4 一臂**（#25，未测）：`reapp` 必须 ≈ `encap`，`left`/`ret` 不涨，`flush` 恒 0，且**必须越过 7188 不停摆** —— 这条同时验证"平台是否真会替我清理 `packets` 里的剩余 buffer"和"投影能不能 `Append`"两个未知。若 `packets.Append` 编译或运行不过，档 4 直接判死，退回档 2。
* **`receiveBatch>0` 一臂**：`rxpend` 在空闲时必须回落到 0；持续 >0 就是回程在借未还，与 send 侧 7188 同构。
* **`injectGetBuffer=true` 一臂**：只看注入成功率与 `rxpend`，用于判定官方点名的取包入口是否真能替代 `RequestVpnPacketBuffer`。

### 10.4 仍然未验证（不允许当已知）

C3（空 `VpnDomainNameAssignment` vs null）、档 4 的投影可用性与平台清理行为、`TerminateConnection` 在本会话形态下的实际清理效果、P1 的新收尾是否仍能正常删掉探测 profile —— 四条全部待实测。**且档 4/批量 Flush/`injectGetBuffer` 三臂都要碰隧道，需明确授权后由 Visual Studio 部署、逐臂单变量跑。**

---

## 11. 【1.0.36 / 组件 1.0.24 追记】观测性地基：发布版第一次有日志

审计期间反复出现的一类障碍不是 API 用错，而是**发布版里查不到发生过什么**：
`Debug.WriteLine` 带 `[Conditional("DEBUG")]`，Release/AOT 二进制里连调用都不存在；
`LogDiagnosticMessage` 要 RAS tracer/ETL 且常被平台丢；`LocalState\*.txt` 全是覆盖式快照。
所以 09 号里好几条"待实测"其实还附带着一个隐藏前提 —— **实测了也看不清**。

本轮补上这个地基：`ClashUWP.Vpn\RollingLog.cs`（两个进程共用，靠 `InternalsVisibleTo` 给前台）。
口径、点位清单、读取方式（新增只读命令 `logtail`）全在
[`Docs/11-滚动日志.md`](11-滚动日志.md)。与审计直接相关的三条：

* **热路径不记日志**（`Encapsulate` / 注入泵 / keep-alive / `GetKeepAlivePayload`）——
  这些地方的观测仍然只走计数器 → `dataface-plugin.txt`。§10.3 的判据一个字没改。
* **`dataface-sessions.txt` 行格式是机器契约**（`HealthMonitor` 与 `SessionWatchdog` 都在按
  前 19 个字符解析时间戳当"平台自动重拨"保险丝）。日志改造刻意没碰它，
  分流靠入队时的显式标记，不靠猜行长相。
* **收尾闸生效与否第一次变得可见**：`profile-delete-refused`、`probe-teardown`、
  `disconnect-inner`、`teardown-selfcheck` 四类行进了日志。
  §10.4 那条"P1 的新收尾是否仍能正常删掉探测 profile"从此不必只靠 `dataface-v4.txt` 单点。

仍未实测的部分照旧：本轮只到"编译 + AOT 通过"，日志的**真机输出**尚未验证。

---

## 12. 【1.0.46 / 组件 1.0.26 追记】T1–T4：Start 之后的出口、取包入口、Decapsulate 归还链、记账原子化

> 本轮性质：**纯代码轮**。没有发起任何连接、没有写任何碰隧道的 `debug-cmd`、没有部署。
> 每条改动只是"与官方条文对齐 + 可编译 + 默认行为变更被显式声明"，**连通性与运行级结论一律待实测**。
> 版本：组件 `ClashUWP.Vpn 1.0.26`（`ClashUWP.Vpn.csproj:52`）、主工程 `ClashUWP.csproj:94` 的
> `PackageReference` 同步 1.0.26、App `Package.appxmanifest:38` = `1.0.46.0`。
> 编译：`dotnet pack` 0 错误 → `artifacts/packages/ClashUWP.Vpn.1.0.26.nupkg`；
> `Tools/UwpBridge` Release|x64 AOT publish `ok : True`、`exitCode : 0`、日志含 `Generating native code`，
> 产物 `ClashUWP/AppPackages/ClashUWP_1.0.46.0_x64_Test/ClashUWP_1.0.46.0_x64.msix`。
> 最后一次是**干净重建**：`dotnet clean ClashUWP\ClashUWP.csproj -c Release -p:Platform=x64` →
> `dotnet restore --force` → 桥 `-target Publish`（71.9 s）。
> ★ 顺带把主工程 csproj 注释里那条"同版本号不重新拉取"的坑又撞实了一次：`--force` 之后
>   `%USERPROFILE%\.nuget\packages\clashuwp.vpn\1.0.26\lib\…\ClashUWP.Vpn.dll` **仍是旧那份**
>   （与 `bin\Release` 新产物的 md5 不同）。本轮靠"旧那份里已含 `RequestPostStartTeardown` /
>   `TakeSendBuffer` / `NoteTakeSendFailure` / `TakeReceiveBuffer` / `PostStartTeardownDelaySeconds`
>   全部符号"确认装进包的代码就是 T1–T4 之后的"；唯一没进去的改动是**一行注释里的行号**。
>   结论：**下次改插件必须继续 bump 版本**，`--force` 不足以刷新全局缓存。
> 行号全部给 **1.0.26 现值**（§10.2 里登记的旧行号因 T1 插入整体后移，以本节为准）。
> 每条"现状（1.0.25）"都用 `git show HEAD:ClashUWP.Vpn/ClashVpnPlugin.cs` 逐条复核过
> （HEAD 的 `<Version>` 正是 1.0.25），下文出现括号 HEAD 行号处即为旧文件位置。

### 12.0 一句话账

T1 补"Start 已成功后再抛"这一段没人收尾；T2 把两处取包入口切到文档点名的 API，废弃入口降为可回退对照臂；
T3 给 `Decapsulate` 的归还不变式补注释并加 `Status` 观测；T4 把记账里最后两处非原子读写收进 `Interlocked`。

**行为变更声明（不许略读）**：本轮**有意改了产品默认两处** —— 回程注入取包
`injectGetBuffer` false→**true**，以及新增的 keep-alive 取包 `sendGetBuffer` 默认**true**。
两者只动 send / receive **池的取包层**；§10.1 定下的实测成立档 **`encapReturn=2`（Append + 每包 Flush）**、
**`receiveBatch=0`（每注即 Flush）** 一个字没动，热路径形态不变。回退各写一行 `settings.txt`，见 §12.5。
> ★ **1.0.27 复核轮已把其中一处翻回**：`sendGetBuffer` 默认 **false**（`GetVpnSendPacketBuffer()` 降为
> `=1` 的待测臂），理由是它零实测、而 §10.1 的口径是"没有实测证据前不改默认"。
> `injectGetBuffer=true` 维持（那边有 docs/76 `injectmode=1` 的 24 包实测背书）。
> 本节按 1.0.26 原样留档，现值以 **§13** 为准。

### 12.1 T1 —— Start 之后的失败路径收进唯一的收尾闸

**现状（1.0.25）**：`Connect` 的 catch 尾部（现 `ClashVpnPlugin.cs:686-699`，HEAD `:669-672` 就三行
`if (!_started) AbortConnectionAttempt(…); throw;`，其上注释原文是
"仍然只 rethrow，由平台拆通道后回调 Disconnect → `channel.Stop()` 收尾"）里，`AbortConnectionAttempt`
只覆盖 Start **之前**的三类失败；`_started == true` 这一支（桥 FailFast / 步 8 之后任何一处抛）
只有 `throw;`，注释把收尾寄托在"平台拆通道后回调 `Disconnect`"上 —— 而**同一个 catch 上面 `:658`
就记着实测反例**（"Start 失败 ⇒ 不会有 Disconnect 来收尾"），`:606` 与 `:2395` 记着 run 20 的观察
（OS 拆通道时直接换新进程重新 `Connect`、从不回调 `Disconnect`）。
即：这是一个**没有证据的假设**，而"拆不干净"的代价 00:21 已经量过（孤儿 NRPT + 幽灵路由 = 整机断网，Docs/08 §10.10）。

**改成什么**：`_started==true` 分支改为显式调用新增的 `RequestPostStartTeardown(string why)`（`:798`，
延迟常量 `PostStartTeardownDelaySeconds = 3` 在 `:740`），内部动作依次为：
① 先 `SessionWatchdog.Stop("post-start 失败，收尾由 Connect 的 catch 发起")` —— 保证**每轮只有一个发起者**；
② `LogSession` 写 `evt=post-start-teardown reason=… delay=3s 入口=EnsureSessionTornDownAsync（只等不删 profile）`（`:804`）；
③ `RollingLog.Warn("post-start-teardown", …)`；
④ `Task.Run` 后台等 3 s 后调 **`GracefulTeardown.EnsureSessionTornDownAsync()`**（`:816`）；
⑤ 结果写 `evt=post-start-teardown-done report=…`（`:826`），`report` 里明确区分
"会话已确认收干净" 与 "★未确认收干净，profile 保留，需手工 `rasdial /disconnect`"。
**没有**改成 `channel.Stop()`，**没有**在已 Started 的会话上调 `TerminateConnection`。

**官方条文出处**（本地 26100 元数据
`microsoft.windows.sdk.net.ref/10.0.26100.57/lib/net8.0/Microsoft.Windows.SDK.NET.xml:89172-89178`，逐字）：

> `VpnChannel.TerminateConnection(String)` — "Allows a VPN plugin to indicate an error message and terminate
> the connection attempt performed inside the IVpnPlugin.Connect method. **Should not be called on reconnect cases.**"
>
> `VpnChannel.Stop()` — "Destroys a previously established VPN channel object.
> Disassociates and closes the *outerTunnelTransport* connection to the VPN server."

两条各自封死一条捷径：`TerminateConnection` 条文限定"建连尝试"且明写重连场景不该调 ⇒ Started 之后不能用；
`Stop()` 的条文只说"Destroys … Disassociates and closes"，**没有任何一句授权插件用它去主动结束一个刚 Start 成功的会话** ——
"它是用来回应平台发起的断开"是本仓库 §4/§10 的既有判读（结论：插件侧没有自主退出原语），不是条文原话。
所以收尾只能走用户级 `VpnManagementAgent` —— 也就是仓库里**唯一那份** `GracefulTeardown` 实现，
这正是本条要求的"收进唯一的收尾闸"。

**为什么用 `EnsureSessionTornDownAsync` 而不是 `DisconnectThenDeleteAsync`**：后者会删 profile，
而这次失败与 profile 无关，删它等于把用户配置一起赔进去。前者只做
"状态非 Connected 就直接返回（天然幂等，撞车无害）→ 否则 `Agent.DisconnectProfileAsync` → 轮询到连续两轮非 Connected，
上限 `WaitSeconds=20`"，不碰 profile。

**为什么 `throw;` 保留**（三条理由 + 代价，全写进了方法文档注释）：
① 异常是平台能收到的**唯一**失败信号，吞掉等于把"Connect 失败"谎报成"成功但没流量"；
② 收尾走的是用户级 agent，与 rethrow 互不干扰，两者不是二选一；
③ 代价明说：**平台会不会因此回调 `Disconnect` 仍无实测数据 ⇒ 本轮收尾不依赖该回调**，
所以宁可显式发起，也不等那个回调。

**风险**：后台任务里的 3 s + 最多 20 s 都发生在 `Connect` 返回之后，绝不把等待放进同步平台回调（run 34 教训）；
若平台自己也在拆通道，两个发起者可能同时动作 —— 缓解是先停哨兵、且 `EnsureTornDownAsync` 用"重新取 profile 读状态"
而不是信任旧对象；`EnsureSessionTornDownAsync` 找不到 profile 时返回 `(true, "profile 不存在")`，
这条路径会把"其实没建起来"误报成"收干净了"，靠 `report` 原文区分。

**未验证项**：post-start 抛异常时平台侧实际行为（是否重拨、是否回调 `Disconnect`）、
`TerminateConnection` 之外的用户级断开能否清干净 Started 之后的半装状态、
以及这一支在真机上**能否被触发**（要造一个 Start 后 FailFast，本轮未造）。

### 12.2 T2 —— 取包入口切到文档点名 API，废弃入口降为可回退对照臂

**现状（1.0.25）**：两处默认取包全用被本地元数据标 **"Not supported."** 的
`RequestVpnPacketBuffer(VpnDataPathType, out …)` + 同被标废弃的枚举 `VpnDataPathType`：
回程注入（`InjectAppend`，现 `:2192` → 取包 `:2197`）与 keep-alive（`GetKeepAlivePayload`，现调用点 `:1359`）。
`GetVpnReceivePacketBuffer()` §10.1 实现过但默认关（`injectGetBuffer=false`，HEAD 的 `TakeReceiveBuffer` 文档
里默认臂写着"【默认】`RequestVpnPacketBuffer(VpnDataPathType.Receive, out buf)`"）；
`GetVpnSendPacketBuffer()` **在 HEAD 的插件源码里 grep 计数为 0**。

**改成什么**：取包入口各自收进一个方法，默认走文档具名 API，废弃入口留作显式对照臂（不许删）。

| 侧 | 入口方法（现行号） | 默认臂 | 回退臂 | 归还形状 |
|---|---|---|---|---|
| receive（注入泵） | `TakeReceiveBuffer`（`:2299`） | `channel.GetVpnReceivePacketBuffer()`，返回 null 抛 `InvalidOperationException` | `injectGetBuffer=0` → `RequestVpnPacketBuffer(Receive, out …)` | 完全不变：`InjectAppend` + `FlushReceive` |
| send（keep-alive） | `TakeSendBuffer`（`:2340`） | `channel.GetVpnSendPacketBuffer()`，返回 null 计一次并返回 null | `sendGetBuffer=0` → `RequestVpnPacketBuffer(Send, out …)` | 完全不变：交回出参，`borrowed=null`；抛异常走 orphan 归还 |

调用点：keep-alive 在 `:1359`（`VpnPacketBuffer? buf = TakeSendBuffer(channel); borrowed = buf; if (buf != null) { … }`
—— 写入 `Length=1` / `KeepAliveControlByte` / 交回出参那段一字未改）；注入在 `TakeReceiveBuffer` 内部。
借出后**每个出口都归还**：send 侧靠原有 `borrowed` 变量 + `:1404` 起的 orphan 归还
（`Interlocked` 计数、`Length=0` → `AppendVpnSendPacketBuffer` → `FlushVpnSendPacketBuffers`），
receive 侧靠 `:2216 / :2228` 的 `s_receiveBuffersPending` 记账 + §10.1 已保证的四个冲出口。
观测新增两列：`takeFail` 进 `DatafaceCounters()`（`:1445-1456` 内，`:1454`；
逐轮心跳 `evt=hb` 就是经 `SessionWatchdog.cs:201` 调这个方法，所以心跳行自动带上它）与
`takeFail=/takeErr=`（`evt=disconnect` 行，`:1532`）；快照另出
`SendGetBuffer=` / `TakeSendFailures=` / `TakeSendFirstError=`（`:1649-1651`）。
失败原因只走计数与快照，**不加热路径日志**（§11 口径）。

**官方条文出处**（同一个 XML，逐字）：

> `VpnChannel.GetVpnReceivePacketBuffer()`（`:89032-89035`）—
> "Requests a **VpnPacketBuffer** object from the receive packet buffer pool to be used in
> the decapsulation or injection of a received packet from the VPN server into the local network stack"
>
> `VpnChannel.GetVpnSendPacketBuffer()`（`:89036-89039`）—
> "Retrieves a **VpnPacketBuffer** object from the send packet buffer pool to use in encapsulation and
> transmission of a data packet from the client network stack to the VPN server."
>
> `VpnChannel.RequestVpnPacketBuffer(VpnDataPathType, VpnPacketBuffer@)`（`:89090-89094`）—
> "**Not supported.** Requests a **IVpnPacketBuffer** from the specified pool.
> There is one **IVpnPacketBuffer** for the send path and another **IVpnPacketBuffer** for the receive path"
> （枚举 `VpnDataPathType` 本身亦标 "is not supported and may be altered or unavailable in the future"）

前两条是**文档点名的具名入口**，第三条对自己一直在用的入口写着 "Not supported." —— 这就是本条改动的全部依据。
另外 §10 曾把第三条误记成 "one pool for the send path…"，本轮把 `Inject()` / `TakeReceiveBuffer`
的注释改成逐字原文（含开头的 "Not supported."）。

**先遣档案对照（红线要求，动手前查过）**：
`D:\fluentapps\repos\clashUWP研究` docs/76 的 `injectmode=1` 臂用的正是具名 receive 入口，
**24 包全部 `attempt=0` 一次成功** ⇒ receive 侧换默认有同路线实测背书。
**send 侧（`GetVpnSendPacketBuffer`）在两个仓库里都没有任何实测数据**，
投影失败亦无历史记录 ⇒ 这条臂是本轮风险最高的一处，靠 `takeFail`/`takeErr` 两列 + `sendGetBuffer=0` 回退兜住。

**风险**：具名入口与废弃入口借的是**同一个池**、归还路径（Append+Flush）一致，差别只在取包层，
所以失败形状不新 —— 取不到包今天就已经表现为 `keepAlivePacket = null`（平台按"本轮没有 keep-alive"处理）或
`injFail`（stage=`request`）。真正的新风险只有两个：① `GetVpnSendPacketBuffer()` 在 AOT 投影下是否可用（编译已证实有该成员，
**运行未证**）；② 若它返回 null 而非抛异常，keep-alive 会静默缺席 —— 由 `takeFail` 计数暴露，不靠猜。
send 池被借空是 7188 那类停摆的病根，所以本轮**刻意没有**动 send 侧的 Append/Flush 形态。

**未验证项**：`GetVpnReceivePacketBuffer()` 长跑（是否仍会撞 receive 侧在借未还）、
`GetVpnSendPacketBuffer()` 的实际可用性与 null 语义、两臂对照下的 `inj/rxpend/rxflush/ka/takeFail` 一致性。

### 12.3 T3 —— `Decapsulate` 的归还链做防御性闭合

**现状（1.0.25，`git show HEAD` 复核过）**：`Decapsulate`（HEAD `:1105`，现 `:1248`）体内只有计数与首包信息，
注释写的是行为描述"这里只记数、不碰 buffer"（HEAD `:1114`），**没有**把官方不变式写成约束
（"本方法不得请求 buffer；若请求，必须走两条链之一归还"），`encapBuffer.Status` 从未读过。
本项目外层 transport 按设计指向静默黑洞 `192.0.2.1`，所以历史上 `decap=0`，这条路径实际不跑 ——
"不跑"不等于"以后不写"，本条是纯防御。

**改成什么**：① 方法文档注释把不变式写明 —— 本方法不得请求 buffer，将来任何入站处理**必须**走
`decapsulatedPackets`（注入客户端 TCP/IP 栈）或 `controlPacketsToSend`（回给 VPN 服务器）两条链之一；
② 开头 `Interlocked.Increment(ref _decapsulateCalls)`（`:1285`）；
③ 对到达的 buffer **只读 `Status`、只记录、不处置**：`try { status = encapBuffer?.Status.ToString() ?? "null"; } catch (Exception exSt) { status = exSt.GetType().Name; }`，
非 `Ok` 时 `Interlocked.Increment(ref _decapNonOkStatus)`（`:1299`）+ 记首个原因 `_decapStatusFirst`（`:1300-1301`），
一次性 `LogDiagnosticMessage("…（首次=…，只记录不处置）")`（`:1318`）；
④ 首包信息里带上 `status=`，会话开始处归零（`:583-584`）。

**官方条文出处**（MSDN remarks，经先遣 docs/65:138-140 收录，本地 XML 无此 remarks，出处如实标在那儿）：

> docs/65:138（`ivpnplugin_decapsulate_1667754039.md` -remarks）—
> "It is required that any **IVpnPacketBuffer** objects requested by the plugin be returned to the VPN framework
> **via either the *decapsulatedPackets* chain**, to inject into the client TCP/IP stack,
> **or the *controlPacketsToSend* chain**, to reply back to the VPN server."
>
> docs/65:140（`vpnchannel_requestvpnpacketbuffer_7633600.md` -remarks）—
> "Any **IVpnPacketBuffer** object requested by the plugin must eventually be returned to the VPN platform
> (**i.e. via IVpnPlugIn.Encapsulate or IVpnPlugin.Decapsulate**). Otherwise the plugin may be unable to
> request new buffers until the outstanding buffers have been returned."

`Status` 可读性是**本地元数据证实**的（`Microsoft.Windows.SDK.NET.xml:89858-89861` + `:89904-89912`，逐字）：

> `VpnPacketBuffer.Status` — "Gets the status of **VpnPacketBuffer**. The status property may be used to
> report errors when manipulating the **IVpnPacketBufferList.**"
> `VpnPacketBufferStatus` — "Defines constants that specify the possible states of the VPN packet buffer."
> `InvalidBufferSize` — "Specifies an invalid buffer size." / `Ok` — "Specifies that the state is ok."

这条与 §10.1 档 3 之死**不矛盾**：抛 `InvalidCastException` 的是 `VpnPacketBufferList` 的**枚举器**
（无 `GetAt` / 无索引器 / 无 `Status`），而 `VpnPacketBuffer.Status` 是正常投影的实例属性 ——
本轮编译通过即为该结论的（编译级）佐证。读动作仍包 `try`，因为投影层的静态正确不等于运行时正确。

**风险**：几乎为零 —— 只读一个属性、只计数、不改控制流，热路径之外的回调且历史上 `decap=0`。
唯一实质风险是 `LogDiagnosticMessage` 在真实入站场景下按包刷屏，已用"只记非 `Ok`"+一次性守卫压住。

**未验证项**：真机上平台到底会不会回调 `Decapsulate`（仍只有 `decap=` 一列可证伪）、
`Status` 的实际取值分布、`InvalidBufferSize` 在本形态下是否可能出现。

### 12.4 T4 —— 记账原子化

**现状（1.0.25）**：两处口径缺陷。
① `FlushReceive` 用**读—减**（先 `Volatile.Read(ref s_receiveBuffersPending)` 再 `Interlocked.Add(…, -n)`），
泵线程与 Append 侧并发时 `rxpend` 会漂；② 会话计数器仍有裸 `++` / 裸赋值：
`_encapsulateCalls++`、`_encapListItemsSeen += listSize`、`_encapsulatedPackets++`（两处）、
`HandleOutboundPacket` 里五处 `++`、`_keepAlives++`、`_lastKeepAliveTick` 读写、
`TryReturnSendBuffer` / `TryFlushSendBuffers` / `NoteSendReturnFailure` 的计数与"首个原因"赋值、
以及会话开始整段归零的裸赋值（`:550-590`）。

**改成什么**（全部只动记账，不动任何分支语义）：

| 位置（现行号） | 改成 |
|---|---|
| `FlushReceive`（`:2244`，核心 `:2260`） | `int taken = Interlocked.Exchange(ref s_receiveBuffersPending, 0);` → Flush 成功才 `Interlocked.Add(ref s_receiveBuffersFlushed, taken)`（`:2276`）；**Flush 抛异常则把 taken 加回去**（`:2270`）并 `NoteInjectFailure("flush", ex)` 后返回，绝不带着已销账的数失败 |
| `EncapsulateCore`（`:872`，`:890`） | `_encapsulateCalls` / `_encapListItemsSeen` → `Interlocked`；`_encapListSizeMax` 的读用 `Volatile.Read` |
| 两处 `_encapsulatedPackets++`（`:936`、`:979`） | `Interlocked.Increment` |
| `HandleOutboundPacket`（`:1013` 起） | 进函数先 `uint encapSeen = Volatile.Read(ref _encapsulatedPackets)` 供周期性日志用；`_nonUnicastDropped`（`:1026`）、`_ipChecksumFixed` / `_transportChecksumFixed`（`:1043-1045`）、`_echoReplies`（`:1069`）、`_unknownPackets`（`:1059`）全部 `Interlocked`；"首个未知包只报一次"改成 `if (Interlocked.Increment(ref _unknownPackets) == 1)` —— 原来先自增再判 `==1` 在并发下会重复报 |
| `NoteSendReturnFailure`（`:1218`） | 计数 `Interlocked.Increment`；首个原因改 `if (Interlocked.CompareExchange(ref _sendReturnFirstError, reason, "") == "")`。**注意**：本轮一度写成先 `Exchange` 再判，那会把"首个"变成"任一个"并抹掉原值，已纠正为纯 `CompareExchange`（所有 first-error 字段初值都是 `""`，判等成立） |
| `GetKeepAlivePayload`（`:1353`） | `uint kaSeq = (uint)Interlocked.Increment(ref _keepAlives)`，前 3 次与每 20 次的守卫用 `kaSeq`；`_lastKeepAliveTick` 改 `Interlocked.Read` / `Interlocked.Exchange`；orphan 计数 `Interlocked.Increment`（`:1404`） |
| `Decapsulate`（`:1285`、`:1299`） | 见 §12.3，同一口径 |
| 会话开始归零（`:550-590`） | 整段裸赋值 → `Interlocked.Exchange(…, 0 / "")`（字符串字段 `_decapFirstInfo` / `_decapStatusFirst` 保留裸赋值：只有一个写者且紧接在 `_started=false` 之后） |

字段类型**没改**：`System.Threading.Interlocked` 在现代 .NET 上确实有 `UInt32` 重载
（`Increment` / `Add` / `Exchange` / `CompareExchange`）与 `Int64 Read(Int64 ByRef)` —— 用
`pwsh` + `ForEach-Object ToString` 反射确认过；早先用 Windows PowerShell 5.1（.NET Framework）探到的
只有 `Int32/Int64`，那是**探测工具本身的口径错**，据此改成 `uint`→`int` 会是白白放大 diff。

**读侧没做完，明说**：三处读者（`DatafaceCounters()`、`evt=disconnect` 行、`WriteDatafaceSnapshot()`）里
只有本轮新增/跨线程那几列用了 `Volatile.Read`（`_encapsulateErrors`、`s_takeSendFailures`、
`s_receiveBuffersPending`、`s_receiveBuffersFlushed`），其余计数器仍是裸读。
理由：都是对齐的 32 位字段，硬件上读不撕；三处读者都是一次性格式化、不在循环里重复读同一字段，
裸读在这里最坏只让"这一行的数偏旧"，不参与任何分支判定；全量替换要动 60 余处而换不到行为差别。
**如果下一轮要把 `rxpend` 曲线当硬判据，先补这一处。**
另一处已知非原子：`_encapListSizeMax` 的 max 更新（`:893-894`）是"`Volatile.Read` + 裸赋值"，
属良竞态 —— 最坏是 `max` 少报一批，不会多报。

**官方条文出处**：本条不涉及 API 条文，依据是仓库自己的度量口径 ——
§10.1 已经因为"读数口径不对"作废过一轮单变量实验（`ReadInt` 硬编码上界导致 `encapReturn=3/4/5` 静默落回 2）。
`rxpend` 是 §10.3 里判"回程在借未还"的**唯一**依据，非原子减法等于给判据本身掺水。

**风险 / 已知残余不精确**：`Exchange` 与 `Flush` 之间若有新包 Append，那部分会被计入本轮 `taken` ⇒
`rxpend` **偏保守（偏高）**、`rxflush` 可能重复计数。方向刻意选"偏高"而不是"偏低"——
偏高的假警（"好像有在借未还"）可以由 `Flush` 成功后归零自愈，偏低的假安（"账面干净"）会掩盖真漏。
两列都是**纯诊断**，代码里没有任何分支读它们，所以不影响行为。

**未验证项**：`rxpend` / `rxflush` 在真机长跑里的实际曲线（尤其 `receiveBatch>0` 时）；
本轮所有原子化改动**没有也不可能改变隧道行为**，其正确性判据仍是 §10.3 那三条。

### 12.5 开关与默认值（本轮新增 / 翻转）

| 键 | 1.0.25 默认 | 1.0.26 默认 | 位置 | 回退写法（`%LOCALAPPDATA%\Packages\<包名>\LocalState\settings.txt`） |
|---|---|---|---|---|
| `injectGetBuffer` | false（走 `RequestVpnPacketBuffer(Receive)`） | **true**（走 `GetVpnReceivePacketBuffer()`），1.0.27 维持 | `LocalSettings.cs:312`，`Load()` 复位 `:420`，读取 `:454` | `injectGetBuffer=0` |
| `sendGetBuffer` | —（新增） | ~~true~~ → **false**（1.0.27 翻回，见 §13.1） | `LocalSettings.cs:351`，`Load()` 复位 `:421`，读取 `:455` | `sendGetBuffer=1` 才进 `GetVpnSendPacketBuffer()` 待测臂 |
| `encapReturn` | 2 | 2（未动） | `LocalSettings.cs`，档 4/5 仍是待测臂 | 见 §10.1 |
| `receiveBatch` | 0 | 0（未动） | 同上 | 见 §10.1 |

生效值单行摘要日志照 §10.1 的约定继续打印，本轮把两个入口**分开**列出
（`injectGetBuffer=` / `sendGetBuffer=`，`LocalSettings.cs:453-454`）—— 静默回落是已发生过的坑，不许合并成一个"取包口径"。
区间校验沿用 `ReadInt` 的显式上下界重载；`ReadBool` 不认识的值直接保持默认，
两个开关各自的文档注释里写明了"本臂在本仓库仍是未实测"。

### 12.6 未验证项汇总 + 需要你点名的实测臂

**只到编译级 / 读级的结论（不许当运行级已知）**：
`GetVpnSendPacketBuffer` / `GetVpnReceivePacketBuffer` 的投影可用性与 null 语义（编译证明成员存在且 AOT 通过，仅此）；
`VpnPacketBuffer.Status` **只到"已投影、编译通过"，运行时可读仍未证**（所以才包 `try`）；
T1 那条 post-start 分支能否被真机触发、以及触发后平台侧反应；
`takeFail` / `takeErr` / `rxpend` / `rxflush` / `decap=` 各列的真机读数；本轮全部滚动日志输出。

**需要明确授权 + VS 部署后逐臂单变量跑的实测臂**（本轮一条都没跑，且其中四条会碰隧道）：

1. **`sendGetBuffer=1` 臂验证（1.0.27 起是**opt-in**，不再是默认）** —— 判据：`takeFail` 恒 0、`ka` 仍按节奏涨、
   `ret`/`flush` 与 `encap` 同步、长跑不停摆。`takeFail>0` 就把开关写回 `0`（= 默认，无需改动）。
   ★ 注意 `takeFail` 现在两臂共用：读数必须与快照里的 `SendGetBuffer=` 一起解释。
2. **`injectGetBuffer` 默认臂验证（1.0.26 新行为）** —— 判据：注入成功率、`rxpend` 空闲时回落 0、`inj` 与 `rxflush` 同步。
3. **档 4（#25，未测，需授权）** —— 判据不变：`reapp` ≈ `encap`、`left`/`ret` 不涨、`flush` 恒 0、且必须越过 7188 不停摆。
4. **档 5 / `receiveBatch>0`（需授权）** —— 批量归还路径，判据同 §10.3。
5. **T1 的 post-start 分支** —— 需要人造 Start 后 FailFast（例如桥强制抛），属碰隧道实验，**未获授权前不做**；
   另需你点名才写的 `clashuwp-cli connect` 那一条也仍然挂着。
6. **`Decapsulate` 真被回调与否** —— 需要外层 transport 指向活服务器的臂（与 by-design 黑洞冲突），
   本轮只到"`decap=` 一列从此可证伪"。

**做不到 / 没做的事，明说**：本轮没有部署、没有装包、没有连接，因此**不存在任何运行级验证**；
T2 的 send 臂是先遣档案里也找不到实测数据的空白，我只把它做成"默认开 + 有计数 + 一行可回退"，
没有假装它已被证实；`VpnPacketBufferList` 的枚举限制（档 3 之死）本轮没有重新验证，仍按 §10.1 的结论处理。

> ★ **【2026-10-18 17:02 追记】本节已被运行级证据部分覆盖** —— 1.0.47 装上机并真连了一次，
> §12.6 的读数缺口补上了大半，详见 **§14**。下面这段保留为"当时的原始口径"，冲突时以 §14 为准。

## 13. 【1.0.47 / 组件 1.0.27 追记】复核轮：默认臂翻回、`_started` 复位、打包链取证

> 本轮性质：**对 §12 的复核**，仍是纯代码轮 —— 没有部署、没有装包、没有连接、没写任何碰隧道的
> `debug-cmd`。做了两件修复 + 一次端到端的"源码→包"二进制核对。
> 版本：组件 `1.0.27`（`ClashUWP.Vpn.csproj:52`）、`ClashUWP.csproj:94` 同步、App `Package.appxmanifest:38` = `1.0.47.0`。
> 编译：`dotnet pack` exit 0 → `artifacts/packages/ClashUWP.Vpn.1.0.27.nupkg`（`.local/pack-1.0.27.log`）；
> `Tools/UwpBridge -Build … -Target Publish` → `ok : True`、`exitCode : 0`、79.6 s、含 `Generating native code`，
> 产物 `AppPackages/ClashUWP_1.0.47.0_x64_Test/ClashUWP_1.0.47.0_x64.msix`（`.local/build-1.0.47.log`）。

### 13.1 T2 的一半被推翻：`sendGetBuffer` 默认 true → **false**

理由只有一条，而且是本仓库自己定的口径：**没有实测证据前不改默认**（§10.1，档 3 就是"看着更贴
条文"却死在投影层的那次）。两条取包臂的证据量并不对称 —— receive 侧有先遣项目 docs/76 的
`injectmode=1`（24 包全部 `attempt=0`）背书，所以 `injectGetBuffer=true` **维持**；send 侧在
**两个仓库里都零实测**（§12.2 自己就写了"没有任何实测数据"），而 keep-alive 是热路径上唯一由我们
主动向 send 池借包的动作。让用户的第一次连接去当实验，不是"按条文摆正"能买到的。

代码一条没删：`sendGetBuffer=1` 就是那条待测臂（`TakeSendBuffer` 的注释与 `LocalSettings.cs:351/:421/:455`
都写明了位置互换与依据）。★ **读数口径变了**：`takeFail` 是两臂共用的计数，现在默认臂失败也进同一列，
所以它必须与快照里的 `SendGetBuffer=` 一起解释，不能单看非零就判"新臂坏了"。§12.6 第 1 项已同步改成
"opt-in 臂验证"。

### 13.2 T1 的补刀：post-start 分支里复位 `_started`

`_started` 原先只在 `:550` 置真、只在 `Disconnect`（现 `:1496`）置假 ⇒ 桥 FailFast 抛出后它仍是
`true`，而下游两道闸都读它：`EncapsulateCore` 的 `if (!_started) return;` 和 `PumpLoop` 的
`while (… && _started …)`。泵本就被同一个 catch Cancel 了，但**平台在拆这条会话的途中若还往实例交包，
`Encapsulate` 会照常对一个正在销毁的通道跑完整数据面** —— 1.0.34 的外层守卫只保证"不抛出去"，不保证"不干活"。
现在 `:716` 在 `RequestPostStartTeardown` 之前先把它关掉的。
★ 位置是刻意的：catch 上半段的**阶段归因**（`_started ? "Start 已成功…" : "StartWithTrafficFilter 异常"`）
和分支判定读的是同一个字段，提前置假会把桥 FailFast 伪装成 Start 失败 —— 那是 §12 C4 刚记过的坑。
幂等：`Disconnect` 真来了再置一次同值，无副作用。

### 13.3 打包链取证：同版本号 repack **不会**刷新 NuGet 全局缓存（本轮独立复现）

复核时发现的形状：源码 06:15:05 改过 → 组件 06:18 重打包 → MSIX 06:26 产出，但
`ClashUWP/bin/x64/Release/…/win-x64/ClashUWP.Vpn.dll` 的 sha256 等于 `%USERPROFILE%\.nuget\packages\clashuwp.vpn\1.0.26\lib\…`
那份 **05:51 的旧 dll**（`03dbb3…`），与新构建产物（`2c0177…`）**不同**。
⇒ **同一版本号 repack，restore 永远命中已解包的旧份，`--force` 也不刷新**（§12 头部已自记过一次，本轮取证一致）。
本轮用 `ilspycmd -t` 逐类型比对了旧份与新份：`ClashVpnPlugin` / `LocalSettings` / `SessionWatchdog` /
`GracefulTeardown` 的反编译源码**逐字相同**，整体差异只剩投影胶水里的 `global::` 前缀与 `//IL_` 注释噪声
⇒ 1.0.46.0 那个包**侥幸没有行为差**（没进去的是一行注释）。但方法错就是错，`1.0.27` 是照 `ClashUWP.Vpn.csproj:50-51`
那条自家注释补的 bump。

★ **新增收工自检（下次改插件必做，两条都是机器可验的）**：
① 装包前核对 `sha256(~/.nuget/packages/clashuwp.vpn/<ver>/lib/net10.0-windows10.0.26100/ClashUWP.Vpn.dll)`
== `sha256(ClashUWP.Vpn/bin/Release/…/ClashUWP.Vpn.dll)`，且缓存份的 mtime ≥ 源码 mtime；
② 包内**没有**独立的 `ClashUWP.Vpn.dll` —— 自包含组件被 AOT 合并进主模块（清单注释【事实 2】：`Path="ClashUWP.dll"`），
所以 ① 那条哈希核对是"源码 → 包"链路上唯一能验的一环，别指望在 msix 里找组件文件。
本轮 1.0.27 的核对结果：缓存份 == 组件产物 == `ClashUWP/bin` 份，三处 `30ca6562…`；
再反编译 `ClashUWP/bin` 份确认 `_started = false;` 与 `SendGetBuffer = false` 都**在包里的代码里**。

### 13.4 仍开着的（不许当已修）

1. **T1 的收尾跑在一个随时可能被回收的进程里**。`RequestPostStartTeardown` 丢 `Task.Run`，先 `Delay(3 s)`
   再进闸（闸内最长再等 20 s），而 `VpnBackgroundTask.Run` 的 deferral 早在 `Run` 返回时就 `Complete()` 了
   （sample 同形）⇒ Connect 抛出、平台结束会话之后，宿主进程随时可能没了，这条收尾就**跑不完**。
   `:759` 那句"让出 3 s 是保险不是实测结论"仍然成立，而它恰好落在最容易被杀的位置。
   两个候选改法（都要动 Connect 的控制流，本轮没做）：立刻发起断开、把"等"交给下一个触发点；
   或在 catch 里同步等但把上限压到 2–3 s。**判据现成**：`plugin.log` 里有没有 `evt=post-start-teardown-done`。
2. §12.6 列的六条实测臂**一条都没跑**；`GetVpnSendPacketBuffer` / `GetVpnReceivePacketBuffer` 的投影可用性
   与 null 语义、`VpnPacketBuffer.Status` 的运行时可读性，全部仍停在"编译级/读级"。
3. 审计里"从未接触"的那一族本轮**一条没动**：`IVpnPlugInReconnectTransport`（v15/23493，本 TFM 可实现）、
   `IVpnChannel4`（`StartWithMainTransport` / `StartExistingTransports` / `TransportContext`）、
   `ActivityStateChange`、`SystemHealth`、`VpnPacketBuffer.TransportAffinity/AppId`、`IVpnPacketBufferList2`
   （未投影，需手写 QI）。"全量"这个结论**没有被本轮改变**。
4. 记账遗留：`_encapsulateFirstError` / `_decapStatusFirst` 这类"只记首个"的读-判-写仍非原子
   （并发同报时可能覆盖，无害，本轮按 §12.4 的口径不动）；`_encapListSizeMax` 保持普通赋值（单写者，注释已给理由）。


---

## 14. 【1.0.47 上机追记】第一次运行级读数：默认臂健康、T1 未触发

> 本轮性质：**运行级验证**。1.0.47 装上机、真连了一次、断开、验收工。
> 详细过程与全部读数见 `Docs/15-1.0.47上机验收清单.md`；本节只回写**对本审计的账**。
> 时间：2026-10-18 17:02:12 发枪 → 17:02:17 桥起 → 17:03:25 会话结束（存活 69 秒）→ 17:03:26 收工。

### 14.1 §12.6 六条实测臂的账

| # | 实测臂 | 本轮状态 | 依据 |
| --- | --- | --- | --- |
| 1 | `sendGetBuffer=1` 臂 | **默认臂（`=0`）已验且健康**；opt-in 臂（`=1`）未验 | 快照 `SendGetBuffer=0 TakeSendFailures=0`；`ret=3230/3230`、`flush=3230`、`left=0` |
| 2 | `injectGetBuffer` 默认臂 | ✅ **已验** | 快照 `InjectGetBuffer=1 ReceiveBuffersPending=0 ReceiveBuffersFlushed=4014`；`injFail=0` |
| 3 | 档 4（#25） | 未验（需专门造） | `reapp=0` / `left=0` 说明默认臂没走到那条路径 |
| 4 | 档 5 / `receiveBatch>0` | 未验 | 快照 `ReceiveBatch=0`（默认值） |
| 5 | T1 的 post-start 分支 | **未触发**（本轮桥一次就起来，没有 FailFast） | `plugin.log` 无 `post-start-teardown` 相关行，也无 `start 失败` |
| 6 | `Decapsulate` 真被回调与否 | 仍不可判定，但**与 by-design 一致** | `DecapsulateCalls=0 DecapFirst=` 空 —— 外层 transport 无真包，平台无包可交 |

⇒ **§12.6 那句"六条一条都没跑"现在要改**：其中 1 的默认臂与 2 已拿到运行级读数，
剩下的是**需要专门造场景**的三条（档 4 / 档 5 / T1），不是"什么都验不了"的空白。

### 14.2 §13.1 的核心论点被运行级证据加固

§13.1 把 `sendGetBuffer` 默认臂翻回 `false` 的理由是"没有实测证据前不改默认"，而本轮提供了**那条保证**：

- `TakeSendFailures=0` —— 翻回默认臂**没有引入取包失败**
- `SendBuffersReturned=3230 / SendBuffersFlushed=3230 / SendBuffersLeftForCleanup=0` —— 借出的 buffer **全部归还**
- `SendBuffersReappended=0` —— 没有重入堆积
- 长跑 69 秒 `EncapMaxBatch=14`、无 7188 停摆迹象

⇒ **§13.1 的选择是对的，且现在有运行级证据**，不再是"没证据所以保守"。

### 14.3 §13.4 第 1 条（T1 收尾可能跑不完）仍然开着

本轮**没有触发 T1**（桥一次就起来），所以 §13.4-1 的问题既没被证实也没被否证。
但本轮提供了一条**新的旁证**：会话 69 秒里 `evt=session-end` 正常落下、`encap=3230 inj=4014` 完整收口、
断开后 **NRPT 条数=0**、**无死接口路由**、网络恢复 ⇒ **正常的收尾路径是干净的**。
⇒ 风险集中在**异常路径**（桥 FailFast → Connect 抛异常 → 平台结束会话 → 宿主进程可能已被回收 → 那条 `Task.Run` 收尾跑不完），
这条路径本轮没走。**改法（立刻发起断开 / 或把 3 秒等待压到 2~3 秒）仍未做**。

### 14.4 §13.4 第 3 条（"从未接触"的那一族）本轮一条没动

`IVpnPlugInReconnectTransport` / `IVpnChannel4` / `ActivityStateChange` / `SystemHealth` /
`VpnPacketBuffer.TransportAffinity` / `IVpnPacketBufferList2` —— 仍是"审计未覆盖"，
**"全量"这个结论没有被本轮改变**。
（`VpnChannel4` 的投影存在性本轮有旁证：`.local/vc.cs` 是本机 winmd 的 `VpnChannel` 投影源码，
里面确有 `StartWithMainTransport` / `StartExistingTransports` / `GetSlotTypeForTransportContext`，
但**投影存在 ≠ 我们用过它**。）

### 14.5 §13.4 第 2 条：投影可用性从"编译级"升级到"运行级"（部分）

| 项 | 之前 | 现在 |
| --- | --- | --- |
| `GetVpnReceivePacketBuffer` 投影可用性 | 编译级 | ✅ **运行级**：`InjectGetBuffer=1` 跑通，`rxflush=4014`、`rxpend=0`、`injFail=0` |
| `GetVpnSendPacketBuffer` 投影可用性 | 编译级 | ❌ 仍停在编译级 —— **默认臂 `sendGetBuffer=0` 根本没调它**，只有 opt-in 臂会调 |
| `VpnPacketBuffer.Status` 运行时可读 | 未证 | 未证（本轮没读这一列） |

★ 第二行是本轮最容易被误读的一处：**默认臂跑得通，恰恰因为它不碰 `GetVpnSendPacketBuffer`**。
所以"send 侧投影可用性"这条**仍然不能说已验**。

### 14.6 一条操作口径（踩过，记下来）

`dataface-plugin.txt` **是会话结束时才落盘的**，不是周期写。
本轮 17:03:11（会话中）去读它，读到的是 10-17 23:33 的旧文件，一度误判为"1.0.46/47 引入了快照回归"。
⇒ **读这个快照必须在会话结束后**；会话进行中要读实时读 `watchdog.txt`（每 25 秒刷一次）。

---

## 15. 【2026-10-18 17:15 追记】查 CsWinRT 官方文档：档 3 死在投影层的根因，官方定性 + 可用 workaround

> 起因：哥哥问"为什么不改对"（指 A1 档 4 迟迟不落地）。
> 本轮性质：**外部文档查证 + 本机二进制反编译取证**。没有改代码、没有装包、没有连接。
> 目的：把"档 3 为什么死""官方有没有更好的解法""档 4 是否为官方认可的路径"三条一次钉死。

### 15.1 ★ 更正上一节的一处错口

§14 及更早的口径说"档 4 死在投影层" —— **这是错的**。
死在投影层的是**档 3**（`foreach` 枚举）；**档 4（`RemoveAtBegin` + `Append`）是 1.0.34 就实现完的可用代码**，
只是从未实测。§10.3 与 `LocalSettings.EncapReturn` 的定义注释里写的"档 4 已实现（1.0.34）"才是对的。

### 15.2 官方定性：这是平台侧 API 的问题，不是 CsWinRT 的锅

**完全对应的官方 issue：[CsWinRT #2373](https://github.com/microsoft/CsWinRT/issues/2373)**（2026-03-27 开，状态 open + external）
—— 症状与本项目完全同形：`InvalidCastException` + `IObjectReference.As` 抛异常 + AOT 下 `GetEnumerator` 一取就炸。

维护者 manodasanW 的回复（原文）：

> This looks like an issue with **the underlying API** as the `QueryInterface` call for
> `IEnumerable<DisplayArea>` is the one failing here. I confirmed in a C++/WinRT app too that
> **the call to `First` from `IVectorView` also fails there**.
> I will look into opening a bug on the team that owns this API.

**更早的历史 issue：[#747](https://github.com/microsoft/CsWinRT/issues/747)**（2021-02 开，2026 年仍 open；
里程碑从 Release 1.1.3 一路拖到 1.3.7 ⇒ 官方自己也没修）。
microsoft-ui-xaml #6454 于 2023-09-14 以 "Duplicate of #747" 关闭。

⇒ **官方定性：底层平台 API 的 `IEnumerable` QI 失败，不是投影层的 bug。**
这解释了为什么这个坑能横跨 5 年、跨 WinUI3 / DisplayArea / VPN 三个产品面。

### 15.3 根因（本机反编译坐实，不是推测）

反编译 `Microsoft.Windows.SDK.NET.dll`（10.0.26100.57）的 `VpnPacketBufferList` 投影：

```csharp
private IObjectReference Make___objRef_global__System_Collections_Generic_IEnumerable_VpnPacketBuffer_()
{
    Windows_Foundation_Collections_IIterable_1_...EnsureInitialized();
    ...NativeObject.As<IUnknownVftbl>(IEnumerableMethods<VpnPacketBuffer>.IID)   // ★ 抛点
}
```

而 `WinRT.Runtime` 里的 `ABI.System.Collections.Generic.IEnumerableMethods<T>`：

```csharp
static IEnumerableMethods()
{
    PIID = GuidGenerator.CreateIIDUnsafe(typeof(IEnumerable<T>));   // ★ CLR 侧接口的 IID
    ComWrappersSupport.RegisterHelperType(typeof(IEnumerable<T>), typeof(IEnumerable<T>));
}
```

**它 QI 的是 `System.Collections.Generic.IEnumerable<T>` 的 IID，而不是 WinRT 的 `IIterable<T>` GUID
（`FAA585EA-6214-4217-AFDA-7F46DE5869B3`）。**
平台对象只实现后者 ⇒ QI 返回 `E_NOINTERFACE` ⇒ `InvalidCastException`。
⇒ **与 run 36 的实测完全吻合**：`EncapEnumerated=0`、`EncapEnumFirstError=InvalidCastException:Specified cast is not valid.`
—— 平台一直在回调、一直在交包，是我们读不出来。

### 15.4 ★ 投影漏掉了官方文档里的 `First()`

微软 Learn 的 `VpnPacketBufferList` 文档页（winrt-26100）**明确列有**：

```
public IIterator<VpnPacketBuffer> First();
```

Remarks 原文：「You can iterate through a VpnPacketBufferList in C# or Microsoft Visual Basic.
In many cases, such as using **foreach** syntax, the compiler does this casting for you and you won't need to
cast to `IEnumerable<VpnPacketBuffer>` explicitly.」

**但本轮反编译 26100.57 参考程序集，CsWinRT 生成的 `VpnPacketBufferList` 投影全文 207 行里根本没有 `First()`
（`grep First` 零命中）。** 原始接口 `IVpnPacketBufferList` 也没有它
（成员只有 `Size`/`Status`/`Append`/`AddAtBegin`/`RemoveAtEnd`/`RemoveAtBegin`/`Clear`）。

⇒ **官方文档描述的那条路，在 C# 投影里被 CsWinRT 漏掉了。**
这就是档 3 死得如此彻底的第二层原因：文档说的 `foreach` 走的是坏掉的 `GetEnumerator`，
而真正对应 WinRT ABI 的 `First()` 在 C# 侧压根调不到。

### 15.5 官方给的 workaround，以及它为什么正好就是档 4

**CsWinRT #2373 的 workaround 原文**：

> The workaround of using an **integer indexer** as in microsoft-ui-xaml#6454 works.

⇒ **但这条对我们不适用**：`VpnPacketBufferList` 的 ABI 里**没有 `GetAt`、没有索引器**
（官方文档的方法表也只有 `AddAtBegin`/`Append`/`Clear`/`First`/`RemoveAtBegin`/`RemoveAtEnd` + `Size`/`Status`）。

**所以在"这个集合上"官方能给的唯一可行形态，就是"用集合自己的 mutator 取元素"** ——
`RemoveAtBegin()` 摘出来、处理完 `Append()` 塞回去 ⇒ **这正是档 4**。

⇒ **档 4 不只是"我们的选择"，它是官方 workaround 在本集合上唯一可落地的形态。**
（microsoft-ui-xaml #6454 里 ArensT 给的 indexer 版本能work，是因为 `IReadOnlyList<DisplayArea>` 有 `GetAt`；我们这个集合没有。）

### 15.6 查过并排除的第三条路：手写 ABI 层 `First()`

`WinRT.Runtime` 的 `ABI.Windows.Foundation.Collections.IIterableMethods<T>` 在 **AOT 路径下不做 QI，直接调 vtable 第 6 槽**
（`IIterable<T>::First` 的 ABI 位置）；`ABI.System.Collections.Generic.IIteratorMethods<T>` 也有完整的
`MoveNext`/`get_Current`/`get_HasCurrent`/`GetMany`，同样走 vtable。
⇒ **技术上可以手写 QI 到 `IIterable<VpnPacketBuffer>`（GUID `FAA585EA-…`）拿到 `First()`。**

**但这条路解决不了 A1**，理由：A1 的病根**不是"读不到包"，而是"摘出来的 buffer 没还给平台"**。
用 `First()` 迭代同样要摘，摘了还是得还 ⇒ 该还的还得还，A1 的两条后果（偏离契约 + 裸 L3 包被重复发到局域网）一条都不消。

**代价却高得多**：手写 ABI vtable 调用（`UnmanagedCallersOnly` + 函数指针）+ 自己管 `HasCurrent`/`MoveNext`/`Current` 时序
+ AOT 下写错就是 AV。⇒ **收益为负，明确不做。**

### 15.7 结论

| 问题 | 答案 |
| --- | --- |
| 档 3 为什么死 | 平台 API 的 `IEnumerable` QI 失败（官方定性），CsWinRT 投影照抄这条路 |
| 官方有没有更好的解法 | 有 workaround（indexer），但**对本集合不适用**；本集合上官方唯一可行形态 = 档 4 |
| 档 4 是不是"更对" | ✅ 是官方 remarks 那句 "will clean up the remaining IVpnPacketBuffer objects in *packets*" 的直接实现 |
| 档 4 是不是官方认可 | ✅ 官方 workaround 在本集合上的唯一落地形态 |
| 档 4 为什么还没跑 | **没有理由了** —— 实现完整（1.0.34）、判据明确、零改码（一行 `settings.txt`） |

⇒ **本节把"档 4 一直没跑"从"没想清楚"重新定性为"欠债"。**
它现在有三重支持：官方 remarks 的直接实现 + 官方 workaround 的唯一落地形态 + 判据机器可验。
判据仍是那四列（`reapp`≈`encap`、`flush`≡0、`left`/`ret` 不涨、**越过 7188 不停摆**），
其中"越过 7188"是生死线 —— 2026-10-18 那次实测只到 `encap=3230`，**连停摆线的一半都没到**，
要判生死得跑够 18202 那个量级（run 35 用了 497 秒）。
