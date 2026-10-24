# 41 · Partner Center 商店提交材料（逐字段可粘贴）

> 触发：用户「用 uia 操作浏览器进入合作伙伴中心，尝试上传商店，要填的表有点多，我懒得写了，你写下」。
> 本篇就是那些"表"的**成稿**：每个字段一段可直接粘进 Partner Center 的文本，粘之前不用你再想措辞。
> 口径：①凡是**官方规格**（字数上限、图片尺寸、必填项）都带 learn.microsoft.com 出处；
> ②凡是**关于本应用的事实**都带仓内现读出处（文件:行号）或装机一手日志；
> ③**我不能替你决定的事**统一列在 §七，标了【待你定】的字段在拿到答案前不要提交。
> 建文时间：2026-10-22 02:0x。装机版本 1.0.91.0。

---

## 一、先说这次"进浏览器"的实测结果：卡在微软登录，进不去仪表板

不是我没试，是**内置浏览器过不去这道门**，取证如下（全是本批现读）：

1. 导航 `https://partner.microsoft.com/zh-cn/dashboard/home` ⇒ 302 到
   `login.microsoftonline.com/common/oauth2/v2.0/authorize?client_id=fabfbdc4-…`（合作伙伴中心那个租户），
   页面停在标题「登录到您的帐户」+ 一级标题「尝试使你登录」+ 一个 `progressbar "请稍候"`，
   **连续 4 次快照、等了两分多钟都没有换成输入账号密码的表单**。
2. `list_network_requests` 里那一支关键请求都 200 了：`GET https://login.live.com/Me.htm?v=3`（静默 SSO 用来探
   已登录 MSA 的隐藏 iframe，`evaluate_script` 现读到这枚 iframe 是 `hidden: true`），
   但 `document.cookie.length` 只有 **7** —— 也就是 **Entra 那侧根本没有可用会话**，静默 SSO 只能无限等。
3. 直接在地址栏加 `&prompt=login` 强制交互式，**仍然停在同一页**；再导航 `https://login.live.com/` 单独看，
   页面只剩 logo 和页脚，`visibleInputs: []`（**一个可见输入框都没渲染出来**）。
4. `list_console_messages` 全程 0 条 —— 排除了"脚本报错中断"这种能自查的因，指向**内嵌浏览器把跨站存储/
   第三方 Cookie 挡了**，而微软这套 attemptSSO 在被挡时不会降级成表单，就是干等。

⇒ **结论**：这条路由你本人在**已经登录过微软账号的浏览器**里走最省事（`D:\fluentapps` 那边你日常用的 Edge 就有会话）。
要么①你在内置浏览器窗口里手动完成一次登录（登录态会留在内置浏览器的 profile 里，之后我就能一路点下去），
要么②你把 §二～§六 的文本贴进你自己的 Edge 里的 Partner Center 页面，我只管把文本和包准备好。
**我不碰你的账号密码和 2FA**，这条没有商量余地。

顺带一条**好消息**（现读 `ClashUWP/Package.StoreAssociation.xml`，23042 字节，未跟踪文件）：

```
<Publisher>CN=A19C62A9-1AA6-44AB-9F57-2DE6C380D9C9</Publisher>      ← 与 Package.appxmanifest:47 逐字相同
<PublisherDisplayName>云漠星</PublisherDisplayName>
<DeveloperAccountType>MSA</DeveloperAccountType>
<ProductReservedInfo><MainPackageIdentityName>8B8CC4F4.ClashUWP</MainPackageIdentityName>
                     <ReservedNames><ReservedName>ClashUWP</ReservedName></ReservedNames>
<PackageInfoList LandingUrl="https://devcenterapi.dce.mp.microsoft.com/dashboard/Application?appId=9NV6BMCQ21G1" />
```

⇒ **商品早在 Partner Center 保留过了**，产品号 `9NV6BMCQ21G1`，预留身份 `8B8CC4F4.ClashUWP` 与清单 `Identity Name`
（`Package.appxmanifest:45-48`）**逐字相同**。所以 Docs/29 §4-B.2 那条"要不要改身份"的疑问是**已解决**，
这次不用改名、不用重新预留、不用担心身份对不上被拒。同账号下还有 `8B8CC4F4.EasePDF` 等三个包。

