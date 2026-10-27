# Docs/68 — 对照 Clash Verge / mihomo 源码核对 IPv6 三档（1.0.129）

> 批次：**1.0.129**（`Package.appxmanifest` → `1.0.129.0`）
> 触发：用户「对照clash verge修好」。
> 纪律：本批**不靠记忆**——Verge 与 mihomo 的结论全部现读源码；我们自己的旧结论同样现读重验。

---

## 一、结论先写

1. **三档的数据面语义没有问题，不需要换路线**：档 0 = mihomo 上游默认形态，档 1 = 关 v6 DNS
   并让 `parseIPV6` 清空 v6 形状，**并且**在解析层把"拨任何 v6 形状的目标"这条路整体关掉
   （★★ 这条是本节原来写反的地方，见 §四 第 6 行订正与 §十一 的 clean-room 反证：
   顶层 `ipv6: false` ⇒ `resolver.DisableIPv6=true` ⇒ 字面 v6 节点地址直接 `ip version error`），
   档 2 = 与 Verge 的 tun 模式 DNS 半边同形、再额外给内核
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
5. **流量级已验**（2026-10-27 晚，路由器「IPv6功能」开启后本机有了真 v6）：档 2 = Docs/69 §一、
   档 1 的代价与档 0 的泄漏面 = Docs/70 §四/§三；行内 `dns: {…}` 与 v6 大包仍未验（§八）。
   **降档复位也已验**：1.0.129.0 装机后档 2 → 档 0 两次连接拿到
   `arm=0 显式写回 SKIP_SYSTEM_IPV6_CHECK=0（原值 1）`（§八）。

**验证**：`Tools\run-offline-tests.cmd` → **467 PASS / 0 FAIL**；主工程 Debug / Release
（x64）各编一遍 → **0 警告 0 错误**；1.0.129.0 装机后档 2 → 档 0 两次连接（§八）。

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
| ~~`General.IPv6` **只**喂 `updateDNS`~~ ★★ **此行原判读是错的，已订正** | `hub/executor/executor.go:393-396`（`updateGeneral` 里 `resolver.DisableIPv6 = !general.IPv6`）、`hub/route/configs.go:395`（热重载同一句）、`component/resolver/resolver.go:165-166 → :69-74`（`LookupIPWithResolver` 在 `DisableIPv6` 时转 `LookupIPv4WithResolver`，字面 v6 ⇒ `ErrIPVersion`） | 同（Meta HEAD 行号差 ≤5） | **顶层 `false` 会阻止向任何 v6 节点拨号**：字面 v6 服务器地址在解析层就死（`dns resolve failed: ip version error`），域名节点也不再得到 AAAA。`updateDNS`（`:238-249` `ipv6 := c.IPv6 && generalIPv6`）只是它的**第二个**消费者。⇒ 档 1 确实把"v6 节点"整条打死，本机 clean-room 见 §十一 |
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
| 节点 v6 `/128` 排除 | 无 | 无 | **有**（1.0.126/1.0.127）——★ 但档 1 下内核拨不出 v6 外层包（§四 第 6 行订正），这条排除在档 1 是**防御性冗余**；真正用到它的是档 2 | **有**（且这条排除是必需的：档 2 允许解析 v6 节点） |
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

- `Tools\run-offline-tests.cmd` → 463 PASS / 0 FAIL（新增 6 条）；收口时 467 PASS / 0 FAIL。
- `dotnet build -c Debug -p:Platform=x64` → 0 警告 0 错误。
- `dotnet build -c Release -p:Platform=x64` → 0 警告 0 错误。
- 源码级：v1.19.32 的 4 个文件与 Meta HEAD 逐条核对（§四表格两列行号）。
- 装机 `1.0.129.0` 后走完 **档 2 → 档 0** 的两次连接（2026-10-27 01:01–01:03，同一插件进程 pid=16180）：
  - 档 2（`settings ... ipv6=2 v6pool=fdfe:dcba:9876::/64`）：
    `routes arm=2 … v6in=[2000::/3,fdfe:dcba:9876::/64] v4ex=40 v6ex=3`；
    `bridge-start arm=2 设 SKIP_SYSTEM_IPV6_CHECK=1（mihomo 官方逃生口；Verge 不设它）`；
    `clash_bridge_start 返回 rc=0 用时=241ms`；内核配置四件齐（`ipv6: true` / `dns.ipv6: true` /
    `fake-ip-range6: fdfe:dcba:9876::/64` / `inet6-address: [fdfe:dcba:9876::1/126]`）。
  - 断开后降到档 0 再连：
    `bridge-start arm=0 显式写回 SKIP_SYSTEM_IPV6_CHECK=0（原值 1），保持 mihomo 原生校验`
    —— **进程级状态跨会话复位成立**（常驻桥未重建，原值确实是上一轮留下的 1）；
    `routes arm=0 … v6in=[] v6ex=0 剔除=3`（v6 节点排除按设计丢弃）；
    `复用常驻内核（排空 137 包，配置已变 → 尝试热重载）` → `PUT /configs?force=true -> 204`。
  - 档 0 的内核配置：无顶层 `ipv6`、无 `fake-ip-range6`、无 `inet6-address`；
    出现的 `dns.ipv6: false` 是订阅原文（`config.yaml` 第 9 行），不是我们写的。
