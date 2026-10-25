# 63 · 1.0.122：状态通知的点击改走协议激活回前台 / 订阅自动更新对齐 Clash Verge（连着 VPN 也照样更新）

> 版本：`Package.appxmanifest` `1.0.121.0 → 1.0.122.0`。
> 触发：用户口径「点击通知时唤起的不是前台ui，联网搜索最佳修复方案，另外，自动更新订阅支持一下连接状态更新」，
>   第二问（"支持连接状态更新"要哪种语义）得到「跟随clash verge行为」。
> ★ **证据状态（写这份文档时的实情）**：
>   - §一 的取证链**全部来自装机实物**（本机 1.0.121 的 `wpndatabase.db` + `logs/app.log`），不靠推断。
>   - §三 的**前提已在本机单独验过**（2026-10-25 20:03/20:09 两次 `clashuwp://open` 激活）——
>     那一遍装的是**旧版 1.0.121**，验的是"协议激活这条路本身带回不回前台 UI"，与我的改动无关。
>   - §三 的**修法本身（1.0.122）未装机**：通知真的点了能回前台、按钮真的能断开，
>     都还在 §六 那张表上挂着"未验"。
>   - §四 的口径来自**现读 Clash Verge 源码**（文件:行在下面逐条写着），
>     离线夹具把新判据钉死了（`Tools/YamlShapeTest` 全绿）；**连着隧道到点真的更新了一次**未验。

---

## 一、症状与取证链：点击状态通知，唤起的不是前台 UI

### 1.1 装机实物：那条通知的 XML 长什么样

从 `%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db` 里读出的**本机实际存着的那条**
（`tr -d '\000'` 去掉 UTF-16 的 NUL 后 grep；这是 1.0.81 设计、一直装到 1.0.121 的形态）：

```xml
<toast launch="action=viewMainWindow">
  <visual><binding template="ToastGeneric">
    <text>{statusTitle}</text><text>{statusRate}</text><text>{statusTotal}</text>
  </binding></visual>
  <actions><action content="断开连接" activationType="foreground" arg…
```

两条事实，都是**读出来的**不是推测的：

1. **根元素只有 `launch`，没有 `activationType`** ⇒ 官方那张表写得很死：按钮的
   `ActivationType` **默认为前台**，正文同理。所以点正文 = **前台激活"引发这条通知的那个 AUMID"**。
2. **那枚按钮明写了 `activationType="foreground"`**，`arguments` 是自定义串
   （`action=…` / `disconnect`），要应用自己在激活参数里认。

而引发这条通知的进程是**插件宿主**（`StatusToast.Start` 只在 VPN 插件侧跑）。
从 1.0.106 起宿主挂在**第二个** `<Application Id="VpnHost">` 上（`AppListEntry="none"`，
`D:\fluentapps\repos\ClashUWP\ClashUWP\Package.appxmanifest:233`），
⇒ `CreateToastNotifier()` 拿到的是 `…!VpnHost` 这个 AUMID，
点击的默认前台激活去的就是**那个无头入口**。

同一个库里 `!App` 与 `!VpnHost` **两条都注册着**（`app:immersive`，日期都到 2026-10-25），
这一条只证明"两个入口都能收通知"，不证明点击落到了哪一个 —— 所以下面用日志判。

### 1.2 装机实物：点击时刻的日志里**没有任何激活分发**

`app.log` 里用户点通知的两个时刻，各冷起了一个进程，而**整条启动里没有一行 `激活 kind=`**：

```
19:17:44.202 pid=3316 app 启动 version=…/1.0.121.0 pid=3316
19:17:44.767 pid=3316 protocol 已订阅 CoreApplicationView.Activated
19:17:44.875 pid=3316 health-arm 武装健康哨兵（认领模式：会话非本前台发起）
19:18:01.038 pid=3316 lifecycle Suspending            ← 16.8 秒后被挂起，全程没有窗口
19:24:29.092 pid=4612 app 启动 …                      ← 第二次点击，同一形状
19:24:29.594 pid=4612 protocol 已订阅 CoreApplicationView.Activated
19:26:46.767 pid=4612 lifecycle Suspending
```

