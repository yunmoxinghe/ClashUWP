# 44 · 判据逐条对齐官方 sample（1.0.92）——Connecting 进活动会话这把尺子

**缘起**：Docs/43 的 clean-room 证明"连着 A 拨 B 卡在 Connecting"在官方原样代码里成立，而我们在系统侧调用上每一维都比官方强（无界 `t.Wait()`、没有活动会话闸、Connecting 期间三层无出口）。用户据此定方向：**"对齐官方"取"对齐判据、保留一键切换"**——把官方那几把尺子的写法照齐，不回收我们比官方强的三处增强。

> ★【1.0.93 续，见 `Docs/46`】本档那句方向已被下一批**部分推翻**：用户改口「把和官方示例不同的 hook 方法都删掉（前台进程那部分）」。
> 于是 **#2 那把尺子（`Connecting` 进活动会话）保留**，而靠它长出来的东西全删了 ——
> 按钮路径的 `SerializeBehindActiveSessionAsync`、切换路径的 `Connecting` 拒绝分支与 `EnsureTornDownAsync` 收尾调用、
> 以及 §一 #9「`Ok` 之后轮询到 `Connected`」两段式读数；**#4 那条明写的"对 `Connecting` 仍然试断开"故意偏离也一并收掉**（断开改成只在 `Connected` 上发 RPC，逐字对 `:532`）。
> 本档 §三 那张装机四臂表是 **1.0.92 的历史数据，不回填**；§四 那句"已出包已装机已跑臂"同样只指 1.0.92。
> ★ 另订正本档引用过的一处口径：官方 `DeleteExistingProfile:125-135` **不是裸删**，它先 `DisconnectProfileAsync` 再 `DeleteProfileAsync`，
> 只是**不等收尾** —— `Docs/38:93` 早记对了，差别在"等不等"，不在"断不断"。

## 〇、先订正我自己提问时写错的一句

给选项时我把 `SerializeBehindActiveSessionAsync` / `EnsureTornDownAsync` 描述成"A 案里那段**非官方**的串行拆除逻辑"，说要对齐就该拆。现读代码后**这句不成立**：它们正是官方 sample `:80`（"There should be zero or one of these at all times"）与 `:101-113` 那句建模的实现，拆掉等于回到"两条隧道并发"。**没拆。** 真正与官方不一致的是另一处更细的东西——**这把尺子只量 `Connected`，不量 `Connecting`**（下详）。

---

## 一、对照表（官方行号 ↔ 我们行号 ↔ 1.0.91 现状 ↔ 1.0.92 处置）

官方 = `D:\fluentapps\repos\UwpVpnPluginSample\CSharp\TestVpnPluginApp\MainPage.xaml.cs`（HEAD `d589fe0`，本轮实测未改逻辑）；我们 = `ClashUWP/Vpn/VpnProfileSetup.cs`，行号为 1.0.92 现读。