---

## 二、受限能力说明（Submission options → Restricted capabilities）★ 唯一的真门

官方口径（Docs/29 §4-A 已定，出处 [App capability declarations](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/app-capability-declarations)）：
上传含 `<rescap:Capability>` 的包时平台会要求逐条说明"怎么用、为什么必须"；`networkingVpnProvider`
在受限能力表里**没有**"多数情况不批"的附加语；侧载不受此限。

我们清单里声明的三条（现读 `Package.appxmanifest:170-189`）：`internetClient`、`internetClientServer`、
`privateNetworkClientServer`、`rescap: networkingVpnProvider`。前两条非受限，要填的是后三条。

**下面这段直接粘（中文页用中文版，英文页用英文版）**：

> 本应用是 Clash/mihomo 协议的用户态客户端，不自带任何节点或加速服务，只连接**用户自己提供**的订阅与服务器。
>
> **networkingVpnProvider** —— 用来通过 Windows 内置 VPN 平台建立隧道，这是本应用的核心功能，也是唯一受支持的实现方式：
> ① 包内实现 `IVpnPlugIn`（`ClashUWP/Vpn/ClashVpnPlugin.cs`），在 `Connect` 回调里用 `VpnChannel.StartWithTrafficFilterAsync`
> 建立会话，由 mihomo 内核在用户态完成报文的封装/解封装（对应 `Encapsulate`/`Decapsulate`）；
> ② 用 `VpnManagementAgent` + `VpnProfile` 在本机创建、更新、删除**属于本应用自己的** VPN profile，
> 用户在界面点"连接/断开"时才调用 `ConnectProfileAsync`/`DisconnectProfile`，不做任何静默拨号；
> ③ 隧道的 DNS 与路由（NRPT、traffic filter）由 Windows VPN 平台按官方契约下发，本应用不自行改写系统路由表、
> 不安装任何内核驱动。没有这条能力，UWP 沙箱内的应用无法创建任何 VPN 会话，本应用只剩一个读不到状态的界面。
>
> **internetClientServer** —— 应用内前台与同包内核之间通过 `127.0.0.1:9090` 的 REST 控制接口和 DNS/代理监听端口通信，
> 需要在本机绑定端口并接受回环连接（`Mihomo/MihomoController.cs:595-596` 的 `NewLoopbackClient` 就是这条回环，
> 且显式 `UseProxy = false` 以免被自己下发的代理劫走）。
>
> **privateNetworkClientServer** —— 订阅规则里允许直连局域网段（DIRECT 规则与用户自定义的分流），
> 因此需要访问本机所在局域网；仅按用户订阅中声明的规则访问，不扫描、不枚举设备。
>
> 用户如何获益：在自己电脑上用系统原生 VPN 入口（通知区盾标、设置→网络和 Internet→VPN）跑自己的 Clash 订阅，
> 断网时由应用内的健康哨兵回退到直连，不需要管理员权限、不需要第三方驱动。
> 若没有这些能力：应用无法建立隧道，也无法显示内核状态，功能整体不成立。

**英文版**（同一页可切语言时备用，逐段对应上面）：

