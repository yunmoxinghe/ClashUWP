# 25 · 订阅 ↔ 系统 VPN profile 同步修复（应用 1.0.72）

> 触发：用户反馈「之前看订阅的部分似乎有 bug，跟系统同步也有些问题」。
> 本批只动前台侧（订阅管理 / 系统 profile 生命周期 / 轮询），
> **组件保持 1.0.40、插件与数据面零改动**。
>
> 结论先说：不是一处 bug，而是**六处互相放大**的问题叠在一起：
> 已存在的系统 profile 一直在走 `Add`（必然失败 `Other`）、
> 启动对账只补缺不修正、3 秒轮询把 app 内的切换当成"系统侧切换"回填掉、
> 重复 Launch 会多建一套永不上屏的组件和轮询、删除顺序会让正文先消失而 profile 留下、
> **拉取时把机场响应头里的订阅名丢掉了**（用户反馈的"名字显示为拼音/字母串"）。
> 全部已修，1.0.72 已重新构建；**尚未上机验证**（需用户双击安装，见 §6 验收清单）。

---

## 1. 证据（全部来自本机已安装版本 1.0.64 的 `LocalState\logs\app.log`）

订阅 id / URL 中的 token 在本文一律截断，不落原文。

### 1.1 已存在的系统 profile 走 Add，永远 `Other`

```
01:33:01.380 pid=23388 profile-sub 订阅profile[ClashUWP · iz9ld….one] subId=iz9ld….one-link-… 注册=Other
01:33:02.511 pid=23388 profile-sub 订阅profile[ClashUWP · iz9ld….one] subId=iz9ld….one-link-… 注册=Other
01:33:06.201 pid=23388 profile-sub 订阅profile[ClashUWP · iz9ld….one] subId=iz9ld….one-link-… 注册=Other
…
01:33:10.875 pid=23388 profile-sub 订阅profile[ClashUWP · hkom1….one] subId=hkom1….one-link-… 注册=Ok
01:33:12.808 pid=23388 profile-sub 订阅profile[ClashUWP · hkom1….one] subId=hkom1….one-link-… 注册=Other
01:33:21.910 pid=23388 profile-sub 订阅profile[ClashUWP · hkom1….one] subId=hkom1….one-link-… 注册=Other
```

同一 subId 第一次 `Ok`（新建），随后每次 `Other`（对象已存在）。
代码在 1.0.62~1.0.71 里对"按 subId 找到的已有 profile"仍调
`AddProfileFromObjectAsync`（`VpnProfileSetup.EnsureSubscriptionProfileAsync` 旧实现），
所以「更新显示名 / ServerUri / CustomConfiguration」这条路径**从未生效**。

### 1.2 3 秒轮询每轮都"回填"，写到自己被文件锁打回

```
23:50:43.025 profile-sub-adopt 快速面板连接的订阅 iz9ld… 与生效 iz9ld… 不同，已回填 config.yaml 并记为生效
23:50:43.095 profile-sub-adopt 快速面板连接的订阅 iz9ld… 与生效 iz9ld… 不同，已回填 config.yaml 并记为生效
…（每 ~3 秒两条，持续数分钟）…
23:50:49.556 profile-sub-adopt 回填失败: COMException: 另一个程序正在使用此文件，进程无法访问。
```

注意前后打印的 id **完全相同**，判定却认为"不同"：调用方读的是
`MainComponent` 的 `_subActive` 镜像字段，与 `settings.txt` 里的真实生效 id 分叉。
更根本的是判定条件本身错了：app 内切订阅只热重载配置、不重连 profile，
「隧道仍是 A、生效已改成 B」是**正常中间态**，旧逻辑每 3 秒把 A 回填回来。

### 1.3 启动对账日志重复两条 / 多一套轮询

```
23:56:18.872 pid=23388 app I profile-sub-sync 启动对账：订阅 2 份，系统profile 已就位 2，新补 0
23:56:18.872 pid=23388 app I profile-sub-sync 启动对账：订阅 2 份，系统profile 已就位 2，新补 0
23:56:34.652 pid=23388 app I profile-sub-sync 启动对账：订阅 2 份，系统profile 已就位 2，新补 0
23:56:34.652 pid=23388 app I profile-sub-sync 启动对账：订阅 2 份，系统profile 已就位 2，新补 0
```

