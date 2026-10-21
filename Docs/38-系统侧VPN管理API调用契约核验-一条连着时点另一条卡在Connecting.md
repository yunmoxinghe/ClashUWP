# 38 · 系统侧 VPN 管理 API 调用契约核验 —— 「一条连着时点另一条，卡在正在连接中」

> 触发：用户报"我在其中一个是连接状态时点击连接另一个，会一直显示正在连接中，但就是不连接"，并要求
> "再核验下系统侧的 api 是否正确调用"。
> 核验时间：2026-10-21 **03:58 建档**（装机 1.0.85，`app.log:60`）→ **04:10 二次核验**（联网 + 先遣档案 + 本机复点；装机已推进到 **1.0.87**，见 §3.3）。
> 口径：本篇是**代码现读 + 官方元数据/官方 sample 现读 + 本机日志取证 + 联网检索 + 先遣项目一手档案**，不改代码、不部署、不拨号。
> 每条判定给可复核的出处（文件:行号 / 日志原文 / 元数据行号 / URL）。
> **★ 07:01 状态变更**：用户拍板"走官方方案" ⇒ **§四 的 F1/F2/F3/F4（+D7 死条件）已经动手**，
> 改动范围与"已核/未核"全部记在新增的 **§十**；本批**只编译验过，未部署、未提交、未拨号**。
> F5/F6/F7 仍未动（后两条是我们自加的保险，不在官方修法里）。
> **★ 23:13 状态变更（第二批）**：用户口径"先把阻塞系统的代码按照官方文档修复" ⇒ **F6 落地**
> （`ProfileStore` 的无界 await 与"强行执行"两处 + 插件 `Connect` 内那个无上限的 socket `Wait`），
> 并把 **D8 / D6 一起关掉**。这一批与 §十 同属 `1.0.88.0`，同样**只编译验过、未部署、未提交、未拨号**，
> 逐处文件:行号、与 F6 原案的**两处偏离**、以及"哪些数值是官方的 / 哪些是我们定的"全在新增的 **§十一**。
> **★ 00:29 状态变更（Phase B 装机验收）**：`1.0.88.0` **已打包、已装机（同包家族）、已用 UIA 实拨 4 次**，
> §十二 的 Phase A 基线因此有了前后对照 —— 逐臂日志原文、四个一手耗时数、以及**仍未采到样本的路径**（悬挂臂 /
> F3 的按钮分支 / `ExplainDialError` 的 17 个非 `Ok` 成员）全在新增的 **§十三**。
> 一句话结果：**F1/F2/F4 装机可见，D2 关闭，切换臂上 `0x80070006` 由 1 次变 0 次（n=1）；§11.6 第 1 行判据写错了，已订正。**
> 仍未动的是 **F5 / F7**，外加 §十一-11.7 那条本批范围外的新发现（重连场景不该调 `TerminateConnection`）。
> **★ 01:15 状态变更（第三批 · "修bug"）**：§13.5 的措辞债与 §11.7 的重连禁令**都已落地并装机**（`1.0.89.0`），
> 顺带在同一次装机诊断里挖出并修掉 **F5**（CLI 写死过期包身份）与**诊断函数里两条假判据**
> （`1.0.90.0` 复验），以及同一族"写死包身份"的**三个取证脚本**。
> 逐处文件:行号、C1–C4 四臂日志原文、以及**重连门控正向分支仍 0 样本**这件事全在新增的 **§十四**。
> 本篇仍未动的只剩 **F7**（我们自加的保险，不在官方修法里）与 §14.5 那三条明写的边界。
> **★ 01:40 状态变更（第四批 · 提交前自查）**：回头跑离线夹具时发现**第三批把夹具编译弄断了**
> （`MihomoController.cs:1577` 引 `DebugCommandLoop`，而夹具 link 不动那条链 ⇒ `CS0103`）。
> 路径算法搬进新增的 `Backend/LocalStatePaths.cs`，`1.0.91.0` 出包装机复验，见 **§14.6**。
> 本篇列的待办自此只剩 **F7** 与 §14.5 那三条边界；1.0.81–1.0.91 已按逻辑拆笔提交完毕。
> **本轮自我订正共 6 处**（两处把结论**改强**、两处**改弱**、一处改**事实**、一处补**边界**），汇总在 §八 开头，正文逐条带「★ 订正（04:10）」标记。

---

## 〇 · 结论速览

**系统侧调用整体不违规，但对 `VpnManagementAgent` 这一层的"返回值"和"前置条件"两条官方契约，我们有 5 处确认偏离（D1–D4、D8）；其中"前置条件"那一处正好落在用户这次点的路径上（「连接 VPN」按钮），而 1.0.86 的 A 案只修了另一条路径（「启用订阅」）。**

更关键的一条：**这个症状目前无法定案，是我们自己造成的观测缺口** —— 平台把拒绝原因放在返回值里，我们既不判定、又只在 20 秒轮询结束后才写那一行日志，所以"被平台拒绝"和"平台受理了但会话起不来"在日志里长得一模一样。

> **★ 04:10 二次核验新增（联网 + Scout 档案）**：本症状**不需要靠猜**了 —— 同路线先遣项目
> `D:\fluentapps\repos\clashUWP研究` 留有**一手记录**：`ConnectProfileAsync` 的 await 可以**永不返回**
> （docs/60 §3.1 实测挂 >3.5 min 无输出），期间 **RAS 层 `rasdial` 显示 Connected**、
> 而平台侧 profile 状态**常驻 Connecting**（docs/62 §2：T+5/10/15/20/25/30 s 全 Connecting，不再 Abort），
> 后续重试**一律回 `AlreadyConnected`**，`rasdial /disconnect` 清掉 RAS 僵尸才拿到真回答。
> 外部也有一手同症状（微软 Q&A 3287785，任务栏"断开→再连另一条→卡在正在连接、只能重启"，
> 微软独立顾问原话"不能排除属于系统的 BUG"）。
> ⇒ 结论从"平台的真实回答我们没抓到"升级为"**这条 await 根本没有返回上限，是我们代码里唯一的无界等待**"
> —— 详见新增 **§三之五**、**F1 订正**、**新增 F6/F7**、**D8**；本篇初稿共有 **6 处自我订正**，逐条汇总在 **§八-8.1**。

| # | 环节 | 官方契约（现读出处见 §一） | 我们的实现 | 判定 |
|---|---|---|---|---|
| D1 | 连接前置条件 | sample：`ConnectionStatus == Disconnected` 才 `ConnectProfileAsync`（L496） | `VpnProfileSetup.cs:363-364` 把发起前状态**打印出来**后照样拨 | 🔴 确认偏离 |
| D2 | 连接返回值判定 | 元数据：返回的是 "error status"；sample：`!= Ok` 走 ERROR 分支（L499-506） | 只插值成 `"连接请求=X"` 混进一行文本，**无一处分支** | 🔴 确认偏离 |
| D3 | 断开返回值 | 同上（`DisconnectProfileAsync` 也返回 error status） | `GracefulTeardown.cs:188` **整个丢弃** | 🟠 确认偏离（影响收尾判据可信度） |
| D4 | 活动会话数模型 | sample 注释："There should be **zero or one** of these at all times"（L80）+ 可连清单只收 `Disconnected`（L101-113）+ 官方 Intune 参考页「Only assign one profile per device with *Device Tunnel* enabled.」（§3.5.2） | 5 个拨号点里只有 1.0.86 新增的 `SwitchToSubscriptionAsync` 遵守"先收尾再拨"；「连接 VPN」按钮（`MainComponent.cs:518-519`）不遵守 | 🔴 结构性缺口，**本症状的根因候选就在这里** |
| D5 | 发起过程的观测 | —— | `profile-connect` 一行要等最多 20 s 轮询跑完才写（`:381`）；中途被悬挂/进程被换掉 ⇒ **零痕迹** | 🟡 观测缺口（今天 02:33 三次发起就是这样） |
| D6 | RPC 串行闸 | —— | `ProfileStore` 等锁 3 s 超时后"强行执行 + Warn" ⇒ 串行只是尽力而为 | 🟡 已知未证实（`ProfileStore.cs` 头注释自认）<br>**★ 23:13 订正：已改 —— 等不到闸就本轮放弃并出声，绝不并发（§十一-11.2 第 1 行）** |
| D7 | 轮询终态判据 | 元数据：`VpnManagementConnectionStatus` **只有 4 个成员**，没有 `Waiting` | `:377`/`:781` 的 `now != "Waiting"` 是永不成立的死条件；讽刺的是 `:366-369` 那段注释**自己就写了**"投影里没有 NotConfigured/Waiting（实测编译报 CS0117）" | ⚪ 无害（字符串比较不会因为多个条件出错），但和它自己的注释互相打脸 |
| D8 | **拨号 await 的时限**（04:10 新增） | 官方无任何一句说 `ConnectProfileAsync` 会限时返回；sample 就是裸 await | `ProfileStore.Run`（`:73-88`）对 `call()` **不加超时** ⇒ 这是全仓**唯一无界的平台 await**；且悬挂时 `acquired=true` 永不 `Release` ⇒ **串行闸被永久占住**，之后每次 agent 调用都要等满 3 s 再"强行执行"（并发跑 RPC） | 🔴 结构性缺口；先例里这条形态同时解释了 `0x80070006`（Docs/33 §三 候选：只有撞上连接 RPC 的那一读会炸）<br>**★ 23:13 订正：已按 F6 关掉 —— 每次调用都有有界等待（读 3 s / 写 45 s），悬挂不再占死闸，也不再并发补第二条；实现与 F6 原案的两处偏离见 §十一-11.3** |

---

## 一 · 官方契约的两条一手出处

### 1.1 本地 SDK 元数据（26100 投影，ilspycmd 反编译产物，本轮按行号现读）

文件：`C:\Users\gold\AppData\Local\Temp\winmd-dump\Windows.Foundation.UniversalApiContract.decompiled.cs`
（源为 winrt-26100 moniker 元数据；导出时间 2026-10-19 21:45）

| 行号 | 原文（元数据里的 XML doc） | 用处 |
|---|---|---|
| 218049-218054 | `ConnectProfileAsync`：**"Connects to an existing VPN profile."** → `IAsyncOperation<VpnManagementErrorStatus>`，`<returns>` **"An enum value indicating the error status."** | 返回值语义 = **错误状态**，不是"已受理" |
| 218064-218069 | `DisconnectProfileAsync`："Disconnects from a **connected** VPN profile." + 同一句 returns | D3 的依据；官方措辞要求"已连接"才断 |
| 216175 / 216183 | 接口侧两个签名同为 `IAsyncOperation<VpnManagementErrorStatus>` | 与实现一致 |
| 218074-218084 | `VpnManagementConnectionStatus` = `Disconnected / Disconnecting / Connected / Connecting`（4 个，**无 Waiting**） | D7 |
| 218088-218140 | `VpnManagementErrorStatus` 19 个成员：`Ok, Other, InvalidXmlSyntax, ProfileNameTooLong, ProfileInvalidAppId, AccessDenied`，其后 **13 个**带 `[ContractVersion(..., 196608u)]`：`CannotFindProfile`、**`AlreadyDisconnecting`（"The connection is in the process of being disconnected."）**、**`AlreadyConnected`（"The connection already exists."）**、`GeneralAuthenticationFailure, EapFailure, SmartCardFailure, CertificateFailure, ServerConfiguration, NoConnection, ServerConnection, UserNamePassword, DnsNotResolvable, InvalidIP` | **平台有一整个"连接类拒绝原因"词表**，我们一个都没判定过 |

★ 订正（04:10，联网现读官方枚举页）：那 13 个成员的 `ContractVersion` 值 **196608 = 3<<16 = UniversalApiContract v3 = Windows 10 1607 / SDK 14393**，
**不是**我初稿写的"19041+"。官方页的 Version history 表逐条列了这 13 个成员"added in 1607 / 14393"，与元数据一致
（出处见 §三之五 的链接；数值口径 `Ok=0 … AlreadyConnected=8 … InvalidIP=18`，全文 19 项）。
⇒ 这条订正让判据更强而不是更弱：**这些拒绝原因在我们的最低支持版本（`TargetPlatformMinVersion=17763`，`ClashUWP.csproj:5`）上就存在**，
所以 F1/F2 的返回值分支没有任何兼容性借口可退。

⚠️ 诚实边界：`AlreadyConnected` 的原文只有 "The connection already exists." 一句，**没有说明"已存在"指的是"这条 profile 上已有会话"还是"整机已有一个活动 VPN 会话"** —— 这个歧义只能靠装机取一手回答（见 §四 F1 的判据设计：把返回值当场落一行，就分得清）。

### 1.2 官方 sample 现读（本地入仓：`D:\fluentapps\repos\UwpVpnPluginSample\CSharp\TestVpnPluginApp\MainPage.xaml.cs`）

| 行号 | 原文 | 契约 |
|---|---|---|
| 79-83 | `/// Returns the first connected profile with the correct VpnPluginPackageFamilyName. **There should be zero or one of these at all times.**` | 官方对"活动会话数"的建模：**至多一条** |
| 101-113 | `GetConnectableProfilesAsync`：**只**把 `ConnectionStatus == Disconnected` 的放进可连清单（注释 "There might be zero, one, or more return values"） | 连不了 ≠ 可以硬拨；平台侧的"可连"判据就是 Disconnected |
| 495-507 | `Log($"Profile current connection status code starts as ...")` → **`if (ConnectionStatus == Disconnected)`** → `ConnectProfileAsync` → **`if (connectStatus == VpnManagementErrorStatus.Ok) Log("connected OK") else Log("ERROR: Unable to connect profile. ... error status code is {connectStatus}")`** | D1 + D2 的正主：前置条件 + 返回值分支，两件事 sample 都做了 |
| 521-533 | 断开同理：`if (ConnectionStatus == Connected)` 才 `DisconnectProfileAsync`，再按 `Ok` 分支 | D3 的对照 |
| 121-131 | `DeleteExistingProfile`：**先 `DisconnectProfileAsync` 再 `DeleteProfileAsync`** | 官方也是"先断后删"（我们的 `GracefulTeardown` 比它多加了等收尾这道闸，这条我们更强，不是偏离） |

⇒ 一句话：**官方这一层的设计是"先确认这条 profile 处于 Disconnected，再拨；拨完先看返回值再看状态"**。我们是"不管状态直接拨，返回值只当字符串打印，最后一秒才写日志"。

---

## 二 · 我们的 5 个拨号点逐处核对

| 调用点 | 前置条件 | 返回值处理 | 判定 |
|---|---|---|---|
| `VpnProfileSetup.cs:350-383 ConnectAsync(subId)` ← **「连接 VPN」按钮**（`MainComponent.cs:518-519`，连的是 `subActive.Id`） | ❌ 只打印 `发起前状态`（`:363`） | ❌ `:364` 插值成 `"连接请求=X"`，无分支 | 🔴 D1+D2 全中；**用户这次点的就是这条** |
| `VpnProfileSetup.cs:715-790 SwitchToSubscriptionAsync`（1.0.86 A 案，「启用订阅」路径） | ✅ 显式要求当前那条 `Connected` 才切（`:733-741`）；无会话时**不擅自拨号**；切前必过 `EnsureTornDownAsync`（`:755-765`） | ✅ `:767` 拿到 `VpnManagementErrorStatus` 变量，末尾按"是否落到 Connected"决定 Ok 与 Info/Warn | 🟢 与官方契约一致（★ 04:10 订正：**已在机上**（1.0.87，§3.3），但本机至今**零条 `profile-switch` 记录** ⇒ "契约一致"是代码层判定，**未验**） |
| `VpnProfileSetup.cs:936-947 DisconnectAsync` | 🟡 走 `FindConnectedProfileAsync`（隐含"连着的那条"），但没显式读状态 | 🟡 打印不分支（`:944`） | 🟠 D2 轻 |
| `VpnProfileSetup.cs:1027-1029`（probe，`autoProbe=0` 默认不跑） | ❌ 刚 `AddProfileFromObjectAsync` 完直接拨 | ✅ `:1014` 对 Add 分支、`:1029` 打印平台返回值 + 连后回读 | 🟡 诊断路径，形态可控 |
| `StartupProbes.cs:118-119 / 208-212`（Dataface 探测） | ❌ | 🟡 写进 `dataface-v4.txt`，不分支 | 🟡 同上，非产品路径 |

另外两处与本症状直接相关的实现事实：

