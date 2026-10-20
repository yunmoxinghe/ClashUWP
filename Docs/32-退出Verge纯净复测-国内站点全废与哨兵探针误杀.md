# 32 · 退出 Clash Verge 的纯净复测：头条判据成立，同时暴露四条问题

状态：本轮**只测不改代码**。日期 2026-10-20 06:12–06:22，用户在场合规授权（"用 uia 把 clashverge 从托盘退出，然后再测，检查有啥问题"）。
目的：Docs/31 §4.4 留的那句"可达性判据被 Clash Verge 的 `Meta` wintun 污染，不算验过"——把它做穿。
结论先说：**隧道内可达性判据成立**（境外 204 实测通），但干净环境一次性暴露了
**国内站点在隧道内全废**（P1，一直被 Verge 掩盖）、**兜底哨兵探针目标本身不通**（P2）、
**常驻内核复用不重绑网卡**（P3）、**内核日志不可见**（P4）。

---

## 一、UIA 退 Verge 的实际路子（含被挡住的那条）

托盘图标是 `SystemTray.NormalButton` / `AutomationId=NotifyItemIcon`，在 `Shell_TrayWnd` 下：

| 动作 | 结果 |
| --- | --- |
| `winapp ui search "Clash Verge" -w <Shell_TrayWnd>` | 命中，tooltip 就是状态面板：`Clash Verge 2.5.8↵系统代理: off↵TUN: on↵订阅: iKuuu_V2.yaml` |
| `invoke`（左键，InvokePattern） | **可用**，不需要前台 ⇒ 打开主窗口 |
| `click --right`（右键，要走托盘菜单） | **被挡**：`Target window is not in the foreground — refusing to right-click`。`focus` 同样拒；`send-keys "win+b" --via send-input --allow-system-keys` 也拒（explorer 的窗口非前台）。这是 winapp 的守卫，不是权限问题 |
| ⇒ 改用主窗口里的等价入口 | 设置页有一枚 **`退出`** 按钮（`ListItem "退出"` / `btn-4a17`），UIA invoke 一下就是整机退出，与托盘菜单那条"退出应用"同一条代码路径 |

退出后：`clash-verge`（GUI）与 `verge-mihomo`（内核）都没了，`Meta` 网卡变 `Disconnected`；
`clash-verge-service` 是 Windows 服务，常驻不动（正常）。
还原：`Start-Process 'C:\Program Files\Clash Verge\clash-verge.exe'`，TUN 自动起来，
`Meta` 重新 Connected 并抢回 `0.0.0.0/0`，`baidu` 参照回到 200/0.049s——机器与睡前状态一致。

★ 本轮**没有**用 `Stop-Process` 杀 Verge，也**没有**动它的订阅与开关（退出前 TUN on / 系统代理 off，还原后一样）。

---

## 二、干净基线（Verge 已退，隧道未拨）

- 默认路由只剩 `WLAN → 192.168.0.1`；`Get-DnsClientServerAddress`：WLAN = `{192.168.1.1, 192.168.0.1}`。
- 无 `ClashUWP` 路由、`nrpt=0`。
- 死掉的 wintun（`本地连接 2`，ifIndex 15，`InterfaceMetric=5`）只剩 `255.255.255.255/32`、`224.0.0.0/4`、
  `fe80::/64` 这类链路本地路由，**没有 0.0.0.0/0** ⇒ 不构成 memory「Orphan NRPT machine blackout」那种黑洞。
- 可达性参照（决定后面所有判据怎么读）：
  - `http://www.baidu.com/` → **200 / 0.036s**（直连互联网是好的）
  - `114.114.114.114` → **443 TCP 不通**（`Test-NetConnection` False）、53 TCP 通、HTTPS 超时
  - `223.5.5.5:443` → **通**
  - `www.google.com` 直连 → 超时（境内预期）

★ 第二组直接推翻 memory「114.114:443 只在直连下应答」：**现在连直连也不应答**。见 P2。

