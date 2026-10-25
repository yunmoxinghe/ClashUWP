# mihomo 内核内存实测与降内存路径（1.0.117）

## 缘起

哥哥提"Go 后端太重，常驻 50 MB，换 Rust 咋样"，随后追问"为什么 Clash Verge 只要 3-4 MB"。上一轮我给的"生态经验推得"对比表被要求"读一次机器实时数据"，实测数据推翻了 "Verge 3-4 MB" 的观察，也钉死了 "换 Rust 减不了内核内存" 的结论。本篇把数据、结论、可动的杠杆一次落地。

## 一、实测数据

时间：2026-10-24，Verge Rev 运行中，VPN 已连接。

### 1.1 三条进程内存

```
Id     ProcessName             WS_MB    PrivMB
6552   verge-mihomo             53.4     94.7   ← mihomo 内核本体
14860  clash-verge              48.8     12.7   ← Tauri UI 主进程
4744   clash-verge-service      11.6      4.9   ← 提权服务（SYSTEM）
                                -----
                    合计 WS：  113.8 MB
```

`ClashUWP.exe` 当时未运行，未直接对比。WebView2 的 `msedgewebview2.exe` 子进程未计入（通常每条 20-100 MB）。

### 1.2 进程树（`Get-CimInstance Win32_Process`）

```
services.exe (1220)
└─ clash-verge-service.exe (4744)         11.5 MB   SYSTEM 权限，无窗口
   └─ verge-mihomo.exe (6552)             54.0 MB   ★ mihomo 内核，藏在 service 子进程
explorer.exe (18320)
└─ clash-verge.exe (14860)                46.7 MB   有窗口
```

**哥哥看到的 "3-4 MB" 是任务管理器 "进程" 页把 SYSTEM 权限的后台进程折叠了**（"Clash Verge Service" 那一组默认不展开子进程），不是真实占用。要看真数：任务管理器 → **"详细信息"** 页 → PID 6552；或管理员权限打开任务管理器。

## 二、钉死的结论

1. **mihomo 内核本体 ≈ 54 MB WS / 95 MB 私有**，是行业常态，不是 ClashUWP 独有的问题
2. **换 Rust 减不了这块**：Verge Rev = Rust 前端 + 同一个 Go mihomo，实测反证
3. **拆 sidecar 只是让任务管理器数字好看**，整机内存不变或更高（Verge 三条 114 MB > ClashUWP 一条 ~50 MB）
4. **Tauri/Rust 前端本身也不轻**（`clash-verge.exe` 48.8 MB），换 UI 框架省不了

## 三、降内存路径（按预期收益/风险比排序）

以下每条的"预期收益"数字全部是**生态经验推断级**（沙箱无外网没法查 mihomo 官方 profile），真实数字必须上机 Process Explorer 盯 Working Set 才能钉死。

### A. rule-providers 精简 + lazy-load（预期 5-20 MB，最大杠杆）

机场订阅往往塞十几条 rule-provider（Apple/Google/Microsoft/OpenAI/…），全量加载 = 内存大头。

- `rule-providers.*.lazy-load: true`（首次命中才载入）
- 砍用不上的规则集（Apple 分流对中国用户往往没必要）

**风险**：低。**前置核实**：订阅合成器在哪、rule-providers 是不是订阅下发的（哥哥的机场配置说了算）。

### B. DNS 收敛（预期 3-8 MB）

- `dns.enhanced-mode: redir-host` 而不是 `fake-ip`（fake-ip 有 ip↔domain 反查表）
- `dns.cache-size: 512`（默认 4096）
- `nameserver` 精简到 2-3 个

**风险**：中——fake-ip 是很多机场的推荐模式，改成 redir-host 可能影响分流。**前置核实**：Docs/23 定过 DNS，有没有硬约束。

### C. TUN 栈切换（预期 5-10 MB）

- `tun.stack: system` 或 `mixed`（gvisor 用户态协议栈最肥）
- 二选一：`gvisor` **或** `sing-tun` 只留一套（现在 `go.mod` 两套都链了）

**风险**：中高——ClashUWP 是 VPN Plugin 路径，TUN 数据面走向要先看。**前置核实**：`ClashUWP/Vpn/` 和 `Tools/mihomo-bridge/main.go` 里 TUN 是给谁用的、能不能切 system 栈。

### D. sniffer 关掉（预期 2-5 MB）

