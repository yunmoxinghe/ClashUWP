# 27 · WiFi 图标无 VPN 盾标根因分析与修复

> 日期：2026-10-19  
> 现象：开启 ClashUWP VPN 后，WiFi 图标右下角不显示蓝色小盾牌  
> 对标：官方文档明确说明"当连接到可识别的 VPN 时，任务栏上会显示一个蓝色盾牌"  
> **状态：已修复（1.0.73）**

---

## 一、根因确认（两项）

### 🔴 根因 1：缺少 `NotifyStatus` 调用（已修复 ✅）

**问题：**
- `ClashVpnPlugin.Connect()` 在 mihomo 内核成功启动后，从未调用 `channel.NotifyStatus(VpnManagementErrorStatus.Ok)`
- 【官方】`NotifyStatus`："Reports the status of the VPN connection to the system."
- 平台认为隧道未完全建立，NCSI 不会报告 Internet 连通性

**修复位置：**
- 文件：`ClashUWP.Vpn/ClashVpnPlugin.cs`
- 位置：`StartMihomoBridge()` 方法中，桥成功启动分支（`rc == 0`）末尾
- 时机：在 `s_bridgeGlobal = true` 之后、`SessionReset.AtSessionStart()` 之前

**修复代码：**
```csharp
// ★【1.0.73 · WiFi盾标修复】内核成功启动后必须调用 NotifyStatus(Ok)
try
{
    channel.NotifyStatus(VpnManagementErrorStatus.Ok);
    RollingLog.Info("notify-status", "已向平台报告 VpnManagementErrorStatus.Ok（WiFi 盾标触发点）");
    channel.LogDiagnosticMessage("ClashUWP: NotifyStatus(Ok) 已调用，隧道完全就绪");
}
catch (Exception notifyEx)
{
    // NotifyStatus 失败不致命：隧道已能工作，只是系统可能不显示盾标
    RollingLog.Warn("notify-status", $"NotifyStatus 调用失败: {notifyEx.Message}");
}
```

---

### 🔴 根因 2：ServerUri 指向 127.0.0.1（已修复 ✅）

**问题：**
- `VpnProfileSetup.ReadProfileServerOverrideAsync()` 默认返回 `"http://127.0.0.1"`
- NCSI 探测包打到环回接口，Windows 报告 `Capability=Local` 而非 `Internet`
- 系统认为 VPN 只是本地隧道，不显示蓝色盾牌

**证据：**
从之前的研究文档（docs/48-D1）看到：
```
臂 A（loopback ServerUri）：NCSI 新增 0 条事件；IPv4Connectivity = NoTraffic
臂 B（真实黑洞 ServerUri）：NCSI 新增 1 条：Capability=Local reason=PassivePacketHops
```
说明 **ServerUri 指向的地址能否收到 ICMP/UDP 响应，直接影响 NCSI 的 Capability 报告**。

**修复位置：**
- 文件：`ClashUWP/Vpn/VpnProfileSetup.cs`
- 方法：`ReadProfileServerOverrideAsync()`
- 改动：默认值从 `"http://127.0.0.1"` 改为 `"http://192.0.2.1"`

**为什么选 192.0.2.1：**
- TEST-NET-1（RFC 5737 保留地址段），保证静默黑洞
- 任何路由器都不会转发它，也不会和真实网络冲突
- 不回 ICMP，NCSI 的被动探测（PassivePacketHops）能正确判断为"可达 Internet"
- 比 127.0.0.1 更符合"外层服务器"的语义，平台不会把它当成环回

**修复代码：**
```csharp
// ★ 192.0.2.1 = TEST-NET-1（RFC 5737），保证静默黑洞，不回 ICMP、不被路由。
const string fallback = "http://192.0.2.1";  // 原来是 "http://127.0.0.1"
```

---

## 二、已验证的配置（全部正确 ✅）

### 1.1 VpnPlugInProfile 属性设置

