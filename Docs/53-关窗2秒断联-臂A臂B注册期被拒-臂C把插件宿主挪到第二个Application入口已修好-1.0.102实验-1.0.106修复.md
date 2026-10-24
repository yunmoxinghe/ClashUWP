# 53 · 关窗那 2 秒断联：多实例两臂被拒（1.0.102/1.0.103），臂 C 换"入口身份"把它修掉了（1.0.106）

用户口径：**"修复关闭窗口短暂断联问题"**。

★ 本篇的结论在 1.0.106 换了：**臂 A / 臂 B 确实被平台在注册期判死（§二，那两条留档不动）**，
但"同一包内无解"这句是错的 —— 还有第三根杠杆<b>入口身份</b>没试。
**臂 C（§五）装机实测成立：关窗后宿主进程与隧道原样活着，2 秒断流消失。**
1.0.102 这个版本号只存在于两次"注册失败"的包，从未装机，整机网络零影响。

---

## 一、症状与机制（都是现读装机实测，不是推演）

| 时刻 | 事件（Docs/31 §4.3，1.0.77 真拨号取证） | 判读 |
| --- | --- | --- |
| 04:35:39.954 | `pid=15504 bgtask 后台任务被平台取消（Canceled）——宿主进程可能被回收` | 用户关掉窗口的这一刻 |
| 04:35:41.570 | `pid=5160 app 启动 version=…/1.0.77.0` | 平台<b>另起一个进程</b>（无头宿主） |
| 04:35:42.594 | `pid=5160 connect 平台发起连接 #1 resident=全新内核` | 重拨，内核从零起 |

机制链（每一环都有 file:line）：

1. 清单只有一个 `<Application>`，`windows.backgroundTasks` 的 `vpnClient` 与 `windows.appService` 都声明在它下面，
   插件类由 Package 级 `inProcessServer`（`Path=ClashUWP.dll`）进程内加载 —— `Package.appxmanifest:120-143` + `:219-226`；
2. 单实例语义 ⇒ 前台在跑时，vpnClient 激活被路由进<b>同一个 pid</b>（1.0.77 实测：插件日志与 app 日志同 pid；
   Docs/47 §六 P1、清单注释【进程隔离的实测依据】）；
3. UWP XAML 窗口（`Reactor.Uwp` 的 `ReactorApplication<MainComponent>`，`App.cs:30`、`App.cs:241-263`）
   最后一个视图关闭 ⇒ 进程被回收，**没有任何可拦截的 Closing API**；
4. `VpnBackgroundTask.Run` 的 deferral 在 `finally` 里就 `Complete()`（`Vpn/VpnBackgroundTask.cs:117`+`:129`）
   ⇒ 插件靠<b>宿主进程活着</b>继续跑，进程一死平台就 `Canceled`（`:74-80`）；
5. 平台拆掉连接、按自己的节奏重拨（≈1.6 s），新宿主 pid 下 `s_bridgeResident` 是进程内静态位、必然 false
   （`Vpn/ClashVpnPlugin.cs:84`、`:479-482`）⇒ 走全量 `clash_bridge_start`（≈0.4 s 冷启，`:2311-2355`）
   ⇒ 合计约 2 s 的整机断流。

**"关掉界面隧道仍在"是成立的**（隧道自己回来了），本批要消掉的是中间那 2 秒。
⇒ 上面这五环讲的是<b>1.0.103 及以前</b>的形状；第 2 环"单实例把插件路由进 UI 进程"这一条
在 1.0.106 被臂 C 拆开了（§五），其余四环逐字未变。

---

## 二、本批做了什么：把"推演"换成"实测"

1.0.77 起这条判词一直是**推演**（Docs/31 §4.3 / Docs/29:144 R6 / Docs/47 §一：
"不上多实例，代价是 CLI 通道另开实例，收益只是省掉间隙"），而 Docs/47 §七.1 自己写着
"多实例下 vpnClient 路由到哪个实例 = 0 样本，从未真开过试"。用户既然要求修，就先把它试出来。

### 臂 A —— 只加多实例属性

