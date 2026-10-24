# 43 · 官方示例 clean-room 实测：一条停在 Connecting 时拨另一条，官方也卡死，而且没有出口

**问题**：用户报的症状（"其中一个处于连接状态时点连接另一个，会一直显示正在连接中，但就是不连接"）到底是我们发明的 hook 方案造成的，还是官方模型本身就是这样。
**做法**：把微软官方 `UwpVpnPluginSample`（C#）改成**有两条 profile**，其它逻辑一字不动，装机用 UIA 驱动，看官方原样代码会不会走到同一个形状。
**结论（一句话）**：**会。** 官方代码不但在 A 处于 `Connecting` 时照样把 B 拨出去（它压根没有"至多一条活动会话"的闸），而且卡住之后**官方没有任何出口** —— `Disconnect` 按钮此时是灰的，`DisconnectOnClick` 自己又只认 `Connected`，示例全文不调 `TerminateConnection`。本次实测 B 在 `Connecting` 里停了 **≥5 分 56 秒**，`ConnectProfileAsync` 那一次 await **始终没返回**；期间同一台机器上对 A 的三次拨号**每次都正常在 20–30 秒内返回 `ServerConnection` 并回到 `Disconnected`。

---

## 一、被测对象与改动范围（先证明"这是官方"）

| 项 | 值 |
| --- | --- |
| 上游仓库 | `D:\fluentapps\repos\UwpVpnPluginSample`，HEAD `d589fe0`（2022-03-08 "Removed original boilerplate & direct people to the forums"） |
| 构建方式 | VS 18 Community 自带 MSBuild（遵守 AGENTS.md 第 2 条，未自制 ps1）：`MSBuild.exe CSharp\TestVpnPluginApp.sln -restore -p:Configuration=Debug -p:Platform=x64 -p:AppxBundle=Never` ⇒ `exit=0` |
| 产物 | `AppPackages\TestVpnPluginApp_1.0.24.0_x64_Debug_Test\TestVpnPluginApp_1.0.24.0_x64_Debug.msix`，签名者 `CN=gold`（`EDF4E8E0…D509DE9F`），`Get-AuthenticodeSignature` = `Valid` |
| 装机 | PS 5.1 `Add-AppxPackage`，包全名 `TestVpnPluginApp-SL_1.0.24.0_x64__y0tg80em3rma2` |

`git diff --stat` 只有 5 个文件、26 行新增。**拨号与断开两条路径、插件本体全部零改动**：

| 文件 | 改了什么 | 是否影响被测逻辑 |
| --- | --- | --- |
| `TestVpnPluginApp/MainPage.xaml.cs` | `CreateAppProfile()` → `CreateAppProfile(string profileName)`；新增 `PROFILE_NAME_B` 常量与 `CreateAppProfileBOnClick` | ❌ 只多建一条同名不同字的 profile |
| `TestVpnPluginApp/MainPage.xaml` | 多一个「Create Profile B」按钮 | ❌ |
| `TestVpnPluginApp/Package.appxmanifest` | `MaxVersionTested` 21329→26100；`Publisher` `CN=TestingCertForDebugging`→`CN=gold` | ❌ 环境适配（仓库里 `.pfx` 被 gitignore，换本机已受信的 `ClashUWP_TemporaryKey.pfx` 免 UAC） |
| `TestVpnPluginApp/TestVpnPluginApp.csproj` | `TargetPlatformVersion` 22000→26100、`TargetPlatformMinVersion` 18362→19041（NU1201：组件工程要求 uap10.0.19041）、换签名证书 | ❌ |
| `TestVpnPluginAppBg/TestVpnPluginAppBg.csproj` | `TargetPlatformVersion` 22000→26100 | ❌ |

`ConnectOnClick`（`:465`）、`DisconnectOnClick`（`:525`）、`GetConnectableProfilesAsync`（`:105`）、`GetFirstConnectedProfileAsync`（`:87`）、`UpdateTextBlocks`（`:268`）、`TestVpnPluginAppBg/VpnPlugin.cs` 全部与上游逐字相同。

**为什么示例天然能造出这个形状（我上一轮判断错过一次，这里订正）**：示例的 `PROFILE_NAME` 只是"建 profile"时用的常量，`ConnectOnClick` **完全不参考它** —— 它 enumerates `GetConnectableProfilesAsync()`（`:468`，过滤条件只有 `== Disconnected`，见 `:111`），多于一条就弹官方的「Unconnected Profiles」选择框。也就是说**官方示例本来就是多 profile 的**，只是默认只建一条。

---

## 二、一手时间线（UIA 逐步驱动，读的都是官方自己写的界面）

