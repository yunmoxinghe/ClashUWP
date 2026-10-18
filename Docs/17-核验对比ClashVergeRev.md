# 17 · 核验对比 Clash Verge Rev（基准：桌面 Tauri GUI 客户端）

日期：2026-10-18 · 版本：ClashUWP 1.0.59（组件 ClashUWP.Vpn 1.0.29）

本篇回答两个问题：**对着 Clash Verge Rev 我们核验下来差多少**，以及**下一步计划表**。
核验口径：我们这侧直接读了 `ClashUWP/Frontend/MainComponent.cs`(1.0.59)、
`ClashUWP/Backend/SubscriptionManager.cs`、`SubscriptionProtocol.cs` 与 `Docs/12` 缺口表；
Verge 侧取官方仓库 README（`clash-verge-rev/clash-verge-rev`）功能清单。

---

## 0. 最重要的前提：不是"同一个内核没抄全"

**内核是同一个** —— 我们与 Verge 都跑 mihomo（Meta）**v1.19.32**
（`Tools/mihomo-bridge/go.mod` 与 Verge 的 `verge-mihomo.exe` 同版本号，见 `Docs/12` §7.2）。
差异**不在内核**，在封装层与平台原语：

| | Clash Verge Rev | ClashUWP |
| --- | --- | --- |
| 形态 | 桌面 Tauri GUI（Win/mac/Linux） | UWP VPN 插件（Win 独占） |
| 接管 | 系统代理 + TUN 虚拟网卡 | Windows 平台 L3 隧道（`IVpnPlugIn`） |
| 内核 | 内置 + 可切 Alpha | 内嵌 `clash_bridge.dll` 固定版 |

⇒ Verge 的「系统代理 / TUN 虚拟网卡 / 托盘 / CSS 注入」是**桌面封装特性**，
在我们这侧要么被平台隧道**架构替代**、要么在 UWP 里**不适用**——
**不能简单数功能当成"缺口"**。真正要补的是"配置增强 / 状态可操作性 / 可视化编辑"那几块。

---

## 1. 结论先说

- **核心可用能力齐了**：连得上、选节点、换订阅、切模式、看连接、看日志、浏览器一键导入 —— 基本达标。
- **真缺口约 12 项**，集中在：配置增强（Merge/Script）、规则可视化编辑、手动测速+流量图、连接强断、
  GeoData/provider 更新、IPv6、按应用分流、网络跟随、主题、WebDav、内核挂起。
- **我们占优 1 项**：泄漏防护此前实测我们 2/4、Verge 1/4（WebRTC 因窄 DNS include 干净）。
- **架构替代/N/A 约 6 项**：系统代理、TUN 虚拟网卡、CSS 注入、托盘等。

---

## 2. 逐项核验对照表

| 能力 | Verge 做法 | ClashUWP 现状（证据） | 判定 |
| --- | --- | --- | --- |
| 内核 | 内置 mihomo + 可切 Alpha | 内置 mihomo 1.19.32 固定 | ✅达标（无切核） |
| 接管方式 | 系统代理 + TUN 虚拟网卡 | 平台 L3 隧道（`IVpnPlugIn`） | 🔁架构替代（更彻底） |
| TUN 模式 | 有（system/gvisor/mixed） | 内核 TUN 关，平台承担（`KernelConfig.cs` `tun.enable:false`） | 🔁架构替代（N/A） |
| 系统代理/守卫 | 有 | 无（L3 全接管，不需要） | 🔁架构替代（N/A） |
| 托盘/快速切换 | 有 | UWP 无任务栏托盘 | ⚠️平台限制 |
| 订阅导入(URL) | 有 | 有 + 浏览器一键 `clash://`/`clashuwp://`（`App.cs`+`SubscriptionProtocol.cs`） | ✅达标（更顺） |
| 多 profile/切换 | 有 | 有（多 yaml 切换，`SubscriptionManager.ActivateAsync`） | ✅达标 |
| 订阅更新 | 有 | 有（`SubscriptionManager.UpdateActiveAsync`） | ✅达标 |
| 配置增强 Merge/Script | 有 | 无 | ❌真缺口（G11） |
| 可视化规则编辑 | 有 | 无 | ❌真缺口 |
| 选节点/分组 | 有 | 有（UI 点选 + `cache.db` 持久化，`MainComponent.SelectAsync`） | ✅达标 |
| 节点延迟显示 | 有 | 有（读 `history`，`MainComponent.MemberList`） | ✅达标 |
| 手动测速按钮 | 有 | 无（仅自动 url-test） | ❌真缺口（G3 半） |
| 活动连接视图 | 有 | 有（**只读**，`MainComponent.ConnectionList`） | ✅达标（只读） |
| 强制断开连接 | 有 | 无 | ❌真缺口（G4 半） |
| 流量图/速率 | 有 | 仅连接上下行文本 | ❌真缺口（G3 半） |
| 日志 | 有 | 有（可滚动，`MainComponent.LogPanel`） | ✅达标 |
| 运行模式 rule/global/direct | 有 | 有（按钮，`MihomoController.RunModeAsync`） | ✅达标 |
| GeoIP/GeoSite 更新 | 有 | home 已可写（`KernelHome.cs`），无更新机制 | ❌真缺口（G10 半） |
| 规则集 provider 更新 | 有 | 无 | ❌真缺口（G11） |
| 跟随网络变化换 DNS | 有 | 无（`NetworkInformation` 仍 0 处，`Docs/12` G6） | ❌真缺口（G6） |
| IPv6 全链路 | 有 | 无（`ClashVpnPlugin.cs` 写死 `ipv6:false`） | ❌真缺口（G7，碰隧道） |
| 按应用分流 ACL | 有 | 无（仅实验 `selfSplitTunnel`） | ❌真缺口（G9，碰隧道） |
| 内核挂起/恢复 | 有 | 无（阻塞 Go 工具链，`Docs/12` G5） | ❌真缺口（G5，阻塞） |
| 主题色/明暗 | 有 | 单主题 | ❌体验缺口 |
| CSS 注入 | 有 | UWP WebView 不适用 | 🔁架构替代（N/A） |
| WebDav 备份同步 | 有 | 无 | ❌体验缺口 |
| 泄漏防护 | 实测 1/4 | 实测 **2/4**（WebRTC 因窄 include 干净） | ✅我们占优 |

