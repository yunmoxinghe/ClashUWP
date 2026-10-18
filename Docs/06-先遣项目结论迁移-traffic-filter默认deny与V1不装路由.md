# 06 · 先遣项目结论迁移：黑洞主修换轨到 traffic filter

先遣项目 = `D:\fluentapps\repos\clashUWP研究`（`ClashUwpPoc`，105 篇 docs，收口于 2026-10-11）。
它已经把「UWP VPN 插件 + AppContainer 内 gVisor + 真 mihomo」这条路走通过一次，
判据 8（公网裸 IP 经隧道拿到 `HTTP/1.1 404 / Server: nginx`）在它手里 PASS 过。
本篇只记「对我们当前症状有效、且已经过它单变量实测」的结论，不复制它的研究过程。

---

## 0. 一句话

我们上一轮实测的失败形状 —— **curl 到百度 0.069 秒就退出码 000，不是等超时** ——
在先遣项目里是**已归因完毕**的现象，不是黑洞：
`StartWithTrafficFilter` 收到**默认构造的空 `VpnTrafficFilterAssignment`** 时，
WFP/ALE 层对全部 app 流量**默认 deny**，本地瞬败。

1.0.16 的主修就是这一条：**恒走 V2、traffic filter 传 null**。

---

## 1. 判决依据（先遣项目 docs/76 的三态单变量 A/B）

同一份 `VpnRouteAssignment`、同一份注入逻辑，只换传给 `StartWithTrafficFilter`
最后一个实参（`<tfmode>`）：

| tfmode | 传入 | GenFail | reqSeen（进隧道的包） | 判读 |
|---|---|---|---|---|
| 0 | 空 `VpnTrafficFilterAssignment` | **23** | 1 | 全被 block；那 1 个是装 filter 之前瞬间溜过的 |
| 1 | 同上但 `AllowOutbound/AllowInbound = true` | 0 | **0** | 不 block 了，但包**绕过 VPN** 走物理网卡 |
| 2 | **`nullptr`** | 0 | **24** | ✅ 全部进隧道 |

配套的 ICMP/TCP 表现：空 filter 下 ping 报 `General failure`（`IP_GENERAL_FAILURE`
= 本地 IP 层发不出去），换 TCP 抓到真实 socket 错误码 **`WSAEACCES (10013)`**
= WFP/ALE 主动 BLOCK。两者是同一个东西的两种表现，**都不是超时**。

两条必须记住的语义坑（docs/76 §5.4）：

1. `AllowOutbound=true` 的意思是「不匹配任何 filter 的流量**允许直连出网（绕过 VPN）**」，
   不是「允许进隧道」。这个语义不实测一定搞反。
2. **V1 `Start()` 静默丢弃 `Ipv4InclusionRoutes`**（docs/76 发现一，83-T0 vs 84-R1）：
   V1 臂的路由表里只有 `255.255.255.255/32`、`224.0.0.0/4`、`10.0.1.2/32`，没有 include；
   V2 臂 `+2.6s` 才出现 `192.168.21.0/24 nh=0.0.0.0 metric=256` 并命中 `if=45`。

## 2. 我们旧代码同时踩了这两条

`ClashVpnPlugin.Connect` 原先按 `DnsIntoTunnel` 分叉：

| 开关 | 旧走法 | 命中的已证缺陷 |
|---|---|---|
| `dnsInTunnel=1` | V2 + `new VpnTrafficFilterAssignment()`（空对象） | tfmode=0 = **默认 deny**，app 出站瞬败 |
| `dnsInTunnel=0` | V1 `Start(...)` | **0.0.0.0/0 根本没装上**，流量不进隧道 |

也就是说两条臂各自坏在一个不同的机制上，而上一轮实测（1.0.14，`dnsInTunnel=0` 基线）
恰好是后者 —— 这解释了「插件计数照样涨（LLMNR/组播这类链路本地流量不走路由判定），
但应用连不上外网」。旧注释里「trafficFilters 传空对象即可 —— sample 也是」是误读：
sample 的 XML 不给 `<trafficfilter>` 时**压根不调 V2**。

## 3. 1.0.16 的改动（单一变量）

`ClashUWP.Vpn/ClashVpnPlugin.cs`：

- 删掉 overload 分叉，**恒调 `StartWithTrafficFilter`**，`trafficFilterAssignment` 传 `null`；
- `namespaceScope` 传 `domains ?? new VpnDomainNameAssignment()` —— DNS 进不进隧道
  从此只影响 NRPT，不再影响路由与 filter；
- 删掉 `BuildNamespaces()`（V1 路径退役，`VpnNamespaceInfo` 官方本就标 Not supported、
  我们 AOT 下实测抛 NRE）；
- 日志改成 `StartWithTrafficFilter 完成（trafficFilter=null，include=0.0.0.0/0）`，
  让取证能一眼看到走的是哪条路。

