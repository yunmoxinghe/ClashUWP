# mihomo 集成方案（路线 A · 进程内 DLL）与 ABI 设计

> 状态：方案已确认可行（基于 mihomo v1.19.32 + sing-tun v0.4.27 源码通读），
> 正在落地 Go 桥接层。本文是实现的规格与证据留档。

---

## 1. 目标

把 mihomo（Clash.Meta）作为**进程内原生 DLL**接进 UWP VPN 插件，替换
`ClashVpnPlugin.Encapsulate` 里的 ICMP echo 假内核：

```
IVpnPlugIn.Encapsulate（拿到 L3 原始 IP 包）
        │  put_packet()
        ▼
  ┌─────────────────────────┐      get_packet()      ┌──────────────┐
  │   Go c-shared DLL       │ ──────────────────── ▶ │  AppendVpn…  │
  │  mihomo gvisor tun 栈   │                        │  Inject 回本 │
  └─────────────────────────┘                        │  机协议栈    │
        （进程内 netstack 终结 TCP/UDP → 代理出网）    └──────────────┘
```

数据面不走 outer tunnel transport，与现有 `ClashVpnPlugin` 的进程内注入架构同构。

---

## 2. 架构结论（关键源码事实）

### 2.1 `Tun` 接口只是 `io.ReadWriter + Close`

sing-tun `tun.go`：

```go
type Tun interface {
    io.ReadWriter
    Close() error
}
```

→ 我可以自己实现一个「虚拟 Tun」，背后是内存队列桥接到 C#，**不需要真实 wintun 设备**。

### 2.2 gvisor 栈要求 `GVisorTun`（`stack_gvisor.go`）

```go
type GVisorTun interface {
    Tun
    WritePacket(pkt *stack.PacketBuffer) (int, error)
    NewEndpoint() (stack.LinkEndpoint, stack.NICOptions, error)
}
```

`NewGVisor` 会断言 `options.Tun.(GVisorTun)`，所以我的虚拟 Tun 还需实现
`WritePacket` + `NewEndpoint`（返回一个自定义 `stack.LinkEndpoint`）。

### 2.3 `LinkEndpoint` 的现成范式：`WintunEndpoint`（`tun_windows_gvisor.go`）

wintun 版 gvisor endpoint 的做法直接可抄，只是把「读 wintun」换成「读我的桥」：

- `Attach(dispatcher)`：起一个 `dispatchLoop` goroutine，循环 `Read()` 拿包 →
  按 `header.IPVersion` 判断 IPv4/IPv6 → `dispatcher.DeliverNetworkPacket(proto, pkt)`。
- `WritePackets(list)`：把回包逐个 `tun.Write(packet.AsSlices())`。

我的 `VirtualEndpoint` 完全照这个形状，只是包来源/去向是我自己的内存队列。

### 2.4 需要 bypass 的唯一硬点：mihomo 的 `tunNew`（Windows 走 wintun）

`listener/sing_tun/server.go` 的 `New()` 主线是对的（建 tunnel handler、DNS hijack、
路由、`NewStack("gvisor", stackOptions)`），唯独这两步写死了真实设备：

```go
tunIf, err := tunNew(tunOptions)        // server_windows.go → tun.New() → wintun
...
tun.NewStack(strings.ToLower(options.Stack.String()), stackOptions)
```

**两个可行接法（选其一）**：
- **A. 复制 `sing_tun.New` 进桥接模块**：把 `tunNew` 换成「返回我的 VirtualTun」，
  其余照抄，导入 mihomo 的 `constant` / `listener/sing` / `adapter/inbound`。
- **B. fork mihomo**：给 `listener/sing_tun` 加一个「外部 Tun 注入」开关，
  桥接模块 `replace` 到 fork。改动最小但引入 fork 维护负担。

倾向 **A**（不 fork，纯库化复用 mihomo 已导出的包）。

---

## 3. C ABI 设计（`//export` + `-buildmode=c-shared`）

```c
// 启动：传入 mihomo 配置（JSON 字节，tun.enable 须为 false）+ MTU。
// 返回 0 成功 / -1 未启动或参数错 / -2 已在运行。
int  clash_bridge_start(const char* config, int config_len, int mtu);

// 停止并释放（可再次 start）。返回 0 成功 / -1 未启动。
int  clash_bridge_stop(void);

// 投递一个「出站」L3 包（Encapsulate 抓到的原始 IP 包）。返回 0/-1（未启动）。
int  clash_bridge_put_packet(const char* data, int len);

// 拉取一个「入站」回包（要 Inject 回本机协议栈的 IP 包）。
// 非阻塞：有包返回 1 并把长度写进 *out_len；
// 没包返回 0；buffer 不足则丢弃该包返回 0。buffer 至少 1500 字节。
int  clash_bridge_get_packet(char* buffer, int capacity, int* out_len);

// 取最近一次错误的 UTF-8 描述（写入 buffer，返回写入字节数）。无错误返回 0。
// 说明：mihomo 的 log 包只有 SetLevel，无可插拔 writer，
// 原计划的 clash_bridge_set_log 日志回调无法接入，改为错误字符串接口。
int  clash_bridge_last_error(char* buffer, int capacity);
```

### 语义对应