同一毫秒两条：`SyncSystemProfilesAsync` 内部记一遍，`MainComponent` 调用方又记一遍。
23:56:34 那一对紧跟在 `CoreApplicationView.Activated kind=Launch` 之后 ——
再次 Launch 时 Reactor 的 `OnLaunched` 会**新建一个 `ReactorHost` 并立即渲染**，
而 `Window.Current.Content ??= host.Root` 让它永远不上屏，组件却活着：
它的 `UseEffect` 照跑，于是多出一整套 3 秒轮询且永不卸载。

### 1.4 删除顺序：正文先删，profile 可能留下

旧 `SubscriptionManager.DeleteAsync`：
`file.DeleteAsync()` → `ClearUrlAsync` → `RemoveSubscriptionProfileAsync`，
且最后一步把 `DeleteProfileAsync` 的返回值丢掉。
一旦 `GracefulTeardown` 判定"会话未收干净、拒绝删除"，
结果是**订阅正文没了、快速设置里的 profile 还在**，点它连接会读不到
`subscriptions\{id}.yaml`（插件回退内置配置或失败）。

### 1.5 启动对账只"补缺"，不"修正"

旧 `SyncSystemProfilesAsync` 先 `HasSubscriptionProfileAsync`，存在就 `present++` 跳过。
profile 的显示名 / ServerUri / CustomConfiguration 一旦漂移（改过
`profile-server.txt`、旧版本写入过错误 subId 等），启动对账永远不会修；
而 UI 侧那次更新又走 Add 返回 `Other`。两条路都堵死。

### 1.6 机场响应头里的名字被丢掉（"名字显示为拼音/字母串"）

对三份已存订阅做了**只读 HEAD 取证**（URL 不落盘，只看响应头）：

```
content-disposition: attachment;filename*=UTF-8''%E6%98%9F%E9%93%BE%E6%9C%BA%E5%9C%BA
  → Uri.UnescapeDataString ⇒ 星链机场
content-disposition: attachment; filename=iKuuu_V2.yaml
  → iKuuu_V2（hkom1 与 iz9ld 两条链接同名）
```

机场早就把可读名字放在 `Profile-Title` / `Content-Disposition` 里了，
而 `FetchAsync` 只读 `resp.Content` 的正文，**这个头从来没被看过**。
于是：

- app 订阅页显示的是 `p.Id`，即 `host + path + token` 派生串
  （如 `iz9ld.no-mad-sub.one-link-…`）；
- Windows 快速设置里的 profile 名是 `ClashUWP · {host}`。

两处都不是中文名，用户看到的就是"一串字母/像拼音"。
注意：**代码里没有任何中文转拼音的逻辑**，订阅正文与内核 JSON 里的
节点/分组名全程是 UTF-8 中文原文（已逐份核对 `config.yaml` /
`config.kernel.yaml`）——问题只在"显示名没有取值"。

---

## 2. 修法（按文件）

### 2.1 `ClashUWP/Vpn/VpnProfileSetup.cs`

- **新增结构化结果** `SubProfileResult` / `SubProfileAction`
  （`Unchanged / Added / Updated / Skipped / Failed`），供对账计数。
  `EnsureSubscriptionProfileAsync` 保留原来的字符串签名，内部转调
  `EnsureSubscriptionProfileResultAsync`。
- **已存在 → Update**（`VpnProfileSetup.cs:328`）：
  - 内容一致（ProfileName、`RequireVpnClientAppUI`、解析后的 subId、ServerUri）
    ⇒ 直接返回 `Unchanged`，**不调任何写 API**；
  - 不一致且 `ConnectionStatus == Disconnected` ⇒
    `VpnManagementAgent.UpdateProfileFromObjectAsync`；
  - 连接中 / 正在断开 ⇒ 整轮跳过并记日志。
    （对活动会话改 profile 没有实测背书；下次启动对账会再对齐。）
  - `subId` 比较用 `VpnPluginConfig.Parse`，避免平台回读 XML 时空白/属性重排造成假差异。