```xml
xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
IgnorableNamespaces="uap mp rescap uap10"
<TargetDeviceFamily MinVersion="10.0.14393.0" MaxVersionTested="10.0.26100.0"/>   <!-- uap10 是 1607 引入，把 §七.3 那个变量先答掉 -->
<Application Id="App" Executable="$targetnametoken$.exe" EntryPoint="ClashUWP.App"
             uap10:SupportsMultipleInstances="true">
```

`msbuild /t:Publish` **过了**（MakeAppx 接受 ⇒ §七.3 的"打包期会不会拒"答案是**不拒**），装机时死在注册期：

> `Add-AppxPackage : Deployment failed with HRESULT: 0x80073CF6, 无法注册包。`
> `AppxManifest.xml(142，10): 错误 0x80080204: 无法注册 … 程序包，因为在 SupportsMultipleInstances 为 true 时无法指定可执行文件。`
> ActivityId `f671dbad-60a3-0000-e44c-8ef8a360dd01`

142 行第 10 列经解包核对，指的不是 `<Application>`，而是这一行：

```xml
<Extension Category="windows.backgroundTasks" Executable="ClashUWP.exe" EntryPoint="ClashUWP.Vpn.VpnBackgroundTask">
```

### 臂 B —— 保留多实例、删掉那个 Executable

理由写在当时：平台加载插件走的是 Package 级 `ActivatableClassId` + `inProcessServer` 的 `Path`（清单顶部【事实 2 的根因更正】），
Extension 上的 `Executable` 可能只是冗余。结果：

> `AppxManifest.xml(150，10): 错误 0x80080204: 无法注册 … 程序包，因为此类型的任务需要自定义后台任务宿主。`
> ActivityId `f671dbad-60a3-0004-dcc5-d6f8a360dd01`

⇒ vpnClient 这类任务**必须**带自定义宿主声明，Extension 上那行 `Executable` 不能删。

### 判语

**两条要求互斥：`uap10:SupportsMultipleInstances` 与"进程内 vpnClient 后台任务"在本包清单里不能共存。**
这条杠杆到此不是"没试"，是**平台在注册期拒了两次**。
⇒ 1.0.103 把清单三处改动（`xmlns:uap10` / `IgnorableNamespaces` / `MinVersion`+`MaxVersionTested` / Application 属性）
原样撤回，与已提交认证的商店包保持逐字同一形态；两个报错作为实测依据留在 `Package.appxmanifest` 的注释里。

---

## 三、还剩什么路（都没做，代价写明白）

| 候选 | 能不能消掉那 2 秒 | 代价 / 风险 | 状态 |
| --- | --- | --- | --- |
| **臂 C：同包内加第二个 `<Application Id="VpnHost">`，把 vpnClient 任务的宿主挂到它上面** | ✅ **实测能**（§五） | 只动清单，插件代码/`inProcessServer`/日志三处口径一律不变 ⇒ 与 Docs/29 那次回收<b>不冲突</b>；代价是清单形态与已提交认证的商店包不再逐字相同（§五.④） | **1.0.106 已装机采用** |
| **把插件拆成独立的 VPN 插件包**（官方"UI 包 + 插件包"两包模型，sample 拆两个 `<Application>` 就是为了进程隔离） | 原理上能，但臂 C 已在单包内拿到同一个结果 | 与 Docs/29 那次"插件回收进主工程"**方向相反**，要重做清单/部署/日志三处口径；商店侧已提交认证的是单包形态（Docs/41/42），`networkingVpnProvider` 能力归属也要重谈 | 从今起**不需要**，除非臂 C 在认证流水线被拒（未证） |
| 关窗后让 UI 进程不死（多视图/隐藏视图保活） | 可能 | UWP 没有可拦截的 Closing API；"藏一个视图骗过生命周期"属于未证实且商店认证政策敏感的旁门 | 未试，不建议 |
| 缩掉间隙里的内核冷启部分（`resident=全新内核` 那 ≈0.4 s） | 只能缩短，不能消除 | `s_bridgeResident` 是进程内静态位，跨宿主必然 false；没有跨进程复用内核的手段 | 臂 C 之后<b>根本没有这一段</b>：宿主不死 ⇒ `s_bridgeResident` 一直是 true（§五.③ 实测同一 pid 连拨两次会话，第二次没走 `clash_bridge_start`） |
| 什么都不做，只保证"隧道自己回来" | — | 用户体感仍是"关窗瞬间掉 2 秒" | 1.0.103~1.0.105 是这一档，1.0.106 起不是了 |

