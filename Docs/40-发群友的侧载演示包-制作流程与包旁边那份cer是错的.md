# 40 · 发群友的侧载演示包：制作流程与「包旁边那份 .cer 是错的」

> 触发：1.0.91 装机复验绿之后，用户要「想给群友发demo」。
> 本篇记三件事：①这条演示包怎么一步步出来的（可复跑），②过程中撞出来的**一条真缺陷**
> （`AppPackages\…_Test\` 里 VS 生成的 `.cer` 和最终装机用的签名证书**不是同一张**，
> 而微软那个官方安装脚本会逐字比对它们 ⇒ 照默认产物发出去，群友那台干净机器**装不上**），
> ③发出去的包现在**已证/未证**各是哪些。
> 口径：全部结论来自现读文件与实跑命令，没有推测。

---

## 一、成品在哪

```
D:\fluentapps\share\ClashUWP-demo-1.0.91\
├── ClashUWP_1.0.91.0_x64.msix          51,618,732 字节（重签后）
├── ClashUWP_1.0.91.0_x64.cer              812 字节  ← 必须是签名那张（见 §三）
├── Dependencies\
│   ├── Microsoft.UI.Xaml.2.8.appx         4.70 MB
│   └── Microsoft.VCLibs.x64.14.00.appx    0.86 MB
├── Add-AppDevPackage.ps1               VS 官方安装脚本（未改一字）
├── Add-AppDevPackage.resources\        它的本地化文案（脚本要读，删了会报缺资源）
├── Install.ps1                         VS 一起生成的旧版脚本，README 里明确叫群友别用
└── 安装说明.md                          给群友看的那份
```

整包 56 MB。**不含**任何订阅地址、节点、私钥（`.pfx` 一个都没拷，`.cer` 是公钥导出）。

---

## 二、可复跑的制作流程

```powershell
# 1) 出包（AGENTS.md 的口径：部署走 VS 官方通道，不自制部署脚本 —— uwp-bridge 就是那条通道）
Tools\UwpBridge\uwp-bridge.exe -Build <abs>\ClashUWP\ClashUWP.csproj -Target Publish
#   → AppPackages\ClashUWP_1.0.91.0_x64_Test\ClashUWP_1.0.91.0_x64.msix（构建期签的证书本机没有私钥）

# 2) 用本机已受信的同主体证书重签（这一步是 Docs/29 §172 / Docs/30 §5 定下来的既有口径）
winapp tool signtool sign /fd SHA256 /sha1 FB3309A61DD6B50D5E08B1F870C448581C4DBEC2 <abs>.msix