版本链：`ClashUWP.Vpn 1.0.7`（本地 feed 重新 pack）→ 主工程 `PackageReference 1.0.7`
→ `Package.appxmanifest 1.0.16.0`。1.0.6 的校验和补齐与 `tun-sample.txt` 采样一并保留，
它们是观测件、不是本轮变量。

## 4. 迁移过来的其它结论（按可执行度排序，本轮未动代码）

1. **判据 8 的定义要照抄**：`curl` 打**公网裸 IP**（他们用 `208.67.222.222` → 真 nginx 404）。
   它把「DNS 通不通」和「隧道通不通」彻底切开，我们的测量计划已经带上 `--resolve` 版。
2. **回环自激**（docs/88/89）：core 出站不绑物理网卡就会被自家隧道抓回，
   修复 = `dialer.DefaultInterface` → `IP_UNICAST_IF`；挑网卡的硬判据是
   **TUN 网卡 MAC 为空、物理网卡必有 MAC**，只按 index 取小会绑到另一块 TUN 上
   （症状与没绑一模一样）。我们 `detectDefaultInterface` 已有排除逻辑，
   但**没有 MAC 判据**，多网卡机器上要补。
3. **url-test 组没人驱动测速**（docs/99 Bug 3，v1.19.32 源码复核成立）：
   `NewURLTest` 不起定时器，`GroupBase.healthCheck()` 只遍历 `providers`（内联 proxies 时为空）
   ⇒ 组的选中永远停在 `proxies[0]`，只有 REST API 会驱动它。我们的配置 `proxies[0]`
   = `RU高速-俄罗斯莫斯科`（离线实测 216ms 活），所以**这次不是它挡的路**，
   但「节点掉了不会切换」是真缺陷，正解是 C# 侧定时打 `PUT /proxies/{组名}/delay?url=&timeout=`，
   **不需要重编 Go DLL**（本机已无 Go/MinGW 工具链）。
4. **本机另一块 TUN 是最大的假 PASS 源**（docs/88/90/98 §5.7）：Clash Verge 的 `Meta`
   适配器会抢 /32 路由、全量劫持 UDP+TCP 53 并回 `198.18.0.x`。
   验收门 = `Get-NetAdapter` 里 Meta 消失 + 裸查隧道 DNS 超时。
   本次查机：`NRPT=0`、默认路由只有 WLAN(idx=6, metric=35)、无 verge-mihomo 进程、
   无 ClashUWP 包 —— **当前是干净环境**，适合开测。
5. **进程级状态跨 Connect 存活 / 高吞吐会换 pid**（docs/80/91/97）：任何「初始化过没有」
   式幂等都要改成「配置指纹」式；计数器只看增量。我们的 resident-bridge + 指纹已是照这条做的。
6. **IPv6 防旁路要两件事一起做**（docs/96）：下发 `assignedClientIPv6` + include `2000::/3`；
   **不要用 `::/0`**（会让 VPN 抢走 IPv4 默认路由、物理网卡 metric 40→~4265）。
   我们现在 v6 传 null，所以 v6 是漏的（本机无 v6 出口，暂不致命，发布前必须补）。
7. **注入别毒化外层传输**（docs/93）：sample 的 `Encapsulate → AppendVpnSendPacketBuffer`
   会把出站包推进外层 socket，导致 OS 拆通道（每次 Connect 全新 pid、从不回调 Disconnect）。
   我们的回程泵只往 receive 池注 gvisor 的包，路径不同，但 1.0.15 新加的
   「组播/广播不喂内核」把 Injected>Encapsulated 这条自激也一并关掉了。

## 5. 判据设计（docs/98 §4、99 §7）搬过来的三条纪律

- **四类分开报**：功能 / 臂有效（判据窗口内有无新 pid）/ 进程换代 / 丢包，互不混淆；
  功能 FAIL 优先于 INVALID，否则仪器问题会把产品缺陷盖住。
- 每个判据自问一句「**隧道压根没起来时它会不会也 PASS**」，会就加门。
- toggle 必须双向：关掉必 fail、打开必 pass，才叫因果，否则只是相关。

## 6. 本轮验证窗口要读的三处证据

1. `dataface-plugin.txt`：`Encapsulated` 是否继续增长、`NonUnicastDropped / IpChecksumFixed /
   TransportChecksumFixed` 是否非零（校验和假设的判决位）。
2. `tun-sample.txt`：`ipck=` / `tcpck=` 字段 —— Windows 交下来的包到底填没填校验和。
3. 宿主侧：`curl --max-time 8 http://208.67.222.222/`（判据 8）与
   `curl --resolve www.baidu.com:443:110.242.68.66 https://www.baidu.com/` ——
   前者通 = 数据面通，与 DNS 无关；后者用来区分 DNS 与隧道。
   10013/General failure 消失即本轮假设成立。

结论口径要求：以上三处**实测过才算结论**，未跑的写「未验证」。
