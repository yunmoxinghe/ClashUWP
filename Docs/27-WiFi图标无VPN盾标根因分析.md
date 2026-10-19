# 27 · WiFi 图标无 VPN 盾标根因分析与修复

> 日期：2026-10-19（**2026-10-20 更正**，见下面"更正记录"）  
> 现象：开启 ClashUWP VPN 后，WiFi 图标右下角不显示蓝色小盾牌  
> 对标：官方文档明确说明"当连接到可识别的 VPN 时，任务栏上会显示一个蓝色盾牌"  
> **状态：根因 2（`ServerUri`）成立；根因 1（`NotifyStatus`）作废 —— 那是个不存在的 API。**
> 另外：本文写作时以为"1.0.73 已修复并发布"，实际上 1.0.73/1.0.74 的组件从未打包，
> 两项改动**直到 1.0.75（组件 1.0.43）才第一次进包**（取证见 Docs/30 §2.1、§4）。

---

## ⓪ 更正记录（2026-10-20，应用 1.0.75 / 组件 1.0.43）

1. **`VpnChannel.NotifyStatus` 在 26100 不存在**。本文原来写的"根因 1"整段作废。
   证据不是二手文章，是官方投影程序集的**全量成员导出**：
   `ilspycmd -t Windows.Networking.Vpn.VpnChannel`
   跑 `~/.nuget/packages/microsoft.windows.sdk.net.ref/10.0.26100.57/lib/net8.0/Microsoft.Windows.SDK.NET.dll`
   ⇒ 460 行成员里**没有任何 `Notify*`**，接口只到 `IVpnChannel/2/4/5/6`。
   （同法确认 `channel.SystemHealth` 与 `VpnSystemHealth.StatementOfHealth` 确实存在，那两条保留。）
2. **它为什么一直没暴露**：那段代码从未编译过 —— 1.0.73/1.0.74 只 bump 了版本号，
   `dotnet pack` 没跑，本地 feed 里最高只有 `ClashUWP.Vpn.1.0.40`，
   主工程还原报 `找不到版本为 (>= 1.0.42) 的包 ClashUWP.Vpn`。详见 Docs/30 §2.1。
3. **代码已撤销**：`StartMihomoBridge()` 成功分支里那段 `NotifyStatus` try/catch 删除，
   原位留注释说明"插件侧没有上报隧道状态的原语"（`Start(...)` 成功 + 首个包被取走后由平台自己推进）。
   1.0.75 的包里 `NotifyStatus` 字符串取证 = 0 命中。
4. ⇒ **盾标这条线索只剩根因 2**，而根因 2 的效果也必须等 1.0.75 装机后才第一次可验（§6 的验收步骤）。

---

## 一、根因确认（两项，其中第 1 项已作废）

### ~~🔴 根因 1：缺少 `NotifyStatus` 调用~~（**已作废**，理由见 §⓪）

**当时以为的问题：**
- `ClashVpnPlugin.Connect()` 在 mihomo 内核成功启动后，从未调用 `channel.NotifyStatus(VpnManagementErrorStatus.Ok)`
- 【官方】`NotifyStatus`："Reports the status of the VPN connection to the system."
- 平台认为隧道未完全建立，NCSI 不会报告 Internet 连通性

**★ 复查结论**：`NotifyStatus` 这条 API 在本项目用的 SDK（10.0.26100）投影里**不存在**，
`dotnet pack` 直接报 `CS1061: "VpnChannel"未包含"NotifyStatus"的定义`。
上面那句"【官方】"引文找不到对应条文，属于**未经核对的引用**（教训：引官方口径就得把
API 名在元数据里现读一遍，见 memory「Verify official first」）。代码已删除，本节只留作取证记录。

**当时的"修复位置"：**
- 文件：`ClashUWP.Vpn/ClashVpnPlugin.cs`
- 位置：`StartMihomoBridge()` 方法中，桥成功启动分支（`rc == 0`）末尾

**已删除的代码（原样留档，便于对照）：**
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

**结论（2026-10-20 更正）：**
- 不是白名单问题。**但当时给的两句理由里只有一句站得住**：
  `ServerUri` 配置不当（根因 2）成立；`NotifyStatus`（根因 1）是不存在的 API（见 §⓪）。
- ★ 上面那句"实测证明 sideload 也能显示盾标"**当时并没有实测背书** ——
  1.0.73 的改动从未进包（§⓪.2），所以上机看到的"仍无盾标"与"修复未生效"是同一件事，
  不能读成"白名单/平台限制"。盾标是否出现，要等 1.0.75 装机后按 §四 重新验一次才知道。

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

### 4.1 ~~检查 NotifyStatus 是否被调用~~（作废）→ 检查**系统 profile 的 ServerUri**

`NotifyStatus` 不存在，plugin.log 里也不会有 `evt=notify-status`（1.0.75 起那行代码已删除）。
现在这条线索要看的是 profile 里实际存的 `ServerUris`，以及 1.0.75 新增的对账频道：

```powershell
# 对账结果：基础 profile 与订阅 profile 各自的新建/更新/跳过/失败
Select-String -Path "$env:LOCALAPPDATA\Packages\<包家族>\LocalState\logs\app.log" -Pattern "profile-base|profile-sub"
# 期望：断开状态下 "订阅profile[…] 已就位" 或 "更新=Ok"；出现 Skipped 说明当时正在连接，重跑一次对账即可
```

再看系统里那条 profile 的 `ServerUris` 是不是 `http://192.0.2.1`（本机 `rasphone.pbk` 或 VPN 设置页），
旧值 `127.0.0.1` 还在就说明对账没跑到它（Docs/30 §1.4）。

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

## 五、修复总结（2026-10-20 更正）

1. ❌ ~~**添加 `NotifyStatus(Ok)` 调用**~~ —— **作废**：该 API 在 26100 不存在（§⓪），
   代码已在 1.0.75 删除，原位留注释说明"插件侧没有上报隧道状态的原语"。
2. ✅ **调整 `ServerUri` 默认值**（唯一成立的根因）
   - 从 `http://127.0.0.1` 改为 `http://192.0.2.1`
   - 理由：TEST-NET-1 静默黑洞，NCSI 能正确识别为 Internet
   - ★ 而且这条 1.0.73 提交时**并没有发布**：组件没打包、装机的 1.0.72 里跑的还是 1.0.40 的插件，
     它到 **1.0.75（组件 1.0.43）** 才第一次进包（Docs/30 §2.1）。
     同批还有 1.0.74 的 `SetErrorMessage` / `SystemHealth` 两条 —— 也都是这次才真正编译过。
3. ★ 1.0.75 另加一项与本文相关的兜底：**基础 profile 进入启动对账**
   （`VpnProfileSetup.EnsureBaseProfileResultAsync`），
   所以已存在的旧 profile（`ServerUri` 还停在 127.0.0.1 的那批）会在断开状态下被 `Update` 自愈，
   不必手工删除重建（Docs/30 §1.4）。

### 一句话总结

**根因是一项：`ServerUri` 指向环回地址，让 NCSI 报告 `Capability=Local`；**
原来并列的第二项（`NotifyStatus`）经官方元数据复查是**不存在的 API**，已撤销。
本批（1.0.75 / 组件 1.0.43）才是这两条改动第一次真正打进包里，
**盾标是否出现仍未上机验证** —— 按 §四 的步骤验，注意 §五.3 的 profile 自愈需要先断开再触发对账。