1. **`OurStatusAsync`（`:882-886`）走 `FindConnectedProfileAsync`（`:868-880`），而该函数在没有 Connected 时兜底回基础 profile**（`ClashUWP`，`RequireVpnClientAppUI=False`）。所以前台"当前状态"读到的字符串来自"**连着的那条，否则基础那条**"——若某条 profile 被平台留在 `Connecting`，前台会**长期显示正在连接中**，而这条兜底路径让"到底是谁在 Connecting"在状态行里看不出来。这是"一直显示正在连接中"的两个可能来源之一（另一个是按钮标签常驻，见 §三）。
2. `RunAsync`（`MainComponent.cs:1450-1462`）先 `setStatus(pending)` 再 `await action()` —— action 不返回，标签就停在 `"连接中…"`。★ 订正（04:10，现读 `ProfileStore.cs:73-88`）：我初稿写"`ConnectAsync` 的 await 是有界的（20×1 s + 取锁 3 s）"**是错的** —— 有界的只有**等锁**那 3 s，`await call()` 本身**没有任何超时**（`Run` 里既无 `CancellationTokenSource` 也不 `WhenAny`）。所以真实形状是：**平台不返回 ⇒ `ConnectAsync` 不返回 ⇒ ① 标签常驻"连接中…"、② `:381` 那行日志永远不写、③ `Gate` 永不 `Release`（D8）**。这三条同时成立时，用户看到的就正好是"一直显示正在连接中，但就是不连接"，而日志上**一片干净**。今天 02:33 前两次发起（`app.log:34/37`）零 `profile-connect` 行 + 进程 02:36 被换新，与该形状**一致**（但不足以证明，见 §三之五）。

---

## 三 · 本机取证（今天，全部只读）

### 3.1 平台状态机（ETW `Windows Networking Vpn Plugin Platform/Operational`，02:30–03:50 共 64 条）

| 时刻 | 事件（id 2020=Connecting / 2021=Connected / 2022=Disconnecting / 2023=Disconnected / 2024=Abort / 2025=Negotiating Network） | 读法 |
|---|---|---|
| 02:31:42→02:31:44 | `ClashUWP · iKuuu_V2` Connecting→Negotiating→**Connected**（1.4 s） | 旧名前缀条目 |
| 02:31:52→02:31:53 | 同条 Disconnecting→Disconnected | 干净收尾 |
| 02:33:50→02:33:51 | `iKuuu_V2` Connecting→**Connected** | **注意**：前台在 02:33:31 / 02:33:33 / 02:33:49 发起了 3 次（`app.log:34/37/45`），**只有 1 次进状态机**，且落在第 3 次发起后 0.3 s |
| 02:36:09 / 02:37:24 / 02:37:51 / 02:38:59 / 02:44:05 | Disconnected 紧接同条 Connecting→Connected（间隔 50–400 ms） | 系统自动重拨（RasMan），不是用户动作 |
| 02:55:07.149→.432 | Connecting→**Abort**（0.3 s） | 重装包那次，非用户动作 |
| 03:17:48→03:17:49 | `iKuuu_V2` Disconnecting→Disconnected | ↓ 同一段 |
| 03:17:53→03:17:55 | **`星链机场` Connecting→Connected** | 换条 profile：**旧会话先 Disconnected，4 s 后新 profile 才进 Connecting** |
| 03:18:39→03:18:43 | `星链机场` Disconnecting→Disconnected → `iKuuu_V2` Connecting→Connected | 同上，反向 |
| 03:22:03→03:22:06 | `iKuuu_V2` Disconnecting→Disconnected→Connecting→Connected | 同条断开重连 |
| **03:22:32 → 04:04:13** | **零事件**（含用户报"卡正在连接中"那段时间） | ⚠️ 关键：**没有一条"停在 Connecting 没有终态"的记录**；★ 04:10 补：这段之后 04:04:13 的那条是**诊断路径**（`cmd connect` 拨基础 profile，1.6 s 落 Connected，04:07:47 `断开=Ok`，见 §3.5.3），不是本症状的复现 |

三条可站住的读法：

- **每次进状态机的 Connecting 都在 0.6–1.7 s 内拿到终态**（Connected，或 02:55:07 那次的 Abort）——平台状态机本身不会挂在 Connecting。
- **record 里所有"换 profile"都是先 Disconnected 再 Connecting**，从未出现两条活动会话重叠；这与 sample L80 "zero or one at all times" 的建模吻合。
- **"在 A Connected 时拨 B"这条路径至今没有一次装机记录**。★ 订正（04:10）：我初稿说"今天 `profile-connect` 行数 = 0"是**查错了文件** —— `RollingLog` 按 category 分流，这一行落在 **`plugin.log`** 而不是 `app.log`。现读全量：今天共 **3 行**（`plugin.log:21/407/770`），形态**全部**是 `发起前状态=Disconnected, 连接请求=Ok, 实际状态=Connected`（02:33:52 / 02:55:25 / 04:04:15）；历史全量里 `连接请求=` 只出现过 `Ok`×6、`ServerConnection`×2，`发起前状态=` 只出现过 `Disconnected`×7（来源：`Docs/30:323,378` + LocalState 归档）。⇒ **"非 Disconnected 时拨号，平台到底答什么"这个一手样本依旧为零**，本症状仍不能凭日志定案 —— 但**先例**已经把候选收窄到一条（见 §三之五）。
- 反过来，**观测缺口本身是实测到的**：02:33 那三次发起（`app.log:34/37/45`）只有第 3 次（02:33:49 → 02:33:52.522）留下了 `profile-connect` 行，**前两次（02:33:31、02:33:33）零痕迹**。这两次到底是"没返回"还是"返回后被别的动作吞了"，现有日志答不了 —— 这正是 F1 要拆成两行的理由。

### 3.2 插件侧会话序列（`LocalState\dataface-sessions.txt`）

| 指标 | 值 | 读法 |
|---|---|---|
| `evt=connect-begin` / `evt=start` | **14 / 14**（04:10 复点，含 04:04:13 那次） | 每一次平台调用 `Connect` 都走到了 Start，**没有"Connect 进来后卡在插件里没 Start"的记录** |
| `evt=disconnect` | **5** | 14 段会话只有 5 次回调 Disconnect —— 与已知"平台拆通道不回调 Disconnect"一致 |
| 同 pid 内在未 disconnect 时再来一次 `connect-begin` | **0 次** | "插件单实例被并发 `Connect`" 这个假设**目前没有样本**（不是证伪，是没测到） |
| 03:17:48→03:17:53（pid 5508，同进程换 profile） | `evt=disconnect alive=230s` **紧接** `connectCalls=2 evt=connect-begin channel=1215209971 resident=复用常驻内核` → 2 s 后 `evt=start` | ★ **换订阅的一手形状**：旧会话先拿到 Disconnect 回调，新 `Connect` 才进来，**严格串行**；channel id 换新值 ⇒ 平台给了新 channel。03:18:39→03:18:44 反向切换同形（`alive=45s` 后 disconnect，再 `connectCalls=3`）。这两次就是 ETW §3.1 里"先 Disconnected 再 Connecting"的插件侧对照，**互相印证** |
| 03:22:05 `connectCalls=2 evt=connect-begin channel=3563348495 resident=复用常驻内核`（上一段 03:22:03 刚 disconnect） | — | 平台确实在同一进程里换 channel 复用实例，但**是串行的**，符合 sample 建模 |

### 3.3 装机版本

★ 订正（04:10）：本篇 03:58 建档时读到的是 **1.0.85.0**，**本轮核验期间装机线自己推进了** ——
`app.log:64` 显示 **04:03:01 起为 `1.0.87.0`**（`ClashUWP/Package.appxmanifest:48` 工作区也是 `1.0.87.0`），
`AppPackages/` 里 1.0.86 的包**不存在**（04:01 打的是 1.0.87，源码最后改动 03:24–03:30 ⇒ **1.0.87 包含 1.0.86 的 A+B 改动**）。
⇒ 所以"1.0.86 A 案不在机上"这句已经过期：**A 案现在在机上**，本篇 §二 第 2 行 🟢 的判定从此有了上机资格（但**尚无一条 `profile-switch` 记录**，所以"成立"仍未验）。
不变的那一条仍然成立：**「连接 VPN」按钮这条路径在 1.0.87 里还是裸拨**（`VpnSession.cs:40-47` 直接透传 `VpnProfileSetup.ConnectAsync`，
`SwitchToSubscriptionAsync` 的调用方只有 `VpnSession.cs:91` 一处 = 「启用订阅」路径）。

顺带记两条**新落到机器上的一手样本**（04:04–04:07，全部只读观测，动作不是我发起的 —— 是 `debug-cmd.txt` 里的 `cmd connect` / `cmd disconnect`）：
`plugin.log:770` = `[ClashUWP] 发起前状态=Disconnected, 连接请求=Ok, 实际状态=Connected`，会话活 213 s 后 `断开=Ok`（`debug-out.txt`）。
两条都走**基础 profile 的诊断路径**，因此：① 又一次只补了"Disconnected 时拨"的样本；② **插件侧 13→14 段会话里同 pid 并发 `connect-begin` 依旧 0 次**。

### 3.4 顺带捞到的两个独立缺陷（与本症状无关，但都属于"系统侧对接"）

1. `ClashUWP.Cli/AppServiceChannel.cs`：`TargetPackageName = "421769c5-17f4-4287-b6c1-193222d21e14"` 是旧包名，装机实际是 `8B8CC4F4.ClashUWP`（family `8B8CC4F4.ClashUWP_4tjvwvefxrz66`）⇒ 不带 `--pfn` 时默认反查失败（本轮实测报"找不到目标包"，加 `--pfn` 才通）。
2. CLI `--help` 的 `--force` 说明行还写 `（connect/recreate/probe）`，`CommandCatalog` 里已经是 `connect/recreate/probe/subuse`（1.0.3 那批改的），文档行漏改。

---

## 三之五 · 先例与外部一手（04:10 补：把"猜"换成"读过的别人踩法"）

### 3.5.1 Scout 同路线档案的一手记录（`D:\fluentapps\repos\clashUWP研究`，本轮现读原文）

| 出处 | 原文（节选） | 对本症状的意义 |
|---|---|---|
| `docs/60-切clr臂-钩子就位与宿主零调度.md:110-131`（§3.1 现象） | `00:52:19 ui: ConnectProfileAsync(...) ← 此后 **再无输出**，挂起 > 3.5 min`；同一张表里 `rasdial` = **`Connected to ClashPocEcho`**、`Get-VpnConnection` **一条都没有**、`Vpn Plugin Platform/Operational` **0 事件** | ★ **await 不返回是真实发生过的形态**，而且发生在**没有任何日志**的那一行之后。判读原文：「RAS 层连上了 → 平台在等插件 → 插件宿主从未被调度 → `ConnectProfileAsync` 永不返回」 |
| `docs/60:140`（§3.2 已排除假设） | 「第一次部署时 RAS 层就有个 `ClashPocEcho` 僵尸，导致 `ConnectProfileAsync` **一律回 `AlreadyConnected`**；`rasdial /disconnect` 清掉后才拿到真正的拨号」 | ★ **RAS 僵尸 ⇒ 一律 `AlreadyConnected`** 是别人一手实测过的因果。⇒ "连着 A 时拨 B 平台会答什么"这个问题，先例给的答案是 **`AlreadyConnected`（且这不是"已连上 B"）** |
| `docs/62-切回native臂-部署成功与宿主零调度.md:58-70`（§2 结果表） | `ConnectProfileAsync`：**45 s 超时不返回**；之后 4 次重试探**全部 `AlreadyConnected`**；profile status：**超时后持续 `Connecting`**（T+5/10/15/20/25/30 s 全为 Connecting，不再 Abort）；`rasdial`：`Connected to ClashPocEcho`（外部 `/disconnect` 可清） | ★★ 这就是用户那句话的机器版原文：**"一直显示正在连接中，但就是不连接"**。而且它同时给出了**清理出口**（`rasdial /disconnect`）与**判据分离点**（RAS 层状态 vs profile 状态） |
| `docs/49-给GPT的汇报-2026-10-07.md:141` | `（后续重试）ConnectProfile(#2..#4) → AlreadyConnected，status=Connecting` | 同一形态的第三次独立记录 |
| `docs/45-J2-API-contract核查-官方文档无法解释CannotFindProfile.md:53` / `docs/61-VPN-Plugin官方机制-激活链与正确调用契约.md:180` | 官方 19 项枚举表，`8 = AlreadyConnected = "The connection already exists." / 「连接已存在」` | 先遣项目也把这条列为"官方文档无法解释其精确语义"的项 —— 与我 §1.1 的歧义标注一致 |
| `docs/46-J3最小实验-两条profile都能连-选错对象假说被证伪.md:11,44-46,57` | 逐条拨两条 profile：`Connect(match[1]) → Ok`、`Disconnect → Ok`、`Connect(match[2]) → Ok`（**顺序，中间夹了断开**）；A/B 表：**phone book 条目不存在 ⇒ 唯一那条 `CannotFindProfile`（重试 4 次一致）**，存在 ⇒ 两条都 `Ok` | ① **它是"A/B 两条 profile"最接近的一手实验，但仍然不是"在 A Connected 时拨 B"** —— 先例也没测过并发臂，这条要写清楚，不能让读者以为已有答案；② 它给了 `CannotFindProfile` 的真实判据（**跟 RAS phone book 是否存在绑定**），F7 判残留时这条比返回值本身更有用；③ 间隔 17.6 s 那次是"等断开"——先例也在**串行**上做，与 sample L80 一致 |

**⚠️ 适用边界（不许偷便宜）**：docs/60/62 那两轮的**成因是"插件宿主零调度"**，我们的机器不是那个形态 ——
本轮 §3.2 显示插件 14 段会话全部 `connect-begin → start` 正常落地，ETW 有事件，宿主活着。
所以先例**不能直接当我们的根因**，它给的是两样更有用的东西：
① **`Connecting` 常驻 + await 不返回 + 重试一律 `AlreadyConnected` 这条组合在 Windows VPN 这一层是真实存在的**，
不是我们臆想的分支；② **同一症状有两个不同的层可以观测**（RAS 层 vs 平台 profile 层），我们的代码和日志**只看后者、不看前者** —— 这才是"定不了案"的真实原因（→ 新增 **F7** 的分层判据；悬挂本身没有出口这件事由 **F6** 补）。

### 3.5.2 外部一手（联网现读，2026-10-21）

- **微软 Q&A 3287785**（`windows10-vpn拔号提示"正在连接"问题`，2020-03-24）用户原文：
  「有多个办公 vpn 互相切换连接时候……**在任务栏点击网络图标，连接 vpn 拔号，断开连接，再连接另外一个 vpn 拔号**，有时候正常连接，有时候连接不上，**提示"正在连接"，只能重启电脑**。
  目前的解决方法：设置 → 网络和 Internet → VPN……点击连接即可，每次都能正常连接」——
  与本篇用户的描述**逐句同形**（同为"在一条上断开后再连另一条"）。微软方独立顾问（Jarl, Independent Advisor）原话：
  「**这个情况，确实很可能属于系统的 BUG**」，并给出干净启动等排除法。⇒ **外部同样定不了案**：这一层没有官方文档承认的"第二条 profile 必须等第一条彻底收尾"的条文，只有实测形态。
- **官方 IoT Core VPN 文档**（learn.microsoft `…/iot-core/connect-your-device/vpn`）的示例代码把 `ConnectProfileAsync` 的返回值**存进 `VpnManagementErrorStatus status` 并判 `Ok`** —— 与 §1.2 sample 同一条契约，**第三个官方写法**（官方从来没有"不判返回值"的示例）。
- **官方 `VpnManagementErrorStatus` 枚举页**（winrt-26100）：19 项 + Version history 13 项均在 **1607 / SDK 14393** 引入（→ §1.1 的订正依据）。
- **官方 Intune「Windows VPN 设备设置」参考页**（现读原文）：
  **「Only assign one profile per device with *Device Tunnel* enabled.」** —— 我们的三条正是 device-scope 插件 profile（全路由、`Type=VpnPlugInProfile`；基础那条 `RequireVpnClientAppUI=False`，订阅两条 `=True`，本轮 `probe.txt` 实测），
  落在官方这句话的适用面上。这是**目前最接近"至多一条"的官方条文级措辞**，比 sample 的代码注释更有分量（sample 只是示例，这条是产品要求）。
  ⚠️ 边界（不许越）：官方那句的语境是**Intune 策略下发**，我们是**应用自注册**的 `VpnPlugInProfile`（本轮 `probe.txt` 三条全是 `Type=VpnPlugInProfile`），
  "Device Tunnel" 这个类型标签是否严格覆盖我们的形态**没有现读证据** —— 所以 D4/F3 的**硬依据仍是 sample L80 + L101-113**，Intune 这句算**同向旁证**。
- **同一页的 Windows 11 / VPNv2 CSP 已知问题段**（现读）：官方明列「**两个新的 VPN 配置文件同时应用于设备**」「**在分配新 VPN 配置文件的同时删除活动 VPN 配置文件**」
  会**导致设备失去 VPN 连接**（"lose their VPN connections"），要等下次 check-in 才恢复。
  ⇒ 官方自己承认：**profile 存储上的并发变更 = 已知会掉连接**。我们的 5 个拨号点 + 启动对账 prune + 热重载都在这块地上并发（D6/D8 因此不只是"未证实"）。


### 3.5.3 本机 04:0x 窗口 + 一处"看不见"的口径坑（本轮新读）

