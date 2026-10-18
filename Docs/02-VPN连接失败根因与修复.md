# VPN 连接失败根因与修复

> 结论：`ClashUWP-Probe` profile 的 `ConnectProfileAsync` 已从 `ServerConnection` 变为 `Ok`，
> 平台状态机已走通 `Connecting → Negotiating Network → Connected`，`VpnChannel.Start` 成功建出 L3 网卡，
> 且 `Encapsulate` 已收到真实 IP 包。

从「能建 profile」到「能连上」，中间趟过三个彼此独立的卡点。下面按时间顺序各说根因与修复。

---

## 卡点 1 · CannotFindProfile（profile 建了但平台连不上）

**现象**：`AddProfileFromObjectAsync` 返回 `Ok`、`GetProfilesAsync` 也能回读到该 profile，
但 `ConnectProfileAsync` 一律返回 `CannotFindProfile`，回读状态恒为 `Disconnected`。

**排除过程（单变量，均不成立）**：
- `ServerUris` 用回环地址 vs 真实 IP —— 无关
- `RequireVpnClientAppUI` 开关 —— 无关
- 连接前加延迟 —— 无关
- 平台日志 `Windows Networking Vpn Plugin Platform/Operational` 无任何事件 —— 说明失败发生在**插件激活之前**的 profile 查找层

**根因**：通过 VS 调试的 **loose 注册**（`Add-AppxPackage -Register`，`IsDevelopmentMode=True`）部署的包，
平台不会把它的 VPN profile 同步进 `rasphone.pbk` / 系统 VPN 索引。profile 只存在于我们自己容器的
`pbk` 里，平台的 `VpnClient` 层根本找不到它。

**修复**：改用 Visual Studio 发布的**签名正式 MSIX 真装**。判据：
- `IsDevelopmentMode = False`
- `SignatureKind = Developer`
- `InstallLocation = C:\Program Files\WindowsApps\...`

真装后平台首次拉起了 VPN 插件宿主进程（svchost 承载），`WinRT` API 写入的 profile 才真正落到平台侧。

---

## 卡点 2 · 0x8007007E（插件后台任务激活失败，模块未找到）

**现象**：解决了 CannotFindProfile 后，撞上 `0x8007007E`（模块未找到），插件宿主无法激活
`vpnClient` 后台任务，状态机在 15 秒后被 `Abort`。

**根因**：`PublishAot` 配置错位 —— 前台 App 开 AOT，把托管组件 `ClashUWP.Vpn.dll` 静态链接进主 EXE 丢失；
组件工程自己没开 AOT，仍依赖 `WinRT.Host` 宿主加载。两边形态不一致，`inProcessServer` 的
`DllGetActivationFactory` 对不上。

**修复**（CsWinRT 2.3.1 原生 AOT 导出模式）：
- 组件工程 `ClashUWP.Vpn` 开启 `PublishAot=true` + `NativeLib=Shared`，自身导出 `DllGetActivationFactory`
- 主工程开 `CsWinRTMergeReferencedActivationFactories=true`，把激活工厂合并进主模块
- 主工程用 **PackageReference**（本地 feed `artifacts/packages`）消费组件的 IL dll（不能 ProjectReference，
  否则主工程拿不到组件 IL，报 NETSDK1130）

---

## 卡点 3 · VpnNamespaceInfo 已废弃 → NullReferenceException → Connecting→Abort

**现象**：前两个卡点解决后，插件已被激活，但状态机仍是 `Connecting → Abort`（同一秒内，非 15 秒超时）。
`OperationalVerbose` 的 Id 2004 日志序列停在：

```
进入 Connect → Channel Id ... → mtu=1400 ... → transport 已连接 →（断，无后续）
```

既没有「`VpnChannel.Start` 完成」，也没有「`Start` 异常」，也没有应用崩溃事件。

**根因**：插件的 `Connect` 是被 `VpnChannel.ProcessEventAsync` 以 **fire-and-forget** 方式调用的，
`Connect` 内抛出的异常会被平台吞掉——所以「停在 transport 之后、无 Start 日志」意味着 `Connect`
在 `Start` 之前就抛了异常，而这个异常点正是 `BuildRoutes()` / `BuildNamespaces()`。

用 `LogDiagnosticMessage`（插件**唯一**不受吞异常影响的日志出口）给两个方法各包一层 try-catch 落日志，
定位到：

```
BuildNamespaces 异常: System.NullReferenceException: Object reference not set to an instance of an object.
   at ClashUWP.Vpn.ClashVpnPlugin.BuildNamespaces()
   at ClashUWP.Vpn.ClashVpnPlugin.Connect(VpnChannel)
```

真凶是 `new VpnNamespaceInfo(".", dnsServers, new List<HostName>())`。查官方文档确认：
[`VpnNamespaceInfo`](https://learn.microsoft.com/en-us/uwp/api/windows.networking.vpn.vpnnamespaceinfo)
已标记 **"Not supported"**，其构造函数 `VpnNamespaceInfo(String, IVector<HostName>, IVector<HostName>)`
同样 **"Not supported"**（建议改用 `VpnDomainNameInfo`）。在本项目 AOT 形态下该构造直接抛 NRE。

> 说明：此前假设「transport 未 connect 导致 Start 失败」是错的——transport 的 `ConnectAsync` 从来都成功
> （日志里的「transport 已连接」每个用例都出现），真正卡死的是被 `Start` 之前的 `BuildNamespaces` 抛异常。

**修复**：`BuildNamespaces()` 改返回**空的 `VpnNamespaceAssignment`**。依据有二：
1. `Start` 的 `namespaceScope` 参数类型本就是 `VpnNamespaceAssignment`（非废弃），空列表合法；
2. 官方 sample 里 `Config.NamespaceAssignment` 的默认值就是 `new VpnNamespaceAssignment()`。

骨架期数据面在进程内、不需要 NRPT/DNS，空 assignment 足够；接真 DNS 时改用 `VpnDomainNameInfo`。

**修复后结果**：`VpnChannel.Start` 成功，状态机 `Connecting → Negotiating Network → Connected`，`Encapsulate` 收到 4 个包。

---

## 诊断方法论（沉淀到后续开发）

- **插件侧唯一日志出口**：`VpnChannel.LogDiagnosticMessage`，落到
  `Windows Networking Vpn Plugin Platform/OperationalVerbose` 的 Id 2004（`Message from VPN App`）。
  `Debug.WriteLine`、`Console` 在插件宿主进程里都不可见 / 会被 fire-and-forget 吞掉。
- **状态机**：`Windows Networking Vpn Plugin Platform/Operational`，Id 2020 `Connecting` / 2025
  `Negotiating Network` / 2021 `Connected` / 2022 `Disconnecting` / 2023 `Disconnected` / 2024 `Abort`。
- **`Connect` 是 fire-and-forget**：任何想在连接阶段暴露的失败，都必须显式 `LogDiagnosticMessage`
  或落文件，否则表现为「无声地走到 Disconnect」。
- **单变量原则**：一次只改一个条件，且以平台日志 / `Get-WinEvent` 为准，不以外侧 profile 回读为准
  （`Get-VpnConnection` 对 UWP 插件 profile 恒返回 0，不能作为存在判据）。
- **遗留诊断代码**：`VpnProfileSetup.ProbeConnectAsync` 里的 `[delayed]` 用例（人为加 3000ms 延迟）
  是为旧 CannotFindProfile 问题设计的，现已过时。实测它反而会触发 `0x800703E3`（I/O 已中止），
  属诊断噪声，接真内核前应移除。