前提：官方 profile 里硬编码 `ServerUris = http://10.137.192.135`（`MainPage.xaml.cs:164`）。本机对它**没有可达路径**（`Find-NetRoute` 走 WLAN/192.168.0.1 出默认路由，实测 `Invoke-WebRequest` 超时、无 ICMP 回）⇒ 这是个静默黑洞，插件永远连不上、**隧道永远起不来**，符合"实测不得占整机网络"。

| 时刻 | 动作 / 观测 | 关键读数 |
| --- | --- | --- |
| 06:10:33 | 启动示例（pid 16996） | Connect/Disconnect 均 `[disabled]`，无 profile |
| 06:11:0x | invoke「Create Profile」、「Create Profile B」 | Status：`Disconnected from …Autoprofile` / `Disconnected from …Autoprofile B`；Connect 变可用 |
| 06:11:3x | invoke Connect ⇒ 官方弹「Select profile to connect」（列表两条）⇒ 选 A ⇒ 弹内 Connect | 日志 `Attempting to connect.` / `Profile current connection status code starts as Disconnected`；Status：**`Connecting to Test VPN Plugin Autoprofile`** |
| （同一时刻） | 检查按钮 | **Connect 仍然可用**、**Disconnect `[disabled]`** |
| 06:11:4x | 再 invoke Connect ⇒ **没有弹选择框**（此时可连的只剩 B，`list.Count > 1` 不成立）⇒ 直接拨 B | Status：A `Connecting`、B `Connecting` **同时两行**；Connect 转为 `[disabled]`（两条都不再 `Disconnected`） |
| 06:12:06 | 读 Status / 日志 | A 回到 `Disconnected`；**B 仍 `Connecting`**；日志计数 = 2×`Attempting` + 1×`ERROR … ServerConnection` |
| 06:12:33→06:13:31 | 每 6–7 秒轮询 10 次 | B **一直** `Connecting`，无变化 |
| 06:14:07 | 检查 | A `Disconnected` ⇒ **Connect 又可用**；Disconnect 仍灰；默认路由仍只有 WLAN 一条 |
| 06:14:37 → 06:15:05 | invoke Connect（第 3 次，拨 A） | A `Connecting` 约 28 秒后回 `Disconnected`，日志出现第 2 个 `ERROR … ServerConnection` |
| 06:15:59 → 06:16:29 | invoke Connect（第 4 次，拨 A） | 同样约 30 秒后回 `Disconnected`，日志第 3 个 `ERROR`；**B 全程 `Connecting`** |
| 06:17:29 | `Stop-Process` 杀掉插件宿主进程（16620） | **B 的 `Connecting` 没有被清掉**（06:17:35→06:18:02 持续 6 次轮询） |
| 06:18:02 | 停止观察 | B 从 06:12:06 确认起累计 **≥5 分 56 秒** |
| 之后 | 关前台应用、`Remove-AppxPackage` | profile 随包消失，机器复原 |

**计数法锁定"哪一次 await 没返回"**（示例的日志不带 profile 名，所以单看文本会有歧义，用计数收口）：4 次 `Attempting to connect.`、3 次 `ERROR … ServerConnection`、**0 次 `Profile connected OK`**。三次拨 A 各自都在 20–30 秒内给出了 ERROR（第 3、4 次的时刻是当场掐表测的），剩下没返回的那一次只能是 06:11:4x 那次拨 B。

---

## 三、四条一手结论

**1）"连着 A 时点另一条"这个动作，官方代码自己就会做，不需要我们任何发明。**
`ConnectOnClick`（`:465-518`）的过滤条件只有"这条 profile 是不是 `Disconnected`"（`:111`、`:506`）。A 处于 `Connecting` 时它既不在可连列表、也不会**拦住**这次点击 ⇒ 官方直接对 B 发 `ConnectProfileAsync`（`:508`），**没有任何"先拆掉活动会话"的动作**。这跟官方 sample 自己 `:87` 那句注释"There should be zero or one of these at all times"是矛盾的：它假设活动会话至多一条，却没有写这个闸。⇒ 我们的 F3（`SerializeBehindActiveSessionAsync`）不是多余的 hook，**是补官方示例没写的那个断言**。

**2）卡住是真的卡住：一次 `ConnectProfileAsync` 在本机实测里 5 分 56 秒以上没有返回，平台侧状态一路 `Connecting`。**
根因位置在官方插件里，肉眼可见：`VpnPlugin.cs:317-318`（UDP 同形 `:290` 附近）
```csharp
Task t = udpSocket.ConnectAsync(server, config.portServiceName).AsTask();
t.Wait();                     // ← 无上限、无取消、无超时
```
这正是我们在 1.0.88 里给它加 3 秒上限的那一行（Docs/38 §十一）。**官方这份代码里它就是无界的**，所以"卡住"不需要我们的代码参与就能成立。