- **异常收口**：单个 profile 的 WinRT 失败只返回 `Failed`，不再把整次
  「拉取订阅」打成"落盘失败"（正文其实已经写好了）。
- **删除检查状态码**：
  - `RemoveSubscriptionProfileAsync` 改为 `Task<(bool Ok, string Message)>`，
    `DeleteProfileAsync != Ok` 时返回失败；
  - `DeleteOurProfileAsync` / `DeleteByNameAsync` / 探测收尾同样检查返回值。
- **重建流程**：删旧未完成（拒绝/失败）时**放弃新建**，避免用 Add 撞重名后
  在日志里伪装成"重建失败"（`RecreateProfileAsync`）。

### 2.2 `ClashUWP/Backend/SubscriptionManager.cs`

- `SyncSystemProfilesAsync`（`:219`）改为**全量幂等对账**：
  每份订阅都调一次 Ensure，按结果计数落一行：
  `新增 / 更新 / 已就位 / 跳过 / 失败`。
- `FetchAsync` 把 profile 注册结果并入返回消息（UI 不再永远只显示"已拉取"）。
- `DeleteAsync`（`:338`）**顺序反转**：先删系统 profile，
  失败则整体失败、正文保留；成功后才删 `subscriptions\{id}.yaml`。
- 删除启动对账调用方的重复日志（汇总行只由 Sync 自己记）。

### 2.3 `ClashUWP/Frontend/MainComponent.cs`

3 秒轮询里的回填改为**边沿触发**（`:226` 起）：

- 首轮只取 `connectedSub` 作基线，**不判定**（启动时"生效 B / 隧道 A"可能是上次残留）；
- 之后仅在 `connectedSub` **发生变化**时处理：
  - 变为非空 ⇒ `AdoptAsync`（系统侧连接/切到了某条订阅）；
  - 变为空 ⇒ 只记一行"系统侧已断开"，生效订阅保持不变。
- app 内切订阅时 connectedSub 不变 ⇒ 不会再被自己撤销。

### 2.4 `ClashUWP/App.cs`

`OnLaunched`（`:241` 起）先看 `Window.Current?.Content`：

- 已有内容树 ⇒ 只补做一次激活订阅，**不调 `base.OnLaunched`**，
  不再创建第二个 `ReactorHost/MainComponent` 及其轮询；
- 空窗口（冷启动 / 协议冷启动补建）⇒ 原路径不变。
  激活订阅抽成 `EnsureActivationSubscribed()`，两种路径共用、Once 语义。

### 2.5 订阅显示名（本轮追加）

`ClashUWP/Backend/SubscriptionManager.cs`：

- 拉取时读响应头：`Profile-Title`（支持 `base64:`）→
  `Content-Disposition` 的 `filename*`（RFC 5987）→ 普通 `filename`。
  全部取不到才退回 host，**名字缺失不影响拉取**。
- 名字落 `settings.txt` 的 `subName:{id}`；沿用追加式、后行胜出。
  响应头这次没给名字时**保留旧名**，不被空串冲掉。
- `Profile` 记录新增 `Name`：
  - app 订阅按钮显示 `Name`（如「星链机场」），不再显示 URL 派生长串；
  - 系统 profile 名变为 `ClashUWP · 星链机场`；
  - 同一机场多条链接给出同名时（本机两份 `iKuuu_V2`），
    UI 追加 host 区分，系统 profile 名退回 host —— 保证快速设置里不重名。
- `SyncSystemProfilesAsync` / `ActivateAsync` / `FetchAsync` 统一走同一套
  名字解析，避免"列表是一个名、系统 profile 是另一个名"。

---

## 3. 行为变化对照