| # | 判据 | 官方 | 我们 | 1.0.91 现状 | 1.0.92 处置 |
| --- | --- | --- | --- | --- | --- |
| 1 | 可连清单只收 `Disconnected` | `:111` | — | 我们没有"清单"概念，只有逐条判 | 不变 |
| 2 | **活动会话 = `Connected` ∪ `Connecting`** | `GetFirstConnectedProfileAsync:92-93` | `FindActiveSessionAsync:576` | ❌ **只认 `Connected`** ⇒ A 在 `Connecting` 时按钮路径与切换路径都拦不住第二条请求 | ✅ **改成两态都算**（`:576-584`），新增具名判据 `IsActiveSessionStatus:592` |
| 3 | 拨号前置：目标本身必须 `Disconnected` | `ConnectOnClick:506` | `ConnectOnceAsync:422-433` | ✅ 已对齐，且更严（`Connected` 视为已在、不重发；`Connecting/Disconnecting` 回"稍后再试"） | 不变 |
| 4 | **断开只在 `Connected` 上发 RPC** | `DisconnectOnClick:532` | `DisconnectAsync:1226`，`Disconnected` 分支 `:1239-1244` | ❌ 对 `Disconnected` 也发，靠 `断开=NoConnection` 分支兜 | ✅ `Disconnected` 直接回话不发（省一次无界 await，也省掉占 `ProfileStore` 写预算的那一下）；**`Connecting` 仍然试** ⇒ 明写的故意偏离，见 §二 |
| 5 | `Connecting` 上要不要"收尾" | 官方无此问题（它不收尾） | `GracefulTeardown.EnsureTornDownAsync:181` | 「非 `Connected` ⇒ 无需断开、直接通过」 | **保持不改**，但把两个问题的分工写进注释：那边答"这条上有没有隧道要拆"（`Connecting` 尚无接口 ⇒ 对删除/重建确实无需拆），这边（#2）答"能不能再发起一条"（官方算会话）——同一个状态、两个问题，不能互相代 |
| 6 | `IsReconnect` 门控 `SetErrorMessage` | `VpnPlugin.cs:87/:572/:640/:595-599` | `ClashVpnPlugin.cs:402/:957/:1943` | ✅ 已对齐（Docs/38 §十四） | 不变 |
| 7 | `TerminateConnection` | 示例一次不调（失败只 `throw`） | 非重连时调 | ⚠️ 故意偏离 | **保留**：Docs/43 §三.3 实测官方形状连杀宿主进程都不解，不调就是我们也没有出口 |
| 8 | 插件 `Connect` 内 socket 等待 | `VpnPlugin.cs:317-318` 无界 `t.Wait()` | 3 s 上限 + `connect-window` | ⚠️ 故意偏离 | **保留**（本轮 clean-room 的直接病根） |
| 9 | 返回值 + 真实状态两段式 | 只看返回值，状态交给 500 ms timer | `ConnectOnceAsync:466-489`（`Ok` 后仍轮询到 `Connected` 才算成） | ✅ 等价且更强 | 不变 |
| 10 | 日志能否归因到 profile | 官方不带名字 ⇒ Docs/43 只能用计数法归因 | 每行 `[ProfileName]` | ✅ | 不变 |
| 11 | 状态轮询周期 | `timer` 500 ms（`:260`） | 前台 3 s + 拨号内 1 s×20 | 未对齐 | **本轮不动，记为债**：改周期动的是整条 3 秒轮询（adopt、DivergenceLine、UIA 判据都挂它上），风险与本主题无关 |

净结果：**两处改（#2、#4），一处明确不改并写清理由（#5），三处故意比官方强并逐条挂账（#7、#8、#9）。**

---

## 二、`Connecting` 入闸之后，行为具体变了什么

`FindActiveSessionAsync` 是**全仓唯一**回答"当前会话在哪"的函数，调用点 5 个：`SerializeBehindActiveSessionAsync:506`（按钮路径）、`SwitchToSubscriptionAsync:1024`（A 案切换）、`ProbeConnectAsync:1331` 与 `Backend/StartupProbes.cs:81`（两个诊断拨号点）。尺子一改，五处一起变，这正是当初把它收成一份的意义。

- **按钮路径**（`SerializeBehindActiveSessionAsync:500`，`Connecting` 分支 `:520-527`）：活动会话是 `Connecting` 且目标不是它 ⇒ **不发断开、拒绝发起**，回话 `旧会话 [X] 状态=Connecting（官方把它算活动会话：sample :92-93，且该态管理面无中止原语：Docs/43 实测一次请求 ≥5 分 56 秒未返回），新连接=跳过（不与未收尾会话并发）`。措辞沿用仓里既有的 `新连接=跳过` 前缀，不去碰 `未发起=` ——那是 `VpnSession.IsNoDialResult` 认哨兵的机器契约。
- **切换路径**（`SwitchToSubscriptionAsync:1003`，`Connecting` 分支 `:1038-1045`）：同一条判据，`profile-switch` 记 `拒绝切换（旧会话正在 Connecting，保持现状不发起新连接）`。**这条必须有**：`Connecting` 的 current 进 `EnsureTornDownAsync` 会读到"非 Connected ⇒ 无需断开"直接放行，等于按钮挡住了、切换漏着走 —— 就是 Docs/43 在官方上量到的那个形态。
- **两个诊断拨号点**：本来就在"有活动会话则跳过"，现在 `Connecting` 也算 ⇒ 探测不再可能和一条在途请求并行（Docs/38 §十那条"探测也是拨号"的口径至此完整）。
- **`EnsureTornDownAsync` 未改**，所以删除订阅 profile、重建 profile、启动对账清理这三条路**行为不变**（`Connecting` 上确实没有接口要拆）。
- **`DisconnectAsync`（#4）**：`Disconnected` 不再发 RPC，回 `[X] 不发=平台报告这条没有活动会话（状态=Disconnected），按已断开处理`；刻意用 `不发=` 而不是 `未发起=`，避免撞上那条机器契约。**`Connecting` 仍然发**，两个理由都写进代码注释：① `HealthMonitor.cs:309` 的"卡在非终态 ⇒ 把网络交回直连"这条兜底**依赖它**（`OurStatusAsync` 在无 `Connected` 时兜底回基础 profile，正好够得到卡在 `Connecting` 的那条）；② Docs/43 证明官方在这个态**一层出口都没有**，我们至少留一次尝试，且 F4 那套按枚举分支的读数会告诉我们平台到底答了什么。装机若证明这一支会留下半起不落的状态，再收掉并在本文订正。

