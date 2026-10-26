# Docs/68 — 对照 Clash Verge / mihomo 源码核对 IPv6 三档（1.0.129）

> 批次：**1.0.129**（`Package.appxmanifest` → `1.0.129.0`）
> 触发：用户「对照clash verge修好」。
> 纪律：本批**不靠记忆**——Verge 与 mihomo 的结论全部现读源码；我们自己的旧结论同样现读重验。

---

## 一、结论先写

1. **三档的数据面语义没有问题，不需要换路线**：档 0 = mihomo 上游默认形态，档 1 = 关 v6 DNS
   并让 `parseIPV6` 清空 v6 形状，档 2 = 与 Verge 的 tun 模式 DNS 半边同形、再额外给内核
   v6 地址（Verge 不给，因为它没有"平台隧道替宿主提供 v6"这回事）。
2. 对照过程中发现并修了**三处真问题**：
   - **`SKIP_SYSTEM_IPV6_CHECK` 会残留**：原实现只在"档 2 且本进程首次全量启动"时设 `1`，
     而常驻桥**降档不重建**（Stop → Start 只发生在升到档 2）。于是 2 → 0 之后同一进程里
     变量还是 `1`，mihomo 在无 v6 机器上会继续保留默认/订阅的 `tun.inet6-address` 与
     `dns.fake-ip-range6` —— 档 0 不再是"上游默认形态"。
     → 改为**每次连接都显式写回 `1`/`0`**（`ClashVpnPlugin.cs` 启动桥之前）。
   - **档 0 在"新建 dns 块"时仍写 `dns.ipv6: false`**：值与上游默认相同（行为无差），
     但破了"档 0 不碰 v6 键"的契约，而且让夹具无法用"输出里有没有 `ipv6`"当判据。
     → 改为**档 ≥1 才写**。
   - **档 2 的 v6 池有两个来源**（这条是本轮顺着 Verge 的"只补缺"写法查出来的）：
     订阅自带 `dns.fake-ip-range6` 时我们原样保留（文档也这么写），而
     `tun.inet6-address` 与 include 路由都由**生效池**推出。两者不同时（尤其订阅给的是
     非 ULA，如 Verge 模板的 `2001:2::/64`），内核 DNS 回的假 v6 不落在任何进隧道的
     路由里 —— 档 2 的"全链路"断在 DNS 上，而配置文件看起来一切正常。
     → 档 2 改为**统一由插件下发**：与生效池不同就改写并进 `DNS 覆盖=` 日志；
     换池只走 `settings.txt: ipv6FakeRange` / `dns-config.txt: fake-ip-range6`。
     档 1 不动它（顶层 `false` 让 `parseIPV6` 解析时清空）。
3. 订正**三处错误归因**：原文写 `SKIP_SYSTEM_IPV6_CHECK`「与 Clash Verge 同口径」——
   **Verge 从不设这个变量**（它是 mihomo 的逃生口，Verge 靠宿主真有 v6）。
   涉及 `MainComponent.cs` 的设置页文案、`ClashVpnPlugin.cs` 注释、Docs/67 两处。
4. 新增离线判据 10 条（`Ipv6VergeAlignShapes129`），把三档"该写哪些 v6 键 / 池从哪来"逐字钉住；
   最后一条顺带修掉 Docs/67 §十二.4 记的日志噪声（`routes` 行把 `剔除=` 印两遍，现只印一次）。
5. **仍未验**：v6 流量级（本机没有 v6 网络）；降档后环境变量复位的装机实物；
   档 0 在"真有全局 v6 的宿主"上的内核栈形态（本机造不出）。

**验证**：`Tools\run-offline-tests.cmd` → **467 PASS / 0 FAIL**；主工程 Debug / Release
（x64）各编一遍 → **0 警告 0 错误**。

---

## 二、证据与版本

