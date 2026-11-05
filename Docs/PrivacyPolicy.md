# ClashUWP 隐私政策 / Privacy Policy

中文版在上，英文版在下（内容等价）。
Chinese version first, English version below (equivalent content).

最后更新 / Last updated：2026-11-05
适用版本 / Applies to：ClashUWP 1.0.x（Microsoft Store 版与侧载版同一份政策）

---

## 一、一句话

ClashUWP 是一个在你自己的设备上运行、只为你自己的网络服务的客户端。
**开发者不收集、不接收、不存储你的任何数据**：没有账号系统，没有统计/遥测，没有广告 SDK，没有崩溃上报。
本应用发出的全部网络请求只可能到达下面第三节列出的那几类目标，而这几类目标**没有一个是开发者运营的**。

## 二、设备上存了什么，存在哪儿

所有数据都存在应用自己的沙盒目录里（`%LOCALAPPDATA%\Packages\8B8CC4F4.ClashUWP_4tjvwvefxrz66\LocalState`），包括：

| 内容 | 文件 | 为什么存 |
|---|---|---|
| 你添加的订阅地址与订阅下发的配置 | `subscriptions\`、`profiles\` | 下次启动要能继续用 |
| 界面开关与配置改写（档位、分流名单等） | `settings.txt`、`split-apps.txt` 等 | 记住你的选择 |
| 滚动日志 | `logs\app.log`、插件侧日志 | 只写本地，用于本机排障；里面可能出现订阅 URL、节点名称 |
| 交给 Windows 的 VPN 连接配置 | Windows 的 VPN 存储（由平台管理，不是我们能选的） | 平台要求 |

**没有任何一项会被上传。**本应用不含向开发者服务器发送数据的代码路径 —— 代码是公开的，这一条可以直接读源码验证。
卸载应用时，Windows 会连同这个目录一起删除，我们不保留任何副本，也没有可供删除的服务器端记录。

## 三、网络请求会到达哪些目标

1. **你输入的订阅地址的宿主服务器。**下载订阅配置时，请求只发往这个 URL 自己的宿主。它会看到你的 IP、请求时间和 URL 里的 token —— 这是"用它给的订阅"必然的事，服务商自己的日志策略不由本应用决定。
2. **订阅里声明的代理节点服务器。**开启 VPN 后，你的应用流量经内核加密转发到你自己选的节点。节点运营方能看到你访问的目标域名/地址和流量元数据（内容按你与节点服务商的约定处理）。**这些服务器不是我们运营的，我们对它们的日志与留存策略不负责任，也无法代为承诺。**
3. **连接性/健康探针。**为了判断"物理网络还在不在、隧道是不是黑洞了"，应用会对少数公开 DNS 服务与域名做 TCP 握手探测：`223.5.5.5`、`119.29.29.29`、`114.114.114.114`、`223.6.6.6`、`119.28.28.28`、`8.8.8.8`、`1.1.1.1`、`www.baidu.com`。这些是**只有握手、不带任何身份信息的空探测**，用途纯粹是决定是否重拨或拆隧道。
4. **DNS over HTTPS 解析器。**默认配置里内核使用 `https://doh.pub/dns-query`、`https://dns.alidns.com/dns-query`（fallback：`https://223.5.5.5/dns-query`、`https://223.6.6.6/dns-query`）。这类请求会带上你正在解析的域名 —— 这是 DNS 的工作方式；这些解析器不是我们运营的，你可以在界面/配置里换成自己信任的服务器或关掉覆盖。
5. **节点测速。**分组测速默认请求 `http://www.gstatic.com/generate_204`（一个只返回状态码的地址），请求经你自己的节点发出，不带身份信息。
6. **你访问的、以及你的订阅配置指定的一切目标。**VPN 建立后，本机的网络出口由你的订阅规则和浏览行为决定；本应用不额外创造目的地。

此外，内核的本地 REST 控制器默认只绑定 `127.0.0.1`（回环），不对外网或局域网暴露；若你自己的订阅声明改写了这一行，以你的订阅为准。

## 四、每个环节谁能看到什么

| 环节 | 能看到的 | 由谁控制 |
|---|---|---|
| 开发者（本应用作者） | 什么都看不到 —— 无服务器、无上报 | — |
| 订阅服务商 | 你的 IP、订阅拉取记录 | 你选择的第三方 |
| 节点/机场服务商 | 你访问的目标、流量元数据与用量 | 你选择的第三方 |
| DoH 解析器 | 你解析的域名 | 可替换/可关闭 |
| 探针目标（公共 DNS） | 一次 TCP 握手（无内容、无身份） | 不可用于其他目的 |
| Microsoft Store | 下载与购买记录等平台遥测，与本应用无关 | 见微软自己的政策 |

## 五、应用声明的能力

`internetClient`、`internetClientServer`、`privateNetworkClientServer`、受限能力 `networkingVpnProvider`。
前三个用于"取订阅 + 本机回环控制内核 + 局域网内按需直连"；`networkingVpnProvider` 是 VPN 插件本身的平台入口。
**不申请** `runFullTrust`、`broadFileSystemAccess`，也不做开机自启。除第三节所列用途之外，这些能力不用于其他目的。

