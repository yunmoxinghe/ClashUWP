# 01 · 官方 VPN Plugin（C# 版）调用链路 —— 逐步注释（官方原文 + 示例行为对照）

> ⚠️ **"谁新以谁为准"这句话不够用（本文 §0.5 有实测证据与撤回说明）。**
> 时间上确实是**文档更新**：VpnChannel 系列网页最后发布 **2026-09-30**，示例仓库 2022-03 冻结。
> 但两者不是同一种东西——文档是 SDK 元数据自动生成的**契约声明**，示例是**被接受的行为**。
> 本文的做法是：**【官方】= 引用 Microsoft Learn 原文作为注释**，【sample】= 记录示例实际行为，
> 冲突时按 §0.5 的五档判据分别处理。

> 源码基准：`microsoft/UwpVpnPluginSample`，`CSharp/` 分支，本地副本在
> `D:/fluentapps/repos/UwpVpnPluginSample`（`--depth 1` 克隆，首次公开提交 2021-10-22）。
> 本文是**正向链路梳理 + 官方文档逐条注释**；审计视角（违规判定）见
> `D:/fluentapps/repos/clashUWP研究/docs/65-官方Sample-API调用链与违规调用审计.md`，两者互补、不重复。
>
> 标注约定：**【官方】**= Microsoft Learn 原文引用（附出处）；**【sample】**= 官方示例实际行为；
> **【推断】**= 本文作者的推论，未经单变量实验，不作为结论。

---

## 0. 一句话结论

官方 C# 版链路只有一条主线：

```
前台 app 建 VpnPlugInProfile
   → 用户/系统调用 ConnectProfileAsync
   → 平台按 VpnPluginPackageFamilyName 拉起「vpnClient」后台任务
   → VpnBackgroundTask.Run() 拿 deferral、复用同一个 IVpnPlugIn 实例
   → VpnChannel.ProcessEventAsync(plugin, triggerDetails)   ← 控制权交给平台
   → 平台回调 Connect / Encapsulate / Decapsulate / GetKeepAlivePayload / Disconnect
```

`VpnChannel.Start()` 之后，平台就创建了一块 **L3 网络接口**——对 ClashUWP 而言，
**VpnChannel 就是 TUN**，出站包从 `Encapsulate` 来，入站包用
`RequestVpnPacketBuffer(Receive)` + `AppendVpnReceivePacketBuffer` + `Flush` 投回去。

---

## 0.5 · 冲突时谁说了算：文档 vs 示例（附本次实测证据）

先给结论：**不能一概"以文档为准"。按冲突类型分五档**，下面每档都有可复核的依据。

### 实测到的时间线（2026-10-11 查）

| 对象 | 时间点 | 证据 |
|---|---|---|
| 示例仓库最后一次提交 | **2022-03-08**（`d589fe0`，"Removed original boilerplate"） | 本地 `UwpVpnPluginSample` clone，`git log -1` |
| 示例面向的平台 | **VS2019+ / Windows 11** | README §Prerequisites 原文 |
| **网页文档最后发布**（VpnChannel / VpnManagementAgent / `Start` / `ProcessEventAsync` / `SetErrorMessage`） | **2026-09-30T20:23:00Z** | 页面 HTML `<meta name="updated_at">` |
| 网页文档上一次发布批次（VpnPlugInProfile / IVpnPlugIn / VpnPacketBuffer / VpnRouteAssignment / VpnTrafficFilter … 以及大量无关页面） | **2025-11-21T14:18:00Z** | 同上 |
| 文档面向的 SDK | 到 **WinRT Build 28000** | 页面 "Applies to" 段 |

#### 网页文档的真实源头（重要，我上一轮查错了仓库）

Learn 页面 HTML 里有两个 meta 直接给出出处，**不是** GitHub 上的 `MicrosoftDocs/winrt-api`：

```
<meta name="original_content_git_url" content="https://cpubwin.visualstudio.com/DefaultCollection/
      windows-uwp/_git/winrt-api-build?path=/winrt-api-build/xml/Windows.Networking.Vpn/VpnChannel.xml">
<meta name="gitcommit" content="...winrt-api-build/commit/2cf6373c0e25acf59939123db50f2f75ae342c3e?...">
<meta name="updated_at" content="2026-09-30T20:23:00Z">
<meta name="ms.date"     content="2017-02-08T00:00:00Z">   ← 陈旧 front-matter，不反映内容更新
```

- 源头是 **Azure DevOps 内部仓库 `winrt-api-build` 的 `xml/Windows.Networking.Vpn/VpnChannel.xml`**，
  即**由 SDK 元数据自动生成的 XML**，不是人工撰写的 markdown；
- `updated_at` 就是页面底部"最后更新"的来源；`ms.date` 是 2017 年的 front-matter，**别拿它当更新时间**；
- 同仓库不同提交印证了两个发布批次：`VpnChannel` 与 `VpnManagementAgent` 同为 `2cf6373c`（2026-09-30 批），
  `VpnPlugInProfile` 为 `3abc0b43`（2025-11-21 批）——说明 **2026-09-30 那次是定向重发，不是整站重发**
  （对照组：`StorageFile` / `Button` / `StreamSocket` / `NetworkInformation` 全部停在 2025-11-21）。

#### 两条必须说清的事实（否则会误判）

1. **我上一轮说"文档最后改动 2021-11-19、比示例还老"——这个结论作废。**
   那是 GitHub `MicrosoftDocs/winrt-api` 仓库 md 的提交日期，而**该仓库并非 Learn 页面的数据源**。
   页面真正的数据源（`winrt-api-build` XML）最近一次发布是 **2026-09-30，比 2022-03 冻结的示例新约 4 年半**。
   → 就"新旧"而言：**文档远比示例新**。
