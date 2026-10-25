# ClashUWP

Windows UWP Clash 客户端 + 系统 VPN 插件。WinUI 3 前台 + `metacubex/mihomo` 内核（通过 `Tools/mihomo-bridge` cgo 桥编成 `clash_bridge.dll`，P/Invoke 加载到 `ClashUWP.exe` 主进程内）。

版本号见 `ClashUWP/Package.appxmanifest`。所有调查/设计文档见 `Docs/`（AGENTS.md 硬约束）。

## 内存占用结论（2026-10-24 实测）

- **mihomo 内核基线 ≈ 54 MB WS**（同一份内核在 Clash Verge Rev 里作为 sidecar 实测）
- **换 Rust 减不了这块**：Verge Rev 就是 Rust/Tauri 前端 + 同一个 Go mihomo sidecar，mihomo 那条一样 54 MB
- **ClashUWP 是 in-process**（UI + 内核挤同一个进程），任务管理器就一条 ~50 MB；Verge 拆三条合计 **114 MB**，反而更高
- 真正降内存的杠杆是**运行配置 + 编译 tags + 按需启停**，不是换语言

详细实测数据、进程树、降内存路径 A–H 排序、需要请示的前置核实项见 [Docs/60](Docs/60-mihomo内核内存实测与降内存路径-1.0.117.md)。
