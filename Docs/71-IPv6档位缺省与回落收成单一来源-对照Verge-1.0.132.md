# Docs/71 — IPv6 档位的缺省与回落收成单一来源（对照 Clash Verge，1.0.132）

> 批次：**1.0.132**（`Package.appxmanifest` → `1.0.132.0`）
> 触发：用户「测试最新版本，确保正确工作」→ 装机验证 1.0.131 的那一次拨号窗口里拿到分叉实物；
> 处置口径由用户定为「**对照官方修复**」。
> 纪律：官方的话现读源码（本批全部行号来自 `main @ 4337bd85`，逐字引）；我们自己的旧结论同样现读重验。

---

## 一、结论先写

1. **1.0.130 抬默认档只抬了一半**：`settings.txt` 里没有 `ipv6` 键时，
   隧道按 **档 2** 跑（插件侧回落用字段初值），前台按 **档 0** 读（`AppSettings` 三处写死 `0`）。
   实物见 §二 —— 同一台机器、同一分钟、两个读数，而界面上亮的是「0 不动」。
2. **这条缺陷不在数据面上**：本次拨号窗口里档 2 的形状、DNS 改写、流量层全部正常
   （§三），所以它是"读数与界面"的缺陷 —— 但正因如此，它会把取证与用户的选择都引到错误的档位上。
3. **对照官方之后采纳的口径**（Verge 的三条动作，见 §四）：
   **默认值只有一处定义；缺键 = 没人表过态 = 取权威默认；键在但值不可用 = 同样当没人表过态，且必须出声。**
   ⇒ 本批把回落值、键名、区间三者一起收进 `LocalSettings.ParseIpv6Arm()`，
   两个读者只剩这一个入口；越界/认不出不再压成 `0`，改为压成 `Ipv6ArmDefault` 并记 `channel=settings` 警告。
4. **官方那条"每轮钉回权威值"（capture/enforce）本批不做**，只记账（§五）。
   我们现在已经是"每轮现读文件 + 改档要重连"，缺的是"外来写入挪走时出声"，不是"每轮钉回"。
5. 订正 Docs/70 §一 那句「默认档真的生效」：**只对插件侧成立**，前台那份当时没读。

**验证**：见 §六（离线 474 PASS / 0 FAIL、Debug+Release x64 各 0 警告 0 错误、装机实物）。

---

## 二、缺陷实物（2026-10-28 01:41–01:44，1.0.131.0 装机）

`settings.txt` 全文 82 行、**一条 `ipv6=` 都没有**（`grep -c ipv6` = 0），这是全新装机的正常状态。

| 时刻 | 来源 | 逐字读数 |
| --- | --- | --- |
| 01:41:40.934 | 前台 `app.log` | `appsettings src=settings.txt autoProbe=0 panelMultiEntry=0 subAutoHours=6 `**`ipv6=0`** |
| 01:42:59.779 | 插件 `plugin.log` | `session L3 隧道接口已建立：includeMode=narrow … `**`ipv6=2`**` v6include=[2000::/3,fdfe:dcba:9876::/64] outer=192.0.2.1:9 mtu=1400` |
| 01:42:59.783 | 插件 `plugin.log` | `bridge-start arm=2 设 SKIP_SYSTEM_IPV6_CHECK=1（mihomo 官方逃生口；Verge 不设它）` |

⇒ 隧道按 2 跑、前台读成 0、设置页按钮亮在「0 不动」（`MainComponent.cs:632` 把
`AppSettings.Ipv6Arm` 喂给按钮，初值 `UseState(0)` 在 `:208`）。

**根因（三处，全部是"回落值写在调用点"）**：

