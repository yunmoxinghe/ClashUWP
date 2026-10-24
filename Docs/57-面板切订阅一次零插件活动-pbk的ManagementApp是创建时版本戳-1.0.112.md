# 57 · 快速设置面板在有会话时点另一条订阅：一次"零插件活动"；顺带查明 pbk 里 `ManagementApp` 是**创建时的包版本戳**（1.0.112）

**缘起**：用户在快速设置（Win+K 那个 flyout）里，连着一条订阅时点另一条，界面卡在「正在验证你的登陆信息」。第一次问的是机制，第二次复现仍失败；用户选定下一步 = **"先把两条订阅条目也重建（验 ManagementApp 这条线）"**。本档记 ①今晚实测到的两种形状、②`ManagementApp` 这条平台行为、③1.0.112 的代码改动与**尚未成立的地方**。

| 项 | 值 |
| --- | --- |
| 版本 | `1.0.111.0` → **`1.0.112.0`**（[ClashUWP/Package.appxmanifest:48](../ClashUWP/Package.appxmanifest#L48)） |
| 构建 | VS 桥 `uwp-bridge.exe -build …\ClashUWP.csproj -configuration Release -platform x64 -target Publish` → `exitCode 0`、`PackageSuccessfullyCreated`（日志 `/tmp/clashuwp-build-112-publish.log`） |
| 重签/验签 | `winapp tool signtool sign /fd SHA256 /sha1 FB3309A6…` → `Successfully signed`；`verify /pa` → `Successfully verified` |
| 离线套件 | `Tools\run-offline-tests.cmd` → `ALL OFFLINE TESTS PASSED`，exit 0 |
| **装机** | ✅ 用户自装 `1.0.112.0`（19:01:09 起 `app.log:1278` 版本行）；随后本批 `1.0.113.0` 于 19:30:44 覆盖装（[Docs/58](./58-切订阅读不出状态那支也先断旧再重拨-热重载不再单独判失败-1.0.113.md)）。⚠ **1.0.112 机上没有跑过 `recreate`** ⇒ 它那条新后缀要到 1.0.113 才第一次上机，见 §五 |

---

## 一、今晚（2026-10-24，1.0.111.0 装机）实测到的**两种不同形状**，不要混成一种

日志三处：包内 `LocalState\logs\app.log`（前台）、`plugin.log`（插件/内核宿主，pid 8384/10408）、`rasphone.pbk`。

| 时刻 | 动作 | 平台/插件侧 | 前台侧 | 判读 |
| --- | --- | --- | --- | --- |
| 18:02:07→18:02:20 | **app 内**切订阅（星链）+ 连接 | `平台发起连接` 后 `Connect 回调内累计阻塞=7981ms`（plugin.log:993），`L3 隧道接口已建立` :985 | `用户发起订阅切换（先断开→后修改→再重连）`:1184、`连接请求=受理=Ok` :1199、`ConnectProfileAsync 返回了但很慢：3735ms` :1198 | 三段式（Docs/55）走通，只是那一次 `Connect` 阻塞了近 8 s |
| 18:03:14→18:03:18 | **面板**点另一条（iKuuu），当时星链正连着（会话 54 s） | `会话结束，存活 54s` :1007 → 2.5 s 后 `平台发起连接 #2` :1015 → `隧道接口已建立` :1025 → `阻塞=1881ms` :1032 | `profile-sub-adopt 系统侧已断开（此前 starlink…）`:1203，18:03:20.884 跟账到 hkom1 | **这一形状是成功的**：平台自己做了"断旧→连新"，隧道 1.2 s 起来。⇒ 用户看到的卡在文案上，不是卡在拨号上 |
| 18:11:50→18:21:34 | iKuuu 连着（`平台发起连接 #1` :1130，`全新内核`，app 于 18:11:46 重启 :1224），期间在面板点**星链** | **零活动**：这一段 plugin.log 里既没有 `平台发起连接`，也没有 `平台回调 Disconnect`；那条会话一直连续到 `会话结束，存活 583s` :1239，而 583 s 的收尾时刻正对上**前台**的用户断开 :1266 | 该窗口 app.log 只有 18:12:26 一行 `Suspending`（前台被挂起），18:15/18:18/18:21 是 CLI 短激活（pid 868/18396/3404） | **请求根本没到达 `IVpnPlugIn.Connect`** —— 与 Docs/43「另一条占着会话时管理面没有中止原语」同形状，但这次连"发起"都没有 |
| 18:21:26 / 18:21:38 | 点「建/重建 profile」 | — | `重建=跳过（本包 [iKuuu_V2] 状态=Connected ⇒ 会话期间不往 profile 存储里写条目）`:1265；断开后 `删旧=完成, 新建=Ok, 回读=找到[ClashUWP]` :1269 | 写入闸（Docs/52）按设计拦住；**只有基础条目被重建** |
| 18:26:38 / 18:26:55 / 18:36:44 | 面板连点（星链↔iKuuu 来回），**前台已挂起**（app.log 18:22:17 后无行） | `#3` 配置源=hkom1、`#4` =starlink、`#5` =hkom1（现在这条），每条都在 1~2 s 内 `隧道接口已建立` | 无行 | ★ 面板路径**不经前台**这件事第三次拿到独立证据；且**先断开后面板切换一律成功** |

一句话：**"面板切订阅"在有会话时要么 2 s 换过去（18:03），要么整个请求消失（18:12–18:21），两种都实测到了；而"断开后面板切换"今晚 3 次全成功。** 所以卡住的那一次不是我们的前台没跟账，是平台侧没发起。

## 二、`ManagementApp` = 创建那一刻的完整包名，就地升级永不回写（本档的头条实测）

`rasphone.pbk`（包内 `LocalState\`）每条 profile 一段。读它的原因是 Docs/30 那批口径：`rasdial`/`Get-VpnConnection` 看不见我们包内 pbk 的条目细节，而这个文件是**唯一**能看到 RAS 层落地字段的地方。

| 条目 | `ManagementApp=`（18:2x 现读） | 行号 |
| --- | --- | --- |
| `[ClashUWP]` | `8B8CC4F4.ClashUWP_**1.0.111.0**_x64__4tjvwvefxrz66` | pbk:148 |
| `[iKuuu_V2]` | `8B8CC4F4.ClashUWP_**1.0.104.0**_x64__4tjvwvefxrz66` | pbk:320 |
| `[星链机场]` | `8B8CC4F4.ClashUWP_**1.0.98.0**_x64__4tjvwvefxrz66` | pbk:492 |

机器上只注册了 `8B8CC4F4.ClashUWP_1.0.111.0_x64__4tjvwvefxrz66` 一个版本（`Get-AppxPackage`）。三条戳之所以是三个版本，与"哪一次装机之后创建/重建过这条"逐条对得上：

- `[ClashUWP]` 刚在 18:21:38 被「建/重建 profile」删旧建新 → 立刻拿到新戳 1.0.111.0；
- `[iKuuu_V2]`、`[星链机场]` 是 1.0.104 / 1.0.98 时代由启动对账 `Add` 出来的，此后十几个版本就地升级**一次都没被改写过**；
- 备份里同构：`D:\fluentapps\backups\ClashUWP-predial-20261018-0159\LocalState\rasphone.pbk` = `…_1.0.38.0_…`，`_pre-1.0.16` 那份 = `…_1.0.14.0_…`。

⇒ **结论：这个字段是创建时写入、就地升级不动、只有删+Add 才换。** 注意它记的是**含版本号**的完整包名，而 `VpnPluginPackageFamilyName`（`profile-dump.txt` 里的 `PluginPackageFamilyName=8B8CC4F4.ClashUWP_4tjvwvefxrz66`，三条都正确、版本无关）才是插件寻址用的键。

## 三、★ 对自己假设的证伪在先：`ManagementApp` 陈旧**不太可能**是"面板点不动"的因

我最初的推断是"面板按 `ManagementApp` 找那个版本的包 ⇒ 找不到 ⇒ 卡验证"。三条反证，都记下来免得下次又这么猜：

1. 插件解析走的是 `VpnPluginPackageFamilyName`（官方文档明说这是 `VpnPlugInProfile` 的必填有效字段），现读三条都对；
2. `ManagementApp` 在整个 Windows 文档里**没有一处**说明它参与拨号或 UI 文案（rasphone.pbk 的键没有任何公开规范）；
3. 18:21:38 那次重建后，`[ClashUWP]` 换了新戳而两条订阅条目仍是旧戳，之后 18:26/18:36 的三次面板连接**全部成功** —— 陈旧戳与"能连"并存。

⇒ 本批改动的正当性只剩两条：**陈旧是实测到的脏数据**（跨版本一致性、以及"管理归属"指向一个已不存在的包名，迟早别处咬人），以及**它是个便宜的单变量实验**——刷新后重演面板切换，能一次性把这条线证真或证伪。**在实测之前，不得读成"修好了卡住"。**

## 四、代码改动（1.0.112，全部前台进程侧，不碰隧道）

| 位置 | 改了什么 |
| --- | --- |
| [ClashUWP/Backend/VpnProfileSetup.cs:384](../ClashUWP/Backend/VpnProfileSetup.cs#L384) | `RecreateProfileAsync` 在基础条目回读之后追加 `sb.Append(await RefreshSubscriptionEntriesAsync())`，汇总行仍走同一类 `profile-recreate` |
| [VpnProfileSetup.cs:417-457](../ClashUWP/Backend/VpnProfileSetup.cs#L417) | 新增 `RefreshSubscriptionEntriesAsync()`：枚举本包名下**带 `subId`** 的条目（排除 `ClashUWP-Probe`/`ClashUWP-Dataface`，基础条目无 `subId` 天然不进来）→ 逐条 `DeleteByNameAsync` → 调 `SubscriptionManager.SyncSystemProfilesAsync("重建")` 按本地清单补回（名称规则只有 SubscriptionManager 知道，不自己拼 profile）。**逐条 try/catch**：读 `ProfileName`/`CustomConfiguration` 正是 Docs/45/51 那条按条目中毒会抛 `0x80070006` 的位置，一条读不出不能带走整轮；清单整体读失败回"跳过"而不抛。 |
| [VpnProfileSetup.cs:1979](../ClashUWP/Backend/VpnProfileSetup.cs#L1979) | `DeleteByNameAsync` 从 `Task` 改成回一句短状态（`删=无/完成/拒绝(会话未收干净)/失败(X)`）——订阅条目刷新必须逐条知道"到底删掉没有"：**没删掉时后面的 Ensure 只会读到旧条目并回"已就位"，而那条的戳根本没换**，日志不能骗人。探测清理那三处调用照旧忽略返回值。顺带把失败措辞里的"探测 profile"去掉（本方法现在也用于订阅条目，原措辞会把订阅条目的删除失败写成 Probe 那条）。 |

守的既有闸（一条没绕）：整轮先过 `ProveStoreWriteSafeAsync()`（[VpnProfileSetup.cs:328](../ClashUWP/Backend/VpnProfileSetup.cs#L328)，Docs/52：有会话就整轮拒绝并给"先断开"出口）；每条删除照旧过 `GracefulTeardown.EnsureSessionTornDownAsync`；`SyncSystemProfilesAsync` 的 `allowPrune` 用默认 `true`，即"删光→按本地清单补→顺手清幽灵"一轮做完。

## 五、装机判据（下次断开后按这个跑，别改判据再跑）

前置：`rasdial` = No connections（★ 有会话时判据 #1 必然读成"重建=跳过"，那不是失败）。

1. 点「建/重建 profile」→ `app.log` 的 `profile-recreate` 行尾必须出现 **`订阅条目刷新=删 2 条 [iKuuu_V2 删=完成] [星链机场 删=完成] → 重建对账：订阅 2 份 + 基础 profile 1 条，新增 2 / …`**（`新增 2` 是关键：读到"已就位"就说明没真删掉）；
2. 立即读 `rasphone.pbk` 三行 `ManagementApp` ⇒ **全部 `1.0.112.0`**；
3. 连 A，然后在面板点 B（复刻 18:12 那一次）⇒ 判据分三档，**都算有效结果**：`plugin.log` 出现 `平台发起连接 #N` = 这条线证真（陈旧戳确实拦得住）；仍 0 活动 = **本档 §三 的证伪成立**，ManagementApp 与面板拨号无关，下一步转去查 RAS 层（Docs/45 的 zombie transport / 原始码 4328，只有重启能解）；换了新错误（例如 flyout 给出"无法连接"而不是卡住）= 记成新形状再分析；
4. 回归项：`启动对账`/`切换对账`/`边沿对账` 三类行文案不变（Docs/50 §五、Docs/56 的判据都按字面匹配，本轮只**新增** `重建对账` 一种触发点，没改默认值也没改其它三处）；台账 `profile-reconcile-owed.txt` 不因这轮新增欠账；`nrpt=0`、死接口路由=0。

## 六、装机记录（2026-10-24 19:01 装 1.0.112 / 19:30 装 1.0.113，全部按 §五 原文判据跑，未改判据）

**取证纪律先自纠一条**：本档写作过程中我曾把 **18:21 那轮 `recreate` 记成"1.0.112 装机后 §五 全中"**。现读 `app.log:1250` 原文是 `18:21:22 pid=3404 … version=8B8CC4F4.ClashUWP/1.0.111.0` ⇒ 那一轮跑的是 **1.0.111**，而 1.0.112 到 `app.log:1278`（19:01:09）才第一次出现在版本行里。这正是 [[feedback-vpn-forensics-metrics]] / [[project-vpn-management-api-contract]] 里"取证前先核版本行"那条纪律又咬了一次：**判据没跑过就是没跑过，不能拿相邻时刻的动作顶**。订正后的事实表如下。

| 时刻 | 进程版本（`app.log` 启动行） | 动作 | 结果 | 对上哪条判据 |
| --- | --- | --- | --- | --- |
| 18:21:23 / 18:21:26 / 18:21:38 | **1.0.111.0**（pid=3404，:1250） | 挂载对账 → 用户点「建/重建 profile」 | 挂载行 `已就位 3`（戳一动不动）；18:21:26 `重建=跳过（本包 [iKuuu_V2] 状态=Connected ⇒ 会话期间不往 profile 存储里写条目）`（:1265，写入闸按设计拦住）；18:21:38 断开后落地 `删旧=完成, 新建=Ok…`，行尾**没有**「订阅条目刷新」后缀——1.0.111 本来就没这段代码 | 只能算 §二"升级+挂载都不改戳"的旁证，**不算 §五 #1/#2** |
| 19:01:09 / 19:08:39 | **1.0.112.0**（pid=18484 :1278、pid=6332 :1303） | 覆盖装 + 挂载对账 | `已就位 3`、`新增 0 / 更新 0 / 跳过 0 / 失败 0`；三条 `ManagementApp` 仍是 1.0.111/1.0.98/1.0.104 ⇒ **判据 #1 到这一刻才真成立**（挂载路径不改旧条目） | ✅ #1 |
| — | 1.0.112.0 | **`recreate` 一次都没跑** | `app.log` 里 1.0.112 两个 pid 名下无 `profile-recreate` 行 ⇒ 1.0.112 的新代码路径**当时未上机** | ⬜ #2 未跑 |
| 19:30:44 | **1.0.113.0**（pid=9844，:1319） | 覆盖装（`rasdial` 先确认 `No connections`、杀前台后 `Add-AppxPackage`） | `Status=Ok`，无 `0x80073CFB`/`0x80073D02`；包家族 `…_4tjvwvefxrz66` 不变 | — |
| 19:31:11 | 1.0.113.0 | `clashuwp-cli --force recreate`（tunnel 级命令，用户已指定这条动作 ⇒ 走 `--force`；下发前 `rasdial` 二次确认无会话） | 回话原文（同 `app.log:1339` 的 `profile-recreate`）：`删旧=完成, 新建=Ok, server=http://192.0.2.1, 回读=找到[ClashUWP], ServerUris=1个(http://192.0.2.1/), 状态=Disconnected；订阅条目刷新=删 2 条 [星链机场 删=完成] [iKuuu_V2 删=完成] → 重建对账：订阅 2 份 + 基础 profile 1 条，新增 2 / 更新 0 / 已就位 1 / 跳过 0 / 失败 0；prune 幽灵 profile：删 0 / 留 0` ⇒ **`新增 2` 而不是 `已就位 3`**，正是 §五 #1 要的形状 | ✅ #1（后缀首现于 1.0.113 这一版） |
| 19:31 之后现读 | — | `LocalState\rasphone.pbk` | 148 / 320 / 492 三行**全部** `ManagementApp=8B8CC4F4.ClashUWP_1.0.113.0_x64__4tjvwvefxrz66`，条目段头 `[ClashUWP]:1`、`[星链机场]:170`、`[iKuuu_V2]:342` ⇒ **§五 #2 到手，三条戳第一次全等于当前版** | ✅ #2 |
| 19:31 之后现读 | — | `clashuwp-cli profiles`（纯读） | 3 条：`ClashUWP｜subId=(基础)｜系统UI=False`、`星链机场｜subId=starlink.aliyundeepsearch.sbs-s-43e3d1984c55e8494baa8e12dac3｜系统UI=True`、`iKuuu_V2｜subId=hkom1.no-mad-sub.one-link-gcoX6ckbzLI5pVgB｜系统UI=True`，状态全 `Disconnected` ⇒ **删旧建新没丢 `CustomConfiguration` 里的 `subId`**，也没把 `RequiredDeviceCommandlineParameters` 写坏（否则插件挑不到订阅正文） | 回归项 |
| 现读 | — | `Get-NetRoute 0.0.0.0/0`、`Get-DnsClientNrptPolicy` | 默认路由仍 WLAN/`192.168.0.1` **一条**；`NRPT=0` ⇒ 「拆不干净」那条整机黑洞的形状没有（[[project-orphan-nrpt-machine-blackout]]）。★ 顺带记一口基线，免得下次把残留读反：挂在 `Get-NetAdapter` 看不见的 ifIndex 上的路由此刻是 **17 条**（ifIndex=1 loopback 7、9 与 21 各 5），这是**系统内置**、不是本轮产物（同一口径见 [[feedback-vpn-forensics-metrics]]） | 收工判据 |

**判据 #3（连 A 后在面板点 B）仍未跑** —— 要拨号 ⇒ 按"碰隧道须用户明确指定"的口径停在门口。这条才是本档真正欠的那一手：#1/#2 只证明"删掉重建会把整段顶掉、三条能钉在当前版"，**没证明陈旧戳会拦住面板拨号**（§三 已经把"陈旧戳=病因"证伪过一次，#3 跑出来 0 活动是预期内的多数分支）。

**#4 回归项**：挂载那轮的 `启动对账` 文案逐字未变（`启动对账：订阅 2 份 + 基础 profile 1 条，新增 0 / 更新 0 / 已就位 3 / 跳过 0 / 失败 0；prune 幽灵 profile：删 0 / 留 0`），本轮只多出 `重建对账` 这第四种触发点；台账 `profile-reconcile-owed.txt` 未因这轮新增欠账。

## 七、与既有档的分工

- **Docs/43**：证明"连着 A 点 B 会悬挂"在**官方原样代码**里成立，且管理面没有中止原语 —— 那是悬挂有 `Connecting` 可读的形状；本轮 18:12 那次是**连发起都没有**，比 Docs/43 更靠前一步。
- **Docs/45/51**：按条目读数中毒（`0x80070006`）。本轮改动里逐条 try/catch 是它的直接产物，不是新发明。
- **Docs/50/52/56**：面板条目只由前台主动推、会话期间不写存储、边沿那轮不 prune。本轮**新增第四个 `SyncSystemProfilesAsync` 触发点（`重建`）**，属对账链扩容，不改前三条判据。