反面证据也要如实记：`UserClick` 这个 `ToastDismissedReason` 在
`app.log` / `app.log.1` / `app.log.2` / `plugin.log` / `plugin.log.1` **五个文件里一次都没有**
（`grep -c` 全为 0），出现过的只有 `TimedOut` / `UserCanceled` / `ApplicationHidden`。
★【2026-10-26 订正】这半句**不是证据**，本段当时把它当反面证据记是记错了：装机 1.0.123 后
  拿真人点击做 §六 第 4 行那一手时，点击之后 `app.log` / `plugin.log` 里**同样一行点击回调都没有**
  （只有 `update:NotificationNotFound` 和 `dismissed:TimedOut`，见 §六 第 9 行）。
  ⇒ 正文点击走协议激活时平台根本不给 `Dismissed` 回调，"零 `UserClick`" 在**修好和没修两种情况下都会成立**
  ⇒ 结构上分不开这两种情况，所以它既不能支持也不能反对任何结论。本段真正有效的只有"整条启动里
  零行 `激活 kind=` + 全程没有窗口"那一条。

⇒ 用户看到的"点了没反应（前台 UI 没起来）"在日志里的形状是：
**起了一个进程，跑了完整的应用启动，从没收到任何激活事件，然后被挂起。**

### 1.3 为什么 1.0.81 那套写法必然落空（三条各自独立成立）

1. `launch="action=viewMainWindow"` 要靠 `ToastNotificationActivationInfo`
   （`args.Kind == ToastNotification`）才读得到。实测**这条激活从未到达过任何处理口**：
   `HandleActivation` 第一行就无条件记 `激活 kind=…`（`App.cs:343`），而 §1.2 那两次冷起
   一行都没有 ⇒ override 与自订 hook 两条都没被喂到。
   ★ 但不要把它读成"`OnActivated` 从不分发"：§3.3 证明**协议冷启动**时它确实被调到了
     （`激活 kind=Protocol` 出现在我们自己订阅 `CoreApplicationView.Activated` 之前）。
     1.0.53 那句"Reactor 那条启动路径没有分发出口"记的是**当时的观察范围**（协议那条），
     本批把它订正成"toast 的默认前台激活没有出口，协议激活有出口"。
   而自定义的 `CoreApplicationView.Activated` 挂钩只认 `Protocol`（`App.cs:353` 直接 return），
   所以即使喂到 `kind=ToastNotification`，旧代码也接不住 —— 这正是把落点改成协议的理由。
2. `hintAfterActivationBehavior="pendingUpdate"` 救不了它：官方原话 ——
   "**这仅适用于具有 `ActivationType` 属于 `Background` 的按钮**"。
   ★ 订正（对着 Docs/34 原文核过，不凭记忆）：Docs/34 第 13/83 行写的是
     "`activationType='foreground'` + `hintAfterActivationBehavior='pendingUpdate'`……
     按钮点击触发前台激活（App.OnActivated 会被调用）"。
     这两处前提**都不成立**：pendingUpdate 要的是**后台**激活按钮
     （Docs/34 第 213 行自己引的那句就写着"应立即从**后台任务**更新该通知"，
     却把按钮写成了 `foreground` —— 同一段里引用和实现是矛盾的，当时没看出来），
     而 `OnActivated` 也从未因这条通知被调用过（§1.2 的零分发就是证据）。
     ⇒ 1.0.122 把这个 hint 整个删掉，不再依赖它。
3. 无头入口被前台激活不会建窗口：`ReactorApplication.OnLaunched` 才是唯一建外壳的地方，
   而它没被调（这正是 §三 那条兜底要解决的问题）。

---

## 二、官方口径（联网查证，逐条可点）