★【2026-10-23 补一行，因为商店那一维改了判】上表默认"臂 C 原样进商店"，但次级入口带 `AppListEntry="none"` 会被包验收拒（除非账号级 `HeadlessAppBypass` 豁免，见 §五.⑥ 第 1 条）。所以真实选项是三个，不是一个：

| 形态 | 关窗断流 | 商店侧 | 开始菜单 |
| --- | --- | --- | --- |
| 臂 C + 保留 `AppListEntry="none"`（= 今天 1.0.106） | ✅ 已装机证明无 | 需账号级豁免，我们有没有 = 0 样本，只能工单问 | 1 条 |
| 臂 C + 删掉该属性 | 理论上不变（隔离靠的是第二个入口身份，不靠隐藏），**但未装机复验** | ✅ 不碰豁免这条路（HostsFileEditor 实走形态） | 2 条（多一条"ClashUWP VPN 插件宿主"） |
| 回退单入口（= 1.0.105 及以前） | ❌ 那 2 s 回来 | ✅ 与进审那份逐字相同 | 1 条 |

---

## 四、和批 A（Docs/52）的关系

批 B 这两臂**只动清单、没装机**，所以 Docs/52 的写入闸与本批无耦合；
1.0.103 = 批 A 代码 + 撤回后的清单。装机验收只做 Docs/52 §五 那六条，判据与拨号预算在那边。
（臂 C 动了清单并装机，见 §五；那两次拨号同时把 Docs/52 §五 的判据跑完了，两边证据不混。）

---

## 五、臂 C —— 把任务的宿主挂到第二个 Application 入口（1.0.106，已装机、已修好）

### ① 改了什么（只有一个变量）

| 位置 | 改动 |
| --- | --- |
| `Package.appxmanifest:221-243` | 新增第二个入口 `<Application Id="VpnHost" Executable="$targetnametoken$.exe" EntryPoint="ClashUWP.App">`，`VisualElements` 带 `AppListEntry="none"`；`windows.backgroundTasks`（`Executable` + `EntryPoint=ClashUWP.Vpn.VpnBackgroundTask` + `<uap:Task Type="vpnClient"/>`）**整体从 App 入口搬到这个入口下** |
| `Package.appxmanifest:159-168` | App 入口原位只留存档注释（臂 A/臂 B 的两条 0x80080204 原文、`Executable` 不能删的那一条） |
| 其余 | **一律不动**：插件代码、Package 级 `windows.activatableClass.inProcessServer`（`:290`，`Path=ClashUWP.dll`）、`ActivatableClassId` 必须与 `Task` 逐字相同的老约束、日志三通道；`EntryPoint` 故意仍填 `ClashUWP.App`，让宿主进程里的运行时/激活行为与今天逐字相同 |

两件事要说清，否则像是推翻旧结论：

- **没有推翻 Docs/29**。那次"插件回收进主工程"的真根因是 CsWinRT 不给非组件工程注册激活工厂（`Docs/29:31`，`Generator.cs` 的 `IsCsWinRTComponent()` 分支），从来不是清单入口的个数；代码合并照旧，`inProcessServer` 仍指同一个 `ClashUWP.dll`。臂 C 只换了<b>任务宿主挂在哪个入口</b>。
- **臂 A 的互斥依然成立**：仍然没有 `uap10:SupportsMultipleInstances`。"多实例 + 进程内 vpnClient 任务"在<b>同一入口</b>下被注册期拒了两次（§二）；臂 C 换的是入口身份，不是实例语义。

★ **臂 C 不是我们发明的形状，是微软给 VPN 插件的上架形状**（2026-10-23 现读补，起因是用户「无头应用不能上架，联网搜索最佳实践」）：
官方 `microsoft/UwpVpnPluginSample` 的 `CSharp/TestVpnPluginApp/Package.appxmanifest`（取法：`gh api repos/microsoft/UwpVpnPluginSample/contents/CSharp/TestVpnPluginApp/Package.appxmanifest --jq '.content' | base64 -d`，逐字原文）就是**一个包两个 `<Application>`**，第二个 `<Application Id="Plugin">` 带 `AppListEntry="none"` 承载 `<uap:Task Type="vpnClient" />`，并自带这句注释：