| 仓库 | 引用 | 拉取时间 | 我们实际编译的是 |
| --- | --- | --- | --- |
| `metacubex/mihomo` | 分支 `Meta` @ `3f27ef39f8c96bbfbc8b0b9bb10f5c9ef60149d8`（2026-10-21） | 2026-10-26 | `Tools/mihomo-bridge/go.mod` 钉 **v1.19.32**；本批把 v1.19.32 的 4 个文件逐字取回核对，语义与 Meta 一致（行号差 1–11 行） |
| `clash-verge-rev/clash-verge-rev` | 分支 `main` @ `4337bd85758d54d7b9044b2da3c9ac1176eae1f3`（2026-10-09） | 2026-10-26 | —（只作对照，不编译） |

下文行号：mihomo 给 **v1.19.32 / Meta** 两列，Verge 只给 main。

---

## 三、Verge 的实际做法（逐条）

| 事实 | 位置（Verge main） | 含义 |
| --- | --- | --- |
| clash 模板硬写 `ipv6: true` | `src-tauri/src/config/clash.rs:78` | 新建配置就带顶层开关，不是"缺省靠 mihomo" |
| tun 模板**没有** `inet6-address` | 同文件 `:61-66`（enable/stack/auto-route/strict-route/auto-detect-interface/dns-hijack） | 内核 v6 地址完全交给 mihomo 默认值 + `verifyIP6` |
| `ipv6` 是 control-plane 键 | `src-tauri/src/enhance/mod.rs:308-328` | 订阅里的 `ipv6` 覆盖不了它；由应用设置（`IClash.ipv6`，`:380`）拥有 |
| tun 模式把 `dns.ipv6` 绑到顶层 `ipv6` | `src-tauri/src/enhance/tun.rs:23-33`；测试 `enhance/mod.rs:1131`（`the_ipv6_fake_ip_range_follows_the_top_level_switch`） | 顶层缺失时 `dns.ipv6` 得 false |
| `fake-ip` + `dns.ipv6` 且缺 `fake-ip-range6` ⇒ 补 **`2001:2::0/64`** | `enhance/tun.rs:43-44`；DNS 覆盖路径在 `enhance/mod.rs:785-806`（注释指明 issue #7373） | 只补缺，不覆盖订阅已有值 |
| **从不设 `SKIP_SYSTEM_IPV6_CHECK`** | 全仓 grep 0 命中 | 无全局 v6 的宿主上，`parseIPV6` 就把 v6 半边清空 |
| 没有额外 v6 路由逻辑 | — | 全靠 mihomo `auto-route`；Verge 不区分"堵泄漏/全链路" |
| UI 出口 | `locale_settings_en.json:105`（clash 设置 "IPv6"）、`:534-538`（DNS 页 "Fake IP Range (IPv6)" / "Enable IPv6 DNS resolution"） | 用户能在应用里开关，不用改订阅 |

**一句话**：Verge 的姿态是"**顶层常开、DNS 跟随、v6 地址交给 mihomo 默认、宿主没有 v6 就自然没有 v6**"。
它没有"替宿主造 v6"的需求，所以也不需要那条逃生口。

---

## 四、mihomo 的实际语义（与我们三档直接相关的 7 条）

| 事实 | v1.19.32 | Meta HEAD | 对我们的含义 |
| --- | --- | --- | --- |
| `DefaultRawConfig().IPv6 = true` | `config/config.go:488` | `:489` | **顶层 `ipv6` 缺省是开**。档 0 "不写" = 开，不是关（与 Verge 模板同值） |
| `RawDNS.IPv6` 默认 **false** | `:507` | `:508` | `dns.ipv6` 缺省不答 AAAA；档 0 由订阅原文决定 |
| `RawTun.Inet6Address` 默认 **`fdfe:dcba:9876::1/126`** | `:551` | `:552` | 订阅不写也可能有 v6 地址（前提：`verifyIP6` 过）。★ 我此前"mihomo 没有默认 inet6"的说法是错的 |
| `parseIPV6`：`!IPv6 \|\| !verifyIP6()` ⇒ 清空 `FakeIPRange6` 与 `Inet6Address` | `:1679-1684`（调用点 `:716`） | `:1680-1684`（`:717`） | 档 1 写 `false` 正是靠这条把订阅给的 v6 形状清干净 |
| `verifyIP6` 先读 `SKIP_SYSTEM_IPV6_CHECK`，命中即 `true` | `config/utils.go:205-207`（整体 `:205-222`） | `:188-206` | 官方逃生口；**Verge 不用**，我们用（理由见 §五） |
| `General.IPv6` **只**喂 `updateDNS` | `hub/executor/executor.go:105`、`:238-249`（`ipv6 := c.IPv6 && generalIPv6`） | 同 | 顶层 `false` **不阻止**向 v6 节点拨号；它只关 DNS resolver/enhancer。所以档 1 不至于把"v6 节点"整条打死 |
| fake-ip 布局：`gateway = 网络号+1`，`first = 网关+3`（网络号+4） | `component/fakeip/pool.go:161-164` | `:172-175` | 我们 `inet6 = 池网络号+1`、DNS 劫持 `::2`、首个假地址 `::4` 全部对齐 |