- 07:04 那次**自动订阅更新**（`subAutoHours=6` 到点，前台 pid=12892，无会话、不拨号）重写
  `config.yaml` / `config.kernel.yaml` 后再读一遍：档 0 形状仍只有订阅自带的 `dns: ipv6: false`（第 9 行），
  顶层 `ipv6` / `fake-ip-range6` / `inet6-address` 一个都没有 ⇒ "档 0 不写 v6 键"在**非拨号路径**上也成立。
- 官方 v1.19.32 二进制的环回 clean-room（§十一）：把 §四 第 6 行那句旧判读证掉，并给出档 1 的代价。
- 本机 v6 就绪度负对照（§十二.1）：软件侧全开、只缺网络侧前缀，含 Teredo/6to4/IP-HTTPS/ISATAP 与注册表。

**仍未验（不写成"已修好"）**：

★【1.0.130/1.0.131 订正·2026-10-27 晚】下表**前五行里有三行已经 closes，一张仍开着**，
下面这张表**不要整张照读**——逐行的现状见 §十二.4 与本批 Docs/70：

| 行 | 当晚状态 | 实物在哪 |
| --- | --- | --- |
| 档 2 的 v6 流量真出得去 | **已验**（两族同码 200/204，源 `fd66:6:6600::2`，出口是节点） | Docs/69 §一；默认档复验 Docs/70 §一 |
| 档 0 在 v6 宿主上的栈形态 | **已验到流量级**（隧道 v6 地址 0 条、指向隧道的 v6 路由 0 条、`Find-NetRoute` 三个全局目的地位全选 WLAN、`ping -6` 3/3、内核连接列表里没有它们） | Docs/70 §三 |
| 订阅池被改写成生效池后的实际假 v6 可达性 | **已验**（`fdfe:dcba:9876::*` 的假 v6 请求真进隧道并拿到 200） | Docs/69 §一、Docs/70 §一 |
| 行内 `dns: {…}` 订阅的池不同源 | 仍未动（本批没造该形态） | — |
| **桥内**也产出同一句 `ip version error` | **拆开算**：行为等价已实测（档 1 桥内核拨 v6 字面量节点回 **503**，同组 DIRECT 200），但那句**原文**仍只在官方独立二进制 clean-room 里出现过 | Docs/70 §四 |
| 档 1 下这 4 个 v6 节点在装机上的失败表现 | **已验**（`delay ipv6-高速-德州` → HTTP 503；`curl -6` 三站点全 rc=6；AAAA 空应答；`/128` 宿主路由在 WLAN） | Docs/70 §四 |
| v6 的 MTU/大包那一层 | 系统侧 `ping -6 -l` 四档全过（Docs/68 §十二.4），**隧道内**的大包仍未看 | — |

另一条**新增的限定**也必须挂在这一节（否则"档 0 泄漏"会被读成"什么 v6 都漏"）：
档 0 会话里 Windows 解析器对 AAAA **四个 DNS 服务器全给空应答**，而 `nslookup` 绕开解析器就能拿到真记录
⇒ 域名那一半不发 v6，是 **NRPT 根规则 + 订阅自带 `dns.ipv6: false`** 的产物，**不是 0 档给的保证**；
真漏的是**写死的 v6 字面量**（不查 DNS 那一半）。细则与全读数见 Docs/70 §三。