| C ABI | 调用方 |
|---|---|
| `put_packet` | C# `ClashVpnPlugin.Encapsulate`（每拿到一个 L3 包投喂一次） |
| `get_packet` | C# 侧的轮询线程 / 或 `Encapsulate` 收尾时顺带 drain |
| `start` | 插件 `Connect` 时（读 `_config` 后、`VpnChannel.Start` 前后） |
| `stop` | 插件 `Disconnect` 时 |

> 注意 `start` 里 mihomo 的 DNS hijack / 路由均由内部 gvisVNET + 我们的
> `VpnChannel` 路由决定；`VpnChannel.Start` 的 `routes`/`namespaces` 仍由前台
> `CustomConfiguration` 下发（mihomo 侧配置与这套要保持一致，MTU ≤ 1400）。

---

## 4. Go 桥接层结构（`tools/mihomo-bridge`，新建 Go module）

```
tools/mihomo-bridge/
  main.go             // package main；//export 四个函数 + 启动时的 hub.Parse 入口
  virtual_tun.go      // VirtualTun：io.ReadWriter+Close + WritePacket + NewEndpoint
  packet_endpoint.go  // VirtualEndpoint：LinkEndpoint（抄 WintunEndpoint 形状）
  core.go             // mihomo 核心启动：hub.Parse / executor + sing_tun.New 的复制版
```

### 4.1 核心启动要点

1. `hub.Parse(config)`（或 `executor.Apply`）拉起 mihomo 的 tunnel/manager/rules/outbounds。
2. 构造 `tun.Options`（`Inet4Address` 用 `VpnPluginConfig.ClientIpV4`/网关、`MTU`、`Stack=gvisor`）。
3. 构造 `StackOptions{Tun: VirtualTun, Handler: <tunnel 的 tun handler>, ...}`。
4. `tun.NewStack("gvisor", stackOptions)` + `stack.Start()`。
5. 桥的 `put_packet` → 入队 → `dispatchLoop` 的 `Read` 取走投喂 netstack；
   netstack 回包经 `WritePackets` → `get_packet` 出队 → C# Inject。

### 4.2 待解决的小项

- `NewStack` 之后 gvisor 的 `Handler`（`sing.ListenerHandler`，实现
  `PrepareConnection/NewConnection/NewPacket`）仍要由 mihomo 的 tunnel 提供 ——
  这正是 `server.go` 里 `sing.NewListenerHandler(...)` 的产物，复制版照用。
- mihomo 配置里必须显式关掉 `auto-route`/`auto-redirect`（Windows 上那会去动系统路由，
  而路由归 `VpnChannel` 管）；只留 `tun` + `stack: gvisor` 的纯内核形态。
- gvisor 栈需要 mihomo 的 geodata / 规则文件（`geoip.dat`/`geosite.dat`/规则集）在进程内
  工作目录，随 MSIX 打包或运行时指定路径。

---

## 5. C# 侧接入

`ClashUWP.Vpn/ClashVpnPlugin.cs`：

1. 新增 `MihomoBridge.cs`（P/Invoke 声明 `clash_bridge_*`，`LibraryImport`/`DllImport`，
   AOT 下用 `[DllImport]` + 源生成 `[LibraryImport]` 兼容形态）。
2. `Connect`：`VpnChannel.Start` 成功后调 `clash_bridge_start(configXml)`。
3. `Encapsulate`：把 `packet` 调 `clash_bridge_put_packet`；收尾 drain `clash_bridge_get_packet`
   回包逐条 `Inject`。
4. `Disconnect`：`clash_bridge_stop`。
5. DLL 随包分发：`clash_bridge.dll` 打进 MSIX，`DllImport("clash_bridge")` 靠 Windows
   加载器从包目录解析（同 `ClashUWP.dll` 的查找路径）。

> 注意 mihomo 是 amd64；arm64 需要 `GOARCH=arm64` 重编（本仓库 slnx 只保留 x64/arm64）。

---

## 6. 环境现状

| 项 | 状态 |
|---|---|
| Go | 1.27.1 装于 `C:\Users\gold\AppData\Local\Programs\Go`（不在 PATH） |
| gcc（cgo 前置） | winlibs x86_64-posix-seh-gcc-16.2.0 zip，**下载中** |
| Go 模块代理 | `GOPROXY=https://goproxy.cn,direct`（github.com 直连不稳，走镜像） |
| mihomo 源码 | v1.19.32 已取到 `GOMODCACHE/github.com/metacubex/mihomo@v1.19.32` |
| mihomo 模块路径 | `github.com/metacubex/mihomo`（**小写 metacubex**） |
| sing-tun | v0.4.27（mihomo go.mod 指定） |

---

## 7. 落地步骤

1. ~~摸清 mihomo tun/hub 库化接口~~（本文第 2 节）
2. ~~确认 gcc 可装~~（winlibs.zip，下载中）
3. 解压 gcc → 设 `CGO_ENABLED=1` + `CC`/`CGO_CC`
4. 新建 `tools/mihomo-bridge` 模块，`replace`/`require` mihomo@v1.19.32，`go mod tidy` 拉全依赖
5. 写 `virtual_tun.go` + `packet_endpoint.go`（gvisor LinkEndpoint）
6. 写 `core.go`（复制 `sing_tun.New`，注入 VirtualTun）+ `main.go`（`//export` ABI）
7. `go build -buildmode=c-shared` 出 `clash_bridge.dll` + `.h`
8. C# `MihomoBridge.cs` P/Invoke + `ClashVpnPlugin` 接线
9. MSIX 打包（DLL + geodata + 规则）→ VS 真装 → 平台日志验证