`sniffer.enable: false`（TLS/HTTP 嗅探状态机）。

**风险**：中——影响 fake-ip 场景下的域名识别。**前置核实**：订阅里有没有依赖 sniffer 的分流规则。

### E. find-process-mode 降级（预期 3-8 MB）

`find-process-mode: off` 或降级到 `strict` 之外的档。

**风险**：高——Docs/20 定过应用分流，很可能强依赖进程匹配。**前置核实**：Docs/20 里"应用分流"的实现路径。

### F. build tag 裁特性（预期 5-15 MB，一次性收益）

`go build -tags` 剔掉用不上的出站：

- `no_quic`（QUIC/Hysteria/TUIC）
- `no_grpc`（gRPC 出站）
- hysteria/tuic/vless-reality 单独 tag 剔

⚠️ **`no_clash_api` 不能开**——C# 侧 KernelView/MihomoController 依赖 mihomo 控制面 RESTful API，Docs/49 才刚钉死"mihomo 只有 GET /group-delay"。

**风险**：中——影响支持的节点类型。**前置核实**：哥哥机场订阅用的出站协议清单。

### G. 按需启停 dll（预期 = 全部 54 MB 归零）

VPN 断开时 `clash_bridge_stop` → `FreeLibrary`；连接时再 `LoadLibrary` + start。

**风险**：高——
- 冷启延迟 1-3s（订阅解析 + rule-provider 下载）
- FreeLibrary 一个含 cgo 静态数据的 dll 未必干净，mihomo 内部 goroutine 可能泄漏
- 必须实测才知道能不能翻车

**Verge 也没做这条**（sidecar 常驻），说明业界公认不划算。

### H. 拆 sidecar（不推荐）

学 Verge 把 mihomo 拆成独立子进程，只是让任务管理器"看起来"轻。整机内存反而更高（Verge 三条 114 MB 实测）。**不解决问题，只是数字游戏。**

## 四、推荐执行顺序

1. **先实测 `ClashUWP.exe` 现在的占用**（哥哥启动一下我再拉一次数据），拆解 "WinUI 3 UI 基线" vs "mihomo 内核基线"
2. **A + B** 一批（rule-providers + DNS），改 config 生成器，风险低，预期收益 8-25 MB → **1.0.118**
3. **C + D** 一批（TUN + sniffer），前置核实清楚再动 → **1.0.119**
4. **E** 单独一批（Docs/20 应用分流强绑定）→ **1.0.120**
5. **F** 单独一批（build tag），要重编 `clash_bridge.dll` + 上机验节点连通性 → **1.0.121**
6. **G/H** 不做

## 五、需要请示

1. **README.md 破例放根目录**：AGENTS.md「所有文档放 ClashUWP\Docs 里」，根 README 是"项目门面"还是"文档"？我先按"门面 = 允许根目录，内容仅摘要 + 指路 Docs/60"处理。若哥哥认为 README 也算文档，可挪进 Docs/ 或删掉
2. **Docs/60 编号**：MEMORY 惯例"新开 Docs/NN 或并入现有听用户定"，60 是按最新编号（59）顺延
3. **A–F 每批都是代码改动 + 版本号 bump**，动手前按哥哥偏好"先描述行为再动手"，等发话
4. **前置核实项**（订阅合成器位置、Docs/20 应用分流实现、Docs/23 DNS 约束、机场出站协议清单），要不要我先扫一遍再报具体改动清单

## 六、证据级别

- **实测级**：Verge 三条进程内存 + 进程树（本轮 PowerShell/CIM 输出）
- **推断级**：mihomo 内部各模块内存拆解（Go 运行时/规则引擎/TUN/DNS/sniffer）；A–G 各条预期收益是**生态经验值**
- **推断级**：Verge Rev 的 Tauri + sidecar 架构细节（进程树实测证实 sidecar 存在，但具体版本没查仓库）

## 七、交叉引用

- Docs/20 应用分流与会话重置（1.0.68）——E 条前置核实
- Docs/23 DNS（1.0.71）——B 条前置核实
- Docs/49 组测速修复：PUT /proxies/{group}/healthcheck 是 clash 老 API，mihomo 只有 GET /group-delay——F 条 `no_clash_api` 不能开的证据
- Docs/59 切订阅读不出来就整组删旧建新再复核（1.0.114）——最新一篇
- MEMORY: "ClashUWP 插件宿主改动交付流水线"——重编 dll 后的签名/验证流程