## 六、儿童

本应用不面向 12 岁以下儿童，也不有意收集任何人的个人信息。

## 七、政策变更

实质变更会更新本页面顶部的日期，并随下一个 Store 版本发布。

## 八、联系

yunmoxinghe@outlook.com
（这是本应用的支持与隐私联系邮箱，也是 Microsoft Store 产品页上填写的支持联系方式。）

---

# ClashUWP Privacy Policy (English)

## 1. In one line

ClashUWP is a client that runs entirely on your own device and serves only your own network.
**The developer collects, receives and stores nothing of yours**: no accounts, no analytics or telemetry, no advertising SDK, no crash reporting.
Every request the app makes can only reach one of the destination classes listed in section 3 below — and **none of them is operated by the developer**.

## 2. What is stored on your device, and where

All data lives in the app's own sandbox (`%LOCALAPPDATA%\Packages\8B8CC4F4.ClashUWP_4tjvwvefxrz66\LocalState`):

| Content | Files | Why |
|---|---|---|
| Subscription URLs you added and the configuration they deliver | `subscriptions\`, `profiles\` | so they survive restarts |
| UI switches and configuration overrides | `settings.txt`, `split-apps.txt`, … | to remember your choices |
| Rolling logs | `logs\app.log`, plugin logs | written locally only, for on-device troubleshooting; may contain subscription URLs and node names |
| The VPN connection profile handed to Windows | Windows VPN storage (managed by the OS, not by us) | required by the platform |

**Nothing is uploaded.** There is no code path that sends data to a developer server — the source is public, so this is verifiable by reading it.
Uninstalling removes the whole folder; we keep no copy and there is no server-side record to delete.

## 3. Where the app's network requests go

1. **The host of the subscription URL you entered.** Fetching a subscription sends a request only to that URL's own host, which sees your IP, the request time and the token in the URL. That is inherent to using a service you subscribed to; its operator's own retention policy is not ours to determine.
2. **The proxy nodes declared by your subscription.** With the VPN on, your traffic is forwarded through the kernel to the servers you chose. Your node operator can see the destinations and traffic metadata it handles. **These servers are not operated by us and we cannot make promises about their logging.**
3. **Connectivity/health probes.** To decide whether the physical network is still alive or the tunnel has become a black hole, the app performs TCP-handshake probes against a handful of public resolvers and one domain: `223.5.5.5`, `119.29.29.29`, `114.114.114.114`, `223.6.6.6`, `119.28.28.28`, `8.8.8.8`, `1.1.1.1`, `www.baidu.com`. They carry **no payload and no identifying information** — their only use is deciding whether to redial or tear down.
4. **DNS-over-HTTPS resolvers.** The generated kernel configuration uses `https://doh.pub/dns-query` and `https://dns.alidns.com/dns-query` (fallback `https://223.5.5.5/dns-query`, `https://223.6.6.6/dns-query`). These requests carry the domain names being resolved — that is how DNS works. These resolvers are third parties; you can replace them or turn the override off.
5. **Node latency tests.** Group speed tests request `http://www.gstatic.com/generate_204` by default (a status-code-only endpoint), issued through your own nodes and carrying no identifying data.
6. **Anything you access, or anything your subscription config designates.** Once the tunnel is up, egress is determined by your own rules and browsing; the app invents no additional destinations.

The kernel's local REST controller binds to `127.0.0.1` (loopback) by default and is not exposed to the LAN or internet; if your own subscription overrides that line, your subscription governs.

## 4. Who can see what

| Party | Sees | Controlled by |
|---|---|---|
| The developer | Nothing — no server, no reporting | — |
| Your subscription provider | Your IP and fetch records | the third party you chose |
| Your node/proxy provider | Destinations and traffic metadata | the third party you chose |
| DoH resolvers | The domain names you resolve | replaceable / disableable |
| Probe targets (public DNS) | One TCP handshake, no content, no identity | used for nothing else |
| Microsoft Store | Its own download/purchase telemetry, unrelated to this app | Microsoft's policy |

## 5. Declared capabilities

`internetClient`, `internetClientServer`, `privateNetworkClientServer` and the restricted capability `networkingVpnProvider`.
The first three are for fetching your subscription, controlling the local kernel over loopback, and LAN behaviour as configured; `networkingVpnProvider` is the platform entry point for the VPN plugin itself.
`runFullTrust` and `broadFileSystemAccess` are **not** requested, and the app does not register a startup task. These capabilities are used for the purposes above only.

## 6. Children

The app is not directed at children under 12 and does not knowingly collect personal information from anyone.

## 7. Changes

Material changes update the date at the top of this page and ship with the next Store release.

## 8. Contact

yunmoxinghe@outlook.com
(This is both the support and the privacy contact for ClashUWP, and the contact entered on the Microsoft Store product listing.)
