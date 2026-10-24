# 46 · 把前台进程里"和官方示例不同的 hook"删掉（1.0.93）——尺子保留、拦截全拆

**一句话结论**：按指令「把和官方示例不同的 hook 方法都删掉」，切法取**按进程**（用户口径：删代码在前台进程的），本批把前台**连接路径**与**断开路径**上包住官方 `ConnectProfileAsync` / `DisconnectProfileAsync` 的自有拦截全部拆掉（下表五行，含配件），让「连接 VPN」按钮、切订阅、断开这三条路与官方 `MainPage.xaml.cs` 同形。**删除/重建 profile 路径上那道"等会话收干净才肯删"的闸按同一个切法也该删，但本批没动** —— 它背着一件有日期的一手事故，理由写在 §六，等你一句话。跑在插件宿主进程里的出口（`TerminateConnection`、socket 3 s 上限）**一个没动**；跨两进程共享的 `ProfileStore` 串行闸与 `GracefulTeardown` 类本体也没动。编译 0 警 0 错，版本 `1.0.92.0 → 1.0.93.0`，**未出包未装机**。

---

## 〇、指令的两种读法，取了哪一种（先把边界划死，免得下次记账找不到北）

"和官方示例不同的 hook" 在上文那张 9 项对照表里跨的幅度太大（从"收掉一条未证的偏离"到"整机网络兜底全拆"），所以按**进程**切：

| 切法 | 本批处置 |
| --- | --- |
| 前台（主应用进程）包住官方 RPC 的自有拦截 | **删** |
| 插件宿主进程里的自有出口（`TerminateConnection`、`Connect` 内 socket 3 s 上限） | **不动**（它们是 Docs/43 §三.3 实测"官方形状连杀宿主进程都不解"之后剩下的唯一出口，删了就是数据面彻底无出口） |
| 跨两个进程共享的设施（`ProfileStore` 串行闸、`GracefulTeardown` 类本体） | **不动**（不是"前台的 hook"；`GracefulTeardown` 仍被插件三个看门狗用着） |
| 官方自己那把尺子（`FindActiveSessionAsync` = `GetFirstConnectedProfileAsync:87-103`） | **不动** —— 它不是 hook，删了反而偏离官方 |
| 前台的定时器兜底（`Backend/HealthMonitor`） | **不动**，但**被 §一 #4 削掉了一只手**，见该处 |

★ 依据（现读，不是记忆）：`Docs/31 §四` + `Docs/38 §十三` 实测 1.0.77 之后 **UI 侧与插件侧同 pid**（"进程隔离没有成立"），所以"按进程切"切的是**代码归属**（哪个调用路径发起），不是 pid。前台路径 = `App.cs` / `Backend/*` / `VpnProfileSetup` 里的动作；插件路径 = `ClashVpnPlugin` 被平台激活的 `Connect`/`Disconnect`。

---

## 一、删掉与改写的五处（行号是 1.0.93 现读；官方列 = `UwpVpnPluginSample/CSharp/TestVpnPluginApp/MainPage.xaml.cs` HEAD `d589fe0`）

★ 下表 #1–#5 是**本批自己的序号**，不要和 `Docs/44 §一` 那张对照表的 `#N` 混（映射：本批 #4 ≈ Docs/44 的 #9「两段式读数」；本批 #5 ≈ Docs/44 的 #4「断开只在 Connected 上发 RPC」连同它那支明写的"故意偏离"；本批 #1/#2/#3 是 Docs/44 的 #2 那把尺子在过去三个版本里长出来的两道闸 + 一处收尾调用，本批把闸拆了、尺子留着）。