下表保留原文，只作历史判据用：

| 项 | 为什么没验 | 需要什么 |
| --- | --- | --- |
| 档 2 的 v6 流量真出得去 | 本机没有全局 IPv6 网络；档 2 只验过配置形状（Docs/67 §十二.6） | 一条有全局 v6 + 支持 v6 出口节点的网络 |
| 档 0 在 v6 宿主上的栈形态 | 本机无 v6 | 同上（v6 网络） |
| 订阅池被改写成生效池后的实际假 v6 可达性 | 只钉住配置形状；可达性要真 v6 出口 | 一条有全局 v6 的网络 |
| 行内 `dns: {…}` 订阅的池不同源 | 该形态按 G6 取舍整块不动，改写不会发生 | 若要收口需重做行内 YAML 改写 |
| **桥内**（链进插件的 mihomo 库）也产出同一句 `ip version error` | §十一 用的是官方独立二进制；同版本同初始化路径，但未在桥内单独观测 | 有 v6 节点的机器上看内核 warning（或本机抬档 1 + 一次拨号窗口） |
| 档 1 下这 4 个 v6 节点在装机上的失败表现（`url-test` 每轮拿一次 `ip version error`） | 需要一次被批准的拨号窗口 + 代理页/内核日志对照；本批没拨号 | 一次拨号窗口（档 1，30 s 足够） |
| v6 的 MTU/大包那一层 | 本机无 v6 出口，且 v6 不允许链路分片；从未在任何窗口观测过 | 见 §十二.2 第 6 步（真 v6 网络 + `ping -6 -l 1452` 对照） |

---

## 九、收尾判据

- [x] 代码：三处修复 + 三处归因订正；Debug/Release 0 警告 0 错误
- [x] 夹具：467 PASS / 0 FAIL
- [x] 文档：Docs/22、Docs/23、Docs/67 订正；本文件建档
- [x] 文档（同日二刷）：§四 第 6 行判读**证伪并订正**、新增 §十一（clean-room）与 §十二（负对照 + 真 v6 网络判据清单）；Docs/22 §3 表档 1 行与 §9.1 同步；记忆 `ipv6-arm-state` 同句已改
- [x] 装机：`1.0.129.0` 升级安装 + 档 2 → 档 0 两次连接的 `bridge-start` 两行实物
- [x] 收尾：断开后 `rasdial` 空、`nrpt=0`、`settings.txt` 的 `ipv6=` 末条 = 0（其后只剩 subUrl/subName 行）、无 debug-cmd 残留、无自动重拨、v6 路由 17 条全系统内置无孤儿 ifIndex
- [x] 流量级：已验（档 2 Docs/69 §一；档 1 Docs/70 §四；档 0 泄漏面 Docs/70 §三）

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
6. **判读一个开关要看它落到哪个全局变量，而不是它出现在哪个函数里**：
   `General.IPv6` 那行的错，出在我只 grep 了 `c.IPv6` 的调用点，而它的真实作用是把值**写进
   `resolver.DisableIPv6` 这个包级变量**——之后所有解析路径读的是变量，不再读配置结构体。
   这类"配置 → 进程级变量 → 到处读"的形状，grep 结构体字段必然漏；
   下次判读上游开关，先 grep 变量名（本批起 v1.19.32 的 tarball 全量 grep 已在手，
   一次 `grep -rn DisableIPv6` 就够）。

---

## 十一、本机 clean-room：把 §四 第 6 行那句反过来说的话证掉

时间 2026-10-27 07:00–07:02。工具是**官方二进制**，不是我们的桥：
`C:\Program Files\Clash Verge\verge-mihomo.exe` = `Mihomo Meta v1.19.32 windows amd64 with go1.26.8`
—— 与我们 `Tools/mihomo-bridge/go.mod` 钉的 **v1.19.32 同版本**。
工作目录 `Temp\v6cleanroom\`（**故意不进仓库**：仓库里不放自制部署/测试脚本，见 AGENTS.md 第 2 条）。

### 11.1 形状（单变量对照）

两份配置**只差顶层 `ipv6` 一行**，都**不写 `dns:` 块**（⇒ `dns.ipv6` 取上游默认 false）：

```yaml
mixed-port: 19080
# 档 A 无此行（= 上游默认 true） / 档 B 加 ipv6: false
mode: rule
external-controller: 127.0.0.1:19099
proxies:
  - { name: L6, type: socks5, server: "::1",      port: 17999, udp: false }
  - { name: L4, type: socks5, server: "127.0.0.1", port: 17999, udp: false }