---

## 3. 差多少（计数）

- **达标**：~12 项
- **架构替代/N/A**：~6 项（系统代理、TUN 虚拟网卡、CSS 注入、托盘等）
- **真缺口**：~12 项（见 §2 的 ❌ 行）
- **我们占优**：1 项（泄漏防护）

---

## 4. 计划表

| 优先级 | 项 | 对应缺口 | 碰隧道 | 代价 | 建议顺序 |
| --- | --- | --- | --- | --- | --- |
| **P0** | 订阅切换/更新后原地热重载（regen config.kernel.yaml + PUT /configs）+ UI「重新加载配置」按钮 | G1 收尾 | 否 | 低 | **已完成（1.0.60）** |
| **P0** | 手动测速按钮 + 流量速率图 | G3 半 | 否 | 低 | 1 |
| **P1** | 连接强断（`DELETE /connections`） | G4 半 | 否 | 低 | 2 |
| **P1** | GeoData/MMDB + provider 自动更新 | G10/G11 半 | 否 | 中 | 3 |
| **P1** | 配置增强：Merge/Script 覆写 | G11 | 否 | 中 | 4 |
| **P2** | 规则可视化编辑面板 | — | 否 | 高 | 5 |
| **P2** | 主题切换（明暗/配色） | 体验 | 否 | 低 | 6 |
| **P2** | 网络变化跟随（先写只读探针验证事件可达） | G6 | 否* | 中 | 7 |
| **P2** | IPv6 全链路 | G7 | **是** | 高 | 8（需授权实测） |
| **P2** | 按应用分流 ACL | G9 | **是** | 中-高 | 9（需名单来源） |
| **P3** | WebDav 备份同步 | 体验 | 否 | 中 | 10 |
| **P3** | 托盘/开机自启 | 平台 | 否 | 高 | 11（UWP 限制多） |
| **阻塞** | 内核挂起/恢复 | G5 | 否 | 高 | 需装 Go 工具链 |

> \*G6 改的是 profile/路由，量上"不碰隧道本体"，但需先在 vpnClient 进程验证 `NetworkChanged` 事件可达。

---

## 5. 核验证据（我们这侧读了什么）

- `ClashUWP/Frontend/MainComponent.cs`（1.0.59，未提交）：VPN 控制、内核运行态、mode 按钮、
  订阅面板（fetch/update/刷新）、选节点（GroupPicker/MemberList/SelectAsync）、连接视图（只读）、
  可滚动日志。**已补**「重新加载配置」按钮（订阅切/更新自动热重载）。**仍无**手动测速、**无**流量图、**无**连接强断、**无**主题。
- `ClashUWP/Backend/SubscriptionManager.cs`：`FetchAsync`/`ActivateAsync`/`UpdateActiveAsync`/`DeleteAsync`/`ListAsync` 齐全。
- `ClashUWP/Backend/SubscriptionProtocol.cs` + `App.cs`：浏览器一键导入 `clash://install-config`、`clashuwp://subscribe` 已落地。
- `Docs/12` 缺口表：G1/G2/G3/G4/G8/G10/G12 已落地或已建调试通道；G5/G6/G7/G9/G11 仍为缺口。
