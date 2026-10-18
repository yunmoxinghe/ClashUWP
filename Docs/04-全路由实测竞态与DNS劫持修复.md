# 04 · 全路由连接实测：竞态自愈、GC 保命与 DNS 劫持

日期：2026-10-14 ~ 10-15 凌晨 · 版本：ClashUWP 1.0.9 → 1.0.11（组件 1.0.3 → 1.0.4）

## 背景

1.0.9 真装（签名 MSIX、`IsDevelopmentMode=False`）后，控制面三用例全 `Connected`
（CannotFindProfile 卡点随真装解除，见 02 文档卡点 1）。但数据面仍有三个症状：

1. 连接成功后 `Injected=0`，Encapsulate 的包全部 `Unknown` 回落假内核 echo；
2. 连续连接必现 `mihomo bridge start 失败 rc=-2: bridge already started`；
3. 全路由 `connect` 后整机应用层超时（当时误判为「又断网了」，见 §4）。

## 一、桥的生命周期竞态（rc=-2 鬼状态）

**时序**（1.0.9 事件日志实测）：

```
23:44:26 Connect → Start 完成（桥启动阻塞 4s+：483KB 订阅 config.Parse + hub.ApplyConfig）
23:44:28 Disconnect 先头到达 —— 实例字段 _bridgeRunning 还是 false，clash_bridge_stop 被跳过
23:44:30 「迟到的」bridge 启动成功，泵挂到已 Stop 的 channel 上，Go 单例 b.stack ≠ nil
23:44:31 下一次 Connect 的 Start → rc=-2 already started → 回落 echo → Injected=0
```

关键认知：**mihomo 桥是进程级单例（Go `Default`），回程泵与 VpnChannel 是每连接一份**；
`VpnBackgroundTask` 每个事件可能 new 新插件实例，实例字段管不住进程级状态。

**修复**（`ClashVpnPlugin.cs`，组件 1.0.4）：

- 泵与桥的运行状态改静态：`s_bridgeGlobal / s_pumpCts / s_bridgeGate`；
- `StartMihomoBridge` 全程持锁；`rc=-2 && "already started"` 不再当失败，
  而是**复用现桥、把新泵挂到当前 channel**；
- `Disconnect` 无条件 `clash_bridge_stop`（Go 侧对未启动桥是 no-op），
  且先抢 `BoundInterface()` 快照——Go Stop 会把 boundIface 清零，现场再查必为空的；
- `Connect` 的 catch（Start 异常）里也做同样自愈，防止异常路径留下活桥；
- Go 侧（需重编 dll，待工具链）：`Start` 在 `stack != nil` 分支里也刷新物理网卡绑定
  （`rebindInterface()`），网卡切换后复用路径不再绑错。

## 二、0x800703E3：transport socket 被 GC 终结

dataface 探测两次在同一位置抛 `COMException 0x800703E3(ERROR_OPERATION_ABORTED)`，
而 selftest 三用例同窗口全部正常。共同嫌疑：`Connect` 是同步回调，局部变量
`_transport` 在 `channel.Start(...)` **调用期间**只靠 WinRT 包装的 RCW 存活，
桥启动等长阻塞里 GC 一次回收就把 socket 终结，框架正握着它 ⇒ I/O 中止。

**修复**：`Start` 之前把 channel 挂到静态 `s_activeChannel` —— 平台侧持有 transport，
channel 有静态根 ⇒ transport transitively 有根。`Disconnect`/Start 异常里释放。

**验证**：1.0.11 起 `Start 异常: 0x800703E3` 未再出现，全路由会话
`已处理 7188 包（注入 8357）`（00:34~00:39 两轮），断开后网络立即恢复。

## 三、recreate 删错 profile

`FindOurProfileAsync` 只按 `VpnPluginPackageFamilyName` 匹配，探测残留的
`ClashUWP-Probe`（Connecting 僵尸态）会被当成主 profile —— 实测 00:26
「recreate」删的是 Probe、回读的也是 Probe。修复：必须 `ProfileName == "ClashUWP"`；
`RecreateProfileAsync` 开头清扫 `ClashUWP-Probe`/`ClashUWP-Dataface` 残留。

## 四、「连接后整机断网」的真身与终态

全路由会话里 `curl https://www.google.com`、`https://dns.google` 超时，
当时读作「断网没修好」。基线对照（**未连 VPN** 时同机直连）：

| 目标 | 未连 VPN | 连上 VPN（窗口内） |
|---|---|---|
| www.baidu.com | 200 | 200 |
| www.bing.com | 302 | 302 |
| 8.8.8.8 / dns.google / google.com | 超时（本网不可达） | 超时 |

即：**隧道内直连流量正常**，「断网」的另一半其实是本网对 Google 系 IP 的封锁 +
免费订阅节点全死（`fr0528.art:21586` 直连亦 `000`）—— 兜底组 `MATCH,🐟` 命中
已故节点，海外目标自然超时。软件侧的自环断网（出站拨号被吞回隧道）已由
`BoundIface=WLAN` 绑定消除，1.0.11 实测成立。

## 五、DNS：tun.enable=false 时 dns-hijack 列表为空

`tun.dns-hijack` 在 `tun.enable=false` 时 sing-tun **不采用默认劫持列表**，
而插件把系统 DNS 下发成 `1.1.1.1/8.8.8.8`，全路由下查询必进隧道 ⇒ 解析悬空。
适配订阅 config 时显式加：

```yaml
tun:
  enable: false
  stack: gvisor
  dns-hijack:
    - any:53      # 劫持任意目的地址的 53 端口，交给 mihomo resolver
```

验证：连接窗口内 `Resolve-DnsName dns.google` 正常返回 A/AAAA。
同时移除订阅的 `port: 7890`/`socks-port: 7891`（UWP 内不做入站监听，防撞 Verge）。

## 已知问题 / 后续

1. **外置控制器 9090 无响应**（会话内 curl 127.0.0.1:9090 亦拒）：`hub.ApplyConfig`
   路径下未绑定成功，待排查；目前用 LocalState 文件回路调试，不阻塞。
2. **IPv6 泄漏**：profile 只下发 IPv4 路由与 DNS，v6 流量走物理网卡直连，
   DNS 返回 AAAA 后应用优先 v6 会造成「慢一次再回落」。需要 v6 穿透时扩展
   `assignedClientIPv6list` + `::/0` 路由。
3. **Go 侧 `rebindInterface` 改动未编进 1.0.11**：本机 C 工具链（winlibs gcc）
   下载中断（`C:\tools\mingw64.7z` 仅 12MB、解压报 Unexpected end），需补齐后
   `go build -buildmode=c-shared` 重编 `clash_bridge.dll` 再发版。
4. 免费订阅节点不可用：属数据源问题；换有效订阅后海外链路应直接可用。
5. `selftest`/`dataface` 闸门文件语义：`probe` 调试命令在 `dataface-v4.txt`
   存在时直接返回旧文——想重跑必须先删闸门（本轮实测踩过）。