> ★★【1.0.80 之后的订正 · Docs/33 §七】这批修完后复测了候选探针，两条要记住：
> ① **同一台机器、Verge 常驻（污染态）下六个候选全通**，包括这里"不通"的 `114.114.114.114:443`
>    —— 所以这些 IP 的"通/不通"本身**不是**物理网络健康的证据，第三方 VPN 一在场就会读反；
>    这正是 P2 最终没有走"换个目标"而是走"多候选 OR + 平台 NCSI 自评 + 判据不可信不动手"的原因。
> ② `223.6.6.6` / `119.28.28.28` / `8.8.8.8` / `1.1.1.1` 这四条**至今没有在干净直连基线下实测过**，
>    本文不替它们背书；要验得再走一次退 Verge 的窗口。

---

## 三、头条判据：隧道内可达性，终于算验过

拨号（UIA `invoke "连接 VPN"`）后：`Get-NetRoute -InterfaceAlias 'ClashUWP*'` = 7 条
（`198.18.0.0/16`、`223.5.5.5/32`、`119.29.29.29/32`、`10.7.0.2/32`、广播/组播），默认路由仍留在 WLAN
（`includeMode=narrow` 应有的形态），`nrpt=1`。

| 判据 | 实机原文 | 结果 |
| --- | --- | --- |
| 隧道内境外可达 | `http://www.google.com/generate_204` → `code=204 time=0.229 ip=198.18.0.16`；`https://www.gstatic.com/generate_204` → `204 / 0.364 / 198.18.0.30`；重建桥后再测 3 次全 `204 / ≈0.23s` | **成立**（remote_ip 落在我们自己的 fake-ip 段 ⇒ 包确实进隧道） |
| 隧道内 DNS | NRPT：`Namespace={.}`、`NameServers={223.5.5.5, 119.29.29.29}`（与 include 的两条 /32 对齐）；`Resolve-DnsName` 走系统解析器：baidu→`198.18.0.19` 417ms、qq→`198.18.0.22` 8ms、google→`198.18.0.15` 15ms、taobao→`198.18.0.26` 15ms | **成立**（fake-ip 秒回，NRPT 生效） |
| 内核与控制面 | `bridge-start clash_bridge_start 返回 rc=0 用时=199ms`（全新桥）/ `复用常驻内核（排空 256 包，配置未变）`（复用）；UI：`内核：已连接 mode=rule`、`代理组 11 个　活动连接 22 条` | **成立** |
| 断开无残留 | `profile-disconnect 断开=Ok` 后 `ClashUWP*` 路由=0、`nrpt=0`，且无自动重拨 | **成立** |

---

## 四、暴露的问题（按严重度）

### P1 · 国内站点在隧道打开时一律不可用——过去被 Verge 掩盖

隧道开着时的矩阵（同一台机器、同一分钟）：

```
http://www.baidu.com/        code=000 ip=            t=10.01s   (rc=28 超时)
https://www.qq.com/          code=000 ip=            t=10.01s   (rc=28)
http://220.181.111.232/      code=200 ip=220.181...  t=0.015s   ← 真实 IP 直连永远好
https://dl.google.com/       code=204 ip=198.18.0.45 t=0.34s    ← 订阅里有显式 DOMAIN 规则，策略=🇨🇳 国内网站(DIRECT)
http://www.google.com/generate_204   code=204        t=0.25s    ← 走节点，由节点侧解析
```

判别做完了才敢下结论，所以排除项也写清楚：

- **不是路由问题**：真实 IP 直连恒 200/0.015s；fake-ip 也确实落在 `198.18/16` 并被 `ClashUWP` 路由接住。
- **不是 DNS 应答问题**：`Resolve-DnsName` 国内域名照样 8–417ms 给 fake-ip。
- **不是节点挂了**：`dl.google.com` 命中的也是 `🇨🇳 国内网站` 这个 `type: select` 组，
  UIA 展开确认它当前选的是 `▶ DIRECT　Direct`，而它 **204 通**——说明 DIRECT 出网本身是好的。