> ClashUWP is a user-mode client for the Clash/mihomo protocol. It ships no servers; it only connects to
> subscriptions and endpoints the user supplies.
>
> **networkingVpnProvider** — required for the app's core feature and is the only supported way to implement it:
> the package implements `IVpnPlugIn` and opens the tunnel with `VpnChannel.StartWithTrafficFilterAsync`; the bundled
> mihomo core performs encapsulation/decapsulation in user mode. The app creates, updates and removes **its own**
> `VpnProfile` objects through `VpnManagementAgent`, and `ConnectProfileAsync` is only invoked when the user presses
> Connect — never silently. DNS and route policies (NRPT, traffic filters) are applied by the Windows VPN platform
> per its documented contract; the app never rewrites the system routing table and installs no kernel driver.
> Without this capability a UWP-sandboxed app cannot create a VPN session at all, and the app would be only a shell.
>
> **internetClientServer** — the foreground UI talks to the mihomo core inside the same package over the loopback
> REST control endpoint (127.0.0.1:9090) plus local DNS/proxy listeners, so the app must bind and accept loopback
> sockets on the local machine.
>
> **privateNetworkClientServer** — subscription rules may legitimately send traffic directly to LAN destinations
> (DIRECT rules and user-defined routing), so local-network access is required. Access is limited to what the user's
> own subscription declares; the app does not scan or enumerate devices.
>
> Customer benefit: run your own Clash subscription through the native Windows VPN entry points (taskbar shield,
> Settings › Network › VPN) with no admin rights and no third-party driver, with a built-in health sentinel that
> falls back to direct connection when the tunnel is unhealthy. Without these capabilities the app does not function.

**别写的话**（Docs/29 §4-A 的口径）：不要出现"接管全局路由 / 修改系统路由表 / 需要管理员驱动"这类表述，
我们实际做的也不是这样。

---

## 三、商店一览元数据（Store listing → Product info / Listings）