| 场景 | 1.0.71 及之前 | 1.0.72 |
|---|---|---|
| 同一 URL 再次拉取 / 启动对账，profile 已存在 | 每次都调 Add，返回 `Other` | 内容一致：`已就位`；不一致且已断开：`Update` |
| 改了 `profile-server.txt` 后重启 | 系统 profile 的 ServerUri 永远不更新 | 对账显示 `更新 1` |
| app 内连接 A 的情况下切到订阅 B | 3 秒后又被回填成 A，切换被撤销 | 保持 B（配置已热重载） |
| 快速设置在 A/B 之间切换 | 每轮都回填（幂等靠 settings 比对） | 只在观察到的切换边沿回填一次 |
| 再次点应用图标 / 系统重放 Launch | 多一套组件 + 一条轮询 | 跳过外壳构建，只有原有那一套 |
| 拉取时系统 profile 注册失败 | UI 只显示"已拉取" | 消息里带 `新建=Other` 等失败原因 |
| 删除订阅时会话未收干净 | 正文先删，profile 留下成幽灵 | 整体失败并保留正文，可重试 |
| 删除时 `DeleteProfileAsync` 返回错误 | 忽略，宣称"已删除" | 返回失败，不再假报成功 |
| 重建 profile 时删旧被拒 | 继续 Add，撞重名后报 `Other` | 直接放弃新建并回原因 |
| 启动对账日志 | 同一毫秒两条 | 一条（含五类计数） |
| 订阅显示名（本机） | `iz9ld…-link-…` / `ClashUWP · starlink…` | 「星链机场」/ `ClashUWP · 星链机场` |
| 同名机场的多条链接 | 只能看 host | UI 名字后补 host；系统 profile 退回 host，保证唯一 |

---

## 4. 未改动 / 已知遗留

1. **`MakeId` 会把订阅路径里的 token 带进 id**（`subscriptions\{id}.yaml`
   文件名、系统 profile 名、日志都会出现）。
   本批不换 id 方案，因为换 id 会让已存订阅的路径/settings 键全部失配；
   要做应连迁移一起做（建议：短哈希 + 迁移时按旧 `subUrl:` 行认领）。
2. **`MakeId` 忽略 query**：同 host + 同 path、仅 query 不同的两份订阅会撞 id。
   与上一条同一批处理。
3. **老订阅不会自动获得新名字**：`subName:{id}` 是拉取时写入的，
   升级后要再点一次「更新当前订阅」才会把响应头名字补进
   app 列表与系统 profile 名。取不到名字的机场仍显示 host（比原来的长 id 短）。
   同一机场多条链接同名时，系统 profile 名退回 host；若连 host 都相同
   （同 host 不同 path），退回 id —— 这时仍会出现长串。
4. **`SubscriptionView.LoadAsync` 的 mtime 取 `DateTime.Now`**，列表时间戳
   不是文件真实修改时间（只影响显示）。
5. **app 内切订阅 + 活动连接**仍是"热重载配置、profile 仍指旧订阅"的形态。
   这是 Verge 式原地切换的代价；系统 UI 上显示的是旧的 profile 名，
   但下次系统侧断开/重连会由边沿回填自愈。改成"切换即重连"会断一次隧道，
   不在本批范围。

---

## 5. 构建与产物

- 应用：`ClashUWP/Package.appxmanifest` → `1.0.72.0`；组件保持 `1.0.40`。
  （显示名修复属于本轮追加，仍在 **尚未安装** 的 1.0.72 批次内，故不额外 bump。）
- 构建（VS UWP 桥，未自制脚本）：
  ```
  Tools\UwpBridge\uwp-bridge.exe -Build <ClashUWP.csproj> -Configuration Release -Platform x64 -Target Publish
  ```
- **★ 1.0.72 第二次构建（AppListEntry 修复）**：
  Plugin Application 原缺 `Executable` / `EntryPoint`，导致 MakeAppx 报
  `error 80080204: The Application element must specify the Executable or StartPage attribute`。
  已补上：
  ```xml
  <Application Id="Plugin"
    Executable="$targetnametoken$.exe"
    EntryPoint="ClashUWP.Vpn.VpnBackgroundTask">
  ```
  同时移除 `AppListEntry="none"`（无头应用豁免需 Partner Center 申请）。
  重新构建成功，包路径不变。
- 产物：
  - `ClashUWP\AppPackages\ClashUWP_1.0.72.0_x64_Test\ClashUWP_1.0.72.0_x64.msix`
  - 已核对包内 `AppxManifest.xml`：`Version="1.0.72.0"`、
    AppService `ClashUWP.Debug`、协议 `clashuwp` / `clash` 均在、
    两个 Application 均有 Executable/EntryPoint。