> "Notice how the foreground app and the background app are in two separate applications, this is vital to keep them running in two separate processes, so if one is closed, it won't terminate the other."

⇒ 与臂 C 的动机逐字同一。差别只有两处，且都不影响结论：sample 的 `Plugin` 入口不写 `Executable`/`EntryPoint`（由 Extension 提供），我们写了；sample 是两工程两包，我们是单工程单包（Docs/29/31 的回收成果保留）。完整证据表（条文 `AppListEntry` 无权限门禁、`HeadlessAppBypass` 不在当前能力表、Store 策略 10.3/10.4 只约束"产品可测可启动"、本机 32 个多入口包全部带隐藏次级入口）在 [Docs/47 §五/§十](47-唯一数据源-清单与进程实例模型-多实例与无头口径.md)。

### ② 注册期：臂 A/B 的死因没有再现

- `msbuild /t:Publish`（UWP 桥 `-Target Publish`）→ `PackageSuccessfullyCreated`，用时 127s；
- `signtool sign /fd SHA256 /sha1 FB3309A6…` → `Successfully signed`；
- `Add-AppxPackage` → **INSTALL OK**，`Version=1.0.106.0`。**0x80080204 没有出现**（这一步正是臂 A/臂 B 死掉的地方）。
- 回读装机清单（`Get-AppxPackageManifest` —— 判据读盘，不读源码）：

  | 入口 | Extension | AppListEntry |
  | --- | --- | --- |
  | `App` | `windows.appService` + 2×`windows.protocol`，**backgroundTasks=0** | （默认可见） |
  | `VpnHost` | `windows.backgroundTasks` Executable=`ClashUWP.exe` EntryPoint=`ClashUWP.Vpn.VpnBackgroundTask`，task=`vpnClient` | `none` |

- `Get-StartApps` 只有 `ClashUWP → 8B8CC4F4.ClashUWP_4tjvwvefxrz66!App` 一条 ⇒ 第二个入口不摆开始菜单，用户可见面没多一条磁贴。

★ **顺带订正一条旧账**：`Docs/25-验收报告 §1.3` 把两件事一起改了 —— 「Plugin 入口缺 `Executable`/`EntryPoint` ⇒ MakeAppx 80080204」和「移除 `AppListEntry="none"`（无头应用豁免需 Partner Center 申请）」，**从没分开验证过哪一条才是因**。今天这个入口是 `AppListEntry="none"` + `Executable` + `EntryPoint` 三样齐全，本地注册直接通过 ⇒ "无头应用要申请豁免"这句在<b>本地注册</b>这条路上被证伪，真正的因是缺 `Executable`/`EntryPoint`。商店认证流水线会不会因 `AppListEntry="none"` 拒 = 0 样本，见 §五.⑥。

### ③ 装机判据：宿主进程与界面进程真的分开了

第一次会话（`includeMode=narrow`，默认路由全程留 WLAN）：