官方字数与图片规格（出处见每行末尾）：产品名称最多 256 字符；描述必填、最多 10000 纯文本字符；
"此版本中有哪些新增功能"最多 1500 字符且**首次提交应留空**；产品功能最多 20 项、每项不超过 200 字符且
**不要自带项目符号**；截图至少 1 张、桌面系列最多 10 张、PNG、单张不超过 50 MB、桌面尺寸不小于 1366×768、
支持 4K（3840×2160），每张可配不超过 200 字符说明；搜索项在导入/导出映射里是 `SearchTerms1..7` ⇒ **7 个**。
来源：[添加和编辑 MSIX 应用的应用商店一览信息](https://learn.microsoft.com/zh-cn/windows/apps/publish/publish-your-app/add-and-edit-store-listing-info)、
[为 MSIX 应用添加应用屏幕截图、图像和预告片](https://learn.microsoft.com/zh-cn/windows/apps/publish/publish-your-app/msix/screenshots-and-images)、
[为 MSI/EXE 应用导入和导出应用商店一览](https://learn.microsoft.com/zh-cn/windows/apps/publish/publish-your-app/msi/import-and-export-store-listings)、
[将应用提交到 Microsoft Store（FAQ）](https://learn.microsoft.com/zh-cn/windows/apps/publish/faq/submit-your-app)。

### 3.1 产品名称【待你定，见 §七-5】

建议就填 `ClashUWP`（与预留名、清单 `DisplayName`（`Package.appxmanifest:53`）三者一致，官方 FAQ 也建议
包与一览同名以免混淆）。

风险提示（我不替你决定）：名字里带 `Clash` 是第三方开源项目的名字，商店策略 11.2（内容与知识产权）
要求名称必须是你原创或已获授权 —— [Microsoft Store 策略 7.16.1](https://learn.microsoft.com/zh-cn/windows/apps/publish/store-policy-archive/store-policy-7-16-1)。
真要规避，可以叫 `云漠隧道`/`YunmoTunnel` 之类，描述里再写"兼容 Clash / mihomo 订阅"。

### 3.2 简短描述（Short description）

```
在 Windows 上用系统自带 VPN 平台跑你自己的 Clash/mihomo 订阅：导入订阅链接、按分组挑节点、一键建立系统隧道，界面里能看内核状态并一键诊断。本应用不提供任何节点或加速服务。
```

（约 90 字，远低于常见上限；页面若有更严格的字数提示以提示为准 —— 这条上限本批未在官方页面核到，故不声称。）

### 3.3 描述（必填，最多 10000 字符）★ 直接粘这一整段

```
ClashUWP 是一个运行在 Windows 上的 Clash / mihomo 协议客户端。它把"代理"做成 Windows 原生 VPN 的样子：
用系统自带的 VPN 平台（Windows.Networking.Vpn）建立隧道，所以你在通知区能看到那枚盾标，
在"设置 → 网络和 Internet → VPN"里能看到并管理本应用创建的连接，不需要管理员权限，也不需要安装第三方驱动。

【重要：本应用不提供任何节点、机场或加速服务】
它只是一个播放器，不是内容源。你需要自己持有可用的订阅链接（Clash / mihomo 格式的 YAML 订阅或节点链接）。
没有订阅链接的用户装上应用后什么也连不上，请谨慎下载。本应用不内置、不转发、不出售任何第三方服务器。

【主要功能】
· 订阅管理：粘贴订阅 URL 一键拉取并解析，支持订阅分组、节点重命名显示、切换与删除。
  也支持通过 clash:// 协议链接把订阅地址带进应用（只填 URL，不会静默导入、不会自动连接）。
· 节点与策略组：按订阅自带的 provider/分组结构展示，支持手动选择与自动选择（url-test）。
· 系统 VPN 集成：为每个订阅创建属于应用自己的 VPN profile，用户点击才拨号；断开后不留残留。
· 内核透明化：应用内可见 mihomo 内核的实时状态、连接列表与诊断结果（配置文件、控制端口是否一致等），
  出问题能一眼看到是"没连上"还是"内核没起来"。
· 健康守护：拨号期间持续探测连通性，隧道判定不可信时不改你的系统状态；异常时可回退直连。
· 命令行遥控：配套 CLI 通过应用内 AppService 通道查询会话状态、驱动诊断命令，便于脚本化排障。

【怎么用（第一次）】
1) 打开应用，进入"订阅"页，把你的订阅 URL 粘贴进来并拉取；
2) 拉取成功后为这份订阅"建/重建 profile"（这会在本机创建一个 ClashUWP 的 VPN 连接条目）；
3) 点"连接 VPN"。系统会弹出 VPN 连接确认，同意后隧道建立，通知区出现盾标；
4) 想排查问题就点"诊断内核"，或在"设置 → 网络和 Internet → VPN"里看系统侧的条目。

【数据与隐私】
本应用不收集、不上传任何个人信息，没有账号体系、没有统计埋点、没有广告 SDK。
你的订阅 URL、选中的节点、内核配置与运行日志都只保存在本应用包内的私有目录（LocalState）里，
只有你自己和这个应用能读到。网络请求只会发往你自己订阅里声明的服务器。
卸载应用会连带删除这些本地数据。

【系统要求与已知限制】
· 仅支持 x64 设备（内核的桥接库目前只出了 x64），暂不支持 arm64 设备。
· 需要 Windows 10 版本 1803（10.0.17763）或更高；已在 Windows 11 26100 上实测。
· 应用依赖 WinUI 2.8 与 VCLibs 14.00 框架包，由 Microsoft Store 自动处理。
· 与其他 VPN/代理客户端（例如同时接管默认路由的桌面代理工具）同时运行时可能互相抢占路由；
  建议同一时间只启用一条隧道。
· 这是一个仍在快速迭代的产品，个别边界场景（同时切换多条 VPN 连接等）仍在验证中。

【联系与反馈】
支持渠道：<【待你定：支持 URL 或邮箱】>
隐私政策：<【待你定：隐私政策 URL】>
```

### 3.4 产品功能（≤20 项，每项 ≤200 字符，别加自己的项目符号）

```
导入你自己的 Clash/mihomo 订阅链接
用 Windows 原生 VPN 平台建立隧道，无需管理员权限
不内置任何节点或加速服务
订阅分组与节点策略组选择
应用内查看内核状态与连接列表
一键诊断配置与控制端口一致性
拨号期间的连通性健康守护
订阅地址与日志只存在本机包内
clash:// 协议带入订阅链接
配套命令行工具远程查询与排障
```

### 3.5 "此版本中有哪些新增功能？"

**首次提交留空**（官方：「如果这是您首次提交应用，请将此字段留空」，出处见 §三 开头那份一览页面）。
后续更新才写，上限 1500 字符，届时从 `Docs/` 的批次记录里摘用户能感知的那几条。

### 3.6 分类与搜索项

- 类别：**工具 / Utilities and Tools**（页面若有"效率"或"系统工具"子类目选之；这是 VPN 客户端的常规归属）。
- 搜索项（7 个）：`clash` 、`mihomo` 、`vpn 客户端`、`订阅` 、`代理` 、`wintun 替代` 、`clash for windows`。
  ★ 注意别用 `wintun` 当卖点：本应用走的是系统 VPN 平台，不含 wintun 驱动，这条只作搜索词、
  说明文字里不提；若担心误导审核，可把第 6 项换成 `系统 vpn`。

---

## 四、隐私声明页（Partner Center 的隐私问卷）—— 逐条答案与依据

问卷条目名各版本页面略有差异，**答案本身**按下面这套口径勾，依据都是现读的：

| 问的问题 | 选 | 依据（可复核） |
| --- | --- | --- |
| 是否收集个人信息 / 是否向第三方分享 | **否** | 全仓 `grep` 只有两类 `HttpClient`：拉用户订阅（`Backend/SubscriptionManager.cs:113`）和打本机回环 REST（`Backend/Mihomo/MihomoController.cs:41-42`、`:595-596`、`Vpn/SessionReset.cs:83`、`Vpn/SessionWatchdog.cs:352`、`Vpn/ClashVpnPlugin.cs:2566`/`:2764`），**没有任何 telemetry / Analytics / ApplicationInsights 依赖**（本批现读，无匹配） |
| 是否有账号登录 / 需要邮箱 | **否** | 应用无账号体系 |
| 位置 / 相机 / 麦克风等传感器 | **否** | 清单未声明（`Package.appxmanifest:170-189` 只有网络 + VPN 四类） |
| 数据是否存储在本机 | **是**（仅本机、包内 LocalState） | 订阅与内核配置写 `LocalState`（`Docs/38` 与 `Backend/LocalStatePaths.cs`）；日志在 `LocalState/logs/app.log`、`plugin.log`（记忆条目 Rolling log channel） |
| 是否会传输"个人数据"（订阅 URL、节点名） | **只发往用户自己指定的服务器** | 订阅 URL 由用户输入，请求目标是 URL 自身的宿主；节点流量目标是用户订阅声明的服务器 —— 这条要在隐私政策里写明白，别写成"不联网" |
| 第三方广告 / 应用内购 | **无** | 包内无相关代码与依赖 |
| 数据删除 | **卸载即清除**（包目录随包移除） | MSIX 行为；注意我们侧发流程里"换签名证书=换包家族"那条不影响商店签名形态 |

**隐私政策 URL 是必填项**（策略 10.5.1：产品访问/收集/传输个人信息就必须有隐私政策，提交时要求填 URL；
出处同上那份策略页）。⇒ 见 §七-1，这条我给不了你，必须是你自己的一个公开可访问页面。
好消息：本应用零遥测，隐私政策可以写得很短，我可以在拿到托管位置后代你起草（要覆盖：收集什么、
用途、存储位置与保留期、第三方、用户如何控制与删除、变更通知）。

---

## 五、测试人员备注（Notes for certification testers）

策略 10.3.x 会要"可测试"：需要订阅才能跑通的产品，如果审核员没法验证拨号，第一次提交容易挂在"无法测试"
（Docs/29 §4-C 已记这条）。建议粘这段：

```
请先读这一段，能省很多来回：

1) 本应用是客户端，不自带任何服务器。完整验证"连接成功"需要一个可用的 Clash/mihomo 订阅链接，
   由用户自备。若你们没有测试凭据，请按第 3 步验证"无订阅时的界面与错误处理"，那部分不需要外网。
2) 首次使用：订阅页 → 粘贴订阅 URL → 拉取 → 为该订阅"建/重建 profile" → 点"连接 VPN"。
   系统会弹出 Windows 原生 VPN 连接确认框，同意后才会建隧道。
3) 不依赖网络的验收点：应用能正常打开并在几个页面间切换；诊断页会显示内核配置与 REST 控制端口的状态
   （未连接时显示"✗ REST API 不可达 (127.0.0.1:9090)"是正常的，因为内核未运行，不代表损坏）；
   删除订阅/删除 profile 不应留下系统 VPN 条目。
4) 若你们需要测试凭据以便验证真实拨号，请在反馈中说明，我们提供一个短期可用的测试订阅链接。
5) 已知限制：仅 x64；不支持 arm64；与同时接管默认路由的其他代理客户端并存时可能互相抢占，
   请在干净环境验证。
6) 日志位置（包内）：LocalState/logs/app.log 与 LocalState/logs/plugin.log，两个文件都要（判据行按调用路径分文件）。
```

★ 第 4 条要不要真的给一个测试订阅、给哪一份、给多久 —— 这是【待你定】，见 §七-4；
本仓里现成的那份 `RyjBM4AAk7gx`（星链机场）**是你自己的付费订阅**，我不擅自写进提交材料。

---

## 六、包上传（Packages）这一页要做的事与现状态

- 要传的包：`ClashUWP\AppPackages\ClashUWP_1.0.91.0_x64_Test\ClashUWP_1.0.91.0_x64.msix`
  （51,618,732 字节，`SHA256=30393F20…D0DDA4`，包标识 `8B8CC4F4.ClashUWP_1.0.91.0_x64__4tjvwvefxrz66`）。
  与保留身份一致（§一），架构 `AppxBundlePlatforms=x64`（`ClashUWP.csproj:115`）。

- ★ **签名这一条已经核掉了**（先前记为"未验"，现已现读官方页面订正）：
  [App package requirements for MSIX app](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements)
  的 "Code signing for Microsoft Store submissions" 一节原文说了四件事：
  ①提交给商店的 MSIX/AppX **不需要**用 CA 根签书的证书；②商店在认证通过后**自动用微软证书重新签名**；
  ③不需要为提交提供 .pfx 或 .cer；④"the Store replaces any existing signature"。
  ⇒ **我们现在这份用 `FB3309A6…` 重签过的侧发包可以直接传**，`ClashUWP.csproj:98` 的
  `AppxPackageSigningEnabled=true` 与侧发签名形态**不用为了提交而改**；Docs/29 §4-B.3 那条待办
  在这里被官方页面**关掉了**（它只在"用 VS 的商店发布配置出包"那个语境里还有意义）。
  注意同一节的对立面：只有提交 **MSI/EXE 安装包**时才要求开发者自己 Authenticode 签 —— 我们不是这种形态。

- ★ 官方更建议传 **`.msixupload`** 而不是裸 `.msix`（同一页："For UWP apps, we recommend that you always upload the
  .msixupload or .appxupload file in the Packages page"）。`…_Test\` 目录里那份 87,284 字节的
  `ClashUWP_1.0.91.0_x64.appxsym`（压缩后的公开符号）就是 `.msixupload` 会带的东西 —— 不带的话
  Partner Center 的崩溃分析（Health reporting）没有符号可看。**做 upload 包这一步要走 VS / uwp-bridge 的
  PublishAppxPackage 通道（AGENTS.md 口径），不自制脚本。**

- ★ 顺带确认的两条硬规则（同一页）：①UWP 包版本号的**第四段必须为 0**（商店会自己改），
  我们 `1.0.91.0` 合规；②`.msix` 单包上限 25 GB、Block map 用 SHA-256，都无风险；
  ③清单里必须带上与开发者账号绑定的身份信息 —— 这正是 §一 那份 `Package.StoreAssociation.xml` 的作用，
  页面原话是"没有这些自动补进清单的信息，**可能会遇到包上传失败**"（所以我们这份关联文件别删，
  它现在是 .gitignore 里的未跟踪文件，重装/换机要记得它必须存在）。

- 截图：**一张都还没有**。包里的 `Assets\StoreLogo.png` 是 50×50（现读），一览要的是 ≥1366×768 的桌面截图，
  两者不是一回事。我可以下一步用 UIA + 窗口截图出 4 张 16:9（1920×1080）再按页面要求裁 —— 这需要应用在前台跑，
  不碰网络，安全。

- 语言：一览至少要有一种商店支持的语言（同一页的支持语言表里有 `zh-Hans`/`zh-cn` 与 `en-*`）；
  本次先只建 **中文（简体）** 一览，英文版等 §三 的文案翻好再补（多语言一览每套都要单独传截图）。

- 提交前本地跑一遍 WACK / `Windows App Certification Kit`（Docs/29 §4-C 的既有口径；
  同一页原文也把它列为"build 之前先测"）。这一步没做，且它会暴露 AOT/原生 dll 载荷问题；
  工具在你机器上是 VS 18 Community 带的，走 VS 通道跑。

- **我不会替你点"提交认证"**。填到可提交状态后停下来给你过目 —— 提交是会进入外部审核流程的动作。

---

## 七、【待你定】这 5 件事不解决就只能停在草稿

1. **隐私政策 URL**（必填）。你有现成页面吗？没有的话我按 §四 的事实代写一份，你放到任意可公开访问的地方
   （GitHub Pages / 你的博客）都行。
2. **支持 URL 或支持邮箱**（一览里的"联系人信息"）。§三-3.3 结尾留了占位。
3. **定价与可用市场**：默认建议 **免费**、市场全选或只选中国大陆。你定。
4. **是否给审核员提供测试订阅**：给的话给哪一条、有效期多久；不给就要接受 §五-3 那种"只验界面"的路径，
   第一次提交可能因此被退回。
5. **商店名是否保留 `ClashUWP`**：见 §3.1 的知识产权风险提示。

---

## 八、这一篇的可复核性

- 登录卡点：§一 表 1–4 条，全部是本批浏览器工具的实际返回（快照、网络列表、`evaluate_script` 结果）。
- 商店已保留：`ClashUWP/Package.StoreAssociation.xml` 首行区与末段（§一 引用），
  与 `Package.appxmanifest:45-48`、`:53-54` 对得上。
- 零遥测：本批在 `ClashUWP/**` 全量 grep `telemetry|Analytics|TrackManager|ApplicationInsights|WebSocket` 的
  实际命中清单（§四 表里列到具体行号），无匹配即无该依赖。
- 官方规格数字：§三 开头那段的四条 learn 链接 + §六 那条 [App package requirements for MSIX app](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements)（含"商店会自动重签、不需要你提供 pfx"原文），均可点开核对。
- 没做/没验的：**§二 那段受限能力文案是我按代码现读写的，没有经过任何微软渠道预沟通，批不批不在我手里**；
  WACK 未跑；截图未生成；Partner Center 页面本身的字段名与顺序**没有一手截图**（登录没过去，§一），
  拿到会话后要先照页面实际字段核一遍再粘，页面若与本篇不一致**以页面为准**并在本篇订正。

## 九、隐藏次级入口与 HeadlessAppBypass 豁免工单文本（2026-10-23 增）

**为什么会多出这一节**：1.0.106 的臂 C 把 `vpnClient` 后台任务挂到了第二个 `<Application Id="VpnHost">` 上，
那个入口用 `AppListEntry="none"` 隐藏（修"关窗 2 s 断流"，见 Docs/53 §五）。本地打包/注册已证不拦，
但**商店包验收这一维会拦**：两家第三方一手写明"次级入口带 `AppListEntry="none"` 需要账号级 `HeadlessAppBypass`
豁免，否则 Store package validation rejects the submission"（原文与取法在 Docs/47 §四 T16/T17）。
该豁免**不是清单里可声明的能力**，是打在开发者账号上的标记 ⇒ 只能开支持工单问微软。

正在审的 `ClashUWP_1.0.91.0_x64` 那份是单入口、一个 `none` 都没有 ⇒ **本节对当前这次认证不影响**，
只在换成臂 C 的包之前必须二选一（取舍表在 Docs/53 §三 末）。

### 9.1 提交前自查（30 秒，别浪费一次工单往返）

先确认账号是否**已经**有豁免：随便打一个带 `AppListEntry="none"` 次级入口的包（我们现成的 1.0.106 就是），
走 Partner Center 的包验证。**通过 = 已有豁免，本节作废**；报"headless / 无权创建无头应用"字样 = 提交 9.2 的工单。
（我这边 0 样本，不知道我们账号有没有；这一步只能在你已登录的会话里做。）

### 9.2 工单正文（英文，直接粘）

> Subject: Request for the account-level HeadlessAppBypass waiver (hidden secondary Application entry, VPN plugin host)
>
> Product: ClashUWP (appId 9NV6BMCQ21G1). Package identity: `8B8CC4F4.ClashUWP`, planned submission `1.0.106.0`, x64, MSIX.
>
> We need to keep a **secondary** `<Application>` entry in our package manifest hidden from the Start menu via
> `AppListEntry="none"`. The entry is not a user-launchable application: it is the activation host for the platform's
> `windows.backgroundTasks` / `<uap:Task Type="vpnClient" />` extensibility point, i.e. our VPN protocol plugin. It has
> no UI and cannot be launched by a user, so a Start-menu entry for it would only be a dead tile.
>
> This is exactly the shape of Microsoft's own UWP VPN plugin sample (`microsoft/UwpVpnPluginSample`,
> `CSharp/TestVpnPluginApp/Package.appxmanifest`), whose second `<Application Id="Plugin">` also carries
> `AppListEntry="none"` and hosts the `vpnClient` task, with the comment "vital to keep them running in two separate
> processes, so if one is closed, it won't terminate the other". We are not trying to ship a headless product:
>
> - our **primary** entry `<Application Id="App">` is visible in the Start menu, launches normally, and carries the whole
>   user experience. Reviewers can launch it, and the product is fully testable through it (Store policies 10.3, 10.4.2, 10.4.3 are satisfied by the primary entry).
> - separating the plugin host into its own Application entry is required for correct behaviour, not cosmetics: with the
>   task hosted on the primary entry, closing the app window tears down the VPN tunnel for about 2 seconds.
>
> Request: grant the account-level HeadlessAppBypass waiver for this developer account so that package acceptance
> validation accepts the hidden secondary entry. If the waiver is not available for our account type, please tell us the
> supported alternative; our fallback would be to remove `AppListEntry` and ship a Start-menu item named
> "ClashUWP VPN 插件宿主" that does nothing when clicked, which we believe is worse for users.
>
> Relevant capability: `networkingVpnProvider` (VPN plugin) — its submission-time justification is handled separately.

### 9.3 工单正文（中文对照，若选中文工单）

主题：为开发者账号申请 HeadlessAppBypass 豁免（清单中次级 `<Application>` 入口需以 `AppListEntry="none"` 隐藏）
产品：ClashUWP，appId `9NV6BMCQ21G1`，包身份 `8B8CC4F4.ClashUWP`，拟提交 `1.0.106.0` x64 MSIX。
被隐藏的入口不是可启动应用，而是平台 `windows.backgroundTasks` / `vpnClient` 扩展点的激活宿主（我们的 VPN 协议插件），
无 UI、用户点击无意义。该形状与微软官方 `UwpVpnPluginSample` 的清单逐字一致（sample 的第二个 `<Application Id="Plugin">`
同样 `AppListEntry="none"` 并承载 `vpnClient` 任务）。主入口 `App` 可见、可正常启动、承载全部用户体验，
策略 10.3 / 10.4.2 / 10.4.3 的可测可启动要求由主入口满足。请求：为本账号授予该豁免；
若本账号类型不可得，请告知受支持的做法 —— 我们的退路是删掉 `AppListEntry`、发一条点击无反应的
"ClashUWP VPN 插件宿主"开始菜单项，这对用户是更差的结果。

### 9.4 本篇这一节的可复核性

- T16/T17 原文与取法逐字记在 Docs/47 §四；官方 sample 清单原文取法记在 Docs/53 §五.①。
- **没有一手的部分**（照 §八 的老规矩明写）：工单该选哪个类目、多久答复、个人开发者能否拿到 —— 我一次都没走过，
  进到 Help + support 之后**以页面实际类目为准**，别照抄任何别人猜的类目名。
- 9.1 那步没做（要你的登录会话），所以本节既不是"必须提交"也不是"不必提交"，是一张等你按下按钮的表。
