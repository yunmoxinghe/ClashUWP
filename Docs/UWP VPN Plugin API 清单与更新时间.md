# UWP VPN Plugin API 清单与更新时间

## 范围

这里将 `uwpvpnplugin` 按 Microsoft 的 [`UwpVpnPluginSample`](https://github.com/microsoft/UwpVpnPluginSample) 理解。它是 Windows UWP VPN 插件示例，不是提供 HTTP/REST 端点的 Web 服务；API 主体是 Windows Runtime 的 `Windows.Networking.Vpn` 命名空间。[1] [3]

## 更新时间

| 项目 | 日期 | 说明 |
| --- | --- | --- |
| 默认 `main` 分支最后一次源码提交 | **2022-03-08** | 提交 `d589fe0`，标题为 “Removed original boilerplate & direct people to the forums”。[2] |
| 仓库可见分支中最近的源码提交 | **2023-12-12** | 分支 `user/veponnam/customUIChanges`，提交 `8ad4ef0`；不是默认 `main` 分支。[6] |
| 仓库归档 | **2026-06-11** | GitHub 标记仓库已归档、只读；这是归档时间，不代表代码当天有更新。[1] [2] |
| 官方 API 文档源文件最后提交 | 命名空间：**2021-02-11**；`IVpnPlugIn`：**2017-08-14**；`VpnChannel`：**2021-11-19** | 是 MicrosoftDocs 源文件的提交日期，不等同于 Windows API 的发布日期或运行时更新时间。[7] [8] [9] |

**结论：**若“最后更新时间”指默认分支代码，日期是 **2022-03-08**；若指任一公开分支的最近源码变更，日期是 **2023-12-12**。仓库于 **2026-06-11** 归档。微软 API 文档页面没有统一标注“API 最后更新”日期；文档源文件提交时间只能代表文档版本记录。

## 插件核心接口：`IVpnPlugIn`

插件必须实现以下 5 个生命周期/数据处理方法：[4]

- `Connect(VpnChannel)`：连接 VPN 服务器、创建通道，并完成认证和通道启动设置。
- `Disconnect(VpnChannel)`：断开服务器连接并释放 VPN 通道相关资源。
- `Encapsulate(VpnChannel, VpnPacketBufferList, VpnPacketBufferList)`：把本机 IP 包封装成 VPN 隧道数据，准备发送到服务器。
- `Decapsulate(VpnChannel, VpnPacketBuffer, VpnPacketBufferList, VpnPacketBufferList)`：解封装服务器数据，把 IP 包交回本机网络栈，并处理可能需要发送的控制包。
- `GetKeepAlivePayload(VpnChannel, VpnPacketBuffer)`：生成可发送到服务器的保活数据包。

样例的 C# 实现在 `CSharp/TestVpnPluginAppBg/VpnPlugin.cs`；C++/WinRT 实现在 `CppWinRT/TestVpnPluginAppBg/VpnPlugInImpl.cpp`。[1]

## 样例实际调用的平台方法

以下是样例源码中使用的主要 `VpnChannel` API；每项后面附上用途简述。[1] [5]

### 事件与通道生命周期

- `VpnChannel.ProcessEventAsync`：将系统触发的 VPN 事件分派给插件处理。
- `VpnChannel.AssociateTransport`：把插件创建的网络传输对象关联到 VPN 框架。
- `VpnChannel.Start`：以配置好的地址和策略启动 VPN 通道。
- `VpnChannel.StartWithTrafficFilter`：按地址、路由和流量过滤规则启动通道，支持按应用或流量范围分流。
- `VpnChannel.Stop`：停止 VPN 通道并结束当前连接。

### 认证与用户交互

- `VpnChannel.RequestCredentials`：向 VPN 平台请求指定类型的用户凭据。
- `VpnChannel.RequestCustomPrompt`：向用户显示插件构造的自定义认证提示。
- `VpnChannel.ActivateForeground`：把 VPN 应用切换到前台，以完成需要 UI 的登录或认证。
- `VpnChannel.SetErrorMessage`：向 VPN 平台提供连接失败或认证失败的错误信息。

### 隧道数据缓冲

- `VpnChannel.RequestVpnPacketBuffer`：从平台缓冲池申请发送或接收用的数据包缓冲区。
- `VpnChannel.AppendVpnSendPacketBuffer`：把已准备好的数据包追加到待发送队列。
- `VpnChannel.FlushVpnSendPacketBuffers`：提交/刷新待发送缓冲区。
- `VpnChannel.AppendVpnReceivePacketBuffer`：把解封装后的数据包追加到接收队列。
- `VpnChannel.FlushVpnReceivePacketBuffers`：提交/刷新接收缓冲区，让数据交付给本机网络栈。

### 诊断

- `VpnChannel.LogDiagnosticMessage`：向系统 VPN 日志写入诊断信息，便于排查连接与数据处理问题。

## `Windows.Networking.Vpn` 类型目录

下列类、接口和枚举按微软命名空间页面逐项列出，并为每项补上用途简述。[3]

### 类（34）

- `VpnAppId`：表示应用标识，可按文件路径、包系列名称或 FQBN 表示。
- `VpnChannel`：管理 VPN 通道、传输关联和数据缓冲。
- `VpnChannelActivityEventArgs`：旧式通道活动事件参数；文档建议改用 `VpnChannelActivityStateChangedArgs`。
- `VpnChannelActivityStateChangedArgs`：提供通道从活动到空闲等状态变化信息。
- `VpnChannelConfiguration`：保存建立 VPN 服务器连接所需的通道配置。
- `VpnConnectionDetailsPreview`：提供预览版 VPN 连接详情数据。
- `VpnCredential`：承载平台请求或收集到的用户名、密码、PIN、OTP 或证书凭据。
- `VpnCustomCheckBox`：旧式自定义复选框提示；建议用 `VpnCustomPromptBooleanInput`。
- `VpnCustomComboBox`：旧式自定义下拉选项提示；建议用 `VpnCustomPromptOptionSelector`。
- `VpnCustomEditBox`：旧式自定义文本输入提示；建议用 `VpnCustomPromptTextInput`。
- `VpnCustomErrorBox`：旧式自定义错误提示；建议用 `VpnCustomPromptText`。
- `VpnCustomPromptBooleanInput`：在 VPN 自定义提示中收集布尔值，通常显示为复选框。
- `VpnCustomPromptOptionSelector`：在 VPN 自定义提示中让用户从多个选项中选择。
- `VpnCustomPromptText`：在 VPN 自定义提示中显示文本说明或提示。
- `VpnCustomPromptTextInput`：在 VPN 自定义提示中收集文本输入。
- `VpnCustomTextBox`：旧式自定义文本提示控件；建议用 `VpnCustomPromptText`。
- `VpnDomainNameAssignment`：向通道分配域名解析、命名空间和代理策略。
- `VpnDomainNameInfo`：描述某个域名空间使用的 DNS、代理及豁免策略。
- `VpnForegroundActivatedEventArgs`：提供 VPN 应用被前台激活时传入的事件参数。
- `VpnForegroundActivationOperation`：表示一次前台激活操作，处理结束时需调用完成方法。
- `VpnInterfaceId`：表示构造和分配 VPN IPv6 接口地址所需的接口 ID。
- `VpnManagementAgent`：创建、编辑、删除、连接和断开 VPN 配置文件。
- `VpnNamespaceAssignment`：旧式命名空间策略分配类型；建议用 `VpnDomainNameAssignment`。
- `VpnNamespaceInfo`：旧式命名空间策略详情；建议用 `VpnDomainNameInfo`。
- `VpnNativeProfile`：表示 Windows 内置 VPN 协议的配置文件。
- `VpnPacketBuffer`：承载待发送或待接收的 VPN 数据包内存缓冲区。
- `VpnPacketBufferList`：按顺序组织多个 VPN 数据包缓冲区。
- `VpnPickedCredential`：旧式凭据选择类型；建议用 `VpnCredential`。
- `VpnPlugInProfile`：表示由第三方 VPN 插件提供的 VPN 配置文件。
- `VpnRoute`：表示一个 VPN 路由网段及其前缀长度。
- `VpnRouteAssignment`：向 VPN 通道分配包含或排除路由。
- `VpnSystemHealth`：读取设备的系统安全健康状态。
- `VpnTrafficFilter`：描述允许通过 VPN 的流量条件，例如应用或 IP 协议。
- `VpnTrafficFilterAssignment`：将一组流量过滤规则作为策略分配给通道。

### 接口（12）

- `IVpnChannelStatics`：提供 VPN 插件使用的静态通道函数。
- `IVpnCredential`：定义 VPN 平台处理请求凭据时使用的接口。
- `IVpnCustomPrompt`：旧式自定义提示接口；当前文档标记为不支持。
- `IVpnCustomPromptElement`：为自定义提示 UI 元素提供共同接口。
- `IVpnDomainNameInfoFactory`：创建 `VpnDomainNameInfo` 对象。
- `IVpnInterfaceIdFactory`：创建 `VpnInterfaceId` 对象。
- `IVpnNamespaceInfoFactory`：旧式命名空间工厂；建议改用 `IVpnDomainNameInfoFactory`。
- `IVpnPacketBufferFactory`：系统保留的缓冲区工厂接口，应用不应自行实现或依赖。
- `IVpnPlugIn`：第三方 VPN 客户端必须实现的核心插件接口。
- `IVpnPlugInReconnectTransport`：支持插件 VPN 传输的重连操作。
- `IVpnProfile`：暴露 VPN 配置文件的基础接口。
- `IVpnRouteFactory`：创建 `VpnRoute` 对象。

### 枚举（15）

- `VpnAppIdType`：指定 VPN 平台识别应用 ID 的类型。
- `VpnAuthenticationMethod`：指定 Windows 内置 VPN 协议允许的认证方式。
- `VpnChannelActivityEventType`：标识 VPN 通道活动事件的类型。
- `VpnChannelRequestCredentialsOptions`：指定凭据请求是否为重试或是否用于单点登录。
- `VpnConnectionTypePreview`：表示预览版 VPN 连接类型。
- `VpnCredentialType`：指定 VPN 平台要请求的凭据组合类型。
- `VpnDataPathType`：旧式数据路径类型；当前文档标记为不支持。
- `VpnDisconnectReasonPreview`：表示预览版 VPN 断开原因。
- `VpnDomainNameType`：区分域名后缀与完整限定域名。
- `VpnIPProtocol`：表示 IP 协议的数值类型。
- `VpnManagementConnectionStatus`：表示 VPN 配置文件当前连接状态。
- `VpnManagementErrorStatus`：表示 VPN 管理操作可能返回的错误状态。
- `VpnNativeProtocolType`：指定 Windows 内置 VPN 协议类型。
- `VpnPacketBufferStatus`：表示 VPN 数据包缓冲区的状态。
- `VpnRoutingPolicyType`：表示 VPN 路由策略类型。

## 使用与兼容性提示

- 应用使用此命名空间时，需在包清单声明受限 capability `networkingVpnProvider`。[3] [4]
- 文档标记为旧版或不支持的类型包括 `VpnChannelActivityEventArgs`、旧式 `VpnCustom...` 控件、`VpnNamespaceAssignment`、`VpnNamespaceInfo`、`VpnPickedCredential`、`IVpnNamespaceInfoFactory`、`IVpnCustomPrompt` 和 `VpnDataPathType`。开发新代码时优先采用文档建议的替代类型，并针对目标 Windows SDK 核对可用性。[3]
- 本清单完整覆盖命名空间页面列出的类型，并逐项说明用途；各类型的全部重载、属性、事件和成员以官方 API 参考页为准。[3]

## 主要来源

[1]: https://github.com/microsoft/UwpVpnPluginSample "Microsoft UWP VPN Plugin Sample 仓库"
[2]: https://github.com/microsoft/UwpVpnPluginSample/commits/main/ "仓库默认分支提交记录及归档状态"
[3]: https://learn.microsoft.com/en-us/uwp/api/windows.networking.vpn?view=winrt-28000 "Windows.Networking.Vpn 命名空间参考"
[4]: https://learn.microsoft.com/en-us/uwp/api/windows.networking.vpn.ivpnplugin?view=winrt-28000 "IVpnPlugIn 接口参考"
[5]: https://learn.microsoft.com/en-us/uwp/api/windows.networking.vpn.vpnchannel?view=winrt-28000 "VpnChannel 类参考"
[6]: https://github.com/microsoft/UwpVpnPluginSample/commit/8ad4ef046db1bd9c0a496ae81a5c02bc5b431e98 "2023-12-12 公开分支提交记录"
[7]: https://github.com/MicrosoftDocs/winrt-api/commits/docs/windows.networking.vpn/windows_networking_vpn.md "Windows.Networking.Vpn 命名空间文档源文件提交历史"
[8]: https://github.com/MicrosoftDocs/winrt-api/commits/docs/windows.networking.vpn/ivpnplugin.md "IVpnPlugIn 文档源文件提交历史"
[9]: https://github.com/MicrosoftDocs/winrt-api/commits/docs/windows.networking.vpn/vpnchannel.md "VpnChannel 文档源文件提交历史"