# 3) 导出这张签名证书的公钥（只导 Cert，不导 pfx）
$src = Get-ChildItem Cert:\LocalMachine\Root | Where-Object Thumbprint -eq "FB3309A6…"
[IO.File]::WriteAllBytes("<目标>.cer", $src.Export([X509Certificates.X509ContentType]::Cert))
```

本机自证（三条都实跑过）：

| 自证 | 结果 |
| --- | --- |
| 包签名 | `Get-AuthenticodeSignature` = **Valid**，主体 `CN=A19C62A9-1AA6-44AB-9F57-2DE6C380D9C9`，指纹 `FB3309A6…` |
| 导出的 .cer 与包内签名证书 | `RawData` **812 字节逐字相同 = True** |
| 证书有效期 | `NotAfter=2027-04-03 05:16:15`，`Subject -eq Issuer`（自签） |

---

## 三、★ 缺陷：默认产物里那份 `.cer` 发出去会装不上

**现象**（现读，非推测）：`AppPackages\ClashUWP_1.0.91.0_x64_Test\ClashUWP_1.0.91.0_x64.cer`
的指纹是 **`4E12845306A70170563A8F0121158CCE5244F72B`**（同样 `CN=A19C62A9…`、同样自签），
而 §一 那个 msix 真正的签名者是 **`FB3309A6…`** —— **同主体、两张不同的密钥对**。
原因是 Docs/29 §172 已经记过的那件事：构建期 VS 签的那张（本机没有私钥，装不上要提权），
我们改用本机受信的 `FB3309A6…` **重签**，而 **VS 生成 `.cer` 的动作发生在重签之前**，
没人回来更新它。

**为什么这对"发群友"是致命的**：`Add-AppDevPackage.ps1` 不是"随便装张证书"，它做两道判据
（行号是本包那份脚本现读）：

- `:597` `$NeedInstallCertificate = ($PackageSignature.Status -ne "Valid")`
  ⇒ 群友那台干净机器上没有 `FB3309A6…`，`Get-AuthenticodeSignature` 判不成 Valid，于是**必然**进这一支；
- `:602-614` 在脚本目录里 `Get-ChildItem *.cer`，**数量必须恰好为 1**：
  少于 1 报 `ErrorNoCertificateFound`，**多于 1 直接 `ErrorManyCertificatesFound` 退出**；
- `:626-627` `if ($PackageCertificate -ne (Get-PfxCertificate $DeveloperCertificatePath))`
  ⇒ 还要把这张 `.cer` 与包内签名证书逐字比，不等就 `ErrorCertificateMismatch` 退出。
- 只有全过才 `:331 certutil.exe -addstore TrustedPeople $CertificatePath`。

⇒ **默认产物原样发出去会撞两道墙**：两份 `.cer` 都在目录里 → `ManyCertificatesFound`；
只留 VS 那份 → `CertificateMismatch`。本机装得上是因为 `FB3309A6…` 早就在我
`LocalMachine\Root` 里（脚本对本机根本不走装证书那支），**这个"能装"完全不能外推**。

**本包的处理**：删掉 VS 生成的那份，把 `FB3309A6…` 导出的公钥证书**沿用同一个文件名**
`ClashUWP_1.0.91.0_x64.cer` 放进目录 ⇒ 脚本数量判据过、逐字比对过。
**这是"制作流程缺一步"，不是脚本的 bug** —— 下次做演示包必须带上 §二 第 3 步。

★ 待办（没做，记下来）：把这条固化进发布流程（`Docs/29 §172` 与 `Docs/03` 的部署口径里都只写了
"重签"，没写"重签后要换 .cer"），并且把这两张同主体证书固定成唯一签名身份 ——
`Docs/30 §8` 那条"运维上固定签名证书"的债至今没还。

---

## 四、给群友的那份说明里，逐条出处

| 说明里的话 | 出处 |
| --- | --- |
| 系统要求 Windows 10 1803+ / x64 | `AppxManifest.xml:51-55`（`MinVersion=10.0.17763.0`、`MaxVersionTested=10.0.26100.0`） |
| 只依赖 WinUI 2.8 + VCLibs.140.00，两个 appx 已带 | 同上两行 `PackageDependency`；文件在 `Dependencies\` |
| 不需要另装 .NET / Windows App Runtime | 清单 `PackageDependency` **只有这两条**，没有 `Microsoft.WindowsAppRuntime.*` ⇒ 自包含发布 |
| 证书指纹 / 主体 / 有效期 | §二 表 |
| 界面操作「订阅 → 建/重建 profile → 连接 VPN」 | UIA 现读到的按钮名：`itm-… ListItem "订阅"`、`btn-profile-… Button "建/重建 profile"`、`btn-vpn-… Button "连接 VPN"` |
| 日志路径 `…\8B8CC4F4.ClashUWP_4tjvwvefxrz66\LocalState\logs\app.log` / `plugin.log` | 本篇装机与 Docs/13 的既有口径；两个文件都要，判据行按调用路径分文件（见记忆条目 Rolling log channel） |
| 「先断开再卸载」与 NRPT 残留 | `Docs/07` 宿主恢复手册 + 记忆条目 Orphan NRPT machine blackout；给的命令 `Get-DnsClientNrptRule \| Format-List *` 后**按 Name 单条删**（`Remove-DnsClientNrptRule` 有 `-Name/-Force`，现读 `(Get-Command …).Parameters`） |
| 报错码 `0x800b0109` / `0x80073CFB` 的处置 | 前者=证书没进 `TrustedPeople`（脚本 `:331` 那步没做成）；后者=同标识不同签名已存在（本机踩过：同版本不同签名重装必撞它） |

---

## 五、这个 demo 现在**已证**与**未证**

**已证（一手，1.0.91）**：`uwp-bridge Publish` 出包 0 error 0 warning；重签后 `Add-AppxPackage`
覆盖装（先撞 `0x80073D02`「应用正在使用」，关掉 pid=1680 后成功）；`app.log` 新行
`version=8B8CC4F4.ClashUWP/1.0.91.0 pid=18816`；应用窗口起得来、UIA 点得动；「诊断内核」两条判据
`✓ 内核配置文件存在 (576 行)` + `✓ external-controller=127.0.0.1:9090（与前台 REST 基址一致）`；
离线夹具 `ALL OFFLINE TESTS PASSED`；收工判据 NRPT=0、只有 WLAN 一条默认路由、`debug-cmd.txt` 不存在。

**未证（发出去前要说清的）**：

1. **第二台机器上的安装流程一次都没跑过** —— §三 那两条判据是**读脚本读出来的**，不是实跑出来的。
   我能证的只有"包签名 Valid + .cer 与签名逐字相同"，证不了群友机器上 Win11 的侧载策略与
   `certutil -addstore TrustedPeople` 的交互。
2. **1.0.91 没拨过一次号**：与 1.0.90 的差集只有 `LocalStatePaths` 那一搬（不在拨号路径上），
   凭据是**操作清单自证**。拨号一手在 1.0.88/1.0.89（`Docs/38 §十三/§十四`）。
3. **诊断里同屏出现的 `✗ REST API 不可达 (127.0.0.1:9090)`** 是内核没跑（未连接），预期；
   但它意味着"群友装完第一次点诊断会看到一个 ✗"，README 里没解释这条 —— 要发之前值得补一句。
4. 用户报的那个症状（一条连着时点另一条卡在「正在连接中」）**三轮仍未复现**
   （`Docs/38 §14.5`），所以"demo 里别当着群友的面切第二条 profile"这句我**没写进 README**，
   要不要写由用户定。