| 观测 | 值 | 对本症状的意义 |
|---|---|---|
| ETW 平台状态机在 **03:22:32 → 04:04:13** 之间 | **零事件**（本轮现拉最新 25 条，紧邻两条就是 03:22:32 Disconnected 与 04:04:13 Connecting） | ⚠️ 用户报"卡在正在连接中"就在这段区间内。**这期间平台状态机根本没被触发**，而我们前台也没有任何"用户发起"行（`app.log` 03:22 之后到 04:03 之间只有 03:48 那次 CLI `profiles`）⇒ **"点了没连上"的那一次，在我们的代码路径之外**（最可能是快速面板/托盘自己走 `VpnManagementAgent` 或 shell 侧的 RAS 入口，前台 app 不参与、也不被激活 —— 与 `VpnSession.cs` 里"快速面板前台无事件、只能轮询"那条既有结论同形） |
| 04:04:13.078 → 04:04:14.688 | `ClashUWP` Connecting → Negotiating → **Connected**（1.6 s） | 又一次"Disconnected 时拨号"的正常样本，**不是**本症状的复现 |
| `plugin.log` 全量 `强行执行`（D6/D8 的可见出口） | **0 次** | ⇒ 本机至今**没有观测到闸被占住**的形态。诚实结论：D8 目前是**结构推断 + 先例驱动**，不是本机实测到的故障；F6 的验收判据就是为了把它从推断变成读数 |
| 桌面侧 `rasdial` / `Get-VpnConnection` | `No connections` / **空**（本轮实测） | ★ **我们的三条 profile 不在桌面 phonebook 里**，它们在包内 `LocalState\rasphone.pbk`（本轮读到 `[星链机场]` / `[iKuuu_V2]` / `[ClashUWP]` 三段）⇒ 先例里"`rasdial /disconnect` 一清就好"那条出口**对我们不成立**，F7 的判据只能从包内读（这也是 `Docs/24`/记忆里"profile 存储两处不一致"那条坑的同源表现） |




## 四 · 修法（对齐官方契约；本轮由四条扩到七条）

| 序 | 改哪 | 改成什么 | 官方依据 | 装机验收判据 |
|---|---|---|---|---|
| **F1** | `VpnProfileSetup.ConnectAsync`（`:363` 前后） | ★ **订正（04:10）：拆成两行，且第一行必须在 await 之前。** 现稿"`:364` 拿到返回值再拼进 `sb`、`:381` 统一写一行"的写法**治不了本症状** —— 一旦 `ConnectProfileAsync` 不返回（§三之五 的先例形态），那**一行永远不会落**，日志和现在一样干净。改成：<br>① **发起即写**（`Connecting` 之前的状态 + 目标 profile 名 + `connectCalls`）；<br>② `await` 拿到返回值后**再写第二行**（`Ok`=Info、非 `Ok`=Warn，枚举名原样）；<br>③ 前置条件按 sample：目标 `!= Disconnected` 就不硬拨 —— `Connecting/Disconnecting` ⇒ 回"平台正在这条 profile 上处理，稍后再试"；`Connected` ⇒ 交给 F3；<br>④ 轮询里把 `!= "Waiting"` 这个死条件删掉（D7） | sample L496 / L499-506；IoT Core 官方示例同样把返回值存成 `VpnManagementErrorStatus` 判 `Ok`；元数据 returns | 断开态点「连接 VPN」→ **先出现"发起"行、再出现"返回值"行**（两行能分开看到，才算 F1 生效）；**故意在已连接时点 → 只有"发起"行被①拦住、出现"没拨"的 Warn，且不产生新会话**；悬挂臂 → **只见"发起"行、20 s 后仍无第二行 ⇒ 本症状第一次可定案** |
| **F2** | `ConnectAsync` / `DisconnectAsync` | 返回值 `!= Ok` 一律按失败回话（**保留轮询**，因为 `Ok` 也不代表连上，`:343-348` 那段口径不变）；把失败原因用枚举名原样带进给用户的字符串 | 元数据 returns；sample else 分支明写 ERROR | `连接请求=ServerConnection` 这类历史形态（`Docs/30:378`）以后必须让前台显示为失败而不是混在一行里 |
| **F3** | 「连接 VPN」按钮路径（`VpnSession.ConnectAsync`） | 已有活动会话且**目标不是那条** ⇒ 走 1.0.86 已有的 `SwitchToSubscriptionAsync`（串行：先 `EnsureTornDownAsync` 再拨），**不再裸拨第二条**；无会话时行为不变（"空壳不拨号"口径照常） | sample L80（至多一条）+ L101-113（只有 Disconnected 可连）+ **Intune 参考页原文「Only assign one profile per device with Device Tunnel enabled.」**（§3.5.2） | 连着 iKuuu 时点「连接 VPN」（生效=星链）→ `profile-switch` 出现"收尾=干净, 新连接请求=…, 实际状态=Connected"三要素齐全；平台 ETW 形状与 §3.1 的"先 Disconnected 再 Connecting"一致 |
| **F4** | `GracefulTeardown.cs:188` | 收下返回值：`Ok` 才叫"已发起断开"；`AlreadyDisconnecting` 视为"平台已在断开"继续等待；其余（`CannotFindProfile` 等）写进 report，让 `WaitSessionDownAsync` 的超时结果**能区分**"断了没收尾"与"根本没断开" | 元数据 218064-218069 + returns；sample L522-533 | 人为让断开被拒（未连接态触发收尾）→ report 里出现"断开请求被拒=X"而不是含糊的"等 20 s 未到" |
| **F5** | §3.4 两项 | CLI `TargetPackageName` 换成实际包名（或改成按 PFN 反查不写死）；`--help` 的 `--force` 列表补 `subuse` | —— | `clashuwp-cli profiles` 不带 `--pfn` 能通；`--help` 文本与 `CommandCatalog.ForcedCommands` 一致 |
| **F6**（04:10 新增，本症状的另一半） | `ProfileStore.Run`（`:73-88`） | 给 `await call()` **加有界等待**：`Task.WhenAny(call(), Task.Delay(budget))`，拨号臂预算 **45 s**（先例里 45 s 是"确定不会返回"的经验线，`docs/62`）、读状态臂沿用现有 3 s 口径；**超时后**：① 返回明确的"平台未在 45 s 内应答"字符串（不冒充失败原因）；② **释放闸**（把悬挂那次标成 `abandoned`，`finally` 照常 `Release`，并 Warn 一行 `profile-store gate-abandoned op=ConnectProfileAsync`）；③ **绝不重试第二次 `ConnectProfileAsync`** —— 先例明确：悬挂后重试**一律回 `AlreadyConnected`**，等于把僵尸状态坐实（`docs/60:140`、`docs/62:65`） | §三之五 一手先例 + `D8`；官方无一句"限时返回"的承诺 ⇒ 超时值是自定的，**必须写在注释里说明是本仓经验线** | 人为造悬挂（把外层指向会回 ICMP 的活服务器是旧办法，现改用黑洞 + 拔 Wi-Fi 无线电的既有安全臂）→ **45 s 整出现"未在 45 s 内应答"，标签不再常驻"连接中…"**；随后 `profiles` 里那条要么落到终态、要么被 F7 指认为残留；**`强行执行` 这一行在超时后不再连续刷屏**（今天全量 = 0 次，见 §3.5.3） |
| **F7**（04:10 新增） | 悬挂/超时后的**残留判据**（接 `GracefulTeardown` 的收尾报告，不新增机制） | 超时或返回 `AlreadyConnected` 时，按**包内** `LocalState\rasphone.pbk` 与插件侧 `dataface-sessions.txt` 的"有没有 `connect-begin`"两路对齐，判"平台已拨但插件未被调度"vs"插件在飞但平台没收尾"；结论写进 report 并给既有清理口径 | `docs/62:65`（`rasdial` 显示 Connected 而 UI 卡 Connecting ⇒ 两个层要分别看）；★ 订正：**桌面侧 `rasdial` / `Get-VpnConnection` 看不见我们的三条 profile**（本轮实测：`rasphone.pbk` 在包内 LocalState，`[星链机场]/[iKuuu_V2]/[ClashUWP]`；桌面 `rasdial` 回 `No connections`、`Get-VpnConnection` 空）——所以先例里的 `rasdial /disconnect` 这条清理出口**不能照搬**，只能按 `Docs/24` 的包内口径走 | report 里出现"ras-pbk 有条目 / 插件无 connect-begin"这类可分辨的两行；不再只有含糊的"等 20 s 未到" |

**F1 + F6 是这一批的核心**（原来只有 F1）。理由不是"加了个 if"：本症状之所以只能停在"可能/候选"，就是因为这两件事没做 ——
平台把答案放在返回值里，我们把它当字符串冲掉了（F1）；平台**连返回值都可以不给**，我们连"没给"这件事都没有出口（F6）。
补上这两条，下一次复现就能一行定案；不补，任何修法都只是在猜。

> ★ **07:01 状态**：本表 **F1 / F2 / F3 / F4 已按用户"走官方方案"落地（`1.0.88.0`，只编译验过）**，
> 逐处出处与"已核/未核"见 **§十**；**F5 / F6 / F7 仍未动**（F6/F7 是我们自加的保险，不在官方修法里 ——
> 也就是 `D8` 那条"无界 await 永久占闸"**至今仍然开着**）。
>
> ★ **23:13 状态（第二批）**：用户口径"先把阻塞系统的代码按照官方文档修复" ⇒ **F6 已落地**
> （连同 §五 那两行 F6 相关的风险一起处理），**D8 从此关掉**；`F5 / F7` 仍未动。
> ★ 但实现与本行原案有**两处偏离**（不释放闸、读写一律拒），理由与本批全部文件:行号都在 **§十一**，
> 以 §十一 为准；本行那句"释放闸 + 只放开读"是**建档时的设想**，不要照它去读代码。

---

## 五 · 风险表（改动全部落在拨号路径上，逐条给缓解）

| 风险 | 后果 | 缓解 / 观测 |
|---|---|---|
| F1 的"前置条件"把**合法的快速重连**挡掉（比如上一段刚 Abort、状态还停在 `Connecting` 的残留态） | 用户点了没反应 | 文案必须区分"平台正在处理"与"被拒"，且**保留那条 `发起前状态` 原文**；F4 之后残留 `Connecting` 有专门的清理判据（`:285` 已在扫 Probe/Dataface 残留） |
| F3 让「连接 VPN」按钮变成"可能先断开" | 用户以为"连接"是纯加法，实际会拆掉现有会话 | 这正是产品语义本身（切订阅=切隧道，1.0.86 已按此改 CLI 危险级到 `tunnel`）；前台文案要写"已断开 X 并连到 Y"，失败时写"未完成"，沿用 `ActivateAsync` 那套 `已切到 / 切到…未完成` 两分支 |
| F3 与 A 案共用 `ProfileStore` 串行闸，切换期间有 20 s 收尾 + 20 s 轮询 | 其他读状态的调用排队 >3 s ⇒ 走 D6 的"强行执行"并发 | 已有边沿出声（`profile-store` Warn）；装机验收清单里把"这一行是否出现"列为观测项（出现 = 串行闸不够，需要把闸从 RPC 提到动作级） |
| 断开→重连之间出现"整机无默认路由"的空窗 | 短暂断流 | 与 1.0.86 A 案同一敞口，已在 `Docs/36` §5 第 3 项记为未决（健康哨兵全程保持武装兜底 + 收工判据 `nrpt=0`/死接口路由=0） |
| 改动未装机就宣称修复 | 又一批"代码层成立"的空账 | 本批**不部署不提交**；F1–F7 的判据全部写成装机可勾的行（§四 最后一列），任一不过就停下报现象 |
| **F6 超时后释放闸，但平台的 RPC 还在飞**（04:10 新增） | 之后 `GetProfilesAsync` 与悬挂的拨号**真并发** ⇒ 就是 `Docs/33` §三 那条 `0x80070006` 形态 | 这是**两害相权**：不释放闸 = 整个 agent 永久卡死（更坏）。缓解：① 释放时写 `gate-abandoned`，让"这次并发是有记录的"成为硬事实；② 超时后**冻结后续写类调用**（`Connect/Disconnect/Add/Update/Delete` 一律先回"上一次平台未应答，需先收尾"），只放开读；③ 装机判据盯 `0x80070006` 与 `强行执行` 两行的出现次数，出现即回滚 F6 的"释放"半边 |
| **F6 的 45 s 经验线来自先遣项目，不是官方承诺** | 我们自己的机器上真值可能不同（更快返回 / 更久） | 写成常量 + 注释标"本仓经验线，来源 `docs/62:65`"；装机第一次跑悬挂臂时**同时记录 ETW 与真实返回耗时**，用一手数改这个常量，不许让它长期挂着猜的值 |
| **F7 想看的 RAS 层在我们这儿读不到**（本轮实测：桌面 `rasdial` 看不见包内 profile） | 判据只能退化成"包内 pbk + 插件 sessions"两路，比先例少一路 | 已在 F7 行显式写出这条退化；若两路仍不能分辨，就**只报现象不判根因**（本篇纪律），并把它列进 §七 未覆盖 |

---

## 六 · 与既有 Docs 的关系（含一条订正）

- **`Docs/37` §〇 表第 8 行「连接发起 🟢 标准」判定不完整**，已在原文处加订正块。它当时只核对了"用新鲜回读对象 + `ProfileStore` 串行"两件事，**没有核对官方 sample 的两条契约**（`Disconnected` 前置条件、返回值分支）。本篇按现读把该行改判为 🔴 部分标准（D1+D2+D4）。
- **`Docs/36` §6**（1.0.86 A+B）不受本篇影响：A 案自身是按契约写的（§二 第 2 行 🟢），本篇指出的是**它没覆盖「连接 VPN」按钮这条路径**，而用户报的症状正在这条未覆盖路径上 —— 这不是 A 案写错，是 A 案范围不够。★ 04:10 补：A 案现已在机上（1.0.87，§3.3），但**没有一条 `profile-switch` 记录**，所以"A 案成立"仍属未验。
- **`Docs/36` §5 第 2 项**（RasMan 自动重拨与切换竞争）在本篇 §3.1 里有了第一手形状：02:36–02:44 五次"Disconnected 紧接同条 Connecting"就是系统重拨，间隔 50–400 ms，比我们的收尾等待快得多；切换收尾期间若系统把旧 profile 拨回来，会先出现在这份 ETW 里。
- **★ 与先遣项目档案的关系（04:10 新增）**：`D:\fluentapps\repos\clashUWP研究` 的 `docs/60` §3.1/§3.2、`docs/62` §2、`docs/49:141` 是**同一 API、同一症状的另一手实测**（§三之五 逐条抄了原文）。
  这条关系的价值在于**边界**：那两轮的成因是"插件宿主零调度"，我们的机器不是那个形态（插件 14 段会话 `connect-begin → start` 全落地）。
  ⇒ 先例给我们的是**"await 可以永不返回 + 常驻 Connecting + 重试一律 `AlreadyConnected`"这条分支真实存在**，以及**分层观测**的做法，不是现成根因。按 `feedback-verify-official-first` 的纪律，这属于"现读重验别人（和我们自己）的旧结论"，不是照抄。
- **已知记忆口径的两处更新**（04:10）：
  ① `feedback-vpn-forensics-metrics` 那条"profile 存储两处不一致"本轮**拿到新的具体形态** —— 我们的三条 profile **只在包内 `LocalState\rasphone.pbk`**，桌面 `rasdial` / `Get-VpnConnection` 一律看不见（§3.5.3），这条要并进那条记忆；
  ② `Sentinel silent death 0x80070006` 那条的候选（静态缓存 agent）已被 `ProfileStore.cs:20-33` 自己证伪，剩下的"并发撞 RPC"解释在本篇拿到**新的放大机制**（D8：悬挂的 await 永久占闸 ⇒ 之后所有 agent 调用并发跑），两篇要互相指认。

## 七 · 本篇未覆盖（诚实边界）

1. **"A Connected 时拨 B，我们这台机器上平台到底答什么"仍无本机一手**（§3.1：所有 `profile-connect` 样本的 `发起前状态` 清一色 `Disconnected`）。
   ★ 04:10 订正：我初稿把这写成"平台的真实回答从来没抓到过一次、所以只能猜"，**低估了可依据的东西** ——
   同路线先遣档案已有三条独立一手（`AlreadyConnected` + 常驻 `Connecting` + await 不返回，§3.5.1），外部还有一条同症状的用户报告（§3.5.2）。
   ⇒ 现在的状态是**"有先例的候选解释 + 本机缺一次读数"**，不是"零证据"。补上 F1+F6，下一次复现即可定案。
2. 用户报的"正在连接中"**出现在哪个界面仍无装机证据**，但本篇把候选**缩到一个**：
   §3.5.3 显示 03:22:32→04:04:13 这段（含用户报症状的时间）**平台状态机零事件、我们前台也零"用户发起"行** ——
   也就是说那次点击**没有经过我们的代码**。剩下两种：① app 内按钮标签常驻（§二 第 2 点）；② **快速面板/托盘自己发起**（走 shell → `VpnManagementAgent`，前台 app 不参与也不被激活，与 `VpnSession.cs` 里"快速面板前台无事件、只能轮询"的既有结论同形）。
   这一条要用户下一句直接确认："当时是在 app 里点的，还是在右下角快速面板点的？" —— 二选一之后 F1/F3 与"另建一条 shell 侧观测臂"的工作量差别很大。
