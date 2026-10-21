# 40 · 发群友的侧载演示包：制作流程与「包旁边那份 .cer 是错的」

> 触发：1.0.91 装机复验绿之后，用户要「想给群友发demo」。
> 本篇记三件事：①这条演示包怎么一步步出来的（可复跑），②过程中撞出来的**一条真缺陷**
> （`AppPackages\…_Test\` 里 VS 生成的 `.cer` 和最终装机用的签名证书**不是同一张**，
> 而微软那个官方安装脚本会逐字比对它们 ⇒ 照默认产物发出去，群友那台干净机器**装不上**），
> ③发出去的包现在**已证/未证**各是哪些。
> 口径：全部结论来自现读文件与实跑命令，没有推测。

---

## 一、成品在哪

**★ 最终发出去的就一个文件**（用户口径 01:54：「只给 msix 就够了，安装程序会自动处理框架包，他们自己会装证书」）：

```
D:\fluentapps\share\ClashUWP-demo-1.0.91\ClashUWP_1.0.91.0_x64.msix
  49.2 MB   SHA256=30393F20B8B0989B7C4442FB3308C3A4BF8F5C33342306B53B15538DCAD0DDA4
  签名=Valid（FB3309A6…，见 §三）  包标识=8B8CC4F4.ClashUWP_1.0.91.0_x64__4tjvwvefxrz66
```

与它同源的那份在 `ClashUWP\AppPackages\ClashUWP_1.0.91.0_x64_Test\`（重签后，哈希同上）。

这条路把原来由脚本承担的东西换成了两条**我们没验过**的前提：①框架包由安装程序自己从 Store 取
（要求对面联网、且那两个包取得到）；②群友自己把签名证书装进"受信任的人"（指纹
`FB3309A6 1DD6B50D 5E08B1F8 70C44858 1C4DBEC2`，主体 `CN=A19C62A9-…`，2027-04-03 到期，需管理员）。
**§三 那条"VS 生成的 .cer 与重签后的包不是同一张"仍然成立** —— 它现在的后果变成：
谁要是照 `AppPackages\…_Test\` 那份 `.cer` 去装信任，装的是**错的那张**，包还是验不过。
要发证书就从本机 `LocalMachine\Root` 里按上面这个指纹导，别用仓库产物旁边那份。

下面这张树状图是**中间形态**（曾按官方 `Add-AppDevPackage.ps1` 那条路做过一版），保留是因为
它把"要发全须带哪几样"记全了，**但已按用户口径删掉，别照着再发一遍**：

```
D:\fluentapps\share\ClashUWP-demo-1.0.91\          （同级另有 ClashUWP-demo-1.0.91.zip，54.6 MB）
├── ClashUWP_1.0.91.0_x64.msix          51,618,732 字节（重签后，与 AppPackages 那份同一个字节流）
├── ClashUWP_1.0.91.0_x64.cer              812 字节  ← **已被换成签名那张**（见 §三）
├── Dependencies\x64\                   只留 x64 这两个（arm/arm64/Win32/x86 六份 20 MB 已裁掉）
│   ├── Microsoft.UI.Xaml.2.8.appx         4.70 MB
│   └── Microsoft.VCLibs.x64.14.00.appx    0.86 MB
├── Add-AppDevPackage.ps1               VS 官方安装脚本（未改一字）
├── Add-AppDevPackage.resources\        它的本地化文案（脚本要读，删了会报缺资源）
├── Install.ps1                         VS 一起生成的旧版脚本，README 里明确叫群友别用
└── 安装说明.md                          给群友看的那份
```

**上面除第一行 `*.msix` 以外的一切（含那份 zip、`.cer`、`Dependencies\`、三个脚本、`安装说明.md`）
都已在 01:53–01:54 删掉**，成品目录现在只剩那一个文件 —— 现读 `ls` 过。

中间形态整包 56 MB（`.appxsym` 87 KB 那份符号文件没拷，群友用不上）。**不含**任何订阅地址、节点、私钥
（`.pfx` 一个都没拷，`.cer` 是公钥导出）。

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
★ 01:54 后走的是"只发 msix"，连这份换过的 `.cer` 也删了 ⇒ **本节那条缺陷在仓库产物里依然存在，而且更糟**：
`AppPackages\…_Test\` 目录里现在**同时有两份 `.cer`**，01:58 现读 `certutil -hashfile` + `Get-AuthenticodeSignature` 对上：

```
ClashUWP_1.0.91.0_x64.cer            = 4E12845306A70170563A8F0121158CCE5244F72B   ← VS 生成的错那张
ClashUWP-signing-FB3309A6.cer        = FB3309A61DD6B50D5E08B1F870C448581C4DBEC2   ← 包真正的签名者（本机导出的公钥）
ClashUWP_1.0.91.0_x64.msix 的 SignerCertificate = FB3309A6…                        ← 与上一行逐字相同
```

⇒ 谁要是直接在那个目录里跑官方脚本，撞的是 `:602-614` 的**数量必须恰好为 1** → `ErrorManyCertificatesFound`；
只挑 VS 那份 → `CertificateMismatch`。将来发全须时正确做法不变：留 `FB3309A6` 那张、改名成包同名 `.cer`、删掉另一张。

★ 待办（没做，记下来）：把这条固化进发布流程（`Docs/29 §172` 与 `Docs/03` 的部署口径里都只写了
"重签"，没写"重签后要换 .cer"），并且把这两张同主体证书固定成唯一签名身份 ——
`Docs/30 §8` 那条"运维上固定签名证书"的债至今没还。

### 三之五、★ 顺手记下自己犯的第二条：裁依赖不能把 `x64` 那层抹平

为了瘦身只留 x64，我先把 `Dependencies\x64\*` 上提成 `Dependencies\*` —— **这一步是错的**，
现读那份脚本 `:428-443` 的注释把目录结构写成机器契约：

```
<current dir>\Dependencies\<架构无关的 .appx>
                  \x86\  \x64\  \arm\  \arm64\