2. **但"Not supported" 标记是哪一年打上去的，仍无法从公开渠道证实**（Azure DevOps 仓库不公开）。
   已复核的是：在 2026-09-30 发布的当前页面上，`SetErrorMessage(String)` 的 *Not supported* **仍然在**
   （双参重载标注 *Windows 11 24H2, introduced in 10.0.26100.0*）。即：**最新的文档仍然在说它不支持**。

### 五档判据

| 档 | 冲突类型 | 以谁为准 | 理由 |
|---|---|---|---|
| **1** | 文档标 `Not supported` / `not implemented` / `may be altered or unavailable` | **文档** | 平台对契约的否定声明。示例能跑 ≠ 被支持；示例已冻结 4 年，没有跟随平台演进 |
| **2** | 数值上限 / 硬约束（mtu ≤1400、maxFrame ≤1500、mtu<maxFrame） | **文档** | 示例违反但能跑 = 平台当前容忍，不是契约。按文档写永远安全 |
| **3** | 文档说"可以"，示例没用（`GetVpnReceivePacketBuffer` 的 *injection*） | **文档** | 示例没用到 ≠ 不允许 |
| **4** | 文档没写，示例在做（后台线程调 `channel`；不填出参改用 `Append+Flush`） | **示例（但要自担风险）** | 唯一示例更权威的区间：它反映平台真实行为。**必须自己补 A/B 实测**（76 号文档做的就是这件事），当作 undocumented 行为对待 |
| **5** | 版本/能力类事实（API 从哪个 build 起、需不需要 `networkingVpnProvider`） | **文档** | 有 Version history 与 Applies to 表可查；示例的 `MinVersion` 只是它自己的选择 |

### 文档本身也有模糊区（别把文档当圣经）

- `SetErrorMessage` 页面把两个重载挤在同一行，一个标 *Not supported*，另一个（双参）标 24H2 引入；
- `VpnChannelActivityEventArgs` 标"不支持、请改用 `VpnChannelActivityStateChangedArgs`"，
  但 `ProcessEventAsync` 的第二个参数官方只说 "opaque blob"，**没说它实际是什么类型**；
- 示例里 `ApiInformation.IsMethodPresent("...VpnChannel", "ActivateForeground", 2)` 这种
  **带参数个数的运行时探测**，恰恰是文档对重载描述不可靠时的兜底手段——值得照抄这个习惯。

→ 一句话：**契约问题听文档，行为问题看示例 + 自己实测；两者都别当最终判据，单变量 A/B 才是。**

---

## 1. 源码坐标（C# 版共 12 个文件，真正的链路只占 4 个）

| 文件 | 行数 | 职责 |
|---|---|---|
| `CSharp/TestVpnPluginAppBg/TestVpnPluginAppBgTask.cs` | 107 | **后台任务入口**，平台每次调度都从这里进 |
| `CSharp/TestVpnPluginAppBg/VpnPlugin.cs` | 2380 | **IVpnPlugIn 实现**（Connect/Disconnect/Encap/Decap/KeepAlive）+ 解包状态机 |
| `CSharp/TestVpnPluginAppBg/CustomConfiguration.cs` | 1704 | 解析 profile 里那段 `<pluginschema>` 自定义 XML（**非官方格式，sample 自己定的**） |
| `CSharp/TestVpnPluginAppBg/PacketUtils.cs` / `Logger.cs` | 309 / 65 | IP 头解析、日志（`LogDiagnosticMessage`） |
| `CSharp/TestVpnPluginApp/MainPage.xaml.cs` | 541 | 前台：建/删 profile、连接、断开 |
| `CSharp/TestVpnPluginApp/App.xaml.cs` | 185 | 前台：`OnActivated` 接 `VpnForegroundActivatedEventArgs`（Web 认证回流） |
| `CSharp/TestVpnPluginApp/Package.appxmanifest` | — | 后台任务声明 + `networkingVpnProvider` 受限能力 |

---

## 2. 全景链路表（S0–S7）

| 阶段 | 触发方 | 代码位置 | 关键 API |
|---|---|---|---|
| **S0** 安装期 | 部署时平台读取 manifest | `Package.appxmanifest` | `windows.backgroundTasks` / `Task Type="vpnClient"`、`rescap:Capability networkingVpnProvider` |
| **S1** 建 profile | 前台 app 按钮 | `MainPage.CreateAppProfile()` | `VpnPlugInProfile` + `VpnManagementAgent.AddProfileFromObjectAsync` |
| **S2** 发起连接 | 用户 / 系统 UI | `MainPage.ConnectOnClick()` | `VpnManagementAgent.ConnectProfileAsync` |
| **S3** 后台任务入口 | VPN 平台 | `TestVpnPluginAppBgTask.Run()` | `GetDeferral()` → `VpnChannel.ProcessEventAsync` |
| **S4** 建隧道 | 平台回调 | `VpnPlugin.Connect()` | `AssociateTransport` → `RequestCredentials` / `ActivateForeground` → 连服务器 → `Start`/`StartWithTrafficFilter` |
| **S5** 数据面 | 平台回调 | `Encapsulate` / `Decapsulate` / `GetKeepAlivePayload` | `Append*PacketBuffer` + `Flush*PacketBuffers` |
| **S6** 前台认证回流 | `ActivateForeground` | `App.OnActivated()` | `VpnForegroundActivatedEventArgs` → `ActivationOperation.Complete(valueSet)` |
| **S7** 断开 | 用户 / 平台 | `VpnPlugin.Disconnect()` | `VpnChannel.Stop()` |

---

## 3. S0 · 安装期：manifest 决定平台能不能拉起你（零行代码，但最致命）

官方 sample 的清单原文（`CSharp/TestVpnPluginApp/Package.appxmanifest`）：