proxy-groups: [ { name: GRP, type: select, proxies: [L6, L4] } ]
rules: [ "MATCH,GRP" ]
```

17999 上**没有监听**（故意），这样两条族别各自停在"最远能到的那一步"。
流量从 `127.0.0.1:19080`（mixed 口）走，目标 `www.msftconnecttest.com`（NCSI 域名，
只取内核自己的 dial 日志，不评价外网可达性）。全程环回，**不碰整机网络、不碰隧道、不写 debug-cmd**。

### 11.2 结果（逐字）

| 档 | L6（`server: "::1"`） | L4（`server: "127.0.0.1"`，同轮做对照） |
| --- | --- | --- |
| 顶层缺省（= true） | `[::1]:17999 connect error: dial tcp [::1]:17999: connectex: …actively refused`（**进到 socket 层**，单轮 10 次重试） | `127.0.0.1:17999 connect error: dial tcp … connectex: …actively refused` |
| `ipv6: false` | `[::1]:17999 connect error: dns resolve failed: ip version error`（**停在解析层**，连 `dial tcp` 都没出现） | 与上一行**逐字相同** ⇒ 单变量成立 |

⇒ **`General.IPv6` 不止喂 DNS 的取值，它决定"字面 v6 节点地址能不能被拨"**；
`dns resolve failed: ip version error` 的来源在 v1.19.32 里逐段可读：
`executor.go:396`（`updateGeneral` 里 `resolver.DisableIPv6 = !general.IPv6`）→ `resolver.go:165-166`
（`LookupIPWithResolver` 的 `else if DisableIPv6 { return LookupIPv4WithResolver(...) }`）→
`resolver.go:69-74`（`ParseAddr` 成功但 `!ip.Is4()` ⇒ `ErrIPVersion`，定义在 `:43`）。
配套的另两道门：`resolver.go:107`（`LookupIPv6WithResolver` 开头 ⇒ `ErrIPv6Disabled`，AAAA 那条）
与 `resolver.go:32`（包级变量**默认就是 `true`**，所以这个门是"配置写进来才打开"的形状）。
热重载走 `hub/route/configs.go:395` 同一句，所以我们 `PUT /configs?force=true` 那条路也吃这个门。

### 11.3 由此落到产品端的三条口径

1. **档 1 的"堵泄漏"比我原先写的更硬**：不只是"关掉 AAAA + 清空配置里的 v6 形状"，
   而是内核**拿不到任何 v6 下一跳**——字面 v6 节点与 AAAA 两条都断。
   这解释了三档对照表里档 1 为什么不需要 `SKIP_SYSTEM_IPV6_CHECK`。
2. **代价**：本机订阅（星链机场，07:04 那次自动更新后 **33 个节点**）里 **4 个节点的 server 是 v6 字面量**
   （3 个不同地址：`2602:f6f6:2:b39d::1` ×2、`2604:a00:101:2b:25::1`、`2602:f9f3:0:2::470`，
   都是 `ipv6-高速-*`，hysteria2 ×3 + tuic ×1）⇒ 抬到档 1 后这 4 条**必然不可拨**。
   更难受的是它们同时在 `自动选择`（url-test）与 `故障转移`（fallback）组里，
   每轮测速都拿一次 `ip version error`，只在内核 warning 里出声。
   以前文档把它写成"只是少一种出口"，实际是"这 4 条从候选变成永远失败的噪声"。
   **抬档默认值这件事的代价一侧要按这条算**（档 1 抬不抬是价值判断，Docs/67 §十）。
3. **门是"族别门"，不是"全局单播门"**：`DisableIPv6=false` 时字面 v6 目标照样放行
   （`resolver.go:169-171` 直接返回解析出的地址，不判 `2000::/3`）。
   ⇒ 档 2 在**没有真 v6 出口的宿主**上会照常去拨 v6 节点，然后在 OS 层失败（unreachable 类），
   不会像档 1 那样在解析层就拒。这条是档 2"形状立得住 ≠ 流量出得去"的机制解释。

**外推边界**（不写成已验）：clean-room 用的是**独立二进制**，不是我们的 `clash_bridge` 里那个
链接进插件的 mihomo 库。同一 v1.19.32 源码、同一 `executor` 初始化路径，语义应当一致，
但"桥内 `ipv6: false` 也产出同一句 `ip version error`"要在有 v6 节点的机器上看内核日志才算闭合。

---

## 十二、本机 v6 就绪度负对照 + 等一条真 v6 网络时的判据清单

### 12.1 本机今天到底缺什么（2026-10-27 07:05–07:07 实测）

| 维度 | 读数 | 含义 |
| --- | --- | --- |
| v6 地址 | 6 个：5× `fe80::/64` 链路本地 + 1× `::1` | **0 个全局单播** ⇒ 插件侧 `face=None` 与设置页"全部非全局"一致 |
| GUA 计数 | `raw2xxx=0`（含排除 Teredo `2001:0::/32` 后仍 0） | 不是判据挑漏，是真没有 |
| v6 默认路由 | 无（17 条 v6 路由全是 `fe80/ff00/::1`，`Publish=No` 全部系统内置） | 无 v6 出口 |
| 路由归属 | 无孤儿 ifIndex（除环回） | **没有死接口幽灵路由**（`project-orphan-nrpt-machine-blackout` 那条雷本轮为零） |
| v6 DNS 服务器 | 各接口都是 `fec0:0:0:ffff::1..3` | 这是 Windows 的**占位符**（ fec0 是废弃的站点本地地址），不等于路由器发了 RDNSS ⇒ 网络侧没下发 v6 DNS |
| NRPT | `Get-DnsClientNrptRule` = **0 条** | 拆会话拆干净了 |
| 转换技术 | Teredo `Type=disabled / State=offline`；6to4、IP-HTTPS、ISATAP 适配器 `Not Present`/default | **本机连"人为造一个 GUA"都没有现成通道**；要 v6 必须网络侧给 |
| 协议栈开关 | `TCPIP6\Parameters` 无 `DisabledComponents`/`Disabled`，各接口无 `EnabledLevel` 覆写；`Get-NetIPInterface -AddressFamily IPv6` 在 WLAN/环回/Default Switch 上 `Connected` | **栈是全开的**，缺的只是 RA/前缀 ⇒ 软件侧无待办，`ipv6-arm-state` 里"抬不抬档"的讨论不必再带"栈可能被禁用"这条 |
| 主动探测 | `TcpTestSucceeded=False` 且 `SourceAddress` 空：Cloudflare `2606:4700:4700::1111`、阿里 `2400:3200:b100::1`、Google `2001:4860:4860::8888`（53） | 三连全无可路由源地址 ⇒ 流量级在本机**证不了任何正向结论**，与 Docs/67 §十二.6 的口径一致 |
| MTU | WLAN 1500；我们的隧道 `mtu=1400`（会话日志） | v6 首部长 40 B、且 v6 不允许中间分片 ⇒ 真 v6 网络上要**单独看这一层**（见 12.2 最后一条） |

**结论**：本机对 IPv6 的唯一缺口是"网络侧不给前缀"。所以能测的都已测完，
剩下三项（档 2 流量、档 0 在 v6 宿主的栈形态、改写池后的假 v6 可达性）**必须换网络**，
换机器、换软件配置都造不出来。

★【同日 21:15–21:25 订正】"必须换网络"这句作废：**不用换网络，缺的那一环是路由器上的一个开关**。
"网络侧不给前缀"的直接原因是 TP-LINK 后台「IPv6功能」处于**关闭**，不是 ISP 不发前缀；
开启后本机立刻拿到真 GUA，12.2 整张清单当场从"等网络"变成"本机可测"。实物见 **12.4**。

### 12.2 拿到一条真 v6 网络后的判据清单（照做即可，按顺序）

1. 先确认网络侧真给了：`Get-NetIPAddress -AddressFamily IPv6` 里有 `2xxx:` 开头的 GUA、
   `Get-NetRoute -AddressFamily IPv6` 里有 `::/0`、`Get-DnsClientServerAddress -AddressFamily IPv6`
   里有**真实** v6 DNS（不是 `fec0:0:0:ffff::`）。缺任何一条 → 后面全是白测。
   手机侧对照：APN 协议要 `IPv4/IPv6`，光猫桥接 + 路由做 PD（`netsh interface ipv6 show interfaces` 的 MTU 要含 v6 分片缓冲）。
2. **档 0** 先看"上游默认"实物：内核配置里应无顶层 `ipv6`、无 `fake-ip-range6`、无 `inet6-address`，
   而 `Get-NetTCPConnection`/`netstat` 上 mihomo 的 tun 应有默认 `fdfe:dcba:9876::1`；
   同时 `plugin.log` 的 `泄漏面读数 face=Global`（这时才会第一次出现 Global 实物）。
3. **档 1** 的正向判据从"配置形状"升级成"行为"：订阅里若还有 v6 字面量节点，
   内核日志应出现 `dns resolve failed: ip version error`；应用侧该节点测速失败但**其它节点不受影响**。
4. **档 2** 三层依次验：
   - 形状层（本机已验完，Docs/67 §十二.6）；
   - 地址层：`2000::/3` 与 `fdfe:dcba:9876::/64` 在隧道、GUA 出现在宿主接口、隧道 v6 仍在；
   - 流量层：`curl -6` 走一个 v6-only 目标（如 `https://[2400:3200:b100::1]` 类）经代理成功，
     且**关掉档位后同一请求失败**——这条"关掉就失败"才是档 2 的真增益证据。
   - 假 v6 池：`nslookup -type=AAAA` 一个域名，回到的是 `fdfe:dcba:9876::/64` 内地址，
     且该地址的路由在隧道（这才证明 §七.2 的"池单一来源"真闭环）。