```

`:452-456` 确实会把 `Dependencies\*.appx` 当**架构无关**依赖安装（所以抹平后本机不会立刻出问题），
但这两个包是 **x64 专属**的框架包 ⇒ 抹平等于告诉 arm64 的机器"你也装这个"。已改回 `Dependencies\x64\`，
`安装说明.md` §三 手动命令里的路径也才因此对得上。**结论：裁依赖只删兄弟目录，不许压平层级。**

---

## 四、给群友的那份说明里，逐条出处

> 那份 `安装说明.md` 已按 01:54 口径删掉，本表**保留**：它是这些说法的出处存档，日后要重写说明照此表即可。

| 说明里的话 | 出处 |
| --- | --- |
| 系统要求 Windows 10 1803+ / x64 | `AppxManifest.xml:51-55`（`MinVersion=10.0.17763.0`、`MaxVersionTested=10.0.26100.0`） |
| 只依赖 WinUI 2.8 + VCLibs.140.00 | 清单 `PackageDependency` 同上两行。**★ 01:54 后这两个 appx 不再随包发**，改由安装程序自己从 Store 取 ⇒ 这条从"已带文件"降级为"未验前提 ①"（见 §一、§五-1） |
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
   ★ 想让它变成一手，只有一条路：找一台干净机器（或新开的本地账户）实跑 `Add-AppDevPackage.ps1`。
   在本机 `Remove-Item` 掉证书存储去"模拟"是不成立的 —— 装包与本机 Root 的交互正是被测对象本身。
   **01:54 后这条未证范围扩大了**：现在随包只发 msix，连"依赖从哪来、证书怎么装"都交给了对面的
   安装程序与手动操作 ⇒ 新增两条未验前提（§一）：①框架包能由安装程序自动从 Store 取到；
   ②群友自己把 `FB3309A6…` 装进受信任的人。任一条不成立都会在装包阶段直接失败 —— 证书那条的码见 §四 末行
   （`0x800b0109`），依赖取不到那条的具体 HRESULT **我这边没有一手样本，不编**。
   ⇒ 现在**只能靠群友回传报错截图/日志**才能把这两条变成一手。
2. **1.0.91 没拨过一次号**：与 1.0.90 的差集只有 `LocalStatePaths` 那一搬（不在拨号路径上），
   凭据是**操作清单自证**。拨号一手在 1.0.88/1.0.89（`Docs/38 §十三/§十四`）。
3. **诊断里同屏出现的 `✗ REST API 不可达 (127.0.0.1:9090)`** 是内核没跑（未连接），预期；
   已按这条在 `安装说明.md` §四 补了一句解释（"没连时那一行 ✗ 不是坏了，看下面两行 ✓"）
   —— **补的是文笔，不是代码，也没重打包**：这句只在说明里，包体与 §一 那份 msix 是同一个。
   ★ 01:54 后那句文笔随 `安装说明.md` 一起删了 ⇒ **现在这行 ✗ 没有任何解释跟着发出去**，
   群友极可能把它当成"坏了"来反馈。要么在群里口头交代一句，要么下一版把它做进应用内的诊断文案。
4. 用户报的那个症状（一条连着时点另一条卡在「正在连接中」）**三轮仍未复现**
   （`Docs/38 §14.5`），所以"demo 里别当着群友的面切第二条 profile"这句我**没写进 README**，
   要不要写由用户定。（README 现已删除 ⇒ 这句同样没有任何载体，需要的话只能在群里口头说。）