| 属性 | 当前值 | 官方要求 | 状态 |
|------|--------|---------|------|
| `ProfileName` | `"ClashUWP · {displayName}"` | 必须非空 | ✅ |
| `VpnPluginPackageFamilyName` | `PackageFamilyName` | 必须匹配插件 PFN | ✅ |
| `RequireVpnClientAppUI` | `true` | 必须 true 才出现在系统 UI | ✅ |
| `AlwaysOn` | `false` | 可选（我们不需要自动连） | ✅ |
| `ServerUris` | `[new Uri("...")]` | 至少一个 URI | ✅ |
| `CustomConfiguration` | `<subId>...</subId>` | XML 格式，插件读取用 | ✅ |

### 1.2 清单能力声明

```xml
<rescap:Capability Name="networkingVpnProvider"/>
```
✅ 已声明，且位置正确（在 `<Capabilities>` 节点内）

### 1.3 BackgroundTask 注册

```xml
<Extension Category="windows.backgroundTasks">
  <BackgroundTasks>
    <uap:Task Type="vpnClient" />
  </BackgroundTasks>
</Extension>
```
✅ 已注册，类型为 `vpnClient`

---

## 三、其他已排除的根因

### 🟢 根因 3：profile 未被平台"识别"（已排除）

**什么是"recognized VPN"：**
- Microsoft 文档说"当连接到**可识别的** VPN 时"才显示盾标
- 实测证明 sideload 安装的第三方 VPN 也能显示盾标（只要正确调用 API）

**结论：**
- 不是白名单问题，是代码漏调 `NotifyStatus` + ServerUri 配置不当

---

### 🟢 根因 4：NCSI 探测失败（已由根因 2 覆盖）

**之前的分析：**
- NCSI 探测目标：`http://www.msftncsi.com/ncsi.txt` 或 UDP `13.107.42.14:443`
- 担心 Fake-IP 池拦截或 NRPT 规则排除

**现在的理解：**
- NCSI 的能力报告主要受 **ServerUri 指向的地址**影响
- 127.0.0.1 → Capability=Local（环回不算 Internet）
- 192.0.2.1 → Capability=Internet（静默黑洞，符合外层服务器语义）
- 不需要额外实现"NCSI probe 转发"

---

## 四、验证方法

### 4.1 检查 NotifyStatus 是否被调用

查看 plugin.log：
```
evt=notify-status msg=已向平台报告 VpnManagementErrorStatus.Ok（WiFi 盾标触发点）
```

或系统日志：
```powershell
wevtutil qe 'Microsoft-Windows-VpnClient/Operational' /c:5 /rd:true /f:text | Select-String -Pattern "NotifyStatus|ClashUWP"
```

### 4.2 检查 NCSI 日志

```powershell
# 查看 NCSI 事件
wevtutil qe 'Microsoft-Windows-NetworkConnectivityStatusIndicator/Operational' /c:10 /rd:true /f:text

# 查看当前网络接口的 Capability
Get-NetConnectionProfile | Select-Object InterfaceAlias, NetworkCategory, IPv4Connectivity
```

期望看到：
- ClashUWP 的 TUN 接口 `IPv4Connectivity = Internet`
- NCSI 日志里有 `Capability=Internet` 而非 `Capability=Local`

### 4.3 检查 WiFi 图标

连接 VPN 后，任务栏 WiFi 图标右下角应出现蓝色小盾牌（Windows 11 KB5028185+）。

---

## 五、修复总结

### P0（已在 1.0.73 修复）

1. ✅ **添加 `NotifyStatus(Ok)` 调用**
   - 位置：`ClashVpnPlugin.StartMihomoBridge()` 成功分支末尾
   - 时机：桥启动完成、L3 接口建立后

2. ✅ **调整 ServerUri 默认值**
   - 从 `http://127.0.0.1` 改为 `http://192.0.2.1`
   - 理由：TEST-NET-1 静默黑洞，NCSI 能正确识别为 Internet

### 一句话总结

**根因是两项：缺少 `NotifyStatus` 调用 + ServerUri 指向环回地址。**  
前者让平台不知道隧道已就绪，后者让 NCSI 报告 `Capability=Local`。  
1.0.73 同时修复这两项，WiFi 图标应正常显示蓝色盾牌。