**两条容易读反的**：

- **档 0 的"不写"不等于"关"**。mihomo 顶层默认 true，所以档 0 在真有全局 v6 的宿主上，
  内核栈可能带着默认 v6 地址（`fdfe:dcba:9876::1/126`）。这是"订阅原文"，不是漏修；
  快照里的 `v6栈=on/off` 只反映**档位意图**，不是实测值。
- **Go 的 `IsGlobalUnicast()` 对 ULA 返回 true**，而我们的"泄漏面"判据
  `RouteInclusion.IsGlobalUnicastV6` 只认 `2000::/3`（`(b[0] & 0xE0) == 0x20`）。
  两个谓词不是一回事：前者问"mihomo 认不认为这台机器有 v6"，后者问"这些 v6 能不能
  绕过隧道上公网"。本批不改任何一方，只把区别写清（Docs/22 §5.1 的"理论可行"仍成立：
  隧道 ULA `fd66:…` 若在 AppContainer 里枚举得到，`verifyIP6` 本来就会过；SKIP 是把
  不确定性压成确定，不是唯一通路）。

---

## 五、三档对照表（Verge 只有一档可类比）

| 维度 | Verge（tun 模式） | 我们档 0 | 我们档 1 | 我们档 2 |
| --- | --- | --- | --- | --- |
| 顶层 `ipv6` | 模板硬写 `true`，control-plane 拥有 | 不写（= 上游默认 true） | **写 `false`** | **写 `true`** |
| `dns.ipv6` | 跟随顶层 | 不写（订阅决定；缺省 false） | **写 `false`** | **写 `true`** |
| `dns.fake-ip-range6` | 缺省补 `2001:2::0/64` | 不写 | 不写 | **写生效池**（默认 `fdfe:dcba:9876::/64`） |
| `tun.inet6-address` | 不写（交给 mihomo 默认） | 不写 | 不写 | **写 池网络号+1/126** |
| v6 路由 | mihomo auto-route | 不加 | `2000::/3` | `2000::/3` + 池前缀 |
| 节点 v6 `/128` 排除 | 无 | 无 | **有**（1.0.126/1.0.127） | **有** |
| `SKIP_SYSTEM_IPV6_CHECK` | **不设** | 不设（显式写 `0`） | 不设（显式写 `0`） | **设 `1`** |
| 宿主无 v6 时的结果 | 内核侧 v6 半边被清空 | 上游默认被清空 | 显式清空 | **形状立得住**（平台隧道提供 v6） |

**为什么 SKIP 只有我们需要**：Verge 的 v6 来自宿主；我们的 v6 来自**平台下发的隧道地址与路由**
（`fd66:6:6600::2` + `2000::/3`）。要让 mihomo 的 gvisor 栈在无 v6 宿主上也带 v6 地址，
只能过 `verifyIP6` 这一关。设了就必须自己收回（本批的残留修复）。

---

## 六、本批改了什么