---

## 三、装机四臂 —— 实测结果（2026-10-22 06:39–07:00，1.0.92.0 装机，pid 14164/14556/5796）

| 臂 | 动作 | 判据（写死在成文之前） | 实测 |
| --- | --- | --- | --- |
| A · 新闸正向 | A 处 `Connecting` 时点另一条（按钮或切订阅） | `profile-connect`/`profile-switch` 出现 `状态=Connecting … 新连接=跳过`，且**整轮只有 1 条"发起=即将发出连接请求"** | ❌ **0 样本 · 未采到，原因见下方"臂 A 为什么打不进去"** |
| B · 不回归 | A `Connected` 时切 B | 三要素齐全（`断开请求=Ok`、`收尾=干净`、`新连接请求=Ok, 实际状态=Connected`），形同 Docs/38 §十三 A3/B3 | ✅ **采到 1 次**（06:43:55→06:44:02，`[星链机场]`→`[iKuuu_V2]`），三要素逐字齐全：`发起=已发出断开请求 … 断开请求=Ok … 收尾=干净 … 新连接请求=Ok, 实际状态=Connected` + UI 标签"已切到「iKuuu_V2」"。★ **带一条折扣**：那句"收尾=干净"是 `WaitSessionDownAsync` **两次读状态抛 `COMException` 之后**给的结论（形状与结论都在 `Docs/45` §三）；这次它是对的（平台自己同一秒打了 `会话结束，存活 5s`，新会话也起来了，两条独立旁证），但判据形状本身有毒 |
| C · 断开侧 | 没有任何会话时点「断开 VPN」 | 只有 `不发=…（状态=Disconnected）`，`profile-disconnect` 内**不再出现** `断开=NoConnection` | ✅ **采到**（06:39:31.813 pid 14164）：`profile-disconnect [ClashUWP] 不发=平台报告这条没有活动会话（状态=Disconnected），按已断开处理`，全量日志这一轮无 `断开=NoConnection`。★ 同一臂在中毒之后（06:47:36 / 06:48:41 / 06:51:37 / 06:54:08 四次按）只剩"用户发起断开"一行、`不发=` 再也不出 —— **那不是 #4 的回归，是 `Docs/45` 那条读数中毒** |
| D · 兜底不回归 | 制造一次卡在非终态（沿用既有安全手段，不碰隧道 debug-cmd） | `HealthMonitor` 的 `非终态（Connecting）→ 只断开把网络交回直连：断开=…` 仍出得来 | ❌ **未采 · 被 `Docs/45` 挡住**：这条臂要验证的正是"卡住时那条断开请求发得出去"，而本轮断开路径已经哑了。读数恢复之前跑它没有意义 |