```xml
<!-- 1) 前台 app 与后台插件拆成两个 Application：官方注释明说这是为了
        "keep them running in two separate processes, so if one is closed,
         it won't terminate the other" -->
<Application Id="App"    Executable="TestVpnPluginApp.exe" EntryPoint="TestVpnPluginApp.App" />
<Application Id="Plugin">                       <!-- AppListEntry="none"，不出现在开始菜单 -->
  <Extensions>
    <Extension Category="windows.backgroundTasks"
               Executable="TestVpnPluginApp.exe"
               EntryPoint="TestVpnPluginAppBg.VpnBackgroundTask">
      <BackgroundTasks>
        <uap:Task Type="vpnClient" />           <!-- ★ 只有这个 TaskType 会被 VPN 平台拉起 -->
      </BackgroundTasks>
    </Extension>
  </Extensions>
</Application>

<!-- 2) CLR 宿主：C# 插件靠它激活，C++ 版没有这一节 -->
<Extension Category="windows.activatableClass.inProcessServer">
  <InProcessServer>
    <Path>CLRHost.dll</Path>
    <ActivatableClass ActivatableClassId="TestVpnPluginAppBg.VpnBackgroundTask" ThreadingModel="both" />
  </InProcessServer>
</Extension>

<Capabilities>
  <Capability Name="internetClientServer" />
  <Capability Name="privateNetworkClientServer" />
  <uap:Capability Name="sharedUserCertificates" />
  <rescap:Capability Name="networkingVpnProvider" />   <!-- ★ 受限能力 -->
</Capabilities>
```

逐步注释：

| 节点 | 行为 | 依据 |
|---|---|---|
| `<uap:Task Type="vpnClient" />` | 声明本包提供 VPN 客户端插件；安装后**由 VPN 平台自动注册**该后台任务（不需要自己 `BackgroundTaskBuilder.Register`） | sample 源码注释：*"this background task will automatically be registered by the VPN platform"* |
| 两个 `Application` 拆进程 | 前台 UI 关掉不会带走插件宿主进程 | sample 清单内注释（同上） |
| `windows.activatableClass.inProcessServer` + `CLRHost.dll` | C# 插件的 WinRT 激活入口。**这是 C# 版与 CppWinRT 版最大的结构差异** | 本仓库 61 号文档 §9 已就"C++ 版 manifest 没有 CLR 宿主"做过对照 |
| `rescap:Capability networkingVpnProvider` | 使用 `Windows.Networking.Vpn` 全部 API 的前提 | 【官方】`VpnChannel` / `IVpnPlugIn` / `VpnManagementAgent` / `VpnPlugInProfile` 三个类的 Windows requirements 均列 *App capabilities: networkingVpnProvider* |
| `MinVersion 10.0.21329.0` | sample 声明的最低平台版本 | sample 清单（注意：这是 sample 的选择，不是 API 的最低要求——`IVpnPlugIn` 官方标 *Windows 10, introduced in 10.0.10240.0*） |

> **`networkingVpnProvider` 是受限能力（restricted capability）。**
> sample README 原文：*"The VPN apps in Windows are curated: VPN apps will need the
> `networkingVpnProvider` restricted capability."* —— 上架需要微软审批，这是 ClashUWP 分发路径的硬前提。

---

## 4. S1 · 前台建 profile（`MainPage.CreateAppProfile()`）

```csharp
VpnPlugInProfile profile = new VpnPlugInProfile();
profile.RequireVpnClientAppUI      = true;                    // ①
profile.ProfileName                = PROFILE_NAME;            // "Test VPN Plugin Autoprofile"
profile.VpnPluginPackageFamilyName = packageName;             // ② Package.Current.Id.FamilyName
profile.CustomConfiguration        = "<pluginschema>...</pluginschema>";  // ③
profile.ServerUris.Add(new Uri("http://10.137.192.135"));     // ④
var status = await agent.AddProfileFromObjectAsync(profile);  // ⑤
```

| # | API / 属性 | 【官方】原文 | 注释 |
|---|---|---|---|
| ① | `RequireVpnClientAppUI` | *"Gets or sets a value that determines whether this VPN profile appears in the system's VPN connection UI."* | `true` = 该 profile 出现在系统 VPN UI 里（1607/14393 引入）。**不加就只能在自己 app 里连** |
| ② | `VpnPluginPackageFamilyName` | *"Gets or sets the package family name of the VPN plug-in to be used for this VPN plug-in profile. **This field is required for VpnPlugInProfile to be valid.**"* | ★ 官方明文：必填。它决定 S2 之后平台去拉哪个包里的 `vpnClient` 任务。`Package.Current.Id.FamilyName` 拿到的就是本包 PFN |
| ③ | `CustomConfiguration` | *"Gets or sets an XML string to configure the VPN plug-in. **Refer to the documentation from the VPN plug-in for format and support.**"* | ★ 官方只说"是段 XML"，格式**完全由插件作者自定义**。sample 的 `<pluginschema>`（`<port>` `<ipAddress>` `<transport>` `<loglevel>` `<routes>`…）是自己造的，由 `CustomConfiguration.cs`（1704 行）解析。**ClashUWP 可以自己定义 schema 传 mihomo 配置** |
| ④ | `ServerUris` | *"Gets a list of server URIs to connect to for the VPN profile."* | 注意：sample 的 `Connect()` 里遍历的是 `config.cfg.ServerHostNameList`（来自 `VpnChannel.Configuration`），不是这个 `ServerUris` |
| ⑤ | `AddProfileFromObjectAsync(IVpnProfile)` | *"Creates a new VPN Profile based on the **VpnProfile** input object."* | 返回 `VpnManagementErrorStatus`，`Ok` 才算成功。另有 `AddProfileFromXmlAsync`（走 ProfileXML，MDM 场景） |
| — | `DeleteProfileAsync` / `DisconnectProfileAsync` | *"Deletes an existing VPN profile."* / *"Disconnects from a connected VPN profile."* | sample 建 profile 前先 `DeleteExistingProfile()`，避免重名堆积 |