| 时刻 | 证据（原文） | 判读 |
| --- | --- | --- |
| 07:38:08 | `pid=9488 app 启动 version=…/1.0.106.0` | 界面进程 |
| 07:39:20.986 | `pid=6368 app 启动 version=…/1.0.106.0 pid=6368` | 点「连接 VPN」后平台另起一个进程 |
| 07:39:22.122 | `pid=6368 plugin I bgtask 插件进程启动 … pid=6368` | ★ **宿主 6368 ≠ 界面 9488** ⇒ §二 判语里"本包无解"那句被推翻 |
| 07:39:22.577 | `L3 隧道接口已建立：includeMode=narrow … outer=192.0.2.1:9` | 隧道起，ifIndex 47 = `iKuuu_V2` Connected |
| 07:39:24.056 | `clash_bridge_start 返回 rc=0 用时=291ms` | 本次冷启（该进程首次） |
| 07:39:24.279 | `Connect 回调内累计阻塞=3070ms` | 与 1.0.105 同一量级 ⇒ 搬入口没把这段改慢 |
| **07:41:25** | UIA invoke「关闭 ClashUWP」 | 用户动作 |
| 07:41:26.104 | `pid=9488 app I lifecycle Suspending` | 界面进程从此消失；**plugin.log 没有 `Canceled`** |
| hb 9 → hb 10 | `07:41:22 alive=117s` → `07:41:35 alive=130s` | ★ 严格 +13s 连续，关窗时刻正好落在两跳之间 ⇒ **零断流** |
| 07:41:28 / :53 / 07:42:19 | 宿主 3 轮 `Suspending`→`Resuming`→`bgtask 插件进程启动 pid=6368`，`run-instance reused=1`、`connectCalls=1` | 每 ~25s 的重激活<b>不是臂 C 带来的</b>：1.0.105 的老会话（07:16~07:21，同进程宿主）同样 14 次。同一 pid、同一实例复用，无重复 Connect |
| 07:42:44 / 07:42:54 | `dataface-sessions.txt`、`watchdog.txt` 仍由 6368 写；`223.5.5.5=OK fake-ip=OK:198.18.0.18 内核=OK:1.10.0`，`encap` 8148→8871→9547 | 无头期间隧道真在搬运 |
| 07:43:33 | 重开界面（新 pid 16840）状态行 `当前状态：Connected`、`代理组 11 个　活动连接 15 条` | 前台事后跟上了无头会话 |
| 07:43:58.092 | `session 会话结束，存活 276s encap=12748 inj=15738` | 用户主动断开；276s 内无重建 |

### ④ 长跑：窗口关了 8 分 49 秒，会话没断

第二次会话，拨号后**立刻**关窗（用户的真实动作顺序）：

| 时刻 | 证据 | 判读 |
| --- | --- | --- |
| 07:46:47.025 | `connect 平台发起连接 #2 resident=复用常驻内核 pid=6368` + `bridge-start 复用常驻内核（排空 125 包，配置未变）` | ★ 臂 C 的第二个收益：宿主不死 ⇒ `s_bridgeResident` 保住 ⇒ 二次拨号不再走 `clash_bridge_start`；`Connect 回调内累计阻塞` 从 3070ms 降到 **1207ms** |
| **07:47:07** | 关窗，界面进程 16840 被 reclaim | 此后机器上只剩 6368 |
| 07:47:09 | 宿主 `Suspending`→`Resuming`，无 `Canceled`、无 `会话结束` | 关窗后 2 秒 —— 旧形状在这里就该断了 |
| 07:52:53 | `hb 27 alive=363s 223.5.5.5=OK fake-ip=OK:198.18.0.18` | 无头 5m46s，心跳单调 |
| 07:55:44 | `hb 38`；`Get-Process ClashUWP` 只回 `6368` | 无头 8m37s |
| 07:55:56 | `session 会话结束，存活 549s encap=21328 inj=24251` | 549s = 9m09s，其中窗口关闭状态下 **8m49s**；全程心跳没跳号 |

### ⑤ 收工安全核对（本轮两次拨号的账）

隧道接口残留 **0** / NRPT 规则 **0** / 默认路由 **WLAN 192.168.0.1**（从未移走）/ 无适配器接口上的路由 17（本机既有基线，非残留，口径见 [[feedback-vpn-forensics-metrics]]）/ 台账 `profile-reconcile-owed.txt` **0 字节** / 无 `debug-cmd.txt`。
两次断开都由用户动作发起（`profile-disconnect 断开=Ok`），最后一次之后 plugin.log 里 `平台发起连接` 与 `Canceled` 各 **0** 条 ⇒ **没有东西在自动重拨**。

### ⑥ 臂 C 没覆盖的（别当成全绿）