| # | 删掉的方法 / 分支 | 原位置（1.0.92） | 官方对应 | 删除后的形状 | 代价（明写） |
| --- | --- | --- | --- | --- | --- |
| 1 | `SerializeBehindActiveSessionAsync`（"拨另一条之前先把当前活动会话收尾，收不干净就拒绝发起"，1.0.88 · Docs/38 F3） | `VpnProfileSetup:500-552`，调用点 `:371` | 官方**没有**：`ConnectOnClick:465-518` 取"只收 Disconnected 的清单"（`:105-118`）后直接 `ConnectProfileAsync`（`:508`），全程不看别的 profile | `ConnectAsync:358` = 取目标 → `ConnectOnceAsync` 一步 | A 挂着（含卡 `Connecting`）时拨 B 不再被拦 ⇒ 可能进 `Docs/43` clean-room 在官方原样代码上量到的形状：B 这一次 await **≥5 分 56 秒不返回**、期间管理面无出口。**这正是本批要的对照组**，别当新病查 |
| 2 | 切换路径的 `Connecting` 拒绝分支（1.0.92 · Docs/44 §一 #2 的对等闸） | `VpnProfileSetup:1038-1045` | 同上，官方无切换动作 | `SwitchToSubscriptionAsync:918` 里 `current` 只用来答"有没有会话/是否已在这条上" | 同 #1；另外 1.0.92 那批"臂 A 新闸正向"装机判据从此**不存在**，臂 A 无法再验 |
| 3 | 切换路径的 `EnsureTornDownAsync`（"先拆旧会话、等它连续两轮不是 Connected、否则拒绝新连接"，1.0.86 A 案） | `VpnProfileSetup:1057-1067` | 官方无 | ★ **1.0.109 已反转**：切订阅改回「先断开 → 后修改 → 再重连」，见 [Docs/55](55-切订阅改走先断开后修改再重连三段式-反向修订1.0.93的切换不收尾-1.0.109.md) | 旧会话半起半落时新请求并发；`VpnChannel.Start` 条文"There can only be one VPN profile per process"由平台自己回答（`ParallelConnectedReportAsync` 的既有口径本来就是**只观测不处置**，至此一致） |
| 4 | `ConnectOnceAsync` 第④步：`Ok` 之后再轮询 20×1 s 拿真实状态（Docs/44 §一 #9"我们比官方强"） | `VpnProfileSetup:466-489` | 官方到此为止（`:508-514` 只看返回值打一行），状态交给 500 ms timer（`:258-268`） | `ConnectOnceAsync:405` 发完请求即返回；`ok` = **平台受理**，不代表隧道起来 | ★ **装机判据换词**，见 §五。前台 3 秒轮询（`MainComponent`，官方 timer 的等价物）成为"已连接"唯一来源 |
| 5 | `DisconnectAsync` 里两处偏离：① 用 `FindConnectedProfileAsync`（无 Connected 时兜底回基础 profile）当尺子，于是对 `Disconnected` 也发一次 RPC；② 1.0.92 明写「故意偏离 · 不藏」的**对 `Connecting` 仍然试断开** | `VpnProfileSetup:1239-1251` | 官方两步：`GetFirstConnectedProfileAsync`（`:87-103`，`Connected ∪ Connecting`）取会话 → **只在 `== Connected` 时**发 `DisconnectProfileAsync`（`:532`） | `DisconnectAsync:1117` 改成同形两步；非 `Connected` 一律回 `不发=…` 一行，一个 RPC 都不发 | ★ **`Backend/HealthMonitor.cs:311` 那条"卡在非终态 ⇒ 把网络交回直连"从此只会拿到 `不发=状态=Connecting`** —— Docs/44 §四早就预告过这一刀，本批让它成真。前台在 `Connecting` 悬挂时**再没有任何出口**（与官方一致：官方本来也没有，且 Docs/43 实测连杀插件宿主进程都不解） |

配件一并删除（闸没了它们就没有别的调用方，不留死代码）：`ConnectingRefusalPrefix`、`SameProfile`、`RefreshForDialAsync`（原先为"刚被我们断过的那条要回读新鲜快照"而存在）。`ConnectOnceAsync` 的 `refinder` 参数同时消失 ⇒ 每次拨号少一次 `GetProfilesAsync`，也少一次占住 `ProfileStore` 写预算的机会（`Docs/38 §十一` 那条排队）。

---

## 二、★ 订正一条我自己写错的（本批现读官方代码时撞出来的）

我在本批向你要 scope 那张表的第 #3 行里，把官方删除路径写成「`DeleteExistingProfile:125` 就一句 `RemoveProfileAsync`」。**这句是错的** —— 而且**本仓早就记着正确的那一半**：`Docs/38:93` 那行写的就是「官方也是先断后删，我们的 `GracefulTeardown` 比它多加了等收尾这道闸」。我这次没现读它就往外复述，属于 `feedback-verify-official-first` 那条纪律的复发，账记在这儿。

现读 `MainPage.xaml.cs:125-135`：

```csharp
if (profile.ProfileName == profileName)
{
    await agent.DisconnectProfileAsync(profile);   // ← 官方也先断
    await agent.DeleteProfileAsync(profile);       // ← 紧接着删，但【不等】
}
```

⇒ 官方与我们的差别**不是**"删之前断不断"，而是**"等不等平台把会话收干净"**。这条订正影响两件事：