5. **Teredo 假阳性检查**（新加的必查项）：若这台机器启用了 Teredo，它会拿到
   `2001:0000::/32` 的地址，**能通过我们 `(b[0]&0xE0)==0x20` 的 GUA 判据** ⇒
   `face=Global` 可能是隧道套隧道的假面。判法：`Get-NetAdapter` 里出现
   `Teredo Tunneling Pseudo-Interface` 且状态 Up 时，读数要标注来源接口，不能直接当"宿主有公网 v6"。
6. **MTU/大包层**（本轮清单里唯一没人验过的一层）：v6 不允许链路上分片，
   隧道 1400 之外还要看节点封装开销。判法：`ping -6 -l 1452 <v6 目标>` 与经隧道的一次同样请求对比，
   只出不去而日志无 `ip version error` ⇒ 优先怀疑 MTU，不是档位。

### 12.3 联网复核（本轮把 §二 的两个仓库结论又现读了一遍）

| 项 | 本轮实际做法 | 结果 |
| --- | --- | --- |
| mihomo 语义 | 拉 **v1.19.32 全量 tarball** + Meta HEAD 定点文件，本地 `grep -rn` | §四 表 7 行里 **6 行成立**，第 6 行证伪并已订正（§十一） |
| Verge 从不设 `SKIP_SYSTEM_IPV6_CHECK` | `git clone --filter=blob:none --sparse`（main @ `eefd0258`）后全仓 `grep` | 0 命中，**结论保持** |
| 我们用 `gh api` 代码搜索当判据 | `SKIP_SYSTEM_IPV6_CHECK`/`DisableIPv6` 在 mihomo 仓 `total_count=0`，而两个符号都在源码里 | ★ **GitHub 代码搜索 API 对本仓不可信**，不能作为"某符号不存在"的证据；今后一律 clone/tarball 后本地 grep（本轮的 §四 订正就是这条教训的直接产物） |
| 上游最新版 | v1.19.33 已存在（`config.go`/`utils.go` 行号与 32 有 ≤11 行漂移） | 我们钉的是 **v1.19.32**，与装机 Verge 二进制同版本；升级前 §四 那 7 行要重读一遍 |