相关：`VpnManagementAgent` 类说明 —— *"Allows the creation, deletion, edit, connect, and disconnect
flows for VPN…"*（出处：`.../vpnmanagementagent`）。

---

## 5. S2 · 发起连接（`ConnectProfileAsync`）

```csharp
if (pluginProfile.ConnectionStatus == VpnManagementConnectionStatus.Disconnected)
{
    var connectStatus = await agent.ConnectProfileAsync(pluginProfile);
}
```

【官方】`ConnectProfileAsync(IVpnProfile)`：*"Connects to an existing VPN profile."*

这一步之后**控制权离开前台 app**：平台根据 profile 的 PFN 找到插件包，拉起它的
`vpnClient` 后台任务。前台 app 在此后**不再参与**隧道建立，除非插件主动
`ActivateForeground`（见 §7 步 8）。

---

## 6. S3 · 后台任务入口（`TestVpnPluginAppBgTask.Run`）—— 每次调度都从这里进

```csharp
public void Run(IBackgroundTaskInstance taskInstance)
{
    BackgroundTaskDeferral _deferral = taskInstance.GetDeferral();      // ①
    try
    {
        TestVpnPlugin plugin;
        if (((IDictionary<string, object>)CoreApplication.Properties).ContainsKey(pluginId))
            plugin = (TestVpnPlugin)((IDictionary<string, object>)CoreApplication.Properties)[pluginId];  // ②
        else {
            plugin = new TestVpnPlugin();
            ((IDictionary<string, object>)CoreApplication.Properties).Add(pluginId, plugin);
        }

        VpnChannel.ProcessEventAsync(plugin, taskInstance.TriggerDetails);  // ③
    }
    catch (Exception ex) { /* 只记日志，不 re-throw */ }
    finally { _deferral.Complete(); }                                       // ④
}
```

| # | 行为 | 官方 / 依据 |
|---|---|---|
| ① | `GetDeferral()` | 标准 UWP 后台任务语义：拿到 deferral 才能阻止宿主进程在异步工作完成前被挂起或终止。sample 注释：*"To keep the host process from being suspended or terminated while the background task is running"* |
| ② | **必须复用同一个 `IVpnPlugIn` 实例** | sample 注释原文：*"It is important that on each invocation of this background task, we provide the same instance of the IVpnPlugin object… the plug-in may have a partially decapsulated packet stored while it awaits more data."* 载体是 `CoreApplication.Properties`（**进程内**字典） |
| ③ | `ProcessEventAsync` | 【官方】*"Processes any pending VpnChannel events."*；签名 `ProcessEventAsync(object thirdPartyPlugIn, object event)`，第二个参数官方定义为 *"The event object that was passed to the app from a background task **Run** method parameter. App code… treats this object as an opaque blob."* —— **不要自己解析 `TriggerDetails`，原样透传** |
| ④ | `deferral.Complete()` | sample 注释：*"The important thing is to make sure that we complete the deferral so that the process will not get stuck in a 'Running' state."* 放在 `finally`，异常也照常完成 |

> ⚠️ **【推断】一个必须自己验证的张力点**：sample 把插件实例存在 `CoreApplication.Properties`（进程内存）里，
> 隐含假设是"宿主进程在多次调度之间活着"。但本仓库 80 号文档实测过
> *"每次 Connect 可能是全新 pid —— 插件不得依赖进程内存保存状态"*。
> 两者冲突。**落地 ClashUWP 时不能依赖进程内存保存 mihomo 状态**，要按 80 号的结论做外部持久化。

---

## 7. S4 · `Connect()` —— 建隧道（12 步，逐步官方注释）

`VpnPlugin.cs:138-606`。【官方】`IVpnPlugIn.Connect(VpnChannel)`：
*"This method instructs the VPN plug-in to connect to the VPN server and establish the VPN channel."*