| 结论 | 出处 |
| --- | --- |
| Toast 根元素有 `Launch`（"Toast 激活应用程序时传递给应用程序的字符串"）与 `ActivationType`（"**指定当用户单击此 Toast 正文时将使用的激活类型**"）两个独立属性 | [Toast 内容架构](https://learn.microsoft.com/zh-cn/windows/apps/develop/notifications/app-notifications/toast-schema) |
| 按钮 `ActivationType` 的默认值是**前台**（"控制单击时此按钮将使用的激活类型。**默认为前台**"） | 同上 |
| 同一张表里 `ProtocolActivationTargetApplicationPfn` 的说明证实 `ToastActivationType.Protocol` 是**按协议 URI 激活**（"如果使用 `ToastActivationType.Protocol`，您可以选择指定目标 PFN，这样即使有多个应用程序注册处理同一协议的 URI，也始终会启动您所需的应用程序"）—— 本包只有一个候选，不需要 PFN | 同上 |
| `pendingUpdate` 只对 **`ActivationType=Background`** 的按钮生效（"请注意，这仅适用于具有 `ActivationType` 属于 `Background` 的按钮"） | [Toast提示：更新激活挂起](https://learn.microsoft.com/zh-cn/windows/apps/develop/notifications/app-notifications/toast-pending-update?tabs=builder-syntax) |
| 协议激活的语义是"应用启动 URI，其方案名称此应用注册为处理"；**应用仅接收包清单中列出的 URI 方案**的激活事件 ⇒ 由 `<Application>` 上声明的 `uap:Protocol` 决定谁收到 | [ExtendedActivationKind](https://learn.microsoft.com/zh-cn/windows/windows-app-sdk/api/winrt/microsoft.windows.applifecycle.extendedactivationkind?view=windows-app-sdk-1.2) · [负责处理 URI 激活](https://learn.microsoft.com/zh-cn/windows/uwp/launch-resume/handle-uri-activation/) |
| UWP 里 `OnActivated` 收所有激活事件、`Kind` 判类型；WinUI 侧也可用 `AppInstance.GetActivatedEventArgs()` 读激活参数 | [负责处理 URI 激活](https://learn.microsoft.com/zh-cn/windows/apps/develop/launch/handle-uri-activation) |

★ 两条 Learn 页面在 `en-us` 路径下 404、只有 `zh-cn` 可取（本文引的是 `zh-cn` 原文）；
  引用一律以搜索返回的原文片段为准，没有凭记忆改写。

---

## 三、修法：正文与按钮都写 `activationType='protocol'`

### 3.1 为什么是协议激活，而不是别的三条路

| 备选 | 为什么不走 |
| --- | --- |
| 把 `uap:Protocol` 也声明到 `VpnHost` 上 | 两个入口声明同一 scheme 会把"谁收到"变成系统选择，等于把一条**已被装机证明可用**的通道（`App` 的协议导入）搞成变量；而通知需要的恰恰是确定性。 |
| 改回 `foreground` + 读 `ToastNotificationActivationInfo` | 前台激活去的还是**引发通知的那个 AUMID**（`!VpnHost`，无头），换个 args 类型不会让它长出窗口。 |
| 按钮走 `background` 后台任务 + `pendingUpdate` | 能成立（官方语义就在），但要把"断开 VPN"塞进 `windows.backgroundTasks` 执行环境，而那条通道现在归 `vpnClient` 任务用；本批只是修"点了回不回前台"，不值得为它新增一个后台任务身份。 |
| **协议激活（选定）** | OS **按 URI** 解析到声明该 scheme 的 `<Application Id="App">`，与"通知由哪个 AUMID 引发"完全解耦；而这条分发路径是本仓**唯一被装机证明会触发**的那条（§3.3 当场又验了一次）。 |

### 3.2 改了什么（代码）

`ClashUWP/Vpn/StatusToast.cs:738` 的 `StatusXml()`：

```csharp
"<toast launch='clashuwp://open' activationType='protocol'>" + … +
  "<actions>" +
    $"<action content='断开连接' activationType='protocol' arguments='clashuwp://disconnect?n={_nonce}'/>" +
```
（同时删掉了 1.0.81 那个 `hintAfterActivationBehavior='pendingUpdate'` —— 依据见 §1.3 第 2 条。）

`ClashUWP/App.cs:422` 在 `SubscriptionProtocol.TryParse(raw)` **之前**分流：

```csharp
var act = SubscriptionProtocol.TryParseAppAction(raw);
if (act != null) { HandleAppAction(act.Value); return; }
```
`HandleAppAction`（`App.cs:493`）先 `ActivateMainWindow()`，`disconnect` 再走
`DisconnectFromActivationAsync`（`App.cs:509`）。
★ 顺序是硬要求：`TryParse` 认不出 `clashuwp://open`，会回"clashuwp:// 里没有 url 参数"
  然后**整条返回**（§3.3 那份实物就是这句话），动作永远轮不到。

`ClashUWP/Backend/SubscriptionProtocol.cs:128` 新增 `TryParseAppAction`：
只认 `clashuwp://` 的 `open` / `disconnect` 两个词；**带 `url`/`u` 参数的一律不当动作**（留给导入）。

### 3.3 ★ 前提已在本机单独验过（装的是旧版 1.0.121）

修法成立与否有个前置问题：**协议激活到底带不带回前台 UI？** 这一条不需要新包就能验，
所以我先拿现装的 1.0.121 验了（20:03:26、20:09:37 各一次 `Start-Process clashuwp://open`）：

```
20:03:26.607 pid=8840  app 启动 version=…/1.0.121.0
20:03:27.448 pid=8840  protocol 激活 kind=Protocol argsType=…ProtocolActivatedEventArgs
20:03:27.449 pid=8840  protocol 外壳未建，补调 OnLaunched(null) 创建 UI
20:03:27.728 reactor-startup.log  [reactor] build NavigationViewElement …   ← UI 树真的建了
20:03:27.863 pid=8840  protocol 收到激活 uri=clashuwp://open/
20:03:27.863 pid=8840  protocol 解析失败: clashuwp:// 里没有 url 参数        ← §3.2 那句"必须先分流"的实物
20:09:…      ApplicationFrameHost pid=14992  MainWindowTitle = "ClashUWP"   ← 窗口在
```

三条结论，都只依赖实物：
1. **协议激活 → 外壳补建 → Reactor 建出 UI 树 → 有标题为 ClashUWP 的窗口**。
   ⇒ §3.1 选的那条路是通的，这不是推断。
2. 到达 `HandleActivation` 的那一行 `激活 kind=` 出现在 `已订阅 CoreApplicationView.Activated`
   **之前**（27.448 < 27.863）⇒ 冷启动时这一分发来自 `App.OnActivated` 那条 override
   （`App.cs:325` 现在原样转发 `HandleActivation`），不是我们自订的那条 hook。
   ★ 这条**部分推翻**了 1.0.53 的记法（"Reactor 那条启动路径没有分发出口"）：
     至少协议冷启动有出口。两条都汇到 `HandleActivation` 是对的设计，不必争谁唯一。
3. `uri=clashuwp://open/` —— **尾斜杠是系统加的**，`Host` 仍是 `open` ⇒ 判据按 host 认词是对的，
   别按整串相等比（夹具第 ①② 条钉的就是这个）。
4. 顺带记一条：第一次激活留下的 pid=8840 在第二次点击时**已经不在了**，
   系统重新冷起了 pid=17648（同一条日志形状）。所以"点通知"在真实使用中走的就是
   **冷启动 + 外壳补建**那支，不是热激活。

★ 这一遍**没有**产生任何网络后果：`autoProbe=0` ⇒ `diag 探测=跳过（本次进程不建 profile、不连 VPN）`，
  隧道是用户自己那一条，全程没动。

---

## 四、订阅自动更新：对齐 Clash Verge，撤销"会话进行中不动手"

用户口径是「跟随clash verge行为」，所以先把它读一遍再动自己的代码。
★ 读的是 `main` 分支**实物文件**（curl 下来逐行看的），不是文档、不是二手博客。

### 4.1 Verge 的自动路径（文件:行）

| 事实 | 出处 |
| --- | --- |
| 到点执行的就是那一个函数：`async_task` 里 `feat::update_profile(uid, None, false)` —— 第三个参数是 `is_mannual_trigger` | `src-tauri/src/core/timer.rs:471-475` |
| `update_profile` 与手动更新**同一个函数**，只靠 `is_mannual_trigger` 分流 | `src-tauri/src/feat/profile.rs:203` |
| 动手前的判据只有四条：`is_remote` / `item.url` 存在 / `allow_auto_update`（且**只在自动路径生效**）/ 到点 | `feat/profile.rs:76-84`、`core/timer.rs:159-164`、`:220-222` |
| ★★ **整条链里没有一处问"VPN 连没连"**：`timer.rs` 里那个 `running` 是**同一个 uid 的防重入**（`mark_task_running`，"already running, skip"），不是会话状态 | `core/timer.rs:356/373/393/397` |
| 更新的是当前那份就 `CoreManager::global().update_config_with_force(is_mannual_trigger)` —— 即**原地刷配置** | `feat/profile.rs:214-219` |
| 刷新的闸是"退中/重入/防抖"三处，返回 `Skipped`/`Busy`；自动路径把它们当**非致命**（"本次配置刷新已跳过"） | `core/manager/config.rs:183-208`、`feat/profile.rs:224-226` |

⇒ **Verge 的行为 = 到点就拉、拉完原地热重载，连接状态完全不参与判断。**
   "支持连接状态更新"按这个口径就是：**连着 VPN 也照样更新，并且更新真的进到正在跑的隧道里。**

### 4.2 我们这一路本来就等价（现读自己的代码）

`SubscriptionManager.FetchAsync(url, activate:true)`（`Backend/SubscriptionManager.cs:99`）做的是：
形态校验 → `_fetchGate` 串行 → 写 `subscriptions\{id}.yaml` → 注册系统 profile（`EnsureSubscriptionProfileResultAsync`）
→ 写生效的 `config.yaml` → `SetActiveAsync` → `ReloadActiveConfigAsync()`（`PUT /configs?force=true`，
`Backend/Mihomo/MihomoController.cs:226`）。
代码注释里 1.0.79 就写着「UX 对齐 Verge：拉取并启用后原地热重载，**不必断重连**」。
⇒ 它**不重启内核、不拆隧道**，热重载换的是隧道**里面**的节点/规则。
    `SubscriptionAutoUpdater`（`Backend/SubscriptionAutoUpdater.cs:99`）走的就是这一条，
    所以那一层原先加的 `sessionActive` Skip 拦的是一件**本来就不会发生**的事。

★ 一处**刻意保留的差异**（不是遗漏）：Verge 自动路径传 `force=false`，命中 debounce 就跳过本次刷新；
  我们一直传 `force=true`。理由是 `SubscriptionReload.RegenerateEffectiveFile()` 每次都真的重写了
  `config.yaml`，不刷就等于没更新。这一条**没有取证**（§六 第 5 行）。

### 4.3 Docs/55 那条纪律为什么管不到这里

Docs/55「切订阅=先断开→后修改→再重连」管的是**切换订阅**——换的是"哪一份配置在生效"，
profile/隧道身份会变，那是会真断流的操作。
本节是**同一份订阅的内容更新**，`SetActiveAsync` 的目标没变。
⇒ 把两者当同一件事，就是我当初多拦的那一步。Docs/62 §六 6.3 护栏 1 已按这条就地订正。

### 4.4 代码改动（判据层删掉一整个维度）

- `Backend/SubscriptionAutoPolicy.cs:110` —— `Decide` 的 `sessionActive` 形参与那条 Skip **整段删除**，
  类注释换成 §4.1/§4.2 的口径并逐条带文件:行。
- `Backend/SubscriptionAutoUpdater.cs:90-100` —— 每一拍不再先 `ProfileGate.StatusAsync()` 算会话状态，
  直接把 `ProfileGate.SwitchInProgress` 传进 `Decide`。
  ★ 保留的是"**切订阅进行中** Skip"：两条流程叠在一起写 `config.yaml` 是交叉写，
    这对应 Verge 的 `Busy`（同一个更新在跑就跳过），不是会话。
- `Frontend/MainComponent.cs:875` —— 界面那行小字换口径：
  「到点就拉，连着 VPN 也照样更新…系统 profile 那条路另有更里层的闸：会话期间不写存储、断开后自动补」。
- 默认档位仍是 **0=关**，"认不出的档位当关"、"失败退避 `min(6×2^(n-1),48)`" 两条护栏**没动**
  （它们和会话无关，且各自有夹具断言）。

---

## 五、改动清单（本批全部）

| 文件 | 改动 |
| --- | --- |
| `ClashUWP/Vpn/StatusToast.cs` | `StatusXml()` 两行都改 `activationType='protocol'`（正文 `clashuwp://open`、按钮 `clashuwp://disconnect?n=<nonce>`）；删 `pendingUpdate` hint；新增 `NonceFile`/`_nonce`/`SaveNonceAsync`/`MatchesNonceAsync`（`114/234/241/261`） |
| `ClashUWP/App.cs` | `OnActivated` 只转发 `HandleActivation`（删掉那段从未被执行的 1.0.81 toast 分支）；`HandleActivation` 加动作分流（`422`）；新增 `HandleAppAction`（`493`）与 nonce 闸门后的 `DisconnectFromActivationAsync`（`509`）；删除无引用的 `TriggerDisconnectFromToast` |
| `ClashUWP/Backend/SubscriptionProtocol.cs` | 新增 `AppAction` record 与 `TryParseAppAction`（`101/128`），带"带 url 参数不当动作"这条边界 |
| `ClashUWP/Backend/SubscriptionAutoPolicy.cs` | 删 `sessionActive` 形参与 Skip；类注释换成 Verge 口径（文件:行） |
| `ClashUWP/Backend/SubscriptionAutoUpdater.cs` | 不再读会话状态；注释同步 |
| `ClashUWP/Frontend/MainComponent.cs` | 订阅页那三行小字换口径（`875`） |
| `Tools/YamlShapeTest/YamlShapeTest.csproj` | 把 `SubscriptionProtocol.cs` 链进夹具（纯 `System`，无 WinRT） |
| `Tools/YamlShapeTest/Program.cs` | `SubscriptionAutoShapes` ②/③/④/⑤ 全部换成新 arity（旧的"会话中=Skip"断言删除）；新增 `ToastActionShapes` 七组断言 |
| `Docs/62` | §六 6.3 护栏 1/2 标为**已撤销**并写依据；§八 第 15/16 行换判据（第 16 行作废）；表下那段风险声明改为历史记录 |
| `Docs/63` | 本文件 |

★ 装机那遍之前先记一件事：`settings.txt` 里 `subAutoHours=6` 是**用户自己点的**
  （19:17 那遍还是 0，20:03 起读回 6）。装上 1.0.122 后这一档就代表
  "到点会在你连着的时候真的重写生效配置并热重载" —— 要不要留着，是用户决定，不替他改。

---

## 六、装机成败表（本批）

| # | 命题 | 状态 | 证据 / 缺什么 |
| --- | --- | --- | --- |
| 1 | 旧版点击通知唤起的不是前台 UI | ★ **已验（实物）** | §1.1 存库 XML + §1.2 两次冷起零激活分发（那一段里"零 `UserClick`"那半句已按 §1.2 订正作废，它两种情况都成立） |
| 2 | 协议激活这条路带回前台 UI | ★ **已验（实物，旧版上验的）** | §3.3：`kind=Protocol` → 外壳补建 → Reactor 建 UI 树 → `MainWindowTitle="ClashUWP"` |
| 3 | `clashuwp://open` / `disconnect?n=` 的识别与"不截走导入" | ★ **已验（实物）** | 2026-10-26 00:28 装机后（这批代码随 **1.0.123.0** 那份包第一次上机——`1.0.122.0` 只出过包、从未装机）：`protocol 收到激活 uri=clashuwp://open/` → `toast 通知激活动作 action=open nonce=无`；`?n=deadbeef` → `action=disconnect nonce=有`。解析在真包上跑通 |
| 4 | 点**正文**回到前台 UI | ★ **已验（实物：点击经系统命中测试投递，2026-10-26 00:46，1.0.123.0）** | 触发方式：通知中心（`HWND 853452`，`ShellExperienceHost` pid 6900）里对那条 toast 的 ListItem 走 `winapp ui click`（SendInput 鼠标模拟，落点 1650,225 = ListItem 中心）——**不是**协议启动器。★ 如实分级：手指不是人按的，但**"哪条 URI 被投给哪个入口"这一步是 `ShellExperienceHost` + 通知平台做的**，我的代码没有参与投递；与 §3.3 那遍"`Start-Process` 直接喂 URI 给自己"是两种强度的证据，这一遍补的正是平台投递那一环。★ 前置条件与 §3.3 那遍同样硬：点击时**只有 `-ServerName:VpnHost` 的 pid 18812 在跑**，`!App` 进程压根不存在，且 `rasdial = Connected to ClashUWP`（活会话）。点击后 0.7 s 内 `app.log`：`app 启动 pid=10920` → `protocol 激活 kind=Protocol argsType=…ProtocolActivatedEventArgs` → `protocol 外壳未建，补调 OnLaunched(null) 创建 UI` → `protocol 投影成功 path=as` → `protocol 收到激活 uri=clashuwp://open/` → `toast 通知激活动作 action=open nonce=无` → `toast 已将主窗口激活到前台`；`Get-CimInstance` 当场看见第二个 pid 10920 `-ServerName:App.AppX…`。★ UIA 复核是真 UI 不是空壳：`ApplicationFrameHost` 下 `HWND 3801818 Window "ClashUWP" 772x985 foreground`，树里 `当前状态：Connected`、`内核：已连接　mode=rule`、`代理组 3 个　活动连接 57 条`、实时流量在涨。★ 隧道未动：点击后 `rasdial` 仍 `Connected to ClashUWP`，`app.log` 里没有任何 disconnect 行（正文那一路 `nonce=无`，本就不该碰会话）。**这一行闭合了本批最后一格** |
| 5 | 点「断开连接」**真的断开**（nonce 对得上） | ★ **已验（实物：通知中心里点到按钮，2026-10-26 00:51，1.0.123.0）** | 走法：展开那条 toast（`btn-expandbutton` → `VerbButton "断开连接" (1424,330 452x48)`，展开态里**只有这一个**动作按钮）→ `winapp ui click VerbButton`。★ 这一次前台 `App` 进程**是活的**（pid 10920，§六 第 4 行那一下点起来的），所以吃到的是**热激活**那条路径，与冷启动互补。日志：`00:51:19.127 protocol CoreApplicationView.Activated kind=Protocol` → `protocol 收到激活 uri=clashuwp://disconnect/?n=6e7df2cb1a1c4faf` → `toast 通知激活动作 action=disconnect nonce=有` → `session 用户发起断开（已解除健康哨兵）` → `profile-disconnect 断开=Ok（请求已受理…）`；插件侧同刻 `physical-watchdog-plugin 停止（平台回调 Disconnect）`、`net-follow 停止`、`session 会话结束，存活 550s encap=35060 inj=32598`。收尾三面干净：`rasdial = No connections`、`DnrPolicy` 键不存在（NRPT=0）、默认路由只有一条 `ifIndex=22 → 192.168.0.1`（WLAN）、`Get-NetIPInterface -ConnectionState Connected` 里没有任何 VPN 接口 ⇒ **没有自动重拨**。★ 旧的那遍（`00:29:33` 经启动器 + 从 `toast-nonce.txt` 现读 nonce）仍算另一次实物，本行现在两种触发形态都有 |
| 6 | 伪造/网页触发的 `clashuwp://disconnect` **不动手** | ★ **已验（实物）** | `00:28:59.452 pid=6480 app W toast 断开按钮凭据不匹配（通知里那串与本包记录的不同），拒绝 —— 这条激活不是本应用的通知发出来的`，同刻 `profiles` 回读 `状态=Connected` ⇒ 只留一行拒绝，隧道没动。这条正是当初设计里"外部可触发的协议 + 有网络后果的动作"那道闸 |
| 7 | 自动更新到点时**连着 VPN 也更新**，且不拆隧道 | ✗ 未验（等一次到点） | 本轮只跑了**手动**那一条：活会话里 `subupdate` → `生效阶梯 stage=Accepted presence=NotAsked action=None … 隧道未断`（全文见 Docs/64 §七 #4）。★ 顺带查实：这台机器 `settings.txt` 最后一行是 `subAutoHours=6`，**不是默认 0** ⇒ 到点那一轮迟早会自己在活会话里跑，届时看 `sub-auto` + `evt=hb alive=` 是否连续不重置 |
| 8 | 撤销会话护栏后**没有造出僵尸 profile** | △ 半验 | `profile-sub-sync 启动对账：订阅 2 份 + 基础 profile 1 条，新增 0 / 更新 0 / 已就位 3 / 跳过 0 / 失败 0；prune 幽灵 profile：删 0 / 留 0`（00:28:59、00:29:34 两次）。★ 按 [[project-g6-traffic-sort-autoupdate]] 的口径，这是"本来无事可做"，**不能读成闸拦下了一次写** |
| 9 | 点击**不吃补发额度、不弹引导** | ★ **已验（实物，随 §六 第 4 行那一手一起做）** | 结论与当初担心的两个方向都不是：正文点击走协议激活时，平台**一个 `Dismissed` 回调都不给**。点击时刻（00:46:00.7）之后 `plugin.log` 只有两行、都与点击无关：`00:46:01.318 update:NotificationNotFound（非用户划掉：只按节流补发，不计额度）`、`00:46:03.388 dismissed:TimedOut（横幅到期：只记账）`；`UserClick` 在装 1.0.123 后的 `app.log`/`plugin.log` 里仍是 0，而 `UserCanceled` 只有 2 条、时间戳都是 **10-25 的 18:11:55 / 20:45:29**（那是真的划掉，不是点击）。★ 引导那条从未发出过：`grep 引导` 只命中启动配置行 `引导通知=每天一次`，零条实际发送。⇒ 本批"修好 A 可能弄脏 B"那道敞口**不存在**，§七 第 3 条那个"分开记账"的修法不必做 |
| 10 | `pendingUpdate` 删除后那条通知在点击后不再原位更新 | ★ **已验（实物，答案与当初写的预期相反但无害）** | 实测形状：点击**本身就把那条通知从通知中心摘掉了**（平台行为，不是我们的代码），所以 0.6 s 后下一次原位刷新拿到 `00:46:01.318 update:NotificationNotFound（非用户划掉：只按节流补发，不计额度）`⇒ 走补发通道重新 `Show`。★ 补发的效果在通知中心树里直接读到：组 `grp-clashuwp-6aca「来自 ClashUWP 的通知」` 下**仍只有一条** `itm-clashuwp89kbs59-6ad9`，时间戳从「接收于 0:42」变成「接收于 0:46」，正文 `累计 ↑ 13.27 MB ↓ 4.51 MB` 与前台 UI 同一时刻对得上 ⇒ 原位通道断掉时**不堆积、不重复、额度不动**（`补发上限=3` 从头到尾没被计数消耗，日志里 `第 N 次` 是回调序号不是额度）。⇒ 本行原命题"点击后不再原位更新"成立，但它是**补发**接住的，不是"没影响"；引导那条依旧零发送 |

---

## 七、本批没做 / 遗留

1. **`plugin.log` 里同一个 pid 每 ~25 秒一行"插件进程启动"**（19:52:46 起 pid 15912 反复出现，
   伴随 `Suspending`/`Resuming`/`bgtask Run 返回`）。§六 6.5 那遍就看到了，本批**没有展开分析**，
   也没有改它 —— 它不影响本批两条命题的成立，但它是"无头宿主到底被系统重新激活了几次"的证据，
   下次查 `VpnHost` 入口行为时从这里接。
2. 通知的**引导那条**（`GuideXml`，`StatusToast.cs:758`）没动：它的按钮本来就是
   `activationType='protocol' arguments='ms-settings:notifications'`，与本批的口径天然一致。
3. ~~§六 第 9 行如果实测成 `UserCanceled`，正确修法不是忍着，而是**把引导阈值与点击分开记账**；
   未测之前不写代码（口径同 1.0.78 "判据不可信绝不动手"）。~~
   ★【2026-10-26 已验，不必做】正文点击根本不产生 `Dismissed` 回调（实物见 §六 第 9 行），
   既不是 `UserClick` 也不会被记成 `UserCanceled` ⇒ 没有需要分开记账的两件事，本条作废、不写代码。
4. `App.OnActivated` 与 `CoreApplicationView.Activated` 两条入口都在（§3.3 第 2 条）——
   Docs/29/34 里"Reactor 那条启动路径没有分发出口"这句**需要按 §3.3 订正**，
   本批只在 `App.cs:317` 的注释与 Docs/63 里改了说法，**没有回改 Docs/34 全文**。
   ★【2026-10-26 00:51 实物把这句彻底钉死】1.0.53/1.0.57 那句"override 只是兜底、
   真正会触发的只有 `CoreApplicationView.Activated`"**是错的**：热激活（前台进程已活着）时
   **两条都触发**，`App.cs:325` 的 override 确实被 Reactor 喂到了 ⇒ 见下面第 5 条。
   冷启动时只有 override 那条（自订 hook 是 `OnLaunched` 里才装上的，§六 第 4 行 00:46 那遍
   的顺序就是这个形状：`.911 激活` → `.911 补调 OnLaunched` → `034 已订阅`）。
5. ★ **新暴露的缺陷：热激活下同一次点击被处理两遍**（1.0.123.0 实物，非本批引入，
   是 1.0.57 那次"两条入口都汇到 `HandleActivation`"的形状第一次被真实点击喂出来）。
   一手日志（`clashuwp://disconnect` 那一下）：
   ```
   00:51:19.125 toast 通知激活动作 action=disconnect nonce=有     ← 第 1 遍（OnActivated override）
   00:51:19.125 toast 已将主窗口激活到前台
   00:51:19.127 protocol CoreApplicationView.Activated kind=Protocol ← 第 2 遍（自订 hook）
   00:51:19.127 protocol 收到激活 uri=clashuwp://disconnect/?n=6e7df2cb1a1c4faf
   00:51:19.127 toast 通知激活动作 action=disconnect nonce=有
   00:51:19.128 session 用户发起断开（已解除健康哨兵）
   00:51:19.130 session 用户发起断开（已解除健康哨兵）            ← 两遍各发一次
   00:51:19.400 profile-disconnect 断开=Ok
   00:51:19.412 profile-disconnect 断开=NoConnection             ← 第二遍时平台侧已无会话
   ```
   ⇒ 后果实测**良性**（断开幂等，第二遍拿到 `NoConnection` 并按已断开处理；`open` 只是把窗口
   再激活一次；`subscribe` 只是重填输入框）。但它是"**有网络后果的动作被执行两遍**"这个形状，
   所以不当它不存在来写。
   ★【1.0.124 已按候选 ① 落地】`HandleActivation` 在 `收到激活 uri=` 之后加了一道按 URI 的
   2 秒窗口收口（`IsSecondDelivery`），命中时记 `同一次激活的第二次到达，跳过` 再返回 ——
   没有取消任何一条入口（两条都留着当分发保险，只让动作走一次）；候选 ②「比 args 引用」被否掉，
   理由是两条入口递来的是**不同投影实例**（override 给 `IActivatedEventArgs`，自订 hook 给
   `ActivatedEventArgs` 包装），比引用相等结构上永远不成立。**装机判据**：点一次正文，`app.log` 里
   应当是「一行 `通知激活动作` + 一行 `第二次到达，跳过`」，两行动作行 = 去重没生效。