### 12.4 缺口定位并补上：路由器「IPv6功能」原本关闭（2026-10-27 21:15–21:25 实测）

**先记一条我自己的误判。** 本轮早些时候我说过"路由器 LAN 侧 IPv6 是开着的"，依据是网关的
链路本地地址 `fe80::623a:7cff:fe8d:81d1%22` 能 ping 通（3/3、1–2 ms）。**这条推论不成立**：
只要本机 TCP/IP v6 栈开着，链路本地地址就会存在于网桥上，它证明不了路由器那个功能开关的状态。
后台实物是**关闭**。教训：**链路本地可达 ≠ 网络侧提供 IPv6**，判网络侧只看两样——RA 给的 GUA 和 `::/0`。

**设备**：TP-LINK TL-WDR7660 千兆版，软件版本 `2.0.38`，后台 `http://192.168.0.1/`
（页面在用户 Edge 收藏夹里，凭据自动填充；全程用 UIA 读数，没有手输密码）。

| 时点 | 位置 | 读数（UIA `IsSelected`） |
| --- | --- | --- |
| 改前 | 路由设置 → IPv6设置 → 基本设置 | `ipv6On=False` / `ipv6Off=True` ⇒ **IPv6功能=关闭** |
| 改后（点「开启」→ 保存） | 同上 | `ipv6On=True`；其余保持默认：连接模式=默认、获取方式(前缀)=自动、获取IPv6 DNS=自动获取、内网IPv6源=开启、租期=1440 |