| 步 | 代码 | API | 【官方】描述 / 约束 |
|---|---|---|---|
| 1 | `LogMessage(...)` | `VpnChannel.LogDiagnosticMessage(String)` | *"Writes a diagnostic message to the system log."* 插件唯一的官方日志出口（sample 的 `Logger` 就是包了它，按 logLevel 过滤） |
| 2 | `CheckAPIsExist()` | `ApiInformation.IsMethodPresent("Windows.Networking.Vpn.VpnChannel", …)` | sample 检查 5 个方法：`ActivateForeground`、`AppendVpnSendPacketBuffer`、`FlushVpnSendPacketBuffers`、`AppendVpnReceivePacketBuffer`、`FlushVpnReceivePacketBuffers`。注意：检查的是 WinRT 方法元数据（含参数个数），不是能力 |
| 3 | `channel.Id` | `VpnChannel.Id` | *"Gets the unique instance ID of a VPN channel. This can be used to simplify demuxing operations between two VPN connection instances."* |
| 4 | `new CustomConfiguration(channel, IsReconnect, exceptionFlags)` | `VpnChannel.Configuration` → `VpnChannelConfiguration` | *"Gets or sets a **VpnChannelConfiguration** object corresponding to the configuration of the VpnChannel…"*。sample 从这里取 `ServerHostNameList`、以及 profile 里那段自定义 XML（`cfg.CustomField`） |
| 5 | `channel.AssociateTransport(tcpSocket, udpSocket)` | `AssociateTransport(Object, Object)` | ★ *"Prepares and marks the transport for use by the VPN framework as a transport link communicating the VPN plug-in to the VPN server. **This call must be performed before any other operations related to the VPN framework APIs.**"* —— 必须在任何其他 VPN API 之前调。sample 按 transportType 传 `(tcp,null)` / `(udp,null)` / `(tcp,udp)` |
| 6 | `channel.RequestCustomPrompt(customPrompt)` | `RequestCustomPrompt(IVectorView<IVpnCustomPrompt>)` | 🚩 **【官方】标注 "Not supported."** 支持的重载是 `RequestCustomPromptAsync(IVectorView<IVpnCustomPromptElement>)`：*"Requests information from the user by presenting a prompt…"*。**照抄 sample 这一段是调用一个官方不支持的 API** |
| 7 | `channel.RequestCredentials(authType, isRetry, useSingleSignOn, cert)` | `RequestCredentials(VpnCredentialType, Boolean, Boolean, Certificate)` | *"Request the VPN platform to collect credentials from the user. The plug-in can specify the type and form of credentials…"*；`RequestCredentialsAsync` 条目下还有一条硬约束：*"A VPN plug-in must request credentials before using any type of credential, even if the intent is not to invoke user interaction for it."* sample 在 `AttemptAuthentication()`（1541-1803）里按 `UsernamePassword / UsernamePasswordChange / UsernameOtpPin / UsernamePasswordAndPin / SmartCard / *Certificate` 分类处理，失败最多重试 3 次 |
| 8 | `channel.ActivateForeground("App", new ValueSet())` | `ActivateForeground(String, ValueSet)` | ★ *"Activates the VPN app in the foreground. **You can call ActivateForeground only from your implementation of IVpnPlugin.Connect.** While the app is in the foreground, the regular **Connect** timeout is paused. The **ActivateForeground** call will be cancelled if there's a long pause (about 10 minutes)… If unable to launch, then the plugin should ideally support an authentication method that doesn't need UI."* 官方明说用于 SAML / AAD 这类 Web 认证。<br>官方签名：`ActivateForeground(string **packageRelativeAppId**, ValueSet sharedContext)`，第一参数是 **PRAID**（package-relative application identifier，即清单里的 `Application Id`）；返回 `ValueSet` = 前台回传结果。<br>⚠️ Windows requirements：**Windows 11（10.0.22000.0）起** |
| 9 | `tcpSocket.ConnectAsync(server, port, protectionLevel)` / `udpSocket.ConnectAsync(...)` | `Windows.Networking.Sockets` | 非 VPN API，纯 socket 建连。sample 遍历 `ServerHostNameList` 逐个试；DUAL 模式下 TCP 通了 UDP 不通要先把 TCP 断掉再换下一个服务器 |
| 10 | `config.healthStatus = channel.SystemHealth` | `VpnChannel.SystemHealth` | *"Gets the statement of health of the client machine."* sample 特意 try/catch：napagent 服务关掉时会抛异常 |
| 11 | `channel.Start(...)` / `channel.StartWithTrafficFilter(...)` | 见下方独立展开 | ★ 建隧道（= 建 L3 网卡） |
| 12 | `IsReconnect = true;` | — | sample 自维护标志：本次成功后，下次 `Connect` 就是"重连"，会跳过 pre/post auth 自定义 UI、且失败时不 `SetErrorMessage` |

### 7.1 步 11 展开：`Start` / `StartWithTrafficFilter` 的参数语义（官方逐参数）

【官方】`Start(...)`：*"Creates the VPN channel to be used by the VPN plug-in. **It also creates an L3
network interface** for applications in the client machine to be able to see the corporate network."*

| 参数 | sample 传值 | 【官方】语义 |
|---|---|---|
| `assignedClientIPv4list` | `new HostName(config.clientIpV4)` | *"the IPv4 address assigned by the VPN server to the VPN client. The address should be set on the VPN L3 interface. **If null, DHCPv4 is used.**"* |
| `assignedClientIPv6list` | `V6` 地址或 `null` | *"**If null, DHCPv6 or RD is used.**"* |
| `vpnInterfaceId` | **`null`** | `VpnInterfaceId`。sample 全部分支都传 null |
| `routeScope` | `config.routeAssignment` | *"the routes which should be associated / not associated with the VPN interface"*（分流的落点） |
| `namespaceScope` | `config.namespaceAssignment` | *"the list of name prefixes that are associated to the VPN channel, including its DNS and proxy servers"*（NRPT / DNS 的落点） |
| `mtuSize` | **1500** 🚩 | ★ *"…MTU size of the VPN L3 network interface. This is also the size of the **IVpnPacketBuffers in the Receive pool**. **This value should be configured to be at most 1400.**"* |
| `maxFrameSize` | `config.maxFrameSize`（默认 **1501**）🚩 | ★ *"…max size of the frame defined by the VPN protocol encapsulation without counting the outerTunnelTransport. This is also the size of the **IVpnPacketBuffers in the Send pool**. … **should be <= 1500.**"* |
| `optimizeForLowCostNetwork` | `true` | *"If TRUE the VPN framework will invoke the connect() callback to the VPN plug-in to reconnect it whenever the old network was costed and a new low cost network becomes available."* |
| `mainOuterTunnelTransport` | `tcpSocket` / `udpSocket` | *"…can be a DatagramSocket or a StreamSocket. This socket controls the connection to the VPN server and will be used to send encapsulated IP packets and receive encapsulated data."* |
| `optionalOuterTunnelTransport` | 第二个 socket 或 `null` | 同上（可选） |

`Start` 的 Remarks 里有四条硬约束，原文照抄：

> - *"Any call to this API outside of a connected VPN plug-in will fail, as activity and interactions
>    between the VPN plug-in and the system are controlled by the VPN profile."*
> - *"There can only be one VPN profile per process…"*
> - *"On success the ownership of **outerTunnelTransport** is transferred by the VPN framework.
>    Calling this method multiple times with the same parameters will have no effect. Calling this
>    method multiple times with variations in parameter values will make sure the channel is updated
>    with the latest values without any disruptions to other properties."*
> - *"There can only be one VPN channel per process… The **mtuSize** must always be smaller than the
>    **maxFrameSize**."*