**臂 A 为什么打不进去（下次要照着改方法，不是照着改判据）**：正常订阅的 `Connecting` 窗口实测只有 **≈3 秒**（06:43:49.366 发起 → 06:43:52.387 `实际状态=Connected`，插件侧 `Connect 回调内累计阻塞=3710ms` 同量级），而一次 `winapp ui invoke` 光是起进程 + 解 UIA 树就要 **≈1.3–2 秒**，"连接 VPN（主页）→ 订阅（导航）→ 点另一行"这三跳实测落了 **5.7 秒**（06:43:49.366 → 06:43:55.041），永远掉在 `Connected` 之后 —— 于是打到的是臂 B。**可行的下一版打法**：同一页（主页）连点两次「连接 VPN」，只用两个选择器、零导航，第二击才有可能进那 3 秒窗口；臂 A 的两个拒绝点里，这一击走的正是 `SerializeBehindActiveSessionAsync:520-527` 那支。★ 本轮也**没有**改用 CLI 走捷径：`clashuwp-cli.exe --commands --json` 里 `subuse` 标的是 `"danger":"tunnel"` + `needsForce`，按收工纪律一条不写（"碰隧道的入口未经明确指定不动"）。

净结果：**四臂采到两臂（B、C），A/D 各 0 样本且原因明写**；#2 这把新尺子的"漏口堵住"这一半目前只有代码级证据（`Connecting` 不再落到收尾闸），装机正向分支待补 —— 这条与 `Docs/38` 重连门控"正向分支 0 样本"同性质，记账方式也一样。

---

## 四、风险与代价（明说）

- **正常拨号的 1.5–2.3 秒 `Connecting` 窗口内点第二条，现在会被拒**（Docs/38 §十三量到的正常耗时）。这是官方建模本身的代价，也是本轮要的：Docs/43 里那条 B 就是从"两次点击间隔不到 10 秒"进来的。回话是"稍后再试"性质的完整句子，用户看得见。
- **真悬挂时，切换会一路被拒直到平台自己落地。** 官方落地时间本轮实测是黑洞服务器上 20–30 秒（3 个样本），而悬挂那次 ≥5 分 56 秒没落地 ⇒ 极端情形下用户会看到"切不动"。这是把一个静默的坏形状换成一个有声的坏形状，**不是修好了悬挂本身**。悬挂的根在数据面（插件连不上服务器），管理面本来就没有中止原语。若装机发现"永久 `Connecting` 导致订阅完全不可切"真的成立，需要补的是**一条用户可见的出口**（记在本节，不在本轮范围）。
- #4 对 `Disconnected` 省掉一次 RPC，会让历史日志里 `断开=NoConnection` 这条读数变少；任何按它建的判据要改看 `不发=`（`Docs/30` 那两行是别的调用方，不受影响）。
- 版本 `1.0.91.0 → 1.0.92.0`，MSBuild（VS 18 自带）Debug/x64 编译 **0 警 0 错**；`VpnProfileSetup.cs` 不在 `Tools/YamlShapeTest` 的 `<Compile Link>` 清单里（那条收工判据管的是 `KernelConfig/AppSplit/DnsConfig/LocalSettings/VpnPluginConfig/MihomoController/KernelView/SubscriptionView/SubscriptionShape/SelectionStore/LocalStatePaths`），本轮未跑夹具。**已出包（VS 桥 `-target Publish` + 受信证书重签）、已装机（`Status=Ok`）、已跑臂**，结果见 §三。

---

## 五、跑这四臂的时候撞出一条更重的（不在本轮范围，单独建档）

臂 B 那次切换本身成功，但**之后本包所有进程的状态读数永久抛 `COMException 句柄无效 (0x80070006)`**（★ 这句形状在 2026-10-23 第二轮取证里被订正为**按条目**中毒，见 `Docs/45 §八`），代价是「断开 VPN」按下去只留一行、请求根本发不出去（隧道 386 s 后是我在系统设置里断的）。时间线、已排除的四个候选、`收尾=干净` 那句的判据折扣、以及五条修法（逐项 catch / 断开不许无声 / 不可读时给出"系统设置→VPN"出口 / 属性读也纳入 `ProfileStore` 串行闸 / `ui-poll` 去重掩盖了起始时刻）全在 **`Docs/45`**。臂 A、臂 D 本轮 0 样本正是被它挡住的。

一句话分工：本轮改的是**尺子**（`Connecting` 进不进活动会话），`Docs/45` 那条坏的是**能不能读到尺子**——两者不要混着记账。