| # | 文件 | 改动 | 为什么 |
| --- | --- | --- | --- |
| 1 | `ClashUWP/Vpn/ClashVpnPlugin.cs` | 环境变量改为**每轮连接**显式写回 `1`/`0`（放在停桥重建判断之前，常驻复用也走到）；注释订正"Verge 同口径" | 修降档残留；对齐事实 |
| 2 | `ClashUWP/Vpn/KernelConfig.cs` | 新建 dns 块时 `dns.ipv6` 改为**档 ≥1 才写**；`Transform` 里档 0 的说明改为"缺省即 true、`v6栈=` 只反映档位意图" | 契约与事实 |
| 2b | `ClashUWP/Vpn/KernelConfig.cs` | `EnsureDnsBlock` 改返回说明串；档 2 的 `fake-ip-range6` 与生效池不同就**改写为生效池**并把 `档2 覆盖订阅 fake-ip-range6=…→…` 并进 `DNS 覆盖=` 日志；新增 `SameV6Pool`（按归一形比较） | 池单一来源（§七.2） |
| 3 | `ClashUWP/Frontend/MainComponent.cs` | 设置页 2 档说明删掉"与 Clash Verge 同口径"，改成"mihomo 自己的逃生口，Verge 不设它" | 错误归因 |
| 4 | `Tools/YamlShapeTest/Program.cs` | 新增 `Ipv6VergeAlignShapes129()` 10 条判据 | 离线钉死三档写出的 v6 键与池来源 |
| 5 | `Docs/22` | §3 表档 1 的"顶层不动"改为"顶层 false（1.0.126 起）"；表后补"档 0 的不改≠关 v6"；§4.6 补 1.0.129 环境变量复位；§4 档 2 的"已有不重复插"补 1.0.129 覆盖 | 旧结论订正 |
| 6 | `Docs/67` | §十一 表格第 2 行、§十二.5 表格里的"Verge 同口径"订正；§十二.4 噪声补"已修" | 同上 |
| 6b | `Docs/23` | §6 池三层里补"订阅池不再是第二个来源（1.0.129）" | 同上 |
| 7 | `Docs/68`（本文） | 建档 | — |
| 8 | `ClashUWP/Vpn/ClashVpnPlugin.cs` | `routes` 判据行去掉追加的 `剔除={count}`，只留 `Plan.Describe()` 里的那一个 | Docs/67 §十二.4 记的噪声：`剔除=3 剔除=3` |

新夹具 10 条：

| 判据 | 断言 |
| --- | --- |
| 档 0 一个 v6 键都不写 | 新建 dns/tun 块里不含任何 `ipv6` 字样 |
| 档 1 顶层 false + dns.ipv6 false | 且不写池、不写内核 v6 地址 |
| 档 2 三件齐 | 顶层 true + dns.ipv6 true + `fake-ip-range6` + `inet6-address: [池+1/126]` |
| 档 1 压订阅 | 订阅 `dns.ipv6: true` → 输出 false |
| 档 2 抬订阅 | 订阅 `dns.ipv6: false` → 输出 true |
| 档 2 池不同源 | 订阅 `fake-ip-range6: fd99:1::/64` → 输出生效池，且 `DNS 覆盖=` 里出声 |
| 档 2 池同源（写法不同） | 订阅 `fdfe:dcba:9876::0/64` → 不改、不出声 |
| 档 1 不碰池 | 订阅 `fake-ip-range6` 原样保留 |
| `剔除=` 只印一次 | `Dropped>0` 一个、`Dropped=0` 不出现 |
| （隐含）档 1/2 的 `ipv6: true` 只出现在该出现的位置 | 无重复、不误伤 |

---

## 七、明确**不**改的差异（与理由）

1. **假 IP v6 池用 ULA `fdfe:dcba:9876::/64`，不用 Verge 的 `2001:2::0/64`。**
   ULA 不会与真实公网 v6 冲突，且与 mihomo 自己的 `tun.inet6-address` 默认值同段；
   `2001:2::/48` 是基准测试保留段，Verge 用它是因为它没有"池要能被自家 include 路由兜住"的约束。
2. **档 2 的 v6 池不沿用订阅，统一由插件下发**（1.0.129 改；原文写的是"与 Verge
   `ensure_*` 的只补缺一致"）。Verge 可以只补缺，是因为它的 tun 用 auto-route 捕获**所有**
   目的地址，池子落在哪一段都进得了隧道；我们的路由是显式 include（`2000::/3` + 池前缀），
   池子一旦落在两者之外，`inet6-address`、include 与 DNS 池就不是同一张表。
   ⇒ 与生效池不同（含非 ULA）就改写成生效池，并用 `DNS 覆盖=` 日志出声。
   用户换池的出口仍是 `settings.txt: ipv6FakeRange` 与 `dns-config.txt: fake-ip-range6`。
   残余边界：`dns:` 写成行内流式（见本节第 3 条）时这条改写不会发生（同一条 `IsInlineBlock` 取舍）。