`StartWithTrafficFilter(...)`（1803/17134 引入）：
*"Creates the VPN channel for an arbitrary numbers of transports… **It allows the specification of
traffic filters to use in per-app VPN scenarios.**"* —— 比 `Start` 多一个 `VpnTrafficFilterAssignment`
参数；sample 在 `config.IsV2` 为 true 时走这条。

> 🚩 对本仓库的意义：65 号文档已把 A3（mtu 1500 > 1400）、A4（maxFrame 1501 > 1500）判为硬违规。
> 本文只标注事实，**ClashUWP 实现时必须按官方上限取 mtu ≤ 1400、maxFrame ≤ 1500 且 mtu < maxFrame**。

### 7.2 `Connect` 的错误处理契约

sample 在 `catch` 里：`if (IsReconnect == false) channel.SetErrorMessage(ex.Message);` 然后 `throw;`

> 🚩 【官方】`SetErrorMessage(String)` 单参重载：**"Not supported."**
> 双参重载 `SetErrorMessage(String, UInt32)`（Overload 名 `SetErrorMessageWithDetails`）才是支持的，
> 且 Windows requirements 是 **Windows 11 24H2（10.0.26100.0）起**。
> 替代路径是 `TerminateConnection(String)`：*"Allows a VPN plugin to indicate an error message and
> terminate the connection attempt performed inside the IVpnPlugin.Connect method.
> **Should not be called on reconnect cases.**"* —— 语义上正好对应 sample 这里 `!IsReconnect` 的判断。

---

## 8. S5 · 数据面（三个回调）

### 8.1 出站 `Encapsulate`

【官方】`IVpnPlugIn.Encapsulate(VpnChannel, VpnPacketBufferList, VpnPacketBufferList)`：
*"This method instructs the VPN plug-in to **prepare the indicated L3 IP packet for transmission to
the VPN server**. The process might include any encapsulation specific to the VPN protocol."*

**契约**：从入参 `packets` 取包 → 封装 → 填进出参 `encapulatedPackets`。

**sample 实际做法**（`VpnPlugin.cs:695-769` + `EncapWorker_DoWork`）：

```csharp
while (packets.Size > 0)
{
    buffer = packets.RemoveAtBegin();     // 官方注释强调：用 RemoveAtBegin 保序
    encapQueue.Enqueue(buffer);           // ← 不填 encapulatedPackets，改投后台队列
}
if (!encapWorker.IsBusy) encapWorker.RunWorkerAsync(channel);
// 后台线程里：
channel.AppendVpnSendPacketBuffer(buffer);   // 前面加了 1 字节 VPN_CONTROL_PACKET 头
if (ShouldFlushPacketBuffer())               // sample 里恒为 true
    channel.FlushVpnSendPacketBuffers();
```

相关官方描述：
- `AppendVpnSendPacketBuffer(VpnPacketBuffer)`：*"Appends a send VpnPacketBuffer object to the channel."*
- `FlushVpnSendPacketBuffers()`：*"Flushes any appended send packet buffers."*
- `GetVpnSendPacketBuffer()`：*"Retrieves a VpnPacketBuffer object from the send packet buffer pool to
   use in encapsulation and transmission of a data packet from the client network stack to the VPN server."*

链表容器 `VpnPacketBufferList`（*"Used to manage VpnPacketBuffer objects."*）的官方方法：
`Append`（加到尾）、`AddAtBegin`（加到头）、`RemoveAtBegin`（从头取，sample 用它保序）、
`RemoveAtEnd`、`Clear`、`Size`、`Status`（*"may be used to report errors when manipulating the
IVpnPacketBufferList"*）。
→ **官方契约的正确写法是 `encapulatedPackets.Append(buffer)`**，sample 一次都没调过。

> 🚩 **两条背离**（65 号 A1/B3）：
> 1. 出参 `encapulatedPackets` **没被填充**（平台期望在这里拿到封装好的包）；
> 2. `channel` 被 **BackgroundWorker 后台线程**直接调用（官方 `VpnChannel` 元数据标 `ThreadingModel.MTA` /
>    `MarshalingType.Agile`，但"能在别的线程调"≠"平台期望你在别的线程调"）。

### 8.2 入站 `Decapsulate` —— **这条才是 ClashUWP 的写入口**

【官方】`IVpnPlugIn.Decapsulate(VpnChannel, VpnPacketBuffer, VpnPacketBufferList, VpnPacketBufferList)`：
*"This method instructs the VPN plug-in to prepare receive a buffer from the VPN server
**outerTunnelTransport**, extract any number of IP packets encapsulated in the buffer, and return the
IP packets together with any other reply buffers that are needed to be sent back through the
outerTunnelTransport to the VPN server."*

**关键：入参 `encapBuffer` 可以是 1 个包、多个包、或者半个包。** sample 用 `gDecapState`
（`VPN_PLUGIN_DECAP_STATE`）跨调用保存半包，靠 IPv4 `TotalLength` / IPv6 `PayloadLength+40` 定包边界
（`VALID_LENGTH = 4+1`、`VALID_LENGTH_V6 = 6+1`、`IPV6_HEADER_LENGTH = 40`）。

**实际投递路径**（`FinishWithCurrentPacket`，`VpnPlugin.cs:1338-1384`）：

```csharp
channel.RequestVpnPacketBuffer(VpnDataPathType.Receive, out spCurrentPacket);  // ① 从接收池借 buffer
spPacketBuffer = spCurrentPacket.Buffer;
BufferCopy(ref rawFrame, 1, ref spPacketBuffer, 0, state.packetHostOrderTotalLength);  // ② 去掉控制字节
spPacketBuffer.Length = (uint)state.packetHostOrderTotalLength;                 // ③ 设真实长度
AppendAndFlushReceivePackets(channel, spCurrentPacket);                         // ④ Append + Flush
```