| # | 位置 | 旧写法 | 后果 |
| --- | --- | --- | --- |
| 1 | `Backend/AppSettings.cs:155` | `SettingsParse.Int(map, Ipv6Key, 0, min, max)` | 缺键/认不出 ⇒ **0** |
| 2 | `Backend/AppSettings.cs:99` + `:131` + 无文件分支 `Apply(…, 0, …)` | 字段初值、局部初值、无文件回落各写一个 `0` | 同一件事写四遍 |
| 3 | `Vpn/LocalSettings.cs:625` | `SettingsParse.Int(map, "ipv6", Ipv6, min, max)` | 回落 = 字段初值 ⇒ **2**（与 #1 分叉） |

夹具没拦住的原因：467 条里**没有任何一条**断言"缺键时两个读者回落到同一档"
（`AppSettings.cs` 的注释反而写着"【为什么这不叫两套真值表】夹紧用的是同一个 `SettingsParse.Int` 调用"——
调用同一个函数≠回落同一个值，这句注释把这件事说反了）。

---

## 三、1.0.131.0 装机与拨号窗口里**通过**的部分（数据面没问题）

| 判据 | 实物 |
| --- | --- |
| 包体 | 包内 `AppxManifest.xml` `Version="1.0.131.0"`（★ 读的是解包后的那份，不是仓库里的，Docs/40/42 老坑）；`FB3309A6…` 重签 → `signtool verify /pa` 0 警告 0 错误；AOT 里按 UTF-16 搜到 1.0.131 新文案 `起的默认档`/`装机实测状态`，以及 1.0.129 两条修复的日志串 `显式写回 SKIP_SYSTEM_IPV6_CHECK`/`档2 覆盖订阅 fake-ip-range6` |
| 装机 | 两个 pid 收干净（remaining=0）→ `Add-AppxPackage` → `(Get-AppxPackage …).Version` = `1.0.131.0`，`Status=Ok` |
| 形状 | `bridge-start arm=2 …`、`clash_bridge_start 返回 rc=0 用时=142ms` |
| 流量层 | `curl -6 https://api.ipify.org` **200**、`curl -6 …/generate_204` **204**、v4 对照 **200**；AAAA 回 **`fdfe:dcba:9876::17`**（只有档 2 会改写 `dns.ipv6: true`，这一条同时反证隧道确实是 2）；活连接 `fd66:6:6600::2 → fdfe:dcba:9876::7 Established` |
| 一条不是缺陷的失败 | `api6.ipify.org` **两族同 000**（`curl -4` 也 000）⇒ 按 Docs/69 §三 的锁死口径，是"当前节点 ↔ 该站点"不通，不是档位；换德州节点可复现 200 |
| 内核读数 | `clashuwp-cli status` → `mode=rule \| log-level=info \| interface-name=(未绑)` |
| 收尾 | `disconnect` 后 `rasdial`=No connections、`nrpt=0`、`2000::/3`+池路由 **0 条**、`ClashUWP` 适配器 **0 个**、无 `debug-cmd.txt`、20 s 后无自动重拨；`session 会话结束，存活 75s encap=11081 inj=6590` |

---

## 四、对照官方：Verge 怎么让"默认值"只有一份

**证据仓库**：`clash-verge-rev` 分支 `main` @ `4337bd85758d54d7b9044b2da3c9ac1176eae1f3`（2026-10-09）
—— 与 Docs/68 §二 钉的是同一 commit，行号可直接对照。取源方式见 §七。