3. **行内 `dns: {…}` 整块不动**（G6 起的 YAML 安全取舍）。这类订阅在档 1/2 只拿到
   "接口地址 + v6 路由"半边，内核 DNS 开关按订阅原文。Verge 用结构化 YAML 改写，所以它没有这个限制。
4. **档 0 在 v6 宿主上内核栈可能带 v6 地址**（上游默认）。这是"订阅原文"本身，
   不是要修的缺陷；泄漏面由路由决定（档 0 不加任何 v6 include）。

---

## 八、验证与仍未验

**已做**：

- `Tools\run-offline-tests.cmd` → 463 PASS / 0 FAIL（新增 6 条）。
- `dotnet build -c Debug -p:Platform=x64` → 0 警告 0 错误。
- `dotnet build -c Release -p:Platform=x64` → 0 警告 0 错误。
- 源码级：v1.19.32 的 4 个文件与 Meta HEAD 逐条核对（§四表格两列行号）。

**仍未验（不写成"已修好"）**：

| 项 | 为什么没验 | 需要什么 |
| --- | --- | --- |
| 档 2 的 v6 流量真出得去 | 本机没有全局 IPv6 网络；档 2 只验过配置形状（Docs/67 §十二.6） | 一条有全局 v6 + 支持 v6 出口节点的网络 |
| 降档后环境变量复位 | 要"先抬到 2 → 连一次 → 降回 0 → 再连"的时序与日志 | 一次被批准的拨号窗口，读 `bridge-start` 两行 |
| 档 0 在 v6 宿主上的栈形态 | 本机无 v6 | 同上（v6 网络） |
| 订阅池被改写成生效池后的实际假 v6 可达性 | 只钉住配置形状；可达性要真 v6 出口 | 一条有全局 v6 的网络 |
| 行内 `dns: {…}` 订阅的池不同源 | 该形态按 G6 取舍整块不动，改写不会发生 | 若要收口需重做行内 YAML 改写 |

---

## 九、收尾判据

- [x] 代码：三处修复 + 三处归因订正；Debug/Release 0 警告 0 错误
- [x] 夹具：467 PASS / 0 FAIL
- [x] 文档：Docs/22、Docs/23、Docs/67 订正；本文件建档
- [ ] 装机：`1.0.129.0` 升级安装（需批准）+ 降档复位那条日志实物
- [ ] 流量级：等一条有全局 IPv6 的网络

---

## 十、教训

1. **"与某某同口径"是最容易过期的断言**：Verge 的代码里根本没有那行环境变量，
   而我们连续三个批次把它当成事实抄进了代码注释、界面文案和两份文档。
   与 `feedback-verify-official-first` 同源：引用别人之前先读别人的源码。
2. **默认值也是合同的一部分**：mihomo 顶层 `ipv6` 默认 true、`tun.inet6-address`
   有默认 ULA —— 不写键不等于关掉能力。档 0 的"不碰"是"不改写"，不是"不生效"。
3. **进程级状态必须配对**：设了环境变量就要在降档时收回；常驻桥"不重建"的省事，
   代价是上一轮的全局状态会跨会话活着。
4. **推导值与外来值不能各留一份**：`inet6-address` 与 include 路由是从池推出来的，
   DNS 池却可以来自订阅 —— 两个来源只要不一致，"配置全都在"也能是一个断掉的档 2。
   凡有"推导"参与的值，要么统一下发、要么把外来值读回来当唯一来源；别让两处各自成立。
5. **上游的省事手法依赖它的架构**：Verge 的"只补缺"能成立，是因为 auto-route 捕获一切
   目的地址；我们靠显式 include，就得自己保证池在表里。抄做法之前先问"它凭什么敢这么做"。