3. 全程只读：本轮只发过 `profiles`（read 级）、ETW/日志/包内 `rasphone.pbk` 查询与 `rasdial`（**只读状态，没拨号**）。
   **未拨号、未断开、未碰隧道**；`debug-cmd.txt` 一条没写 —— 04:04 的 `connect` / 04:07 的 `disconnect` **不是本会话发起的**（本篇只事后取证）。
   收工前 `rasdial` = `No connections`、三条 profile 全 `Disconnected`；`nrpt`/死接口路由两项本篇未复查（本篇没有拆线动作，敞口同 `Docs/36` §6.5 第 4 项）。
4. ★ 订正：我初稿说"装机版本仍为 1.0.85、A 案不在机上"**已过期**（§3.3：04:01 打包、04:03 装机为 **1.0.87**，包含 A+B）。
   本篇的**修改没有落地**：未部署、未提交，工作区仍是 1.0.87 的源码 + 本篇文档。F1–F7 属于"待下一批动手"，且**动手前需要先定 §七-2 那个界面归属**。

---

## 八 · 04:10 二次核验：自我订正清单 + 检索来源

### 8.1 本篇初稿（03:58）错在哪，逐条列全

| # | 初稿写法 | 现读后的事实 | 类型 | 位置 |
|---|---|---|---|---|
| ① | "`VpnManagementErrorStatus` 18 个成员，其中 12 个是 **19041+** 才有" | **19 个成员**（`Ok=0 … InvalidIP=18`）；那 13 项是 **1607 / SDK 14393**（`ContractVersion 196608 = contract v3`）引入的，我们最低支持 `17763` ⇒ **全覆盖** | 改**强**：F1/F2 失去兼容性退路 | §1.1 |
| ② | "今天 `profile-connect` 行数 = **0**（查 `app.log`）" | `RollingLog` 按 category 分流，这一行在 **`plugin.log`**；今天实有 **3 行**（`:21/407/770`），形态全是 `发起前状态=Disconnected, 连接请求=Ok, 实际状态=Connected` | 改**弱**：不是"整日零记录"，是"两次发起零记录 + 从未有非 Disconnected 样本" | §3.1 |
| ③ | "`ConnectAsync` 的 await 是**有界的**（20×1 s + 取锁 3 s）" | 有界的只有等锁那 3 s；`ProfileStore.Run:82` 的 `await call()` **无任何超时**，悬挂时 `finally` 不执行 ⇒ **闸永久被占**（新增 D8、新增 F6） | 改**强**：本症状第一次有了代码级通路 | §二-2 |
| ④ | "装机版本 **1.0.85**，A 案不在机上" | 本轮期间装机线推进到 **1.0.87**（04:01 打包、04:03 起 `app.log:64`），**含 A+B**；但没有一条 `profile-switch` ⇒ 上机 ≠ 已验 | 改**事实** | §3.3、§二-2 |
| ⑤ | "平台的真实回答我们**从来没抓到过一次**，所以本症状不能定案"（＝零证据） | 本机确实零样本，但**同路线先遣档案有三条独立一手**（`docs/60` §3.1/§3.2、`docs/62` §2、`docs/49:141`：await 不返回 + 常驻 `Connecting` + 重试一律 `AlreadyConnected`），外部另有一条同症状报告（Q&A 3287785） | 改**强**（从"猜"到"有先例的候选解释"），但**仍不是本机定案** | §三之五、§七-1 |
| ⑥ | （隐含假设）先例里的 `rasdial /disconnect` 可以直接当我们的清理出口 | 实测：桌面 `rasdial` 回 `No connections`、`Get-VpnConnection` 空，**我们的三条 profile 只在包内 `LocalState\rasphone.pbk`** ⇒ 该出口**对我们不成立**，F7 只能退化为"包内 pbk + 插件 sessions"两路 | 补**边界** | §3.5.3、F7、§五 |

### 8.2 检索来源（全部现读，2026-10-21）