1. 本批要删的"删除路径那道闸"，准确的靶子是 `WaitSessionDownAsync`（等连续两轮非 `Connected` 才肯删），不是"先断后删"这个顺序。
2. 更要紧的是：**官方那个"不等"在官方场景里不留残留，在我们的场景里留**。因为官方那份 profile schema（`:143-165`）里**没有 `<dnsservers>`**、路由只有 `includev4 = 192.168.21.0/24` —— 它的插件不写 DNS ⇒ 平台不会下发根 NRPT ⇒ 会话没收干净就删，炸不出 `Docs/45`/`Docs/08 §10.10` 那种"整机解析指向已死的隧道 DNS"。插件侧现读佐证：`TestVpnPluginAppBg/CustomConfiguration.cs` 里 `dns` 只有 19 处、且是**可选解析分支**（`:755-776`），app 侧那份 schema 没用到。

所以"对齐官方"如果连这一步一起对齐，等价于**把一个官方不需要面对的前提，原样搬进一个需要面对它的数据面**。这条是 §六 那个待点头项的完整理由，不是一句"有风险"。

---

## 三、`Connecting` 进不进尺子这件事，现在的形状

`FindActiveSessionAsync`（`:506`）+ `IsActiveSessionStatus`（`:525`）原样保留 1.0.92 的 `Connected ∪ Connecting`，因为它是官方 `:92-93` 的照搬。**变化是它的用途**：

- 1.0.92 时它是**两道拒绝闸**的判据（按钮 + 切换）。
- 本批之后它只回答三件事：「断开该发在谁身上」（`DisconnectAsync`，对官方 `:528-532`）、「当前有没有会话、在哪条」（`SwitchToSubscriptionAsync` 的前置判断）、以及两个诊断拨号点要不要跳过。

`Docs/44 §一 #5` 写的那句分工从此更清楚：`GracefulTeardown` 答"这条上有没有要拆的隧道"，`FindActiveSessionAsync` 答"平台把会话记在哪条上"。**没有第三个问题**了 —— "能不能再发起一条"这个问题本批不再由我们回答，交给平台。

---

## 四、保留清单（不是"忘了删"，是明确不动）

| 项 | 为什么不动 |
| --- | --- |
| `ProfileStore` 串行闸 + 45 s 写预算（对照表 #4） | 跨两进程共享，不是"前台包住官方 RPC 的拦截"而是全仓唯一的 RPC 通道；官方是裸 `await` + 无界 `t.Wait()`，把它换掉等于同时改插件侧 —— 那是"全删"那一档，不在本批 |
| `TerminateConnection` / `AbortConnectionAttempt`（#5）、`Connect` 内 socket 3 s 上限（#6） | 在插件宿主进程里，指令范围之外。且 #6 是 `Docs/43` clean-room 的病根，动了它等于把官方那个无界 `Wait()` 装回来 —— 那要单独一批 |
| `HealthMonitor` / `PhysicalNetworkWatchdog`（#9） | 是定时器兜底，不是包住某个 RPC 的 hook。★ 但它的"非终态出口"这一支已被 §一 #5 削掉，实际效果是：**黑洞保护（Connected→断）仍在，悬挂保护（Connecting→断）没了** |
| 删除/重建路径的 `EnsureTornDownAsync` 六处（`:167` `:831` `:992` `:1294` `:1325` `:1356`） | **等你点头**，理由见 §二 与 §六 |
| `IsNoDialResult` 里 `新连接=跳过` 的识别（`:575`） | 回话词本批不再产生，但识别保留 —— 它是 `VpnSession` 的机器契约（`project-rolling-log-channel` 那条"判据行是保险丝的机器契约"），删识别语句不换来任何东西 |

---

## 五、★ 装机判据换词表（跑臂的人照着这张读，别按 1.0.92 的词找）