| # | 官方动作 | 位置（Verge main） | 我们本批对应做了什么 |
| --- | --- | --- | --- |
| ① | **权威默认只写一处**（运行配置模板硬写 `ipv6: true`） | `src-tauri/src/config/clash.rs:78` | `LocalSettings.Ipv6ArmDefault = 2` 是全仓唯一定义处；`AppSettings` 三处 `0` 全部删除 |
| ② | **缺键 = 没人表过态 = 用权威值补**，不是另写一个回落数 | `clash.rs:18-52` `IClashTemp::new()`：读回来的 map 对模板键做 `if !map.contains_key(&key) { map.insert(key, value) }`；整份文件读失败 ⇒ `Self::template()` 整套回权威值 | `ParseIpv6Arm()`：无 `ipv6` 键 ⇒ `Ipv6ArmDefault`；`settings.txt` 不存在/读失败 ⇒ 同一个常量（字段初值、局部初值、无文件分支三处都引用它，不再抄数字） |
| ③ | **应用自己的表态是 `Option<bool>`**：`None`（没表过态）与 `Some(false)`（选了关）是两种状态 | `clash.rs:376-380`（`pub struct IClash { … pub ipv6: Option<bool>, … }`） | 显式 `ipv6=0` 必须原样读回（夹具第 3 条钉住）；"认不出"不再被压成 `0` —— 旧写法正是把 `None` 压成了 `Some(0 档)` |
| ④ | **每轮把权威值钉回去 + 被挪走就出声** | `enhance/mod.rs:326`（`ipv6` 在 `CONTROL_PLANE_KEYS`）、`:331` `AuthoritativeFields`、`:339` `capture()`、`:348` `enforce()`、`:445` `snapshot_control_plane()`、`:457` `enforce_control_plane()`（`config.extend(snapshot)`）、`:379` `overridden()` | ★ **本批不做 capture/enforce**（§五记账）。我们已经是"每次 Connect 现读文件"，没有"订阅覆盖应用档位"这条路，所以缺的不是钉回，而是"越界出声"这一半 ⇒ 做了：`RollingLog.Warn("settings", …)` 带原话 |

**顺带订正一条旧断言**：Docs/68 §三 表里"Verge 的 `dns.ipv6` 跟随顶层开关、缺失时得 false"
与 `enhance/mod.rs:789` 的 `dns.get("ipv6").and_then(\|v\| v.as_bool()).unwrap_or(false)` 一致 ——
那个 `false` 是 **DNS 功能位的局部缺省**，不是"档位读不出来时该去哪一档"。
本批不能拿它当"回落 0"的官方依据；官方的缺省形状是 ②（回权威默认）而不是"回最保守档"。

---

## 五、本批明确不做（记账）

1. **capture/enforce 结构**：Verge 用"派生后 capture、每次 override 后 enforce"把 `ipv6` 钉成应用独占键。
   我们这边 `ipv6` 档位不经过订阅改写（`KernelConfig.Transform` 按档位覆盖，档 0 不碰），
   等价效果已经有了；真正缺的"外来写入挪走要出声"本批以 `Warn` 形式补上，
   但没有把 ④ 的整套形状搬过来。做不到整套就明说没做整套。
2. **`SettingsParse.Int` 的其余把手**（`EncapReturn`/`SessionReset`/`NetFollow`…）仍是"回落值写在调用点"，
   本批只收 `ipv6` 这一条，因为它已经实测分叉过一次。其余在 Docs/09 审计清单里另算。

---

## 六、改了什么与验证

| # | 文件 | 改动 |
| --- | --- | --- |
| 1 | `Vpn/LocalSettings.cs` | 新增 `Ipv6ArmDefault`（唯一定义，含官方四条依据）、`Ipv6Key`、**`ParseIpv6Arm(map)`**（缺键→默认；0..2 原样；越界/认不出→默认 + `Warn("settings", …)`）；`Ipv6` 字段初值与 `Load()` 的重置都改为引用该常量；`Load()` 的档位行改为 `Ipv6 = ParseIpv6Arm(map)` |
| 2 | `Backend/AppSettings.cs` | `Ipv6Arm` 初值、局部初值、无文件分支 `Apply(…)`、解析行**四处 `0` 全删**；`Ipv6Key` 改为 `LocalSettings.Ipv6Key` 的别名；`NormalizeIpv6Arm` 越界回落到 `Ipv6ArmDefault`；订正那句说反了的「为什么这不叫两套真值表」 |
| 3 | `Frontend/MainComponent.cs` | `UseState(0)` → `UseState(LocalSettings.Ipv6ArmDefault)`；`catch` 分支注释同步（读不到时画默认档，与插件侧同值） |
| 4 | `Tools/DnsConfigTest/Program.cs` | 新增 `Ipv6ArmFallback()` 7 条判据（下表） |
| 5 | `Docs/22` | §3 表后补一段：默认档抬到 2 时前台那份回落没跟着抬，缺省与回落现住 `LocalSettings.ParseIpv6Arm`，指向本文件 |
| 6 | `Docs/70` | §一 那句「默认档真的生效」加限定：只对插件侧成立，前台读数是 0（本文件 §二） |
| 7 | `Docs/71`（本文） | 建档 |