**3）卡住之后官方没有任何出口，比我们的产品更少。**
三层都堵死：
- UI 层：`UpdateTextBlocks` 只在 `Connected` 分支置 `isDisconnectable`（`:287-291`），`Connecting` 分支（`:292-294`）什么都不设 ⇒ `DisconnectButton.IsEnabled = false`（`:313`）。实测整个 `Connecting` 期间 Disconnect 一直是灰的。
- 代码层：就算按钮能点，`DisconnectOnClick` 也只会打一行日志然后什么都不做 —— 它先取 `GetFirstConnectedProfileAsync()`（`:528`，`Connecting` 也算，见 `:92-93`），但紧接着 `:532` 要求 `== Connected` 才发 `DisconnectProfileAsync`。
- 平台调用层：示例全文一次都没调用 `VpnChannel.TerminateConnection`（失败只 `throw`），也没有任何"取消这次建连尝试"的原语。⇒ **杀掉插件宿主进程都不解**（实测 06:17:29 杀完，`Connecting` 持续到卸载包为止），这一点我们此前只在条文层面推断过，现在有一手。

**4）"返回值"和"真实状态"必须分开看，这次连官方形状都反过来了。**
我们 Docs/38 §十二量到的是"平台能在插件回调跑完之前就把 RPC 返成 `Ok`"。本轮是镜像的另一半：**同一条 profile 上，RPC 能在 profile 还挂着 `Connecting` 时就把 `ServerConnection` 交回来**（第 3、4 次拨 A：20–30 秒返回，A 随即 `Disconnected` —— 这条是干净的），而对 B 的那一次**既不返回、状态也不推进**。⇒ 任何"只信返回值"或"只信状态"的实现都会在这一刻读反；`返回值 + 轮询真实状态` 两段式是必需的，不是我们加的复杂度。

**旁证（对装机预算有用）**：黑洞服务器上，一次正常拨号的 `ConnectProfileAsync` 往返是 **约 20–30 秒**（本轮 3 个样本：≈28 s / ≈28 s / ≈30 s）。这与 Docs/38 §十三在 1.0.88 上量到的"正常拨号 await 1.5–2.3 s"是两类形状（那三次平台真给了答案），也和 §11.6 那条被我订正过的 2000 ms 判据同一族：**"返回慢"和"没返回"之间只差一次 await 是否落地**，判据必须按后者写。

---

## 四、对照表：官方缺的 / 我们补的 / 还欠的

| 环节 | 官方示例（本轮实测） | 我们（1.0.88–1.0.91，Docs/38） | 判断 |
| --- | --- | --- | --- |
| 活动会话至多一条 | **无闸**，Connecting 时照样拨下一条 | `SerializeBehindActiveSessionAsync`（`VpnProfileSetup.cs:457-476`）+ 按钮/切换共用 `FindActiveSessionAsync` | 我们补的是官方断言里写了却没实现的那条 |
| 插件 `Connect` 内阻塞 | **无上限** `t.Wait()` | 3 秒上限 + `connect-window` 两行留痕 | 官方=病根，我们=已修 |
| 发起行留痕 | 只有 `Attempting to connect.`（不带 profile 名 ⇒ 事后无法归因，本轮被迫用计数法） | F1 拆两行、发起行在 await **之前**、带 profile | 官方自己的取证缺口比我们大 |
| `Connecting` 时的出口 | **三层全堵**，杀进程都不解 | 门控后的 `AbortConnectionAttempt` / `TerminateConnection`（重连时不调，Docs/38 §十四） | 我们有出口，但**正向分支装机 0 样本**这条债仍在 |
| 返回值语义 | 打 enum 英文名，不做解释 | `ExplainDialError` 19 项中文（除 `Ok` 全无装机样本） | 我们的增量 |
| 悬挂时的并发保护 | **无**；本轮"卡住的 B"与"正常的 A"在同一条 profile store 上并发跑了 3 次 | `ProfileStore.Run` 四出口（排队 ≤3 s、超时不释闸） | 官方形态正是我们 F6 要避免的 |

**这轮没有推翻任何已有结论**；它把 Docs/38 的两条"条文推断"升级成了一手：①插件回调里的无界 `Wait()` 会造成 ≥5 分钟不返回；②`Connecting` 期间管理面无出口、连杀宿主进程都不解。

---

## 五、未证 / 风险 / 样本量