| 旧判据（≤1.0.92） | 新判据（1.0.93） |
| --- | --- |
| `profile-connect [X] … 连接请求=Ok, 实际状态=Connected` | `profile-connect [X] 发起前状态=… 连接请求=受理=Ok（平台已接受请求…）` —— **`实际状态=` 这个词彻底消失**；"连上了"改看 `ui-poll` / `MainComponent` 那三秒一轮的读数 |
| `session 订阅切换结果 ok=True：… 收尾=干净（…）` | `profile-switch [A]→[B] 旧会话不收尾（官方形状）：[B] 发起前状态=… 新连接请求=受理=Ok（…）` |
| `profile-connect/switch … ★拒绝发起 —— 状态=Connecting … 新连接=跳过` | **不再有这一行**（闸删了）。臂 A「新闸正向」的判据在本版本上无法成立 |
| `profile-disconnect [X] 不发=平台报告这条没有活动会话（状态=Disconnected）…` | 两个形状：没有任何会话 ⇒ `不发=平台没有我们的活动会话（Connected/Connecting 都没有）…`；有会话但不是 `Connected` ⇒ `[X] 不发=状态=Connecting（官方只在 Connected 上发断开：sample :532）` |
| `断开=Ok（…由收尾闸判定）` | `断开=Ok（请求已受理；★1.0.93 起前台不等收尾…）` |
| `断开=NoConnection` | 理论上不再出现（`Disconnected` 态根本不发 RPC 了）。按它建的历史判据改看 `不发=` |

★ **`Docs/45` 那条读数中毒的探针判据仍然可用**：按一次「断开」看有没有 `不发=` 这一行 —— 有 = 状态读数活；一行都不剩（只有 `session 用户发起断开`）= 读数还在抛。这条是本批唯一**必须**保住的出声路径，`DisconnectAsync` 的注释里把它写成了硬要求。

---

## 六、待点头的最后一档（没自己删，删不删你一句话）

`GracefulTeardown` 里"等会话真收干净才肯删 profile"这道闸，前台有 6 个调用点（`:167` 删主 profile、`:831` 启动对账 prune、`:992` 删订阅、`:1294`/`:1325` 探测残留清理、`:1356` 哨兵善后）。它不在本批"删"的范围里，只有一个理由：**它的病名有日期**。类注释 §L16-26 记的是 2026-10-16 00:21 没有它时的实测后果 —— profile 在平台仍记 `CONN=1` 时被删，OS 直接扯接口：不回调插件 `Disconnect`、不清自己下发的 DNS 策略 ⇒ 留一条 `Namespace=.` 的根 NRPT + 挂在已消失 ifIndex 上的幽灵路由 ⇒ **整机断网，只能"网络重置"自救**。

按 §二 的现读结论，把它改成官方形状（断完立刻删、不等）在技术上完全可做、且是"对齐官方"最彻底的一档；代价是拿这台机器的网卡做对照。**要不要删、以及是否保留删完之后的只读残留自检（`ResidueReportAsync`，它只探不通就打印清理命令，不拦截任何东西）**，等你回话。你说删我就删，并在本档订正。

---

## 七、验证到哪一步（如实）

- `Tools/UwpBridge/uwp-bridge.exe -Build ClashUWP/ClashUWP.csproj -Target Build -Configuration Debug -Platform x64` 跑了三次：代码改完（`ok: True`、`exitCode 0`、115 s）→ 注释订正后（`ok: True`、107.7 s）→ 收工前再确认（18.5 s，msbuild 判"全部最新"，即产物已覆盖最后一次源码改动）。三次 `warning|error` 命中数**都是 0**（走 VS 的 msbuild，合规 `agents.md` 第 2 条；日志 `clashuwp-build-1093.log`）。
  ★ 时间戳核对：最后一次源码改动 22:51:34，最终产物 `ClashUWP.dll` 22:52:22 —— 产物晚于源码，验证有效。（中途有一次差点把"构建跑在编辑之前"当成已验证，靠对时间戳才发现，记这儿。）
- **未做**：`-target Publish` 出包、重签、装机、跑臂。所以本批**没有任何装机结论**，§一 那张表里的"代价"全是从 `Docs/43`/`Docs/44` 已有实测推的，不是本批量的。
- **未做**：`Tools/YamlShapeTest` 离线夹具 —— `VpnProfileSetup.cs` 不在其 `<Compile Link>` 清单里（口径同 `Docs/44 §四`），本轮无涉及夹具的文件改动。
- 装机之前先读 `Docs/45 §七`：那台机器上 VPN 状态读数**当时仍是坏的**，重启能否恢复未测。开臂前先按一次「断开」验判据活着（§五 最后一条）。

## 八、版本与回退

`1.0.92.0 → 1.0.93.0`（`Package.appxmanifest:48`）。工作区**未提交**（按指令），`Docs/41-45` 仍是 untracked、`VpnProfileSetup.cs` 里同时叠着 1.0.92 与本批两拨改动。要回退本批：`git diff` 里带【1.0.93】标记的块是唯一真相源，或整批 `git checkout -- ClashUWP/Vpn/VpnProfileSetup.cs ClashUWP/Package.appxmanifest`（会连 1.0.92 一起丢，注意）。