- 已核对产物 DLL 含 `UpdateProfileFromObjectAsync` 引用、
  `SubscriptionManager → SubscriptionName.FromHeaders` 调用点与 `subName` 键。
- 离线回归：全绿
  - IpPacketChecksumTest：10 项全过（v4/v6 TCP/UDP/ICMP + multicast 回归）
  - DnsConfigTest：~60 项全过（DNS 模板、split、bool、inline、v6pool）
  - SubscriptionNameTest：**18 项全过**（新增功能验证）
    - Profile-Title 明文/base64/url-safe 解析
    - Content-Disposition filename* UTF-8 解码（「星链机场」实测通过）
    - 优先级规则、清洗、截断、空值兜底
    - FromHeaders 入口函数直连测试

---

## 6. 上机验收清单（装 1.0.72 后）

> 安装仍需用户手动双击 MSIX / `Install.ps1`；本环境不能 `Add-AppxPackage`。
> 以下按顺序做，前 6 条与第 9–10 条不碰隧道，第 7–8 条涉及连接（谨慎）。

1. **启动对账只有一条**：
   `logs\app.log` 里每次启动恰好一行
   `profile-sub-sync 启动对账：订阅 N 份，profile 新增 0 / 更新 0 / 已就位 N / 跳过 0 / 失败 0`
   （若刚升级或改过 server 文件，更新数可为 1）。
2. **不再出现 `注册=Other`**：`profile-sub` 频道应只有 `已就位` 或偶发 `更新=Ok`。
3. **重复拉取同一 URL**：UI 提示应含 `已就位`；`settings.txt` 不应每 3 秒增长。
4. **再点一次应用图标**：日志只有一行 `app 启动`，**没有**第二套轮询
   （观察 `profile-sub-sync` 不重复、`profile-sub-adopt` 无 3 秒节律）。
5. **快速设置切换订阅**（A→B）：日志出现一次
   `profile-sub-adopt 已跟进快速面板的切换 → B`，随后安静；
   不应每 3 秒重复。
6. **app 内切订阅（连接中）**：界面生效态变为新订阅，
   日志**不应**出现把旧订阅回填回来的 `profile-sub-adopt`。
7. **删除非生效订阅**：profile 从 Windows VPN 列表消失；
   若提示"会话未收干净，拒绝删除"，正文应仍在（可断开后重试）。
8. **连接中删除该订阅**：应被"不能删当前生效的订阅"或 teardown 闸挡住，
   不允许出现"正文没了、profile 还在"。
9. **显示名**：装后点一次「更新当前订阅」。
   app 订阅页的 `starlink…` 应显示「星链机场」；
   Windows VPN 列表应出现 `ClashUWP · 星链机场`（旧的 host 名应被 Update 改掉）。
   两份 `iKuuu_V2` 在 app 列表里应能区分（名字后补 host），
   系统 profile 名不重叠。
10. **名字不抖**：连续两次「更新当前订阅」，UI 名字与系统 profile 名不变；
    取不到响应头的机场仍显示 host，不显示空串。
    日志里 `profile-sub` 应出现 `订阅profile[ClashUWP · 星链机场] … 更新=Ok`。

若第 2 条仍出现 `Other`，最可能的剩余原因是**同名 profile**
（同 host 的两条订阅）或平台不接受 `UpdateProfileFromObjectAsync` 对
plugin profile 的修改；把那几行日志（subId 可截断）发回来即可继续定位。

---

## 7. 一句话总结

订阅正文、生效记录、Windows 快速设置里的 profile 是**三份状态**。
1.0.62~1.0.71 的写法默认它们永远一致，于是任何一次漂移都只能靠
"每 3 秒强行回填 + 每次启动 Add 一遍"来掩盖，而这两条路本身就是错的。
1.0.72 把它们改成：**写路径显式对齐（Add/Update/跳过），读路径只在对的时机跟账
（边沿触发），删除先拆系统对象再动文件**。