- **收窄到的形态**：凡是**只能靠 `GEOIP,CN` 命中**（订阅 rules 末尾 `- GEOIP,CN,🇨🇳 国内网站`，`- MATCH,🐟 漏网之鱼`）
  且**需要内核自己把域名解析成真实 IP** 的国内站点全挂；`www.jd.com` / `www.taobao.com` 首解即挂，
  有显式 DOMAIN 规则的域名不受影响。合起来指向：**内核的上游解析在自家隧道里打转**——
  `dns.nameserver` = `119.29.29.29` / `223.5.5.5` / 若干 DoH，而这两个 UDP nameserver 的 IP
  正好被 include 成 `/32` 进隧道，`tun.dns-hijack: [any:53]` 又把 53 端口全捕获 ⇒ 内核发出的解析请求
  被自己的 fake-ip 层接住，GEOIP 评估拿不到真实 IP，DIRECT 拨不出去。
- **为什么以前看不见**：Verge 在的时候（Docs/31 §4.2 那张表）UI 里一堆
  `DIRECT > 🇨🇳 国内网站` 的连接是有字节的，因为外层还有 `Meta` 那条路替我们把上游解析带出去。
  **Verge 既是污染源，也是这条 bug 的遮羞布。**
- ★ 顺手试了 `selfSplitTunnel=1`（arm=1，`VpnTrafficFilterAssignment` + `SplitRouting`，把本包拆到物理网卡）：
  **更糟**——隧道内 google 204 变 `rc=7 connection refused`，baidu 仍超时，且自家诊断直接弃权
  （`watchdog.txt: fake-ip=弃权(selfSplitTunnel=1：本包走物理网卡，非宿主代表)`）。
  与 run 13/15/16/17 的旧结论一致：这条平台原语解决不了自环。**settings.txt 已回滚，与改前逐字一致。**

下一批的候选（未做，写在这只是免得下次又从头猜）：nameserver IP 不进 include（换 NRPT 目标或把 /32 摘掉）、
`fake-ip-filter` 加 nameserver、或给内核解析单开一条不走 tun 的 DNS listener/`bind-address`。
判据就用本轮这组矩阵，`baidu/qq/jd` 从超时变 200 即成立。

### P2 · 物理兜底哨兵的探针目标根本不通，误杀只是时间问题

`HealthMonitor.PhysicalNetworkWatchdogAsync` 与插件侧 `PhysicalNetworkWatchdog` 都是
`TcpProbeAsync("114.114.114.114", 443)`，30s 一次、10 次即 `Cancel + DisconnectAsync + DropProfileAsync`。
本轮基线实测 **114.114.114.114:443 在干净直连下就不通**，于是隧道一建立计数就开始跳：

```
06:13:09 pid=5160 physical-watchdog        114.114.114.114 不可达 (超时)，累计失败 1/10
06:13:10 pid=5160 physical-watchdog-plugin 114.114.114.114 不可达（超时），累计失败 1/10
06:13:42 pid=5160 physical-watchdog        ……累计失败 2/10
```

——**整机网络那一刻是健康的**（baidu 200、隧道 204）。约 5 分钟就会走到"拆隧道 + 删 profile"。
本轮每次都在 10/10 之前手动断开，**没让它动手**，系统侧 profile 仍是 `Count=2`（`ClashUWP` + `ClashUWP · iKuuu_V2`）。

修法很小但必须做：探针目标换本轮实测 `OK` 的 `223.5.5.5:443`（或 `119.29.29.29:53`），
并**绑物理接口**（memory「Health watchdog fallback」的旧结论是"不绑接口 + 吃默认路由"）；
顺带——前台与插件**各跑一个**哨兵（日志里 `physical-watchdog` 与 `physical-watchdog-plugin` 两行独立计数），
双份误杀风险，应该只留一个或让插件侧那个由前台仲裁。