新夹具 7 条：

| 判据 | 断言 |
| --- | --- |
| 缺 `ipv6` 键 → 默认档 2 | 不再回落 0（就是装机上分叉的那一格） |
| 默认档只有一个定义处 | `LocalSettings.Ipv6 == Ipv6ArmDefault`（字段初值与常量同值，不靠抄） |
| 显式 0/1/2 原样 | 主动回档仍然有效，回落改动没吃掉显式的 0 |
| 同键多行最后一行胜出 | `ipv6=1\nipv6=0` ⇒ 0（追加语义不变） |
| 越界值 → 2（不是 0） | `3`、`-1` |
| 认不出/空值 → 2（不是 0） | `abc`、`ipv6=` |
| 键名与区间只有一处定义 | `Ipv6Key=="ipv6"`、区间 `0..2` |

**离线与构建**：`Tools\run-offline-tests.cmd` → **474 PASS / 0 FAIL**（467 + 新增 7）；
`dotnet build -c Debug -p:Platform=x64` → 0 警告 0 错误；`-c Release` 同。

**装机（1.0.132.0，2026-10-28 02:01:41–02:04:36）**：

产物链路与 1.0.131 同（uwp-bridge `-Target Publish` → `AppPackages\ClashUWP_1.0.132.0_x64_Test\` →
`signtool /fd SHA256 /sha1 FB3309A6…` 重签 → PS 5.1 `Add-AppxPackage`）：

| 判据 | 实物 |
| --- | --- |
| 包与签名 | `Get-AuthenticodeSignature` = `Valid / Signature verified`，签名者 `FB3309A61DD6B50D5E08B1F870C448581C4DBEC2`；解出的 `AppxManifest.xml` 里 `Version="1.0.132.0"`；装机后 `(Get-AppxPackage …).Version` = **1.0.132.0**，插件日志 `version=8B8CC4F4.ClashUWP/1.0.132.0` |
| 新代码真进了包 | 在包内 `ClashUWP.dll` 用 `rg -a --encoding utf-16le` 搜到本批新写的两条字面量：`回落默认档` ×1、`ipv6 值不在` ×2（AOT 下字面量是 UTF-16） |
| **分叉那一格被修掉** | `settings.txt` 82 行、`grep -c ipv6` = **0**（一条 `ipv6=` 都不写）。同一时刻两侧读数：`02:01:41.901 pid=6136 app I appsettings … ipv6=2` ↔ `02:02:01.404 pid=20252 plugin I session … ipv6=2 v6include=[2000::/3,fdfe:dcba:9876::/64]` ⇒ 前台与插件读的是同一个数，缺省不再一格 0 一格 2 |
| 形状与桥 | `02:02:00.587 routes arm=2 … v6in=[2000::/3,fdfe:dcba:9876::/64] v4ex=40 v6ex=3`；`02:02:01.407 bridge-start arm=2 设 SKIP_SYSTEM_IPV6_CHECK=1`；`02:02:02.475 clash_bridge_start 返回 rc=0 用时=262ms` |
| 流量层（默认档，装机后第一次拨号） | `curl -6 https://www.gstatic.com/generate_204` → **204**，0.585 s |
| 内核日志级别 | 本轮**没有**抬过 debug，`config.kernel.yaml` 第 6 行 `log-level: info` 即其形；收工后 `clashuwp-cli status` 因常驻桥端口不关而 `TaskCanceledException`（5 s 超时，`02:04:36 cmd status` / `02:04:41 E mihomo-status`）——这是 Docs/61 记过的拆隧道后三态之一，不是本批新缺陷，所以 **log-level 这次只有配置形状读数、没有 REST 读回** |