**开启后路由器自己报的实物**（同页下方状态区，改前这些栏位是空的）：

| 项 | 值 | 含义 |
| --- | --- | --- |
| WAN IPv6 地址 | `240e:305:298b:d300:623a:7cff:fe8d:81d2/64` | 电信真 GUA，**获取协议 = DHCPv6** ⇒ 光猫/ISP 一直在发，只是路由器没接 |
| 内网 IPv6 前缀（PD） | `240e:305:298b:d310::` /64 | 路由器拿到委派前缀并开始发 RA |
| LAN 口全球地址 | `240e:305:298b:d310:623a:7cff:fe8d:81d1/64` | 与本机默认路由的下一跳一致 |
| IPv6 DNS | `240e:40:8000::10` / `240e:40:8000::11` | 下发给 LAN，不是 `fec0:0:0:ffff::` 占位符 |

**本机随之拿到的实物**（12.2 第 1 步四判据全绿）：

| 判据 | 读数 |
| --- | --- |
| GUA | WLAN(ifIndex 22) 3 个：`240e:305:298b:d310:3f8e:1b2f:2ba:5e97/64`（SLAAC）、`…:b4ec:6ded:8839:5d84/128`（隐私临时）、`240e:305:298b:d310::1000/128`（**Dhcp**） |
| `::/0` | `netsh interface ipv6 show route`：`No Manual 256 ::/0 22 → fe80::623a:7cff:fe8d:81d1` ⇒ 上一轮 12.1 的"17 条全是链路本地"归零 |
| 真 v6 DNS | `Get-DnsClientServerAddress` IPv6 家族：`{240e:40:8000::10, 240e:40:8000::11}` |
| AAAA 解析 | `Resolve-DnsName www.baidu.com -Type AAAA` → `240e:83:205:1cd:0:ff:b0b8:dee9` 等 2 条 |
| v6-only 目标 | `curl -6 https://api6.ipify.org` 回 `240e:305:298b:d310:b4ec:6ded:8839:5d84` ⇒ **源地址就是本机 GUA**，不是转换技术 |
|  ICMPv6 连通 | `ping -6 2400:3200::1` 3/3、7 ms（改前是 `transmit failed. General failure`） |
| IPv4 未受影响 | `curl -4 https://www.baidu.com` → `http=200 time=0.107`；路由器保存后无重启、无掉线 |

**MTU 那一层顺手也验了**（12.2 第 6 条，本轮之前无人验过）：WLAN `Link MTU 1500`。
`ping -6 -l` 4 轮对照 `2400:3200::1`：1432（包 1472）4/4、1452（1492）4/4、1460（1500）4/4、
1500（1540，超链路 MTU，由本机自己分片）4/4 ⇒ **这条路径没有 PMTU/分片黑洞**。
第一遍 1452 曾报 50% 丢包，复测同参数 4/4 通过 ⇒ 那是刚拿到前缀后的首包/瞬态，**不是黑洞**，别据此下结论。

**由此改变的口径**：§八「仍未验」里的"桥内是否也产出 `ip version error`""档 2 流量层""档 0 在 v6 宿主的栈形态""改写池后的假 v6 可达性"四项，
**阻塞条件从"没有 v6 网络"变成"没有一次被批准的拨号窗口"**——网络侧已经不缺了，剩下的全是"碰隧道要逐条批准"。
可逆性：回同一页选「关闭」再保存即可复原（本轮只动了这一个开关）。这是**全屋生效**的改动。