> ★【1.0.78/1.0.80 落地后的订正，详见 Docs/33 §二·§八】这段"修法"有两处不能照做：
> ① `223.5.5.5` 与 `119.29.29.29` **恰好在我们窄 include 的 `/32` 里** —— 拿它们当物理探针候选，
>    隧道一开就必然假通过，防线整条失效。实际候选表明确排除这两个 IP。
> ② "只留一个哨兵"**没做也先别做**：前台与插件仍是两条独立循环（分处不同上下文），
>    插件那条是前台崩溃后唯一的幸存者；1.0.78 统一的是**判据与动作**（`PhysicalNetworkProbe`
>    一份实现两边共用），不是把两条并成一条。
> 实测结果：旧口径在同机数出 33 行失败计数，新口径在 1.0.78/1.0.79/1.0.80 共 5 次启动、
> 三段会话（58 s / 1501 s / 1350 s）里失败与动手各 0 行。

### P3 · 常驻内核复用不重绑物理网卡

`residentBridge=1` 的复用路径：Verge 已退、`Meta` 只剩一个 Disconnected 设备，
但 `net-follow` 仍报 `bound_iface=Meta`，并把事件判成"指纹未变"（它比的是接口**枚举**，没算连接状态）：

```
06:12:38 net-follow 武装 bound_iface=Meta 基线=… internet=TP-LINK_81D1[ed2e56f6]   ← 出口已经是 WLAN
06:12:44 net-follow evt=NetworkStatusChanged 指纹=未变 bound=Meta
（杀进程重建桥后）
06:13:46 net-follow 武装 bound_iface=WLAN  ← 立刻对了
```

本轮流量还能出，靠的是 Windows 路由兜底，不是我们的绑定 ⇒ **防自环/换网跟随的权威观测点在这个场景给假信号**。
修法：指纹纳入接口 `MediaConnectionState` / 是否第三方 tunnel，或复用常驻桥时强制重评一次绑定。

### P4 · 可观测性缺口（不是新 bug，但把上面三条的排查成本放大了）

- **mihomo 内核自己的日志没接出来**：`plugin.log` 只有我们这层的行，P1 这种"规则评估卡在解析上"只能靠
  外部 curl 矩阵反推。`config.kernel.yaml` 里 `log-level: info` 是配了的，产物没落盘。
- 包内 9090 从包外读不到（`ClashUWP.Cli` 正在做，Docs/13 / Docs/28 是隔壁的），本轮所有内核侧事实都来自 UIA 读界面。
- `watchdog.txt` 那行 `223.5.5.5=OK|119.29.29.29=超时` 是本轮唯一一眼看出问题的探针——
  值得把"内核上游 nameserver 可达性"做成常规快照项（它正是 P1 的直接判据）。

---

## 五、收工判据

- `Get-DnsClientNrptRule` = 0；`Get-NetRoute -InterfaceAlias 'ClashUWP*'` = 0；断开后复查无自动重拨。
- **没写过 `debug-cmd.txt`**（收工再查：文件不存在）。
- `settings.txt` 已回滚：`selfSplitTunnel` 已删，与本轮开始前逐字一致（改前做过备份比对）。
- Clash Verge 已还原：`clash-verge` 6604 + `verge-mihomo` 17672 在跑，`Meta` Connected 且抢回 `0.0.0.0/0`，
  `baidu` 200/0.049s。★ 注意：这意味着 **P1 在装了 Verge 的机器上不会显形**，下次复测还得走这一轮的路子。
- 系统侧 profile `Count=2` 完好；应用界面开着、未连接（1.0.77）。
- 本轮**未改任何产品代码**，故无新增提交；文档就这一篇。
- ★【后续】本文交出去的 P2 与 Docs/30 §8-A/§8-B 已在**下一批（应用 1.0.78 → 1.0.80）**装机修完并实测，
  落地清单、证据行与订正见 **Docs/33**；P1（DNS 自环）、P3（`net-follow` 绑卡假信号）、P4（内核日志）仍未修。