**收尾判据（02:02:35 断开后，02:04 复跑）**：`rasdial` = `No connections`（断开当刻与 1m49s 后各一次，两次同读）；NRPT 规则数 **0**；`2000::/3` 与 `fdfe:dcba:9876::/64` 路由 **0 条**；`ClashUWP` 适配器 **0 个**；`LocalState\debug-cmd.txt` **不存在**；日志在 02:02:35 之后无 ` I connect ` 行 ⇒ **无自动重拨**。插件侧四条同刻齐：`physical-watchdog-plugin 停止（平台回调 Disconnect）`、`net-follow 停止…events=1 actions=0`、`toast 已撤下状态通知`、`session 会话结束，存活 34s encap=1623 inj=1549 keepAlives=0`。
`settings.txt` 保持**无 `ipv6` 键**的默认档状态，未残留本批的临时写入。

**诚实边界**：越界值（`ipv6=3`）回落默认档 + 出声这条**只有夹具钉住，本批没上机**——
往 `settings.txt` 追加越界行要在真拨号窗口里验才有一手意义，而那条键会改动用户现存的把手文件，
本批没有拿到那次窗口的授权，故不写。

---

## 七、证据取法（本轮踩到的网络实况，记下来免得下次白试）

| 做法 | 结果 |
| --- | --- |
| `git clone https://github.com/clash-verge-rev/…` | **失败**：`Recv failure: Connection was reset`（本机无全局 v6 会话、github 直连时通时断，memory「Local network baseline」） |
| `curl https://raw.githubusercontent.com/…` | **失败**：`000`（连不上） |
| `curl https://api.github.com/repos/.../commits/main` | **成功**：拿到 `sha=4337bd85…`、`date=2026-10-09T02:03:12Z` ⇒ commit 钉得住 |
| `curl https://cdn.jsdelivr.net/gh/clash-verge-rev/clash-verge-rev@main/<path>` | **成功**：逐字取回 `config/clash.rs`(14,903 B)、`enhance/mod.rs`(66,138 B)、`enhance/tun.rs`、`config/verge.rs`、`config/mod.rs` ⇒ 本批所有行号出自这些文件 |
| ★ 注意 | jsDelivr 的 `@main` 不告诉你是哪个 commit；本轮是用 `api.github.com` 先钉 SHA 再引用。下次复用时**先钉 SHA**，否则行号可能对不上 |

---

## 八、教训

1. **"调用同一个函数"不等于"回落同一个值"**：本批的缺陷整个藏在 `SettingsParse.Int` 的第三个实参里。
   凡共享的工具函数带 `fallback` 参数，就要问"每个调用点传的是不是同一个东西"，
   而不是"大家是不是都调了这个函数"。
2. **抬默认值是一次全局改动，不是一行改动**：1.0.130 只抬了字段初值，没扫"还有谁持有这个默认值"。
   ⇒ 收口方式是让默认值只有一个定义处（常量），其它位置只引用；抄数字的位置就是漏抬的位置。
3. **注释里的"为什么这不叫两套真值表"最危险**：这条注释不但没拦住分叉，还把分叉的形状说成了安全。
   判据只能落在能编译、能跑的东西上（夹具第 1 条就是那格分叉）。
4. **"越界压成最保守档"不是官方口径**：官方的形状是"当没人表过态 → 回权威默认，并让挪走的人看得见（出声）"。
   把 `None` 压成 `Some(0)` 在 0 是默认档时看着无害，抬档之后它就是"偷偷把用户放到已知泄漏档"。
5. **装机验证要读两边**：Docs/70 §一 那次"默认档生效"只读了插件侧日志；
   前台那行 `appsettings … ipv6=` 一直是现成的对照读数，当时没抄进来。
   ⇒ 凡"档位/开关是否生效"的判据，前台与插件两条读数要同刻一起抄。