1. **商店侧**：已提交认证的那份包（Docs/41/42）是<b>单入口</b>形态，本清单多了一个 `Application` ⇒ 两者不再逐字相同，下次提交要用新包。
   ★ **2026-10-23 同日二次改判（上面那句"几乎不可能但仍未实测"撤回）**：第三方两家一手写明 —— 次级入口带 `AppListEntry="none"` **会被商店包验收拒**，除非开发者账号已拿到账号级 `HeadlessAppBypass` 豁免。`scottlerch/HostsFileEditor` 撞的形状（可见主入口 + 第二个想隐藏的入口）与臂 C 同形，原文 "hiding it (AppListEntry=\"none\") requires the account-level \"HeadlessAppBypass\" waiver, without which **Store package validation rejects the submission** (hit on the 1.5.0/1.2.0 release)"，而他最后的处理是**放弃隐藏**；`pierreaubert/sotf` 独立同口径。⇒ 分两维：**本地这一维不拦**（上面已证），**商店这一维按会拦来计划**。
   两条出路，都不动插件代码、不丢本节那个修复：① 删 `VpnHost` 的 `AppListEntry` 属性，代价=开始菜单多一条"插件宿主"（⚠️ 删完之后的进程隔离**未装机复验**，真走时按 §五.③ 判据重跑一次拨号）；② 开 Partner Center 工单申请账号级豁免（~~通道与时长 0 样本~~ ⇒ 通道已实测走通一次，见下；时长仍 0 样本；NanaZip 带两条隐藏入口已上架 ⇒ 豁免拿得到，不是死路，但也至少有一家选择绕过去而不是等它）。证据与判据边界：Docs/47 §四 T16/T17、§五·甲 第 8–10 条、§五·丙、§七 第 7/7b 条。
   ★ **2026-10-23 豁免工单已提交（出路② 的第一个样本）**：Partner Center → 帮助+支持 → aka.ms/storedevsupport → Engage Center（Support for Business）→ 新建支持请求。产品=Windows Developer Center，问题类型/子类型=App Management，支持计划=Professional No Charge，严重性=C，联系方式=Email（营业时间）。摘要 "Request account permission: HeadlessAppBypass exemption for MSIX package with AppListEntry=\"none\" background-task entry"；详细描述给出 Product ID 9NV6BMCQ21G1 / PFN 8B8CC4F4.ClashUWP_4tjvwvefxrz66、`VpnHost` 次级入口与官方 NetworkVpnPlugin sample 同构的动机、商店拒绝原文（"You don't have permission to create headless apps…"）、以及"本地 Add-AppxPackage 通过 ⇒ 只卡商店侧验收"。**请求 ID `2610230060002631`，状态 Open（2026-10-23 创建）**，回复走 yunmoxinghe@outlook.com。官方文档侧已核实 `HeadlessAppBypass` 不在受限能力表 ⇒ 没有提交时自选表单那条路，工单是唯一通道；回复时长从今天起开始计样本。
2. **前台哨兵的新暴露面**：Docs/38/46 那条老口径仍在 —— 会话不是前台发起时前台不 Arm。臂 C 之前"关窗 ⇒ 2 秒内平台重拨"，前台在不在无所谓；臂 C 之后无头会话可长期在线，此时**只有插件侧 `physical-watchdog-plugin` 在兜**。这是本轮唯一新暴露出来的面，本批没加固。
   ★ **2026-10-23 当天已修（1.0.107）**：`HealthMonitor.AdoptArm()` 认领模式 + 轮询读到 Connected 且无哨兵即认领；顺带修了同源潜伏 bug（哨兵在会话被快速设置断开后探到硬上限会删掉基础 profile —— 新增"连续两轮终态 ⇒ 哨兵退出不删"出口）。细则与验证状态：[Docs/54](54-前台哨兵认领模式-会话不是本前台发起也有黑洞兜底-1.0.107.md)。
3. **无头宿主的寿命**：只测到 8m49s 在线；会话结束后 6368 仍挂着（`Suspending`，等平台 reclaim）。小时级、以及"整机睡眠/注销后唤醒"这条没测。
4. **面板直连路径**（前台进程从头到尾不存在）在臂 C 下没重测，沿用 1.0.95 的结论。
5. `Suspending/Resuming` 每 ~25s 一轮会不会拖慢插件热路径：没测（心跳仍稳定 13s 一跳，看不出劣化）。
6. §一 那条机制链的第 5 环（平台重拨 + 内核冷启 ≈2s）现在<b>不再发生</b>；但"宿主因别的缘故被杀之后是什么恢复形状"没重测 —— 那条路径理论上仍带那 2 秒。