| # | API | 【官方】描述 |
|---|---|---|
| ① | `RequestVpnPacketBuffer(VpnDataPathType, out VpnPacketBuffer)` | *"Requests an **IVpnPacketBuffer** from the specified pool. There is one **IVpnPacketBuffer** pool for the send path and another **IVpnPacketBuffer** pool for the receive path."* |
| ④-a | `AppendVpnReceivePacketBuffer(VpnPacketBuffer)` | *"Appends a receive VpnPacketBuffer object to the channel."* |
| ④-b | `FlushVpnReceivePacketBuffers()` | *"Flushes any appended receive packet buffers."* |

**另一条官方给的入向通道**（sample 没用，但 ClashUWP 会用到）：
`GetVpnReceivePacketBuffer()` —— *"Requests a VpnPacketBuffer object from the receive packet buffer
pool **to be used in the decapsulation or injection of a received packet from the VPN server into the
local network stack**."*
→ 官方明说 **injection**，即**可以在 `Decapsulate` 之外主动往本机协议栈注入包**。
本仓库 76 号文档"入向注入实测：Append + Flush 可在 Decapsulate 之外主动调用"与这条官方描述互相印证。

### 8.3 `GetKeepAlivePayload`

【官方】：*"This method instructs the VPN plug-in to compute and return a Keep alive payload ready to be
sent to the VPN server."*

sample：

```csharp
channel.RequestVpnPacketBuffer(VpnDataPathType.Send, out keepAlivePacket);  // 从 Send 池借
keepAlivePacket.Buffer.Length = 1;
keepAlivePacket.Buffer.AsStream().WriteByte((byte)VPN_CONTROL.VPN_CONTROL_KEEP_ALIVE);
```

注意与 8.1 的区别：keepalive 是**通过 out 参数直接交回平台**，不需要 Append/Flush。

---

## 9. S6 · 前台认证回流（`App.OnActivated`）

```csharp
if (args is VpnForegroundActivatedEventArgs)                      // 由 ActivateForeground 触发
{
    var activateForegroundArgs = (VpnForegroundActivatedEventArgs)args;
    var creds = new CredentialsDialog();
    await creds.ShowAsync();                                       // 弹 UI 收用户名/密码
    activateForegroundArgs.ActivationOperation.Complete(creds.valueSet);  // 回传给插件
}
```

回到插件侧（`Connect` 步 8）：

```csharp
ValueSet userPass = channel.ActivateForeground("App", new ValueSet());
userPass.TryGetValue("Username", out username);   // 取回前台填的值
```

【官方】`ActivateForeground` 的 Remarks 把这条回路讲得很完整，原文要点：

> - 前台 app 以 `ActivationKind` = **VpnForeground** 被激活；
> - 激活参数类型是 `VpnForegroundActivatedEventArgs`，含三个字段：
>   1. `ProfileName`（String）
>   2. `SharedContext`（ValueSet）—— 即插件传过去的那个
>   3. `ActivationOperation`（`VpnForegroundActivationOperation`）
> - *"When done, you must call the **Complete** method of the **ActivationOperation**, passing in a
>   ValueSet of values that you want to be returned to the IVpnPlugin.Connect call."*
> - *"Back in the IVpnPlugin.Connect call, use the ValueSet returned from ActivateForeground to decide
>   whether the authentication was successful, and handle any errors."*

sample 还示范了版本保护：`ApiInformation.IsTypePresent("Windows.Networking.Vpn.VpnForegroundActivatedEventArgs")`——
因为 `ActivateForeground` 是 Win11 才有的 API，这条保护不是多余的。

> 【推断】对 ClashUWP：这条链路正好可以用来做"订阅链接 / 二次认证 / 节点选择"的前台交互——
> 但要注意官方那条约 10 分钟的取消约束。

---

## 10. S7 · `Disconnect`

【官方】`IVpnPlugIn.Disconnect(VpnChannel)`：*"This method instructs the VPN plug-in to disconnect
from the VPN server and destroy the VPN channel."*

sample（`VpnPlugin.cs:622-676`）：`IsReconnect = false` → 清空 `gDecapState` → **`channel.Stop()`** → 清计数器。

【官方】`VpnChannel.Stop()`：*"Destroys a previously established VPN channel object. **Disassociates and
closes the outerTunnelTransport connection to the VPN server.**"*

> 65 号 B5 提示：`Stop()` 后 socket 所有权已随 `Start` 转移给框架，插件不要再自己 Dispose。

前台侧对应 `VpnManagementAgent.DisconnectProfileAsync`（*"Disconnects from a connected VPN profile."*），
两者是不同入口：前台是"请求断开"，插件侧 `Disconnect` 是"被通知断开"。

---

## 11. 官方明文标 "Not supported" 的 API 汇总（照抄 sample 会踩的坑）

| API | 官方标注 | 替代 |
|---|---|---|
| `VpnChannel.RequestCustomPrompt(IVectorView<IVpnCustomPrompt>)` | **Not supported.** | `RequestCustomPromptAsync(IVectorView<IVpnCustomPromptElement>)` |
| `VpnChannel.SetErrorMessage(String)` | **Not supported.** | `SetErrorMessage(String, UInt32)`（24H2 / 26100 起）或 `TerminateConnection(String)` |
| `VpnChannel.SetAllowedSslTlsVersions(Object, Boolean)` | **Not supported.** | — |
| `VpnChannel.ActivityChange` 事件 | **Not supported.** | `ActivityStateChange` 事件 |
| `VpnChannelActivityEventArgs` 类 | *"is not supported and may be altered or unavailable in the future. Instead, use **VpnChannelActivityStateChangedArgs**."* | `VpnChannelActivityStateChangedArgs` |
| `VpnChannel.StartReconnectingTransport` | *"This API is not implemented, and we recommend that you do not call it."* | — |