- **n=1**。一次实验、一条 B。"卡多久"只测到 5 分 56 秒（我主动停的），**不是"永久"的证明**，只是"实测未在 6 分钟内自行恢复"。
- 无法直接读到插件侧 `LogMessage`（示例的 Logging 面板只显示前台 `Log()`，插件日志走 `VpnChannel`，本轮没建采集臂）⇒ 第 2 条结论里"卡在 `t.Wait()`"是**代码位置 + 外部形状**的合理解释，不是插件日志的直接读数。
- "三次 A 的 ERROR 分别属于哪一次拨号"靠日志计数与时序收口，示例日志不带 profile 名 ⇒ 归因方式已在表中写明，可复核。
- 本轮**没有**复现"两条都卡住"；官方形状是"后拨的那条卡住、先拨的那条正常失败"。这与用户报的症状是否同一条，仍未证 —— 症状（常驻 `Connecting` 且用户看不到任何退出）现在**在官方代码里成立**，但它是否就是用户当时点的那一下，取决于当时走的是 app 还是快速面板（Docs/38 §〇：那段时间我们前台零"用户发起"行）。
- 未改动上游仓库以外的任何东西：ClashUWP `1.0.91.0` 全程未被动过（收工读数见 §六）。

---

## 六、收工判据（全部实跑）

| 判据 | 读数 |
| --- | --- |
| 隧道 / 默认路由 | `Get-NetRoute 0.0.0.0/0` 实验全程与收工后都只有 **1 条**（WLAN，`NextHop 192.168.0.1`），ifIndex 22 |
| NRPT 残留 | `Get-DnsClientNrptRule` = **0** |
| 死接口 / 幽灵路由 | `Get-NetAdapter`：WLAN Up，蓝牙 PAN `Disconnected`（未被牵动），`本地连接 2` Disconnected，`vEthernet (Default Switch)` Up —— 与实验前一致 |
| 自动重拨 | 杀插件宿主后 `Get-Process TestVpnPluginApp` 只剩前台 16996，**没有再生**；卸载后进程 0 |
| 示例包 | `Remove-AppxPackage` 后 `Get-AppxPackage -Name TestVpnPluginApp-SL` 空、`%LOCALAPPDATA%\Packages\TestVpnPluginApp-SL_*` 目录数 0 |
| 我加的依赖 | 为跑 Debug 包临时装的 `Microsoft.NET.Native.Framework.Debug.2.2`（x64）**已卸**；原有 `NET.Native.Framework.2.2`（2 个）/ `VCLibs.140.00`（2 个）计数不变 |
| 产品包 | `8B8CC4F4.ClashUWP_1.0.91.0_x64__4tjvwvefxrz66` `Status=Ok`，未被本次任何操作触碰 |
| profile 归属位置（顺带证实） | 示例的两条 profile 落在 **`%LOCALAPPDATA%\Packages\TestVpnPluginApp-SL_y0tg80em3rma2\LocalState\rasphone.pbk`**，`C:\ProgramData\...\Pbk\rasphone.pbk` 不存在、`Get-VpnConnection -AllUserConnection` 空 ⇒ 我们 memory 里"profile 只在包内 pbk、桌面看不见"是**平台对 packaged VPN 插件的通用行为**，不是我们产品的怪癖 |

---

## 七、复现步骤（要再跑就用这个）

1. `MSBuild.exe D:\fluentapps\repos\UwpVpnPluginSample\CSharp\TestVpnPluginApp.sln -restore -p:Configuration=Debug -p:Platform=x64 -p:AppxBundle=Never -m`
2. PS 5.1：先 `Add-AppxPackage` 依赖里的 `Dependencies\x64\Microsoft.NET.Native.Framework.Debug.2.2.appx`（**不要**带上 VCLibs / NET.Native.Runtime 那两个，机器上已有更新版且在用 ⇒ `0x80073D02`），再 `Add-AppxPackage` 主 `.msix`。
3. `Start-Process "shell:AppsFolder\TestVpnPluginApp-SL_y0tg80em3rma2!App"`；窗口在 `ApplicationFrameHost`，`winapp ui list-windows | grep TestVpn` 取 frame HWND（本轮 5112764）。
4. `winapp ui inspect -w <HWND> -i -d 8`（注意：`-w` 要跟在子命令后面，不是 `winapp ui -w`）；依次 invoke `btn-createprofile-872b` → `btn-createprofileb-872d` → `ConnectButton` →（弹框）列表项 → `PrimaryButton`；再 invoke `ConnectButton` 读第二次结果；`winapp ui get-value Status` / `get-value Logging` 读数。
5. 收工：`Stop-Process` 两个 pid → `Remove-AppxPackage -Package TestVpnPluginApp-SL_1.0.24.0_x64__y0tg80em3rma2` → 再卸 Debug 框架 → 跑 §六 的判据。