- [VpnManagementErrorStatus Enum (Windows.Networking.Vpn) — winrt-26100](https://learn.microsoft.com/en-us/uwp/api/windows.networking.vpn.vpnmanagementerrorstatus?view=winrt-26100)：19 项 + 数值 + Version history（13 项 = 1607 / SDK 14393）→ §1.1 订正依据。
- [利用适用于 Windows 10 IoT 核心版设备的 VPN 功能（learn.microsoft，IoT Core）](https://learn.microsoft.com/zh-cn/previous-versions/windows/iot-core/connect-your-device/vpn)：官方示例 `ConnectVPNProfile()` 把返回值存成 `VpnManagementErrorStatus status` 并判 `Ok` → §3.5.2 第三条官方写法。
- [windows10 vpn拔号提示"正在连接"问题 — Microsoft Q&A 3287785](https://learn.microsoft.com/zh-cn/answers/questions/3287785/windows10-vpn)（2020-03-24）：任务栏"断开→再连另一条→卡正在连接、只能重启"，改从"设置→VPN"连接即可；独立顾问 Jarl：**"不能排除属于系统的 BUG" / "这个情况，确实很可能属于系统的 BUG"** → §3.5.2。
- [Windows and Windows Holographic device settings to add VPN connections using Intune](https://learn.microsoft.com/en-us/intune/device-configuration/templates/ref-vpn-settings-windows)：「Only assign one profile per device with Device Tunnel enabled.」+ Windows 11 / VPNv2 CSP 并发变更导致掉线的官方已知问题段 → D4 / F3 / §3.5.2。
- [在 Intune 中创建 VPN 配置文件以连接到 VPN 服务器](https://docs.microsoft.com/ZH-CN/mem/intune/configuration/vpn-settings-configure)：中文版同一告警段（两条 profile 同时下发 / 下发时删活动 profile ⇒ 失去连接）。
- [About Always On VPN for Windows Server Remote Access](https://learn.microsoft.com/en-us/windows-server/remote/remote-access/overview-always-on-vpn)：Always-on 触发与单会话产品口径的旁证。
- 仓外先例（本地档案，非网络）：`D:\fluentapps\repos\clashUWP研究\docs\60` §3.1/§3.2、`docs\62` §2、`docs\49:141`、`docs\45:53`、`docs\61:180`、`docs\46:11,44-46,57` → §3.5.1。
- [如何断开传入 VPN 连接的连接（Microsoft Learn / KB 2737610）](https://learn.microsoft.com/zh-cn/troubleshoot/windows-client/networking/disconnect-incoming-vpn-connection)：官方给的断开办法是**人工 UI 通道**（`ncpa.cpl` → 右键连接 → 状态 → 断开），不是 API ⇒ §九-5。

---

## 九 · 「官方的解决方案」到底是什么（04:25 补：把官方口径钉死，再对我们的代码）

### 9.1 先做一件取证：官方在这一层**有没有**强拆/中止原语

现读本仓 26100 元数据投影 `Windows.Foundation.UniversalApiContract.decompiled.cs:218006-218069`（`class VpnManagementAgent` 起于 `:218006`，方法块止于 `DisconnectProfileAsync` 的 `:218069`），它的**全部**方法只有 9 个：

```
AddProfileFromXmlAsync / AddProfileFromObjectAsync
UpdateProfileFromXmlAsync / UpdateProfileFromObjectAsync
GetProfilesAsync / DeleteProfileAsync
ConnectProfileAsync / ConnectProfileWithPasswordCredentialAsync
DisconnectProfileAsync
```

- 官方文档页还列了 `GetAlwaysOnActiveProfile(Async)` / `SetAlwaysOnActiveProfile(Async)`，**但这两对在我们这份 26100 元数据里一条都没有**（本轮 grep `AlwaysOnActiveProfile` ⇒ 0 命中；全仓 `AlwaysOn` 只命中两处**bool 属性**：`IVpnProfile.AlwaysOn`（`:216635`，原文"If the VPN profile is an AlwaysOn profile, it is TRUE"）和 `VpnPlugInProfile.AlwaysOn`（`:218620`，微软自己的注释还拼错成 "the **AlwayOn** property"），都不是方法）。
  ⇒ 那是更高 moniker 的东西，**我们 SDK 上用不了**（要用得先编译验证；本轮不猜）。
- 插件侧同样没有退出原语（`Docs/09` 审计 + 记忆 `reference-vpnchannel-api-audit`）：没有 `Disconnect()`，`Stop()` 只用来回应平台发起的断开，`TerminateConnection` 只管"建连尝试"、拆不掉已建好的通道。

**⇒ 官方在这一层没有"把卡住的连接强拆掉"这个 API。**任何"平台有个 reset 就好了"的想法都不成立。

### 9.2 官方给的解法其实是"调用方自己保证串行"，四条合起来就是全部

| # | 官方口径（出处） | 含义 | 对我们的动作 |
|---|---|---|---|
| ① | **先断后连**：sample `:521-533` 要 `Connected` 才 `DisconnectProfileAsync`，`:495-497` 要 `Disconnected` 才 `ConnectProfileAsync`；删 profile 也先断（`:121-131`） | 切换 = **调用方串行**：断开 → 等落到 `Disconnected` → 再连。平台不替你排队 | **A 案已按这个写**（`EnsureTornDownAsync` 再拨，`VpnProfileSetup.cs:715-790`）；缺的是把「连接 VPN」按钮也接进来（**F3**） |
| ② | **等收尾要判状态，不是判"我已经发起过断开"**：`DisconnectProfileAsync` 返回的也只是 error status | "断开请求=Ok" ≠ 会话已收尾 ⇒ 必须回读 `ConnectionStatus` | A 案的 `WaitSessionDownAsync` 已在做；`GracefulTeardown.cs:188` 那条丢弃返回值的要补（**F4**） |
| ③ | **被拒绝时读返回值退避**：`AlreadyConnected(8)` / `AlreadyDisconnecting(7)` / `CannotFindProfile(6)` 这些成员的存在本身就是"平台在告诉你现在不该拨"（枚举页 + 元数据 `:218088-218140`） | 官方的错误处理模型 = **返回值驱动退避**；sample 的 else 分支就是这么写的 | **F1/F2**：两行日志 + 非 `Ok` 一律按失败回话；`AlreadyDisconnecting` ⇒ 继续等，`AlreadyConnected` ⇒ 别重试（先例：重试只会一直吃这个码） |
| ④ | **至多一条活动会话**：sample `:80` + 可连清单只收 `Disconnected`（`:101-113`）+ 官方 Intune 页「Only assign one profile per device with *Device Tunnel* enabled.」 | 产品模型上没有"两条并行" | **F3** 的规范依据；也是我们"连另一条必须先收尾这一条"这条产品语义的官方背书 |

### 9.3 悬挂这件事官方没有解法，所以 F6 是**我们的兜底**，不是官方的解

- 官方文档**没有任何一句**说 `ConnectProfileAsync` 会限时返回，也没有 abort/timeout 参数（`VpnManagementAgent` 就那 9 个方法，9.1 已数完）。先例里它挂过 >3.5 min（`docs/60` §3.1）与 45 s 不返回（`docs/62`）。
- 微软支持侧给的处置全是**人工通道**：Q&A 3287785 的"改从 设置→VPN 点连接"、KB 2737610 的"`ncpa.cpl` → 状态 → 断开"，以及"重启电脑"。官方顾问自己说"很可能属于系统的 BUG" ⇒ **官方没有编程解，只有绕开它**。
- ⇒ 所以按官方口径推到底，正确的次序是：**①–④ 先做对（不制造悬挂）→ F6 的超时只作"万一还是悬挂了别把整机 UI 卡死"的兜底 → 兜底触发时给用户的指引必须是 ④ 的人工通道（去系统设置断开这条），而不是我们自称"已修"**。这条也解释了为什么 F6 要"超时后冻结写类调用、只放开读"——悬挂没清之前，任何再拨都是在往 `AlreadyConnected` 上撞。

### 9.4 一句话回答

**官方的解决方案 = 调用方自己串行（先断、等收尾、确认 Disconnected 再连）+ 按 `VpnManagementErrorStatus` 退避；平台既没有并发模型，也没有强拆/超时原语。**
我们对应的缺口就三条：F1（读返回值并两行留痕）、F3（把按钮路径也纳入串行）、F6/F7（悬挂兜底 + 分层判残留），其中只有 F1/F3 属于"把官方每一步实现完"，F6/F7 是官方没有、我们自己加的保险。

---

## 十 · 实施记录：官方方案已动手（**2026-10-21 07:01，1.0.88**）

> 用户口径："走官方方案" ⇒ 本批**只做 9.2 那四条**（F1/F2/F3/F4 + D7 那条死条件），
> F5（CLI 说明书）与 F6/F7（我们自加的悬挂兜底/分层判残留）**不在这一批**，仍挂在 §四 待办。
> 全部改动只碰**代码 + 清单版本号**，**未部署、未提交、未拨号**（建档时刻：2026-10-21 **07:01**）。

### 10.1 改了哪几处（逐处给文件:行号与"对应官方第几条"）

| 官方第几条 | 文件:行 | 改成什么 |
|---|---|---|
| ① 前置条件 + ③ 返回值 + 观测出口 | `Vpn/VpnProfileSetup.cs` **新增 `ConnectOnceAsync`（:401-447）** | 次序钉死：先落**发起行**（`发起前状态=X 发起=即将发出连接请求`）→ 才 `await ConnectProfileAsync` → 再落**返回值行**。**发起行在 await 之前**，所以悬挂时至少留一条痕迹（本症状第一次可定案）。前置条件：`Connected` ⇒ 回"不再拨第二次"、`Connecting/Disconnecting` ⇒ 回"平台正在这条上处理，稍后再试"，两者都**不发起**。`!= Ok` 走 Warn 并带 `ExplainDialError` 的逐条官方语义。轮询条件改成"只在 `Connecting/Disconnecting` 上继续等"，**D7 那条永不成立的 `!= "Waiting"` 已删**。 |
| ④ 至多一条活动会话（F3） | 同上 **`SerializeBehindActiveSessionAsync`（:457-476）** | 「连接 VPN」按钮路径从此和 A 案同一条纪律：拨**另一条**前先 `GracefulTeardown.EnsureTornDownAsync` 收尾旧会话，**收不干净就拒绝发起**（`新连接=跳过（避免与未收尾会话并发）` + Warn）。 |
| 判据只此一份 | 同上 **`FindActiveSessionAsync`（:488-494）/ `SameProfile`（:497-501）/ `RefreshForDialAsync`（:509-514）** | A 案原先就地写的"当前会话在哪"收进共用方法；按钮与切换两条路读同一把尺子。拨号前再回读一次目标对象（收尾之后旧快照会把"刚被我们断掉的"读成还连着）。 |
| ③ 退避（全部 19 个成员） | 同上 **`ExplainDialError`（:525-547）** | 按 26100 元数据 `:218088-218140` 逐条给中文语义，`AlreadyConnected`/`AlreadyDisconnecting` 明写**不得重拨**；未识别的码按失败原样上报。 |
| ② + ③ 断开侧（F4） | `Vpn/GracefulTeardown.cs` **`EnsureTornDownAsync`（:178-217）** | 断开返回值不再丢弃：`Ok`/`AlreadyDisconnecting`/其余三档分开写进 report ⇒ "未等到收尾"从此能区分「断了没收尾」与「平台根本没接受断开」。另加**发起前一行** `teardown` 日志（同一个观测缺口，断开也会悬挂）。 |
| ③ 断开回话 | `Vpn/VpnProfileSetup.cs` **`DisconnectAsync`（:1100-1132）** | 按枚举分支回话，`NoConnection`/`AlreadyDisconnecting` 不再和 `Ok` 混成一行；非受理码走 Warn。 |
| 副作用：哨兵不能被"没拨的号"误伤 | `Vpn/VpnProfileSetup.cs` **`IsNoDialResult`（:558-560）** | 本批之后 `ConnectAsync` 会**拒绝发起**，而调用方是**先武装哨兵再连接**的 ⇒ 必须能让调用方认出"这一句根本没拨"。判据靠两个固定前缀（`未发起=` / `新连接=跳过`），**改那两处拒绝文案要同步改这里**；`VpnSession` 拿到它之后怎么走两分支见下一行。 |
| 保护不能被"没拨的号"摘掉 | `Backend/HealthMonitor.cs`（:26-34 新增 `IsActive`）＋ `Backend/VpnSession.cs`（:45 记 `hadSentinel`、:58-77 分支） | `Arm()` 是"顶掉上一个再武装"。所以本批之后 `ConnectAsync` 被拦住时，解除动作要**分两种**：点击前<b>没有</b>哨兵 ⇒ 解除本次这个（否则哨兵到点会把**平台自己正在处理**的那条连接当"隧道没起来"断掉，见 `HealthMonitor.cs:309-313` 的非终态出口）；点击前<b>已有</b>哨兵 ⇒ **不解除**，因为本次武装的就是那份保护的延续，摘掉它等于"多点一下按钮就把既有黑洞保护摘了"，那是净变坏。武装仍然在拨号之前 —— 卡死保护没有削弱。 |
| 人工出口指错（订正） | `Vpn/GracefulTeardown.cs`（:238-246） | 原先让跑 `rasdial ClashUWP /disconnect` —— 本篇 §3.5.3 已实测**桌面看不见包内 profile**，照敲只会得到"No connections"。改成官方给人工处置的那条：**系统设置→VPN（或 `ncpa.cpl`）断开并确认状态**。 |
| 版本号 | `ClashUWP/Package.appxmanifest:48` | `1.0.87.0` → **`1.0.88.0`**（同版本重装必撞 `0x80073CFB`，见记忆 `project-same-version-reinstall-0x80073cfb`）。 |
| ④ 探测路径也守"至多一条" | `Vpn/VpnProfileSetup.cs` **`ProbeConnectAsync`（:1177-1191）**、`Backend/StartupProbes.cs`（:76-88） | 两个诊断拨号点从此**在有活动会话时不连 probe**（`跳过=已有活动会话 [x]…探测不得并行拨号`，Warn 一行 + 结果照旧落盘）。它们原先会绕过本批所有判据直接 `ProfileStore.ConnectProfileAsync`，留着它就是留着一个"自己制造并发"的口子。判据共用新暴露的 `FindActiveSessionAsync`（已从 private 抬为 internal，注释里写明理由）。★ 边界：这两处只补了第④条，**没**套用 `ConnectOnceAsync` 的两行留痕 —— 探测的意义就是把平台原话记下来，不该被前置条件改写；而"探测自己也会悬挂"这一条仍归 F6。 |


### 10.2 已核 / 未核（不装机的话就是"代码层成立"）

- ✅ **编译**：`dotnet build ClashUWP/ClashUWP.csproj -c Debug -p:Platform=x64` ⇒ **0 警告 0 错误**（2026-10-21 07:01 起多轮；**最后一轮在 23:12**，含 §十 + §十一 全部改动：`VpnProfileSetup` / `GracefulTeardown` / `VpnSession` / `HealthMonitor` / `StartupProbes` / `ProfileStore` / `ClashVpnPlugin`）。
- ⚠️ **只验了 Debug 配置的 C# 编译**：Release + 原生/AOT 那条链（`build-log.txt` 里 "Generating native code"）本轮没跑，打包时才会走 —— 所以"能装"这件事**仍未证**，按 §10.3 装机判据才算。
- ❌ **未打包、未部署**：按 `AGENTS.md` 第 2 条，部署走 VS/官方桥接工具，等用户发话。
- ❌ **未拨号**：本批没有任何 `ConnectProfileAsync`/`DisconnectProfileAsync` 实调用（不碰整机网络，口径同记忆 `feedback-never-hog-machine-network`）。
- ⚠️ **F1/F3 的验收判据仍然全部未验**（§四 最后一列那三行要点：两行能分开看到、已连接时点只出"未发起"、连着 A 点 B 出 `profile-switch` 三要素）。**A 案到今天也没有一条 `profile-switch` 记录**（§3.3）。
- ⚠️ **本篇 §七-2 那个归属问题仍未答**（在 app 里点的 / 在快速面板点的）。本批改的是 app 内按钮与 CLI `connect` 这两条**经我们代码**的路径；如果那次点击走的是快速面板（shell 直发 `VpnManagementAgent`），**这一批碰不到它**，症状可能照旧。

### 10.3 装机后要盯的五条（`plugin.log` / `app.log` 的 `profile-connect` · `profile-switch` · `teardown` · `session`）

1. 断开态点「连接 VPN」⇒ 先出 `发起=即将发出连接请求`，再出 `连接请求=Ok（平台已受理…）`，最后 `实际状态=Connected`。**三行分得开才算 F1 生效**；只有一行且停在"发起"= 抓到悬挂。
2. 连着 A 时点「连接 B」⇒ `[…B] 发起前状态=Disconnected` 之前应先出现"按官方「至多一条活动会话」先收尾"这一行，随后 `收尾=干净`；收不干净则是 `★拒绝发起 —— …新连接=跳过…`。
3. 已经连着这条时再点「连接 VPN」⇒ `未发起=平台报告这条已经 Connected（会话已在，不再拨第二次）`，且**不产生新会话**（ETW 里不该出现第二次 `Connecting`）；这条同时验证"哨兵没被摘掉"——`session` 里应出现"点击前已有哨兵在跑 ⇒ 不解除"或"当场解除"其中一行，按当时是否已有保护而定。
4. 平台在途时点（状态读到 `Connecting/Disconnecting`）⇒ `未发起=平台正在这条 profile 上处理…稍后再试`（Warn），前台不会看到"连接成功/失败"这类误导文本。
5. 任何一次 `强行执行`（`profile-store` Warn）出现 ⇒ 说明 §五 那条"切换期间 20 s + 20 s 把闸占满"的风险成立，那时才轮到把 F6 提上来做。
   > ★ **23:13 订正**：这一条**已作废** —— F6 已在 §十一 落地，代码里 `强行执行` 这个分支**已经删掉**（等不到闸就本轮放弃，不再并发放行）。
   > 装机要盯的对应行换成 §十一-11.6 那五条（`profile-store` 的三种新 Warn 形态 + `connect-window` 两行）。

---

## 十一 · 实施记录（第二批）：把"会阻塞系统"的两处按官方文档改掉（**2026-10-21 23:13，仍 `1.0.88.0`**）

> 触发链：用户先问「ds说的对吗」→「你的原文引用呢，你怎么知道是不是"系统会依次调用 Disconnect 再 Connect"」
> →「搜索官网，系统侧是如何处理的？我们是否写了阻塞系统的错误代码？」→ 本轮口径 **「先把阻塞系统的代码按照官方文档修复」**。
> 本批**只碰代码 + 本档**：未打包、未部署、未提交、未拨号（同 §十 的纪律）。

### 11.1 官方在这一层到底支持什么（现读，全部一手）

| 官方原文 | 出处 | 能推出什么 | 推不出什么 |
|---|---|---|---|
| "While the app is in the foreground, the regular **Connect timeout** is paused." | `VpnChannel.ActivateForeground`（`IVpnChannel6`，Learn 条文；26100 投影 `:216912-217010` 区段有成员无 Remarks） | **平台在同步等 `IVpnPlugIn.Connect` 返回**，等到一个它自己的超时为止 | ★ **数值官方从没给过** —— 所以本批任何"上限多少秒"都是我们定的，必须逐条标注 |
| `ConnectProfileAsync`："Connects to an existing VPN profile."，`[RemoteAsync]`，returns "An enum value indicating the error status" | 26100 Learn + 本地投影 `:215540-215660`（§1.1） | 这是个远程异步调用，答案只在返回值里 | **没有 Remarks、没有限时、没有并发条款、没有中止原语**（§9.1 已把"有没有强拆"查干净了） |
| "There should be **zero or one** of these at all times"；可连清单只收 `Disconnected` | 官方 sample `MainPage.xaml.cs:80` / `:101-113`（§1.2 现读） | 官方按**至多一条活动会话**建模 ⇒ 调用方自己保证串行，不该在未返回的调用之上再发第二条 | 官方**没有**一句"系统会先 Disconnect 再 Connect"（那条是 DS 的推断，本仓 14 条会话样本里也没有一条真的"连着 A 拨 B"） |
| "TerminateConnection ... **Should not be called on reconnect cases.**" | `VpnChannel.TerminateConnection` 条文 | 重连场景不该调它 | 与"阻塞"无关 ⇒ 记在 §11.7，**本批没动** |

⇒ 官方能支持的只到**原则**那一层：*不要有任何一步无上限* + *不要并发*。数值、拒绝文案、恢复动作全是我们自己的，下面的表逐条把归属写死。

### 11.2 改了什么（逐处给文件:行；★ 行号是本批改动落地后现读所得）

| # | 文件:行 | 旧行为（会阻塞系统的形状） | 改成什么 | 数值归属 |
|---|---|---|---|---|
| ① | `Vpn/ProfileStore.cs:54-78`（类注释新增 §十一 段） | 头注释只写了"超时后照旧放行（不无限等待）"，而 `Run` 的实现根本不放行 | 把两条官方原文、"为什么不取消悬挂那条"、以及**"若平台永不返回 ⇒ 本进程后续 profile 存储调用会一直被拒"** 这个后果全部写进注释 | —— |
| ② | 同上 `:82-113`（`AcquireTimeoutMs` / `ReadBudgetMs` / `WriteBudgetMs` / `_abandonedOp`） | 无常量区分读写，`call()` 不带时限 | 三个显式常量 + 一个"有一条至今没返回"的状态位（`volatile`，**只在超过有界等待之后**置位；正常在飞不置位，仍让后来者排队 ≤3 s —— 那是 §开头"错峰读全好"依赖的行为） | 读 3 s（§四 F6 原案：沿用现有 3 s 口径）；写 45 s（§四 F6 原案，来源先遣 `docs/62:65` 经验线）；★ **都是我们定的，官方无时限** |
| ③ | 同上 `:121-186`（`Run<T>` 全量重写） | `await call()` 无界 ⇒ 平台不返回就**永久占闸**；等锁 3 s 超时后"**强行执行**"⇒ 往同一个 agent 并发发第二条 RPC（正是 `0x80070006` 的形状） | **四个出口**：⓪ 已有未返回的 ⇒ 立即拒（连队都不排，免得前台每轮白等 3 s）；① 排队 ≤3 s 拿到闸 → 正常走；② 排队都拿不到 → 本轮放弃 + Warn，**不并发**；③ 拿到闸但 `call()` 超过预算 → `Task.WhenAny` 判超时，本次按失败回话，**闸留给那条未返回的调用**，并由它的续块在平台真给结果时放行 + 记一行"共 N ms 后返回"。另加一条"返回了但 >2 s"的 Warn（慢也是可观测的） | 续块用 `TaskScheduler.Default` + `ContinueOptions.None`，成败都放行，绝不把闸留死 |
| ④ | 同上 `:188-199`（新 `ProfileStoreBusyException`） | 没有这个概念 | 三条成因（上一条未返回 / 排队超时 / 本条超时）归一个类型；`Message` 就是给用户看的中文，明写"这次没发出"而不是冒充失败原因 | —— |
| ⑤ | 同上 `:201-225`（六个包装方法） | 全部裸 `await Agent.X(...)` | 每个都带预算：读 `GetProfilesAsync`→`ReadBudgetMs`，写 `Connect/Disconnect/Add/Update/Delete`→`WriteBudgetMs` | ★ 静态自查已确认**全仓 profile 存储 RPC 只有这一个 agent 实例、且全部经 `ProfileStore`**（本轮 grep：`ProfileDump:46,90` / `StartupProbe:53` / `StartupProbes:109,119,131,221,261` / `GracefulTeardown:142,201,254` / `VpnProfileSetup` 十余处 / `VpnSession`），没有旁路 |
| ⑥ | `Vpn/VpnProfileSetup.cs:404-418`（`ConnectOnceAsync` 发起前回读） | 回读会抛 0x80070006（旧形态） | `catch (ProfileStoreBusyException)` → 回话 `"未发起=" + Message` + Warn ⇒ 仍然**一行都没拨**，`IsNoDialResult` 的前缀纪律不破 | —— |
| ⑦ | 同上 `:432-446`（真正那次拨号） | `await ProfileStore.ConnectProfileAsync` 之后才有日志 | 本条超时/被拒 ⇒ `head + " 未发起=" + Message`，Warn，返回 `(false, …)`。**发起行已在 await 之前落盘**（§十 F1 的两行留痕），所以悬挂第一次就能定案 | —— |
| ⑧ | 同上 `:458-476`（20 轮状态轮询） | 每轮 `refinder()` 裸 await | 每轮单独 `try/catch`：读不到 ⇒ `实际状态=状态不可读（…）` 并 `break`。★ 注释里写死理由：**读不到不等于没连上，绝不猜成 `Disconnected`**（猜成 Disconnected 会让上层以为可以安全删 profile） | —— |
| ⑨ | 同上 `:494-530`（`SerializeBehindActiveSessionAsync`，官方第④条） | 判据/收尾两处裸 await | 两处各自 `catch`：读不到"当前会话在哪" ⇒ 更不能裸拨 ⇒ `新连接=跳过（…）` + `★拒绝发起`；收尾那步发不出去 ⇒ 同一条拒绝文案。**结论一律是"不发起"，不是"发起并祈祷"** | —— |
| ⑩ | `Backend/VpnSession.cs:45-95` | §十 的两分支哨兵逻辑内联在 `ConnectAsync` 里 | 抽成 `ApplyNoDialSentinelRule(hadSentinel, why)`，`ConnectAsync` 现在**两条路**都走它：回话命中 `IsNoDialResult`，或串行层直接抛 `ProfileStoreBusyException`（catch 后**照原样 rethrow**，让上层文案不变）。武装仍在拨号之前 —— 卡死保护一点没削弱 | —— |
| ⑪ | `Vpn/ClashVpnPlugin.cs:619-642`（插件 `Connect` 内 transport） | `connectTask.Wait()` **无上限**，而这是平台**同步等的回调**里的一步 ⇒ 我们把整个平台的建连流程挂在自己一个 UDP "连接"上 | 改 `Wait(TimeSpan.FromSeconds(3))`；超时 ⇒ `LogDiagnosticMessage` + `RollingLog.Warn("connect", …)` 后**继续走 Start**（语义与原 catch 分支一致：Start 才是平台契约点），并挂一条 `OnlyOnFaulted` 续块，让"晚到的失败"出声而不是静悄悄消失 | ★ **3 s 是我们定的**（官方只承认"有 Connect timeout"，无数值）。本机证据：UDP `ConnectAsync` 不握手不发包、立即返回，14 条会话 `Connect→Start` 全程 1–2 s |
| ⑫ | 同上 `:908-917` + `:943-948`（新 `connect-window` 两行） | 从来没有量过"我们在 `Connect` 回调里占住平台多久" | 成功路径在 `StatusToast.Start` 之后记 `Connect 回调内累计阻塞=Nms`；失败路径在 catch 里记 `Connect 回调失败前累计阻塞=Nms stage=…`。**这是唯一能和官方那句 Connect 超时放在同一把尺上的量** | 阈值不判优劣，只记原值；装机第一轮用它把 45 s / 3 s 这两个自定常量改成一手数（§五 第 7 行同一条纪律） |

### 11.3 ★ 与 §四 F6 原案的两处偏离（不写清就是留一笔假账）

| 项 | F6 原案（04:10 写） | 本批实际 | 为什么改 |
|---|---|---|---|
| 超时后闸怎么处理 | "**释放闸**（把悬挂那次标成 `abandoned`，`finally` 照常 `Release`）" | **不释放**：闸由那条未返回的调用继续占住，等它自己返回/抛时由续块放行 | 官方没有中止原语（§9.1 已取证），所以"释放"之后那条**仍在飞**，下一位一发就是**明知故犯的并发** —— 而那正是本类建档要治的 `0x80070006` 形状。§五 第 6 行当时把这写成"两害相权"，现在判定：**并发那条更坏**，因为它污染的是我们唯一的判据来源（读到的状态可能是假答案，不是没答案） |
| 超时后放开哪些调用 | "冻结**写**类，只放开**读**" | **读写一律拒**（⓪ 那条快速拒绝） | 悬挂期间读回来必然是 stale —— 先例一手就是这么写的：`rasdial` 显示 Connected 而平台 profile 常驻 `Connecting`（`docs/62:65`，§三之五）。"放开的读"只会把假数据喂给哨兵和对账，而这两者都是会动手的 |
| 代价（明写） | 原案担心的是"不释放 = 整个 agent 永久卡死（更坏）" | 本批选的就是"卡死但**有声**"：若平台永不返回，本进程内 profile 存储调用会一直被拒，直到那条返回或进程退出 | 与旧行为的差别不在"会不会卡"（旧写法同样永久占闸，而且**还额外并发**），而在**每一次拒绝都有一行 Warn + 给用户一句中文**，且恢复条件是自动的（续块）。★ 这条至今 0 一手样本，装机若不成立按 §八 的纪律现读重验再订正 |
| 数值 | 拨号臂 45 s / 读臂 3 s | **照抄**：写 45 s / 读 3 s | 没有自改。为什么不压短：切换路径上收尾与轮询各自占闸，压短会把"慢但会回"误判成"悬挂"，而误判的代价（此后全被拒）比多等 25 s 大得多 |

### 11.4 已核 / 未核（不装机就还是"代码层成立"）

- ✅ **编译**：`dotnet build ClashUWP/ClashUWP.csproj -c Debug -p:Platform=x64` ⇒ **0 警告 0 错误**（23:12）。★ 中间一轮**真的失败过**：`VpnProfileSetup.cs:513` 的 `(bool torn, string report);` 是 C# 不允许的声明式元组写法（CS8185×2 + CS0201 + CS0165×2），已改为 `bool torn; string report;` 后干净 —— 记下来是为了说明这轮编译不是走过场。
- ✅ **静态自查**：全仓无旁路 agent（§11.2 第 ⑤ 行）；新异常不会变成"未观察异常"——四条 fire-and-forget 上游都有 `catch`（`ProfileDump.cs:72`、`StartupProbes.cs:43,272`、`StartupProbe.RunAsync` 内层 try、插件 `post-start-teardown` 的 `catch (Exception)`），前台两处 `Task.Run`（`MainComponent.cs:221` 3 秒轮询、`:370` 订阅元信息）各自带 try，`HealthMonitor.RunAsync` 更是"任何异常 ⇒ `statusBlind` ⇒ 绝不动手"。
- ⚠️ **只验 Debug 配置的 C# 编译**；Release + AOT/原生那条链打包时才会走 ⇒ "能装"仍未证（同 §10.2）。
- ⚠️ **跨进程不成立**：闸只在各自进程内有效。app 与插件是两个进程，而**快速面板 / shell 直发**那条根本不经我们代码（§10.2 的归属问题原样未答）——本批不能让那条路变安全，能做的只是别再自己制造并发。
- ⚠️ **UI 在"真悬挂"期间会读不到状态**：`GetProfilesAsync` 一律被拒 ⇒ 状态文本停在最后一次成功值。★ 边界要说准：**正常切换不会** —— 闸只在 RPC **真的在飞**的那几毫秒里被占，20 轮轮询每轮之间闸是空闲的；只有 45 s 那种悬挂才会持续拒。同时哨兵在那段时间会"盲"，按既有纪律"判据不可信绝不动手" ⇒ 不会误杀，但那段时间黑洞保护也确实救不了。
- ❌ **未打包、未部署、未拨号**；**悬挂臂没跑过**：造一次真悬挂 = 刻意让平台不返回并占住整机网络，属需要用户点头的动作（口径同记忆 `feedback-never-hog-machine-network`），届时用既有的 Wi-Fi 无线电臂，不用拔线/禁网卡。
- ❌ **`_abandonedOp` 这条路径至今 0 样本**；`强行执行` 历史也是 **0 次**（§3.5.3）⇒ 本批修的是**代码形状**（无界 await + 明知可能并发），不是已观测到的故障。这一点别在报告里说成"治好了什么"。

### 11.5 与 §五 风险表的关系

§五 第 3 行（"F3 与 A 案共用串行闸，切换期间其他读排队 >3 s ⇒ 走 D6 的强行执行并发"）**从此不再是并发风险，而是可用性风险**：表现为 UI 一轮读不到状态 + 一行 Warn。第 6 行（"F6 超时后释放闸，但 RPC 还在飞 ⇒ 真并发"）按 §11.3 的判定**改成了不释放**，那行的"回滚 F6 的释放半边"这条预案因此不适用，改盯 §11.6 第 2 行。第 7 行（"45 s 是先遣经验线不是官方承诺"）**原样成立**，装机第一轮就该用 `connect-window` 与"共 N ms 后返回"那两行把它换成一手数。

### 11.6 装机后要盯的五行（新增通道：`plugin.log` / `app.log` 的 `profile-store` · `connect-window` · `connect`）

1. **正常一次拨号里 `profile-store` 应该一行都不出**（Warn 三种形态都不该出现）。出现了就是闸真被占过，按下面三条分辨。
   ★ **00:29 订正（装机一手把这条判据写错了）**：这条漏了第四种形态「`X 返回了但很慢：Nms`」，而且 2000 ms 这个阈值**低于正常拨号耗时**——
   §13.3 四个一手样本里有两个越过它（2084 / 2273 ms），而这两次闸都没被占。⇒ 该行今后按「**很慢 Warn 是常态、不构成异常**；只有第 2、3 行那三种形态（快速拒 / 排队放弃 / 迟返回）才是闸真被占过」读，阈值建议下一次动这片代码时抬到 3 s。
2. `那条未返回的 ConnectProfileAsync 最终返回（共 Nms）⇒ 闸已放行` —— **这一行是本症状第一次的定案证据**：它同时给出"平台确实会晚返回"与"晚多久"。★ 拿到 N 之后必须回来改 §11.2 第 ② 行那两个常量（这是 §五 第 7 行欠的账），不许让 45 s 长期挂着先遣项目的经验线。
3. `profile-store` 的另两种 Warn：`X=拒绝（上一条 Y 至今未返回…）`（⓪ 快速拒）与 `等上一次调用超过 3000ms 仍未放行 ⇒ X 本轮放弃`（② 排队拒）。**两者都意味着"这条没发出去"**，前台应看到 `未发起=…` 或 `新连接=跳过（…）`，绝不能看到"连接成功"。
4. `connect-window` 的 `Connect 回调内累计阻塞=Nms` —— 本机对照是 1–2 s（14 条会话，§3.2）。★ 若 N 明显超出（>10 s 量级），说明我们把平台同步等的回调占得太久，那时该动的不是 45 s 那个闸，而是把 `Connect` 里更靠后的步骤（桥启动、看门狗、通知）挪出同步路径；这条判据就是为那一轮准备的。
5. 连着 A 时点「连接 B」（§10.3 第 2 条那个从未拿到样本的动作）——期望顺序仍是 `按官方「至多一条活动会话」先收尾` → `收尾=干净` → `发起=即将发出连接请求` → `连接请求=Ok`；若中途出现 `★拒绝发起 —— 旧会话 [A] 收尾这一步发不出去（…）`，那是本批新逻辑正常生效（宁可不拨也不并发），**不要当成回归**。

### 11.7 仍开着的（本批范围之外，逐条给归属）

- **F5**（§3.4 两项：CLI `TargetPackageName` 写死、`--help` 的 `--force` 漏 `subuse`）——与"阻塞系统"无关，未动。
  ★ **【01:15 第三批已修并装机】**：`AppServiceChannel.cs:47` 改成 `8B8CC4F4.ClashUWP`，help 那一行改成由
  `ForcedCommands` 生成（`CommandCatalog.cs:244`）；CLI 1.0.4.0 装机凭据见 **§14.3**。同族漂移还多清了三处取证脚本。
- **F7**（悬挂/超时后的**分层残留判据**）——未动。本批只保证"悬挂时不再并发"，**没有**新增"悬挂之后怎么判定是哪一层残留"的能力；那条要读的包内 `rasphone.pbk` + 插件 `dataface-sessions.txt` 两路对齐仍是空口。
- ★ **本批新发现、但按口径不属于"阻塞"的一条**（等用户点头才动）：`ClashVpnPlugin.cs` 的 `AbortConnectionAttempt`（`:1042-1070`，四个调用点 `:434`/`:480`/`:588`/`:999`）**不判重连**就调 `SetErrorMessage` + `TerminateConnection`。★ 本批**现读官方 sample 复核过**（`CSharp/TestVpnPluginAppBg/VpnPlugin.cs`）：
  · `IsReconnect` 是 sample **自己的实例字段**（`:87` 声明，`:572` 在 `Connect` 末尾置真，`:640` 只在 `Disconnect` 里置假）⇒ 官方口径是"**平台不给标志，但调用方可以自己推**"：上次 `Connect` 走完了、且**从未被 `Disconnect`** ⇒ 这次就是重连。
  · 它门控了 **两处**交互出口：`SetErrorMessage`（`:595-597`，注释原话 "we do not set the error message for reconnection attempts"）与前后自定义 UI 提示（`:207`、`:434`）；而 **sample 全文一次都没调 `TerminateConnection`** —— 失败就是 `throw`（`:599`）。
  · ⇒ 订正我自己写在 `:1037-1040` 的那条注释："重连场景在插件侧读不到"这半句**事实成立**（26100 确实没有 `DisconnectReason`、没有公开的"这是重连"标志），但我由它推出的结论"**所以只能照现在这样无条件调**"不成立 —— sample 就是用自推标志位来门控的。做法：把插件侧已有的 `evt=disconnect` 痕迹抬成一个静态 `s_disconnectSeen`，重连时**跳过 `SetErrorMessage` 与 `TerminateConnection`**，并记一行 `evt=skip-terminate-reconnect`。
  · 定性：这是"**调错 API**"（条文原话 "Should not be called on reconnect cases."），**不是**"阻塞系统" ⇒ 严格留在本批范围外；但它比 F5/F7 更该优先，**等用户点头就动**。
  ★ **【01:15 第三批已点头、已落地装机】**：实现落点与上面那段草稿有两处不同 —— 标志位叫
  `s_lastConnectFinishedWithoutDisconnect`（`ClashVpnPlugin.cs:402`，**正向**记"上次 Connect 走完且没被 Disconnect"，
  而不是反向记 `s_disconnectSeen`：置真在 `Connect` 末尾 `:957`、置假只在 `Disconnect` 第一行 `:1943`，与 sample 同形），
  `AbortConnectionAttempt` 改成返回 `bool`，重连那一支交给调用方 `AbortOrThrow`（`:1169`）**抛出**而不是静默返回。
  逐处行号、四臂装机日志、以及"正向分支 0 样本 + 标志位是进程级不按 profile 分"这两条边界全在 **§14.1 行 1 / §14.2 / §14.5**。
- **D9 / D10 编号**：§11.2 第 ⑪/⑫ 行（插件 `Connect` 回调内的无上限 socket 等待 + 回调总耗时观测）在本篇 §二 的 D1–D8 表之外，代码注释里按 **D9** 引用（`ClashVpnPlugin.cs:619` 与 `:908` 两处标记）；闸那条沿用 **D8** 编号不再另开。★ 记在这里是为了防止下次读到"D9"以为本篇有第 9 行。
- **本批未做的验证**：悬挂臂（§11.4 第 6 条）、Release/AOT 编译、打包部署、"连着 A 拨 B"真样本 —— 四条全欠，装机一次只能还两条。

### 11.8 本批新增的检索来源（全部现读，2026-10-21）

- `VpnChannel.ActivateForeground` 条文（"the regular Connect timeout is paused"）与 `VpnChannel.TerminateConnection` 条文（"Should not be called on reconnect cases."）：<https://learn.microsoft.com/en-us/uwp/api/windows.networking.vpn.vpnchannel>
- `VpnManagementAgent.ConnectProfileAsync` / `DisconnectProfileAsync` / `GetProfilesAsync`（签名与 returns，无 Remarks）：<https://learn.microsoft.com/en-us/uwp/api/windows.networking.vpn.vpnmanagementagent>
- 官方 sample（`IsReconnect` 的声明/置位/门控、`SetErrorMessage` 的重连豁免注释、失败只 `throw`）：**本地入仓实际路径 `D:\fluentapps\repos\UwpVpnPluginSample\CSharp\TestVpnPluginAppBg\VpnPlugin.cs`**（本批现读 `:87` / `:160` / `:207` / `:434` / `:572` / `:595-599` / `:640`）；`MainPage.xaml.cs:80`、`:101-113`、`:496-506` 在**同级 `TestVpnPluginApp` 项目**里（两个项目分开，别再写成同一路径）
- 26100 本地投影元数据（成员与最小契约）：`winmd-dump/Windows.Foundation.UniversalApiContract.decompiled.cs:215540-215660`（`VpnManagementAgent`）、`:216912-217010`（`VpnChannel`）、`:218074-218140`（两个枚举）
- 先遣项目一手（45 s 经验线、悬挂后 `AlreadyConnected`、两层分别看）：`D:\fluentapps\repos\clashUWP研究` `docs/60`、`docs/62:65`
- ★ 同轴另一篇（本篇不谈"太旧"，那篇不谈"阻塞"）：`Docs/39-VPNPlugin-API调用时效性核验-26100与28000元数据对照.md`


---

## 十二 · Phase A 一手基线：UIA 在 **1.0.87** 上实测（2026-10-21 23:54 → 2026-10-22 00:02）

> 用户口径："uia 下测试" + 两项拍板（"**两步都要：先复现再升级验**"、"拨号范围**含「连着 A 点 B」切换臂**"）。
> 通道：`winapp ui`（inspect / invoke / scroll-into-view），目标窗口 `HWND 1050590`（AUMID `8B8CC4F4.ClashUWP_4tjvwvefxrz66!App`）。
> 全程只点界面上有的东西：**没有写 `debug-cmd.txt`、没碰隧道 REST、没有自制脚本**。
> 装机版本由日志自证：`bgtask 插件进程启动 version=8B8CC4F4.ClashUWP/1.0.87.0`（⇒ 本批 §十/§十一 的代码**一行都没在这台机器上跑过**，这里拿的是**改前基线**）。

### 12.1 四条臂的结果（逐条给时间戳与日志原文）

| 臂 | 动作 | 结果 | 关键日志（原文） |
|---|---|---|---|
| **A1** | 断开态点「连接 VPN」（生效=星链机场） | **t+4 s 到 `Connected`**；插件 `Connect→Start` 全程 **≈1.80 s**（`23:54:14.636` connect-begin → `23:54:16.437` 隧道已建立）——与"1–2 s"那条经验值吻合 | `23:54:17.653 profile-connect [星链机场] 发起前状态=Disconnected, 连接请求=Ok, 实际状态=Connected`<br>★ 注意它比 `23:54:14.146 session 用户发起订阅连接` **晚 3.5 s**：1.0.87 只有这一行、且是**事后**才写 ⇒ **D5 那个观测缺口第一次被时序量化**（悬挂时这 3.5 s 里日志是干净的，什么也看不见）。1.0.88 的 F1 两行留痕就是为了把这 3.5 s 变成"看得见的一条发起行"。 |
| **A2** | **已经 `Connected` 时再点同一条** | 平台答 **`AlreadyConnected`**（一手，此前只有先例）；**不产生新会话**（`connectCalls` 仍是 2，无新 `evt=connect-begin`）；1.0 s 内出结果 | `23:56:15.760 profile-connect [星链机场] 发起前状态=Connected, 连接请求=AlreadyConnected, 实际状态=Connected`<br>另：`23:56:14.674 health-arm 重新武装（顶掉上一个哨兵）` ⇒ `Arm()` 的"顶掉再武装"语义**实机确认**（§10.1 那两分支的前提成立）。<br>★ 这条正是 D2 的形状：19 个成员之一的 `AlreadyConnected` 被**插值进一行文本**，前台与日志都不分支。1.0.88 之后同一动作应变成 `未发起=平台报告这条已经 Connected（会话已在，不再拨第二次）`，且**根本不发这次 RPC**。 |
| **A3** | **连着星链时点 iKuuu_V2**（切换臂，用户症状的字面场景） | **5.8 s 走通，没有卡"正在连接中"**：`23:57:46.199` 发起 → `23:57:46.754` 旧会话 `evt=disconnect alive=210s` → `23:57:49.236` 插件 `connect #3` → `23:57:51.987` 切换成功 | `23:57:51.987 profile-switch [星链机场]→[iKuuu_V2] 收尾=干净（状态=Connected → 发起平台断开 等到收尾=Disconnected>Disconnected（2 s））, 新连接请求=Ok, 实际状态=Connected`<br>⇒ **本仓第一条"连着 A 拨 B"的一手样本**（§3.3 与 §11.4 那句"至今 0 条"从此作废）。形状与官方 sample 的"至多一条"一致：**平台自己先回调 `Disconnect` 再回调 `Connect`**，1.0.86 A 案的串行收尾与它同向。 |
| **A4** | 点「断开 VPN」收工 | t+3 s `Disconnected`；`profile-disconnect 断开=Ok`；`profile-sub-adopt 系统侧已断开…生效订阅保持不变` | 收工判据全绿：**默认路由只剩 WLAN**（`ifIndex 22 / 0.0.0.0/0 / 192.168.0.1`）、**NRPT 规则数=0**、无本家族残留的 Up 状态伪适配器、**60 s 内 `evt=connect-begin` 计数 16→16（没有自动重拨）**。 |

### 12.2 ★★ 本批最有价值的一条：`0x80070006` 被抓现行（候选 → 时序一手）

```
23:57:49.125  plugin W ui-poll   本轮刷新失败（同类不再重复记）：COMException: 句柄无效。 (0x80070006)
23:57:49.236  plugin I connect   平台发起连接 #3 resident=复用常驻内核 pid=19132
```

UI 那次 `GetProfilesAsync` 抛"句柄无效"的时刻，**距平台派发连接 RPC 只有 111 ms**，而它前面正是 A3 的收尾→重拨窗口。本篇 §开头（1.0.81 建档）与 `Docs/33` §三 留的那条候选——"**只有撞上连接 RPC 的那一读会炸，错峰读全好**"——由推断**升级为有时序的一手**。同时 `23:57:53.936 ui-poll 轮询已恢复` 复现了旧观察"约 2~4 s 后第二次读就好了"。

⇒ 这条一手直接支持 §十一 的两处偏离决定：**明知 RPC 在飞时绝不再发第二条**（1.0.87 的"强行执行"就是往这个火口上浇油；同一百毫秒里它还会把 UI 那一读炸掉）。装机后 §11.6 第 1 行的期望是：**同样的切换动作，`ui-poll … 0x80070006` 这一行不再出现**，改成 `profile-store` 的"本轮放弃/拒绝"Warn。

### 12.3 顺手钉死的两条事实（都有出处，不再是推断）

- **app 与插件在同一个进程里跑**：`session 用户发起订阅连接`（前台路径）与 `connect 平台发起连接`（插件回调）两行 **pid 都是 19132**，`bgtask 插件进程启动 pid=19132` 亦然 ⇒ 1.0.77 单清单入口合并的后果是"进程隔离**没有**成立"。所以 §11.4 那条"闸只在各自进程内有效"的边界要说准：**UI 侧与插件侧共享这道闸**，真正管不到的只有 shell/快速面板那条**另一个进程**（rasman/raspvpn）直发的路径。
- **UIA 操作口径**（下次少走弯路）：状态文本 `当前状态：X` **只在主页有**（订阅页轮询它必然读成空 —— 本轮 A3 那 120 s 的"状态列全空"就是这么来的，不是症状）；「断开 VPN」在 `offscreen`，要先 `winapp ui scroll-into-view` 再 `invoke`；页面切换后 slug 会变（本轮 `btn-vpn-303e` → `3088`），每次动作前重新 `inspect`。

### 12.4 本批 Phase A 没做到的（诚实边界）

- **没有复现用户报的"一直显示正在连接中"**：A3 那条字面场景走通了。⇒ 症状要么走的是**不经我们代码**的那条（快速面板/系统 VPN 页），要么需要一个"账本与隧道分叉"的前置态（生效=B 而隧道=A）才触发；后者在 1.0.86 A 案之后不再是正常态，本轮**没法在 UI 上安全地造出来**（要造得先写 `debug-cmd`，那是碰隧道的动作，未获指定不动）。
- **悬挂臂仍未跑**（`_abandonedOp` / 45 s 超时那条路径 0 样本）——本轮是正常路径基线，不含故意造悬挂。
- Phase A 只动了 A 案的**既有**切换路径（1.0.87 里就有的 `profile-switch`），**§十/§十一 新增的任何一行代码都还没被执行过** ⇒ 下一节 Phase B 才是验收。

---

## 十三 · Phase B 装机验收：UIA 在 **1.0.88** 上实拨（2026-10-22 00:08 → 00:29）

> 用户口径不变："uia 下测试" + "**两步都要：先复现再升级验**" + 拨号范围"**含「连着 A 点 B」切换臂**"。
> 通道仍是 `winapp ui`（inspect / search / invoke / scroll-into-view），全程**只点界面上有的东西**：
> 没有写 `debug-cmd.txt`、没碰隧道 REST、没有自制部署脚本（临时只用过一次 `user32!ShowWindow` 恢复被最小化的窗口，用完即删）。
> **装机自证**：`app.log` 第 88 行 `2026-10-22 00:08:57 pid=21176 app 启动 version=8B8CC4F4.ClashUWP/1.0.88.0`；
> 每条 `bgtask 插件进程启动` 也自报 `1.0.88.0` ⇒ §十/§十一 写的那些行**这次是真的在这台机器上跑过**。
> 包经 `uwp-bridge -target Publish` 出、用**同一张已受信旧证书**（`FB3309A6…`）重签、PS 5.1 `Add-AppxPackage` 覆盖装 ⇒
> 包家族 `8B8CC4F4.ClashUWP_4tjvwvefxrz66` 未变，两份订阅 yaml 与 `LocalState` 原样保留（没有走 `Remove`，见 [[project-deployment-queue-wedge]]）。

### 13.1 逐臂对照（同一动作，1.0.87 基线 → 1.0.88 实测）

| 臂 | 动作 | 1.0.87（§12.1） | **1.0.88 实测（日志原文）** | 判定 |
|---|---|---|---|---|
| **B1** | 断开态点「连接 VPN」（生效=iKuuu_V2） | 只有一行、且**事后**才写 ⇒ 悬挂时 3.5 s 盲区 | `00:13:41.508 profile-connect [iKuuu_V2] 发起前状态=Disconnected 发起=即将发出连接请求（下一行才是平台返回值…）`<br>`00:13:43.592 … 连接请求=Ok（平台已受理，继续轮询真实状态）`<br>`00:13:44.637 … 连接请求=Ok, 实际状态=Connected` | ✅ **F1 的"拆两行"装机成立**：发起行在 await **之前**落盘，那 3.5 s 盲区从"日志干净"变成"看得见一条发起行" |
| **B2** | 已 `Connected` 时再点同一条 | 真发一次 RPC，平台答 `AlreadyConnected`，**插值进一行文本**（D2） | `00:14:42.377 发起前状态=Connected 发起=即将发出连接请求`<br>`00:14:42.377 … 未发起=平台报告这条已经 Connected（会话已在，不再拨第二次）`<br>`00:14:42.377 session 本次未发起拨号，但点击前已有哨兵在跑 ⇒ 不解除，保持与点击前相同的保护` | ✅ **D2 关闭**（同毫秒两行 ⇒ **RPC 没发**）；★ 哨兵那条分支（§十"点按钮不能把既有保护摘掉"）**第一次拿到装机一手** |
| **B3** | **连着 iKuuu_V2 点「星链机场」**（切换臂） | 走通（5.8 s），但收尾那句丢弃了断开返回值；**收尾→重拨窗口里 `ui-poll` 炸出 `0x80070006`** | `00:15:57.639 teardown [iKuuu_V2] 发起=已发出断开请求（等平台返回值…）`<br>`00:16:00.460 profile-switch [星链机场] 发起前状态=Disconnected 发起=即将发出连接请求`<br>`00:16:02.733 … 新连接请求=Ok`<br>`00:16:03.829 … 新连接请求=Ok, 实际状态=Connected`<br>`00:16:03.829 session 订阅切换结果 ok=True：[iKuuu_V2]→[星链机场] 收尾=干净（状态=Connected → 发起平台断开 **断开请求=Ok** 等到收尾=Disconnected>Disconnected（2 s））…` | ✅ **F4 的三要素齐全**（断开返回值进了汇总行）；✅ §11.6 第 5 行的期望顺序实测一致；✅ **同一动作形态下 `ui-poll … 0x80070006` 0 次**（Phase A 同动作 1 次）——n=1，只算"没有再出现"，不算"已证明根除" |
| **B4** | 点「断开 VPN」 | `profile-disconnect 断开=Ok`（一句话把"受理"当"断开"） | `00:19:53.287 profile-disconnect 断开=Ok（请求已受理；会话收干净与否由收尾闸判定，这一句不代表已断开）` | ✅ F4 的口径进到了断开侧正常路径；t+3 s `Disconnected` |
| **B5** | 两次点击相隔 **2.7 s**（第二次落在第一次的轮询窗口内） | — | 第二次读到 `Connected` ⇒ `未发起=`，无 RPC | ✅ 前置条件分支生效 |
| **B6**（Phase A 没有的并发臂） | **两条 `invoke` 并发**（相隔 **255 ms**：`00:19:00.337` / `00:19:00.592`） | — | 第一次的 await 在飞（`00.538` 发起 → `02.016` 返回）；第二次那一路的"发起前状态"读**排在闸后面 1843 ms**，拿到锁读到 `Connected` ⇒ `00:19:02.435 未发起=平台报告这条已经 Connected` + 哨兵不解除 | ✅ **本仓第一条"闸在真实并发下工作"的一手数**：既没走到「本轮放弃」（3 s 上限没撞），也没有「强行执行」，更没有第二次拨号 |

### 13.2 ★★ 这批最有用的副产品：`ConnectProfileAsync` 与插件 `Connect` 回调的**一手耗时**

§五 第 7 行欠的那笔账（"45 s 是先遣项目的经验线，本机真值未知"）现在能还一半了 —— 四个正常拨号全部由日志时间戳算出：

| 拨号 | `ProfileStore.ConnectProfileAsync` await 实测 | 插件 `Connect` 回调内累计阻塞（`connect-window`） | 备注 |
|---|---|---|---|
| B1（冷：全新内核） | **2084 ms**（`41.508`→`43.592`；闸自报 2083 ms） | **3985 ms** | 含桥启动 398 ms |
| B3（切换后首拨，复用常驻内核） | **2273 ms**（`00.460`→`02.733`；闸自报 2273 ms） | **2241 ms** | |
| B5 | **1553 ms**（`53.536`→`55.089`） | **1567 ms** | 未越 2000 ms 阈值 ⇒ 闸不吭声 |
| B6 | **1478 ms**（`00.538`→`02.016`） | **1440 ms** | 同一轮里后来者排队 **1843 ms** |

三条结论，按"能定案"的程度排：

1. **45 s 那个写预算有 20–30 倍余量**（本机真值 1.5–2.3 s），而**读预算 3 s 在并发下只剩 1.1 s 余量**（B6：1843 ms）。⇒ 连点三次是**唯一**可能撞出「本轮放弃」的现实形态，本轮两次点击没撞。这个数不改常量，只把 §五 第 7 行那笔账从"未知"改成"已知量级"。
2. **平台可以在插件回调还没跑完时就把管理侧 RPC 返回成 `Ok`**：B1 的 `连接请求=Ok` 落在 `43.592`，而 `Connect 回调内累计阻塞=3985ms` 那行到 `45.791` 才出。⇒ 这条是 §12.4 那句"要造分叉态才能触发症状"的**机制支撑**：`Ok` ≠ 插件已就绪，所以**"返回值 + 轮询真实状态"两段式不能省**（`:343-348` 那段旧口径由一手时序背书）。
3. §11.6 第 4 行的担忧（回调占平台同步等太久 >10 s）**没有出现**：最长 3985 ms，与 §3.2 的 1–2 s 本机对照同量级。

### 13.3 判据行全量清点（`2026-10-22 00:` 时段，`app.log` + `plugin.log`）

```
0x80070006 = 0        本轮放弃 = 0        至今未返回 = 0        gate-abandoned = 0
强行执行   = 0        未在 45 s  = 0      未在 3 s   = 0        状态不可读   = 0
connect-window = 4    返回了但很慢 = 2    未发起= = 3           profile-store = 2
```

⇒ **"阻塞系统"那三条出口一次都没被需要**（悬挂没发生，所以拒绝/超时/迟返回全 0）；
但它们不是空账：**`未发起=` 3 次全部由新代码的前置条件与哨兵规则产生**（B2/B5/B6），`profile-store` 2 次全部是「很慢」这一形态。

### 13.4 收工判据（与 §12.1 A4 同四项，全绿）

默认路由**只剩 WLAN**（`Get-NetRoute 0.0.0.0/0` 排除 ifIndex 22 后为空）；**NRPT 规则数 = 0**；
非物理且 Up 的适配器只有 `WLAN` 与 `vEthernet (Default Switch)`（**没有本家族残留的隧道接口**）；
**62 s 内 `平台发起连接` 计数 20 → 20**（无自动重拨），期间 `00:20` 之后两个日志**零新增判据行**。

### 13.5 Phase B 仍没做到的（诚实边界，逐条给"为什么"）

- **悬挂臂 0 样本**：`_abandonedOp` / 45 s 超时 / `那条未返回的…最终返回（共 Nms）` 三条全没走过。
  造它要先上黑洞再关 Wi-Fi 无线电（`[[feedback-network-simulation-safe]]` 那条），属"碰隧道"的动作，**本轮未获指定不动**。
  ⇒ 所以 **D8 的"有界"半边只有代码 + 预算数，没有装机一手**；§11.6 第 2 行（拿到 N 之后回来改常量）那笔账**仍欠**。
- **F3 的按钮分支 0 样本**：「连接 VPN」只拨**生效**那条，"已有活动会话且目标不是它"需要账本与隧道分叉的前置态（同 §12.4）。
  本轮采到的是**订阅切换路径**（`SwitchToSubscriptionAsync:970`），它与按钮路径**共用 `FindActiveSessionAsync`（:544）这把尺子**（代码层现读已核）。
- **`ExplainDialError` 的 19 项只实地走过 `Ok`**；`AlreadyConnected` 现在由我们前置拦下、不再由平台答出，其余 17 项 0 样本。
- **用户报的"一直显示正在连接中"仍未复现**（与 Phase A 同结论）：四条拨号 + 一次切换 + 两次并发点击全部在 1–4 s 内落到终态。
  快速面板/托盘那条**不经我们代码**的路径依然没有观测臂。
- ★ **本批引入的一处措辞债**：`VpnProfileSetup.cs:420` 的发起行写在 `switch`（前置拒绝）**之前**，
  所以 B2/B5/B6 各出现一次相邻两行「发起=即将发出连接请求」紧跟「未发起=…」，读起来自相矛盾。
  修法是把 `:420` 挪到 `switch` 之后 —— 它仍在**唯一那个无界 await**（`:438`）之前，F1 的性质不丢。
  **本轮没动**：它不影响任何机器判据（`IsNoDialResult` 靠 `未发起=` 前缀，照常成立），而动它要再 bump 版本重装一轮。
  ★ **【01:15 第三批已清偿】**：随 `1.0.89.0` bump 版本装机改掉，行号现在是 `:442`（await 在 `:447`）；
  判据臂 C2 的一手对照（矛盾行 3 → 0）在 **§14.2**。
- 编译侧：Debug x64 0 警 0 错（§10.2），Release 侧随 `-target Publish` 打包一并编译并**装机运行成功**（版本行自证）；本篇 §十/§十一 全部代码路径至此**只在正常拨号形态上验过**。


---

## 十四 · 第三批（"修bug"）：§11.7 那条重连禁令落地 + §13.5 措辞债清偿 + 顺带挖出的四处同族漂移（装机 1.0.89 → 1.0.90）

> 触发：用户对 §13.5 与 §11.7 那两条"本轮没动"的账下口令 **"修bug"**。
> 本批时间线：`1.0.89.0` 打包装机 **00:43** → C1–C4 四臂 **00:43–00:56** → 装机诊断里再撞出两条假判据 →
> 顺带把同一族"写死包身份"的漂移在 CLI 与取证脚本里清了一遍 → `1.0.90.0` 打包装机 **01:07** → 诊断臂复验 **01:09**。
> 口径与前面几批一致：每条判定给可复核出处（文件:行号 / 日志原文 / UIA 取回的控件文本原文）。

### 14.1 本批动了什么（六处，逐处给依据）

| # | 位置（现读行号） | 动的东西 | 依据 |
|---|---|---|---|
| 1 | `ClashVpnPlugin.cs:402`（声明）·`:957`（置真）·`:1943`（置假）·`:1114-1159`（`AbortConnectionAttempt` 改返回 `bool` + 重连分支）·`:1169`（新增 `AbortOrThrow`）·调用点 `:468/:514/:622` | **§11.7 的重连禁令落地**：判"这次是不是重连"用"上次 Connect 走完且平台未回调 Disconnect"自行推断，命中就**既不 `TerminateConnection` 也不 `SetErrorMessage`**，改为抛异常出口 | 条文原文 `VpnChannel.TerminateConnection`："**Should not be called on reconnect cases.**"；官方 sample 现读 `VpnPlugin.cs:572`（`IsReconnect = true` 就在 `channel.Start/StartWithTrafficFilter` 之后那一句）、`:595-597`（`if (IsReconnect == false) { channel.SetErrorMessage(...) }`）、`:599`（紧跟 `throw;`）、`:640`（**只有** `Disconnect` 里置假）、sample 全文 0 次 `TerminateConnection` |
| 2 | `VpnProfileSetup.cs:442`（从 `switch` 之前挪到之后；唯一无界 await 仍在 `:447`） | **§13.5 那条措辞债清偿**：不再在"根本没拨"的分支上谎报"即将发出连接请求" | 1.0.88 一手：`plugin.log:966/967`、`:1066/1067`、`:1113/1114` 三对相邻自相矛盾行；本批对照见 §14.2 C2 |
| 3 | `AppServiceChannel.cs:47` | **F5**：`TargetPackageName` 从 `421769c5-17f4-4287-b6c1-193222d21e14` 改成 `8B8CC4F4.ClashUWP` | `git log -S 421769c5 -- ClashUWP/Package.appxmanifest` → `3b11f69`（1.0.74→1.0.75）把主清单 Identity Name 从 GUID 改名，同时换了发布者 hash；CLI 的发现逻辑没跟着改 ⇒ **从 1.0.75 起不带 `--pfn` 的命令一律"找不到目标包"（退出码 4）** |
| 4 | `Program.cs:14`（三条→四条）·`CommandCatalog.cs:244`（**由 `ForcedCommands` 生成那一行**，不再手抄） | **F5 的 help 侧**：`--help` 教"三条"而闸按四条拦（1.0.86 起含 `subuse`），照说明敲会拿到退出码 7 又在 help 里查不到归类 | 本仓为"两处各写一遍"已认过账（1.0.84 对 `KernelView.Find` 的订正）。改成生成式后，help 行与 `--commands --json` 的 `needsForce=true` **结构上不可能再分叉**（一手对照见 §14.3） |
| 5 | `MihomoController.cs:1577`（走 `DebugCommandLoop.DefaultKernelConfigPath()`，该方法 `:220` 由 `private` 改 `internal`）·`:1599-1616`（`external-controller` 改为按行解析取值） | **诊断函数里两条假判据**：① 包目录名写死成改名前的家族 ⇒ `DiagnoseKernelAsync` 从 1.0.75 起**每次**报"✗ 内核配置文件不存在"；② 路径修好后仍报"✗ external-controller 配置缺失或错误"，因为真行**带单引号** | ① 现读 `git log -S` 同第 3 行；② 装机原文（`config.kernel.yaml:6`）= `external-controller: '127.0.0.1:9090'`，旧判据是 `config.Contains("external-controller: 127.0.0.1:9090")` 整串字面量。★ 这一行**不由我们生成**：全仓 `grep -rn external-controller --include=*.cs` 除本函数外无一处写它，值是订阅 yaml 原样带进来的 ⇒ 引号/空格/端口都由机场决定，字面量匹配在这种输入上必然漂 |
| 6 | `Tools/TestScripts/collect-evidence.ps1:44`、`group-member-census.ps1:3`、`parse-check.ps1:1` | 同一族"写死包身份"的漂移：三个取证脚本还在找 `421769c5-…` 那个不存在的包目录 ⇒ 取证读空、census/parse **抛 FileNotFound**。改成"按 `*ClashUWP*` 且非 `*.Cli` 反查已装包"（后两个文件顺带补了 BOM，按本仓 `.ps1` 惯例给 5.1 读） | 见 §14.3 末段的实跑输出 |

★ **现读之后判定"不是漏改"的一处**：`Tools/TestScripts/restore-network.ps1:66` 也带着那个旧 GUID，但它写的是**交替**模式 `ClashUWP|421769c5-…`，而新包名 `8B8CC4F4.ClashUWP` 本身含 `ClashUWP`，且 Docs/24 §3.1 记的判据还有独立一条 OR（`NameServers` 含 `223.5.5.5`/`119.29.29.29`）⇒ **不会漏判残留**，旧 GUID 只是条死分支。逃生口脚本不在无凭据的情况下动它。

### 14.2 装机四臂（1.0.89，UIA 驱动，pid=12364）

`app.log` 145 行、`plugin.log` 1135 行为基线；两条日志都要跟着读（同 pid、按调用方路径分流）。

**C1 · 断开态点「连接 VPN」** —— 成功建连，`发起=` 与返回值各一行、顺序对：

```
00:43:57.780 app    I session   用户发起订阅连接 subId=starlink.…（已先武装健康哨兵）
00:43:57.948 app    I profile-connect [星链机场] 发起前状态=Disconnected 发起=即将发出连接请求（…）
00:44:00.089 plugin W profile-store ConnectProfileAsync 返回了但很慢：2139ms（阈值 2000ms）
00:44:00.089 plugin I profile-connect [星链机场] 发起前状态=Disconnected 连接请求=Ok（平台已受理，继续轮询真实状态）
00:44:01.201 plugin I profile-connect [星链机场] 发起前状态=Disconnected 连接请求=Ok, 实际状态=Connected
00:44:02.006 plugin I connect-window Connect 回调内累计阻塞=3834ms
```

**C2 · 连着同一条再点一次「连接 VPN」（`:442` 那条措辞债的判据臂）** —— 只有一行拒绝，**前面不再出现"即将发出连接请求"**：

```
00:47:39.290 plugin I session 用户发起订阅连接 subId=starlink.…（已先武装健康哨兵）
00:47:39.342 plugin I health-precheck 第 1 次读状态=Connected（成功 1 次，异常 0 次）
00:47:39.436 plugin I profile-connect [星链机场] 发起前状态=Connected 未发起=平台报告这条已经 Connected（会话已在，不再拨第二次）
00:47:39.436 plugin I session   本次未发起拨号，但点击前已有哨兵在跑 ⇒ 不解除，保持与点击前相同的保护
```

同一时间窗全量清点（`grep -nE "发起=即将发出连接请求|未发起=" 两份日志`）：1.0.88（pid=21176）那三对自相矛盾行是 `966/967`、`1066/1067`、`1113/1114`；1.0.89（pid=12364）只有 `plugin.log:1182` **单独一行**，`00:47:3x` 时段 `app.log` **0 条**新增。⇒ **矛盾行 3 → 0**，且 `IsNoDialResult` 依赖的 `未发起=` 前缀原样保留（没改机器判据）。

**C3 · 连着 A 点 B（订阅切换臂，`星链机场 → iKuuu_V2`）** —— 收尾干净、拨号成功，**且平台回调 `Disconnect` 落在下一次 `Connect` 之前**（这一条同时是第 14.1 行 1 那个重连标志**没有误判**的一手证据）：

```
00:50:53.856 plugin I session   用户发起订阅切换（隧道换 profile）targetSubId=hkom1.no-mad-sub.…
00:50:53.936 plugin I teardown  [星链机场] 发起=已发出断开请求（等平台返回值；官方对这一次 await 没有时限承诺）
00:50:54.129 plugin I physical-watchdog-plugin 停止（平台回调 Disconnect）
00:50:54.153 plugin I session   会话结束，存活 414s encap=26913 inj=27352
00:50:56.640 plugin I profile-switch [iKuuu_V2] 发起前状态=Disconnected 发起=即将发出连接请求（…）
00:50:56.848 plugin I connect   平台发起连接 #2 resident=复用常驻内核
00:50:58.610 plugin I profile-switch [iKuuu_V2] 发起前状态=Disconnected 新连接请求=Ok（平台已受理，继续轮询真实状态）
00:50:58.730 plugin I connect-window Connect 回调内累计阻塞=1882ms
00:50:59.669 plugin I profile-switch [iKuuu_V2] … 新连接请求=Ok, 实际状态=Connected
00:50:59.669 plugin I session   订阅切换结果 ok=True：[星链机场]→[iKuuu_V2] 收尾=干净（… 等到收尾=Disconnected>Disconnected（2 s））
```

**C3b · 反切回（`iKuuu_V2 → 星链机场`）** —— 同形状：`teardown 发起=已发出断开请求` → `会话结束 存活 40s`（`00:51:38.657`）→ `平台发起连接 #3`（`00:51:41.344`）→ `Ok, 实际状态=Connected`（`00:51:43.905`），`Connect` 阻塞 1628 ms，`收尾=干净`。
⇒ 两条方向都过；`会话结束` 恒在下一次 `平台发起连接` **之前** ⇒ `s_lastConnectFinishedWithoutDisconnect` 在切换路上被 `:1943` 正确清零，**重连门控没把正常切换误判成重连**（本批唯一能拿到的 no-regression 凭据）。

**C4 · 用户发起断开** —— F4 那一句返回值不再被丢弃：

```
00:56:37.074 plugin I session   用户发起断开（已解除健康哨兵）
00:56:37.291 plugin I physical-watchdog-plugin 停止（平台回调 Disconnect）
00:56:37.311 plugin I session   会话结束，存活 295s encap=12743 inj=14965
00:56:37.599 plugin I profile-disconnect 断开=Ok（请求已受理；会话收干净与否由收尾闸判定，这一句不代表已断开）
00:56:38.472 plugin I profile-sub-adopt 系统侧已断开（此前 starlink.…），生效订阅保持不变
```

### 14.3 F5 / 诊断 / 取证脚本的装机凭据

**CLI（`ClashUWP.Cli` 1.0.1.0 → 1.0.3.0 → 1.0.4.0，同家族 `ClashUWP.Cli_y0tg80em3rma2`）**

| 命令 | 修之前 | 装机实测（1.0.4） |
|---|---|---|
| `--list` | 退出码 4"找不到目标包" | `8B8CC4F4.ClashUWP_4tjvwvefxrz66` + `已装版本 v1.0.89.0 · AppService=ClashUWP.Debug` |
| `status` | 同上，发不出去 | 真打到主应用并回话：`mode=rule \| log-level=info \| interface-name="WLAN"` |
| `doctor` | 阶段1 即失败 | 阶段1 PASS（包发现）·阶段2 PASS·阶段3 PASS（**AppService 通道连通，主应用已应答**）·阶段4 WARN=空闲态内核未起（自解释）·阶段5 PASS 列出 3 条 profile **全 Disconnected**·阶段6 SKIP（未给 `--target`） |
| `--selftest` | — | 4/4 PASS（AOT 投影没因改动退化） |
| `--help` 的 `--force` 行 | `（connect/recreate/probe）` | `（connect/recreate/probe/subuse）`，且与 `--commands --json` 里 `needsForce:true` 的四条**逐字一致** |

★ 主应用侧也留了同一条通道的一手痕迹（UIA 从「诊断内核」输出的插件日志尾巴里取回）：`… name=ClashUWP.Debug caller=ClashUWP.Cli_y0tg80em3rma2 pid=12364` 紧跟 `cmd status`。
★ 纪律：本批 CLI 侧只发**只读**命令（`--selftest/--list/--help/--commands/doctor/status`），四条 tunnel 级一条没发；`subuse` 那道本地闸不靠抽样验，靠"help 与闸读同一张表"这个结构（第 14.1 行 4）。

**诊断（`DiagnoseKernelAsync`，1.0.90 装机 · UIA 取回的控件文本原文）**

```
✓ 内核配置文件存在 (576 行) path=C:\Users\gold\AppData\Local\Packages\8B8CC4F4.ClashUWP_4tjvwvefxrz66\LocalState\config.kernel.yaml
  ✓ external-controller=127.0.0.1:9090（与前台 REST 基址一致）
```

对照三个阶段：**1.0.88 及以前** = `✗ 内核配置文件不存在`（那个目录根本不存在）→ **1.0.89**（路径修好、判据没修）= `✓ 存在 (576 行)` 但紧跟 `✗ external-controller 配置缺失或错误`，而同一次输出上面两行还写着 `✓ REST API 可达 {"meta":true,"version":"1.10.0"}`，自相矛盾 → **1.0.90** = 两行都 `✓`。
★ 第二条假判据是**第一条修完才露出来的**：不修路径，永远看不见判据本身也错。这正是"半截修复会被点破"的现场样本。

**取证脚本（三条都实跑过）**

- `collect-evidence.ps1 -SkipDnsProbe`：`Name=8B8CC4F4.ClashUWP Version=1.0.89.0 Family=8B8CC4F4.ClashUWP_4tjvwvefxrz66 LocalState=… exists=True`（改前这一节固定输出"未安装包：421769c5-…"）。
- `parse-check.ps1`：`nodes=33 / rules=514 / groups=3`（改前在 `ReadAllLines` 抛找不到文件）。
- `group-member-census.ps1`：`真节点 33 个 / 组 3 个`。

### 14.4 收工判据（与 §13.4 同四项）

`Get-NetRoute 0.0.0.0/0` 只剩 **WLAN（ifIndex 22 → 192.168.0.1）**；**NRPT 规则数 = 0**；
适配器表只有 `蓝牙网络连接 / WLAN / 本地连接 2（Wintun，Verge 的，Disconnected）/ vEthernet (Default Switch)`，**没有本家族残留隧道接口**；
`平台发起连接` 计数在 `00:57:21` = **23**，此后跑完 CLI 两次装/升级、主应用 1.0.90 打包装机并**重新启动**（pid=1680）、CLI `doctor` 经 AppService **激活过主应用**之后，`01:09` 复查仍是 **23** ⇒ **无自动重拨**（这一项比 §13.4 多扛了"重启 + 外部激活"两个诱因）。

### 14.5 本批仍未做到的（诚实边界）

- **重连门控的正向分支 0 样本**：`evt=skip-terminate-reconnect` 装机计数 **0**。原因是本机能采到的序列里平台**每次都先回调 `Disconnect`**（§14.2 C3/C3b 一手；1.0.88 那 4 组会话同样），所以门控恒判"全新建连"。它防的是"没回调 Disconnect 就再拨一次"（run 20 观察过 OS 换进程重拨、从不回调 `Disconnect`）——那条路在 1.0.77 单入口合并后虽同 pid，但仍**无法在不碰隧道的前提下构造** ⇒ **只有条文 + sample 依据，没有装机一手**（与 §13.5 悬挂臂同一档）。
- ★ **门控粒度的一处已知粗**：标志位是**进程级 static**，不按 profile 分。官方 sample 也是进程级（`VpnPlugin.cs:87` 明写 "For demo purposes, only support one instance"），但我们是多订阅产品 ⇒ "A 连着、切到 B"时若 A 的 `Disconnect` 还没来就进入 B 的 `Connect` 且中途失败，会被判成"重连"而少调一次 `TerminateConnection`。**本批没改成 per-profile**：改它要先把"平台到底会不会在两条 profile 间并发回调"量出来，而那是 §13.5 悬挂臂同一族的动作。
- ★ **`SetErrorMessage` 门控的落点比 sample 严**：sample 在 `channel.Start` 成功后立刻置真（`VpnPlugin.cs:572`），我们放在 `Connect` 的 try **末尾**（`:957`）。差异只影响"`Start` 之后、末尾之前抛"这一小段：那一段本来走 `_started==true` 那一支（`RequestPostStartTeardown`），**不碰 `TerminateConnection`**（`:1046` 的 `if (!_started)` 保持原样，仍按 sample 把原始异常 rethrow），所以只是 `SetErrorMessage` 会不会多发一次的区别。写下来免得以后当成两处不一致去"顺手统一"。
- **1.0.90 没有重拨**：两版之间的改动自证只有 `MihomoController.DiagnoseKernelAsync` 一处（不在拨号路径上），其余是 CLI/脚本/注释/版本号。凭据是**操作清单自证，不是二进制对照**。
- **`parse-check.ps1` 的 `truth groups=0` 与它自己数出的 `groups=3` 不一致**（`- name:` 匹配不到本仓 `config.yaml` 的写法）——同一族"字面量判据"，但它是 1.0.71 的一次性核对脚本，**本批没动**，记在这儿。
- 旧账未变：**F3 的按钮分支、D8 悬挂臂、`ExplainDialError` 的 17 个非 `Ok` 成员**仍 0 样本；用户报的"**一直显示正在连接中**"三轮下来（Phase A/B/C）**仍未复现**。

### 14.6 §14 之后自己踩出来的一条：把诊断出口挂进 `DebugCommandLoop` 弄断了离线夹具（1.0.91）

装机验收跑完就收工了，回头例行 `dotnet run --project Tools/YamlShapeTest` 才发现：

```
ClashUWP\Backend\Mihomo\MihomoController.cs(1577,34): error CS0103: 当前上下文中不存在名称"DebugCommandLoop"
```

- **根因**：§14.1 行 5 为了"诊断侧不许再自己拼包目录"，把 `DiagnoseKernelAsync` 改成引
  `DebugCommandLoop.DefaultKernelConfigPath()`。夹具是 `net10.0` 纯控制台、按 `<Compile Link>` 把产品源文件
  拉进自己的编译单元 + `Stubs.cs` 编译桩；`DebugCommandLoop` 背后是命令分发、`RollingLog`、VPN 会话一整条链，
  link 不进来 ⇒ 那一行引用当场悬空。**修法不是把链拖进夹具，而是把"路径算法"从一个"什么都依赖"的类里分出来。**
- **落点**：新增 `Backend/LocalStatePaths.cs`（只依赖 `Windows.Storage.ApplicationData`，夹具 link 得动），
  `DebugCommandLoop.DefaultKernelConfigPath()` 留成一行转发 ⇒ 命令侧调用点与注释口径不动；夹具 csproj link 新文件；
  `Stubs.cs` 的 `StorageFolder` 补 `Path => ""`（诊断只做字符串拼接，不走 IO）。算法与异常退回口径一字未改。
- ★ **教训值得单独记**：本批验的是"主工程 Debug 编译 0 警 0 错 + 装机判据"，而**夹具工程不在解决方案里**
  （`ClashUWP.slnx` 现读不含 `YamlShapeTest`），所以它坏了主工程毫无反应。凡是改了被夹具 link 的那几个文件
  （`KernelConfig/AppSplit/DnsConfig/LocalSettings/VpnPluginConfig/MihomoController/KernelView/SubscriptionView`，
  现在还有 `SubscriptionShape/SelectionStore/LocalStatePaths`），收工判据必须带上跑一次夹具。
- 1.0.91 凭据：夹具 `ALL OFFLINE TESTS PASSED`；`AppPackages\ClashUWP_1.0.91.0_x64_Test\…msix` 经
  `FB3309A6…` 重签、`Add-AppxPackage` 覆盖装（先撞 `0x80073D02`——运行中的 1.0.90 占资源，关掉 pid=1680 后装上）；
  `app.log` 起新行 `version=8B8CC4F4.ClashUWP/1.0.91.0 pid=18816`；「诊断内核」两条判据仍
  `✓ 内核配置文件存在 (576 行) path=…\8B8CC4F4.ClashUWP_4tjvwvefxrz66\LocalState\config.kernel.yaml` +
  `✓ external-controller=127.0.0.1:9090（与前台 REST 基址一致）`，同一次输出里 `✗ REST API 不可达 (127.0.0.1:9090)`
  是内核没跑（未连接，预期）。**1.0.91 没拨号**：改动不在拨号路径上，凭据同样是操作清单自证。