**只有第一个是 sample 实际在调的**（`Connect` 步 6、步 10 之后的 post-auth UI），其余是本文顺带列出，
供 ClashUWP 实现时避雷。

---

## 12. 对 ClashUWP 的落地映射（VpnChannel = TUN）

| mihomo / TUN 概念 | VpnChannel 侧对应 | 官方依据 |
|---|---|---|
| 打开 TUN | `VpnChannel.Start(...)` | *"creates an L3 network interface"* |
| `Tun.Read()`（本机 → 内核） | `Encapsulate(channel, packets, encapulatedPackets)` 的入参 `packets` | *"prepare the indicated L3 IP packet for transmission to the VPN server"* |
| `Tun.Write()`（内核 → 本机） | `RequestVpnPacketBuffer(Receive)` → 填 → `AppendVpnReceivePacketBuffer` → `FlushVpnReceivePacketBuffers`，或用 `GetVpnReceivePacketBuffer()` 主动注入 | `GetVpnReceivePacketBuffer` 官方描述里明写 **injection** |
| MTU | `mtuSize` ≤ **1400**，且 **< `maxFrameSize`** ≤ **1500** | `Start` 参数文档 + Remarks |
| 路由 / 分流 | `VpnRouteAssignment`（`routeScope`） | *"routes which should be associated / not associated with the VPN interface"* |
| DNS / NRPT | `VpnNamespaceAssignment`（`namespaceScope`） | *"list of name prefixes… including its DNS and proxy servers"* |
| 日志 | `VpnChannel.LogDiagnosticMessage` | *"Writes a diagnostic message to the system log."* |
| 配置下发 | `VpnPlugInProfile.CustomConfiguration`（**格式自定义**） | *"Refer to the documentation from the VPN plug-in for format and support."* |

**三条必须记住的硬约束**（全部来自官方 Remarks 原文）：
1. 一个进程只能有一个 VPN channel / 一个 VPN profile；
2. `AssociateTransport` 必须在任何其他 VPN 框架 API 之前调用；
3. `mtuSize < maxFrameSize ≤ 1500`，`mtuSize ≤ 1400`（Receive 池 buffer 大小就是 mtuSize，
   Send 池就是 maxFrameSize）。

---

## 13. 参考链接（均为本次实际访问核对过的真实页面）

- 仓库：https://github.com/microsoft/UwpVpnPluginSample
- `VpnChannel`：https://learn.microsoft.com/en-us/uwp/api/windows.networking.vpn.vpnchannel
- `VpnChannel.Start`：https://learn.microsoft.com/en-us/uwp/api/windows.networking.vpn.vpnchannel.start
- `VpnChannel.ProcessEventAsync`：https://learn.microsoft.com/en-us/uwp/api/windows.networking.vpn.vpnchannel.processeventasync
- `VpnChannel.SetErrorMessage`：https://learn.microsoft.com/en-us/uwp/api/windows.networking.vpn.vpnchannel.seterrormessage
- `VpnChannel.ActivateForeground`：https://learn.microsoft.com/en-us/uwp/api/windows.networking.vpn.vpnchannel.activateforeground
- `VpnPacketBufferList`：https://learn.microsoft.com/en-us/uwp/api/windows.networking.vpn.vpnpacketbufferlist
- `IVpnPlugIn`：https://learn.microsoft.com/en-us/uwp/api/windows.networking.vpn.ivpnplugin
- `VpnManagementAgent`：https://learn.microsoft.com/en-us/uwp/api/windows.networking.vpn.vpnmanagementagent
- `VpnPlugInProfile`：https://learn.microsoft.com/en-us/uwp/api/windows.networking.vpn.vpnpluginprofile
- `VpnChannelActivityEventArgs`：https://learn.microsoft.com/en-us/uwp/api/windows.networking.vpn.vpnchannelactivityeventargs
- 相关本地文档：`clashUWP研究/docs/65-官方Sample-API调用链与违规调用审计.md`（审计视角）、
  `61-VPN-Plugin官方机制-激活链与正确调用契约.md`、`105-vpnchannel-api-调查.md`

---

## 14. 本文未闭环 / 待验证 / 已撤回

**已撤回（其一）**：~~"文档里的 Not supported 标记是示例发布后才补上的"~~ —— 查 `MicrosoftDocs/winrt-api`
commit `58cad8b`（2021-11-19）的 diff，那次只改了一个尖括号转义，与标记无关；
且成员表根本不在该开源 md 里。该推断不成立，予以撤回。

**已撤回（其二，更严重）**：~~"文档最后改动停在 2021-11-19，比示例还老"~~ —— 该日期取自 GitHub
`MicrosoftDocs/winrt-api`，**该仓库不是 Learn 页面的数据源**。页面 `<meta name="original_content_git_url">`
指向 Azure DevOps 内部仓库 `winrt-api-build` 的 SDK 元数据 XML，`<meta name="updated_at">` 显示
**VpnChannel 系列最近一次发布为 2026-09-30T20:23:00Z**（详见 §0.5）。**文档比示例新约 4 年半。**

**仍未闭环**：`Not supported` 标记的具体写入年份——Azure DevOps 仓库不公开，无法证实。
已证实的是它在 **2026-09-30 发布的当前页面上仍然存在**。

1. **插件实例复用的载体**：sample 用 `CoreApplication.Properties`（进程内存），80 号实测"每次
   Connect 可能全新 pid"。两者冲突，需一次单变量实验才能定论。**【推断，未验证】**
2. `VpnChannelActivityEventArgs` 已被官方标记弃用，但 `vpnClient` 后台任务的 `TriggerDetails`
   实际类型官方文档未明示（只说"opaque blob"）。
3. `Start` vs `StartWithTrafficFilter` 在 ClashUWP 场景下的取舍（是否用 per-app traffic filter）
   本文未做判定。
