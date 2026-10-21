# 13 · 调试 CLI —— AppService 在线遥控通道

对应版本：app `1.0.42.0` / CLI 包 `ClashUWP.Cli 1.0.1.0` / 组件 `ClashUWP.Vpn 1.0.25`（本轮未改组件）。
本轮只做一件事：在 `LocalState\debug-cmd.txt` 文件回路之外，再加一条**在线**通道 —— 桌面 CLI 通过 in-app AppService 直接驱动主进程里那张命令表；顺带把这条 CLI 按官方口径打成可安装的 MSIX。

---

## 1. 文件回路解决了"免重装"，但它的四个短板是通道形态决定的

| 短板 | 具体表现 | AppService 怎么解 |
| --- | --- | --- |
| 一问一答会互相覆盖 | 1 秒轮询 + 单个 `debug-cmd.txt` + 消费即删 ⇒ 同一秒下发两条，第二条把第一条的文件盖掉；结果又只有一个 `debug-out.txt`，第二个读者可能读到的是另一条命令的结果。 | 每条请求独立往返，结果直接回给发命令的那个人。 |
| 没有会话概念 | 外部写文件方不知道 app 是不是正卡在长命令上（`logs` 那条会挂 12 秒抓事件流）。 | 请求/响应天然配对，`SendMessageAsync` 有返回就是结束了。 |
| 无调用方信息 | "哪条命令是谁下发的"只能靠 `RollingLog` 里那行 `cmd` 原文。 | 服务端拿得到 `AppServiceTriggerDetails.CallerPackageFamilyName`，连接建立就记一行 ⇒ 审计链多一个"来自哪个包"。 |
| 结果只留最后一次 | 复盘时只能靠日志，不能回溯每一次查询输出。 | 每次调用把结果原样打到 stdout，天然可管道、可重定向。 |

★ **文件回路不是被替换掉了**，它是离线自救：进程没起来、或者整机断网的时候，AppService 一条都发不出去，那时候只有 `debug-cmd.txt` 还能操作。两条通道并存，共用同一张命令表（§3）。

---

## 2. 三条被否掉的路（写下来，免得以后重新讨论一遍）

| 方案 | 为什么不行 |
| --- | --- |
| 在 UWP 里开 `HttpListener` / `TcpListener`，桌面 CLI 走 HTTP | appcontainer 的 loopback 是隔离的，清单里也没有入站监听能力 —— 桌面进程根本连不进来。这条路从前提上就是错的。 |
| background-task 形态的 AppService（`uap:AppService` 带 `EntryPoint`） | 【官方】那样平台会起一个**独立的空壳宿主进程**跑那个 activatable class。空壳里没有 `VpnSession`、没有健康哨兵、没有 `DebugCommandLoop` 的状态 —— 遥控一个空壳等于白做。 |
| 继续只用文件回路 | 上表那四条是"两个进程之间只有一个覆盖式文本文件"这个形态决定的，改代码改不掉。 |

⇒ 结论：**in-app AppService**。主应用清单只在 `<Application Id="App">` 下声明

```xml
<uap:Extension Category="windows.appService">
  <uap:AppService Name="ClashUWP.Debug" />
</uap:Extension>
```

**故意不写 `EntryPoint`** —— 没有 EntryPoint 才是"in-app"：连接请求激活的是**主进程自己**的 `App.OnBackgroundActivated`，进程里所有状态都可及。

另一个对比：这条声明**不需要**在 Package 级 `<Extension Category="windows.activatableClass.inProcessServer">` 里注册 `ActivatableClassId`（那份清单里 `vpnClient` 后台任务必须要，缺了是 0x80080204）；in-app 形态由已存在的 App 实例接管激活，没有独立的类工厂要导出。

---

## 3. 服务端实现：两个 deferral + 一把串行闸（`ClashUWP\App.cs`）

```
OnBackgroundActivated
  └─ args.TaskInstance.TriggerDetails → AppServiceTriggerDetails   ← 拿不到就直接返回
       │    ★ AOT 下这里的类型转换是坑，`as` 需要旁边留一个 `As<T>()` 调用点把它根住，见 §6.2
       ├─ 连接级 deferral：args.TaskInstance.GetDeferral()，只在 ServiceClosed 里 Complete
       ├─ conn.RequestReceived：每个请求自己 e.GetDeferral()
       │    └─ await gate.WaitAsync() → DebugCommandLoop.ExecuteAsync(raw) → SendResponseAsync
       └─ conn.ServiceClosed：记一行 status，然后 Complete 连接级 deferral
```

四条硬约束，都是会静默咬人的：

1. **连接级 deferral 必须活到 `ServiceClosed`。** 提前 Complete ⇒ 平台随时把宿主拆掉，请求处理到一半就没了 —— 表现是 CLI 侧 `SendMessageAsync` 回 `RemoteDisconnected`，而服务端日志里连"我收到过这条命令"都没有。
2. **请求级 deferral 单独拿。** `RequestReceived` 是异步事件，不等 deferral 完成平台认为处理已结束。
3. **★ 一把 `SemaphoreSlim(1,1)` 串行化。** 文件回路天然串行（消费即删，一次一条），AppService 会**并发**投递。`connect` 和 `disconnect` 落在同一秒就是竞态 —— 对隧道来说这是最坏的那种竞态。闸把它退回原来那个"一次只有一条"的语义。
4. **响应一定发得出去，异常不能吞。** 服务端 catch 里回 `ValueSet{["error"]=…}`，且这个 `SendResponseAsync` 自己再套 try —— 连接已经断的时候它必抛，而那时除了日志没人能看见。

命令表只有一个实现：`DebugCommandLoop.ExecuteAsync(string raw)`。文件回路和 AppService 都只是它外面的一层壳 ⇒ 两条通道的命令语义不会漂移（改命令只改一处）。命令表本身没动：`status / connect / disconnect / recreate / probe / controller / conf / groups / logs / select <组名> <节点名> / bind <网卡名|-> / mode <rule|global|direct> / delay [节点名] / connections [kill <id>|kill-all] / logtail [app|plugin|sessions] [行数]`；`watchdog-off` 仍然没有。

---

## 4. 客户端 `clashuwp-cli`（`ClashUWP.Cli\Program.cs`）

> **1.0.2 起本节只讲通道**。客户端已从单文件 `Program.cs` 拆成
> 命令表 / 通道 / 探测 / guard / doctor / selftest 六个文件，新增自说明书（`help`、
> `--commands --json`）、危险命令 `--force` 闸门、`doctor` 六阶段体检与 `guard` 连通性兜底，
> 并以 `ClashUWP.Cli_1.0.2.0_x64.msix` 重新打包 —— 见 Docs/28。
> 下面这段协议描述仍然有效：两条通道（AppService 与 `debug-cmd.txt`）共用一张命令表。

协议与文件回路逐字对齐：请求 `ValueSet{["cmd"]=单行命令}`，响应 `ValueSet{["out"]=原文}` 或 `["error"]=原因`；命令行剩余参数用**一个空格**拼回单行，所以带空格的节点名加引号照抄即可。

```
clashuwp-cli status                     # 只读：内核运行态三要素
clashuwp-cli groups                     # 只读：选节点前必看（组名中文、节点名带 emoji 与前导空格）
clashuwp-cli logtail plugin 100         # 只读：插件进程侧日志尾部
clashuwp-cli --list                     # 只打印目标包 PFN（验证包发现）
clashuwp-cli --selftest                 # 自检 WinRT/AOT 通路，不激活任何应用
clashuwp-cli --pfn <PFN> status         # 指定目标包
clashuwp-cli --timeout 30 status        # 命令超时（秒，默认 120）
```

★ `--list` / `--selftest` **去掉前面的 `--` 也认**（实测两种写法都过）：走应用执行别名时，敲进去的第一个词就是别名本身（`clashuwp-cli selftest`，前面没有 `--` 可给），只认带连字符的写法会让它被当成命令原文发给服务端、回一句"未知命令"。服务端命令表里没有 `list` / `selftest` 这两条 ⇒ 不会撞车。

退出码：`0` 成功 / `2` 连接失败 / `3` 服务端返回错误 / `4` 参数与包发现问题 / `5` 超时 / `6` 自检失败。

两个实测发现（不是从文档读来的）：

- ★ **`OpenAsync()` 在服务端还没声明 `ClashUWP.Debug` 时不快速失败，而是无限挂起**（平台在尝试激活主应用）。第一版 CLI 就无声卡死在这里。所以连接也必须有闸：`OpenAsync().AsTask().WaitAsync(15s)` ⇒ 超时后明确报"服务端未声明 AppService"，退出码 2。
- **`PackageFamilyName` 不写死。** 主包 Identity Name 是那个 GUID（`421769c5-…`），FamilyName 的发布者 hash 后缀每次重装会变（项目记忆里「包名是 GUID」那条坑）。CLI 存 Name，运行时 `PackageManager.FindPackagesForUser("")` 反查 PFN。
- ★ **`AppServiceConnectionStatus` 和 `AppServiceResponseStatus` 是两套枚举，别把它们混成"连不上"。** 连接侧失败（`AppUnavailable` / `AppServiceUnavailable`）退出码 2；`OpenAsync` 已 `Success` 之后服务端没答上，回的是 `响应状态异常: <AppServiceResponseStatus>`、退出码 3 —— 1.0.42 就是这个形状，顺着"连接失败"查会走错方向，见 §6.2。

纪律照抄文件回路那条：`connect / disconnect / recreate` 会碰隧道，本工具只是把命令**原样传过去** —— 自动化脚本里不要无条件下发这三条，收工前确认没有自动重拨。

---

## 5. 打包：按官方 CLI 指南对齐，撞出一条校验器硬约束

官方口径 = [Packaging a CLI Executable as MSIX](https://learn.microsoft.com/windows/apps/dev-tools/winapp-cli/guides/packaging-cli)（winapp CLI 指南）+ [uap5:AppExecutionAlias](https://learn.microsoft.com/uwp/schemas/appxpackage/uapmanifestschema/element-uap5-appexecutionalias) / [Application](https://learn.microsoft.com/uwp/schemas/appxpackage/uapmanifestschema/element-f-application)（清单架构参考）。

| 改动 | 对应官方出处 |
| --- | --- |
| `<uap:VisualElements AppListEntry="none">` | 指南 4.2："CLI tools are invoked from the terminal, so they don't need a Start menu entry"。★ 它是 **VisualElements 的属性**，挂到 `<Application>` 上部署直接 0xC00CE015（主应用那份清单实测过）。 |
| `uap5:Extension Category="windows.appExecutionAlias"` + `ExecutionAlias Alias="clashuwp-cli.exe"` | 指南 4.3。装包后 Windows 在 `%LOCALAPPDATA%\Microsoft\WindowsApps`（在 PATH 上）建重解析点，卸包自动删 —— 这是打包应用进 PATH 的**唯一**正当途径。 |
| `<uap5:AppExecutionAlias desktop4:Subsystem="console">` | 架构参考把 `Subsystem` 列为该元素的可选属性（`console`/`windows`，别名最低 OS 16299）。 |
| `<Application desktop4:SupportsMultipleInstances="true">` | ★ **不是选型，是硬前提**，见下。 |
| `<Properties><Description>` | 指南 4.4 要求 Properties 里写 Description。 |
| `Publisher="CN=gold"` 与签名证书主体一致 | 指南第 5 步的 Important：Publisher 必须匹配签名证书主体。 |
| `Identity Version` 1.0.0.0 → 1.0.1.0 | 内容与清单都变了；升级安装按版本判定。 |

★ **撞出来的那条约束（makeappx 原文，0x80080204）：**

```
The AppExecutionAlias element cannot declare the attribute Subsystem with value console
without declaring the attribute SupportsMultipleInstances with value true in element Application.
```

即别名上声明 `Subsystem="console"` ⇒ `<Application>` 必须同时 `SupportsMultipleInstances="true"`。这与架构参考 Application 那页的备注是同一件事的两面："Console apps will always be multi-instanced and must explicitly declare SupportsMultipleInstances." —— 所以官方「Create a UWP console app」模板把两个属性一起写，不是 C++/WinRT UWP 控制台应用的特供仪式，是校验器强制成对。对本工程无副作用：full-trust 桌面桥 exe 本来就每次调用起新进程（调试 CLI 连着发多条命令是常态）。**唯一要记的连带约束**：声明了 `SupportsMultipleInstances` 就不能再声明 `ResourceGroup`（互斥）。

★ 顺带第二次踩到的同一类坑：**XML 注释里不能出现连续两个连字符**。这文件里吃过两次 —— ① `winapp package` 的占位符扫描**连注释一起扫**（注释里写了那个构建期 token 字面量 ⇒ "manifest contains unresolved placeholders"）；② 注释里写 `--manifest` / `--selftest` 这种双连字符 ⇒ `Xml_InvalidCommentChars`，或 csproj 直接 `MSB4025` 打不开。**清单与 csproj 的注释里不要写带 `--` 的命令行**，要写就写"selftest 命令（见 Program.cs）"。

### 5.1 发布形态换成 Native AOT（官方对可执行文件的推荐），并且实测过投影可用

AOT 编译通过**不代表** WinRT 投影跑得起来：`ValueSet` 本质是 `IDictionary<string, object>`，`IAsyncOperation<T>.AsTask()` 也是泛型跨 ABI —— 这类缺口是**运行时**才炸。所以留了一条 `--selftest`，四步全在本地、不激活主应用、不碰隧道：

```
[PASS] ValueSet 读写 (count=1, type=String)
[PASS] AppServiceConnection 构造/属性回读
[PASS] PackageManager 枚举到 236 个包
[PASS] OpenAsync 异步通路 (不存在包→状态=AppNotInstalled，预期非 Success)
自检结论: AOT 下 WinRT 通道可用
```

第 4 步连的是**故意不存在**的 PFN：没有包可激活 ⇒ 不启动任何应用，只把 `OpenAsync().AsTask().WaitAsync()` 这条异步跨 ABI 通路走一遍，返回 `AppNotInstalled` 正是预期。

同一份源码的量化对照（本机 x64，2026-10-18）：

| 发布形态 | publish 布局 | 冷启动 `--list`（5 次） | msix 体积 | 目标机要求 |
| --- | --- | --- | --- | --- |
| framework-dependent | 27 MB（其中 `Microsoft.Windows.SDK.NET.dll` 单文件 26 MB） | min 244 / **avg 300** / max 493 ms | 7.18 MB | 必须装 .NET 10 运行时 |
| **Native AOT**（+ `StripSymbols`，`DebugType=none`） | 4.0 MB 单文件 | min 57 / **avg 77** / max 109 ms | **2.13 MB** | 无 |

（启动用 `Measure-Command` 连跑 5 次取 min/avg/max；AOT 侧没有第三方依赖，只对比自己的两种发布。）
csproj 里 `PublishAot` / `StripSymbols` / `DebugType=none`（Release 才关符号）/ RID 按 Platform 映射都写死了，`dotnet publish -c Release` 不带 `-r` 就出单文件。

### 5.2 可复现配方（当前产物：`ClashUWP.Cli\ClashUWP.Cli_1.0.1.0_x64.msix`，签名 Valid，CN=gold）

```powershell
cd ClashUWP.Cli
dotnet publish -c Release -o bin/publish        # csproj 已开 AOT，不需要 -r
Copy-Item -Recurse Assets bin/publish/          # folder mode：清单引用的图标必须在布局里
winapp package bin/publish `
  --manifest Package.appxmanifest `
  --cert ../ClashUWP/ClashUWP_TemporaryKey.pfx --cert-password ""
```

两个只有实测才知道的细节：VS 生成的 `TemporaryKey.pfx` **口令为空**（不传空口令是 `Cryptography_Pfx_BadPassword`）；`winapp manifest generate` 出来的清单里 `Executable` 是构建期 token，folder mode 没有 msbuild 目标去替换它，必须改成真实 exe 名。

★ 没验过的两件事，别说成做了：**arm64 的 AOT 发布没跑过**（本机 x64；`dotnet build -p:Platform=arm64` 是通的，0 警告 0 错误，所以 VS 里选 arm64 不会连编译都进不去，但 AOT 链接到 arm64 原生产物这一步没执行）；**多架构 `.msixbundle` 没打**（官方指南 Tip 3 是 `winapp pack ./x64 ./arm64`，要两条发布都成功才有意义）。

---

## 6. 安装与首测

部署走 Visual Studio，这是项目规矩（AGENTS 第 2 条：不绕开 VS、不自制 ps1）；`Add-AppxPackage` 在这台机器上也不可用。CLI 包由使用者自己双击安装（自签证书需已信任）。

★ **【2026-10-22 01:15 订正 · 两处已过期】**
· "`Add-AppxPackage` 在这台机器上也不可用"**是错的**：127 那次是**在 pwsh 7 里跑的**（7 没带 Appx 模块），
  Windows PowerShell **5.1 一直可用**（Docs/30 §5 已订正过一次）。本批 CLI 1.0.3.0 / 1.0.4.0 两次装机走的都是
  `powershell -NoProfile -Command "Add-AppxPackage -Path '…\ClashUWP.Cli_1.0.x.0_x64.msix'"`，**无输出即成功**；
  ★ 参数名是 **`-Path`**（`-Package`/`-FilePath` 都报 `NamedParameterNotFound`）。
· 下面那条"`--list` 应打印 `421769c5-…_<hash>`"也已经过期：主清单 Identity Name 在 `3b11f69`（1.0.74→1.0.75）
  改成了 `8B8CC4F4.ClashUWP`，而 CLI 里的 `TargetPackageName` 没跟着改 ⇒ **从 1.0.75 起 `--list` 一律"找不到目标包"**。
  修法与装机凭据（现打印 `8B8CC4F4.ClashUWP_4tjvwvefxrz66`）在 **Docs/38 §十四 · F5**。
· ★ 顺带记下本批量到的一个包身份事实：**主包与本 CLI 包现在不是同一个发布者** —— 主清单 `Publisher="CN=A19C62A9-…"`
  （家族后缀 `4tjvwvefxrz66`），本工程 `Publisher="CN=gold"`（后缀 `y0tg80em3rma2`）。
  ⇒ **别拿本包的后缀去猜主包**（当年那份过期 GUID 恰好带着同样的 `_y0tg80em3rma2`，看上去"没漂"，就是把两个家族混了）。

2026-10-18 补：agent 侧可行的等价做法是**只拉起官方"应用安装程序"，点它自己的按钮** —— `Start-Process <msix>` 让系统用桌面上的 `AppInstaller.exe` 弹框，再用项目已有的 `winapp ui list-windows / inspect / invoke` 先读页面上写的是哪个版本、确认无误后 `invoke primaryButton`。全程没有自制部署脚本，MSIX 仍走系统唯一的正式安装通道。

⚠ 但这次真正拦住安装的不是包，是**部署队列被一次中断的 Remove 占死了**（见 §6.1）。先诊断再点安装，否则只是往队列里再塞一条永远不会执行的请求。

装完先别名，再只读：

```
where.exe clashuwp-cli.exe        # 别名解析到哪（应命中 %LOCALAPPDATA%\Microsoft\WindowsApps）
clashuwp-cli --selftest           # 不激活主应用，纯验通道
clashuwp-cli --list               # 验包发现，应打印 421769c5-…_<hash>
clashuwp-cli status               # ← 第一条真正打到主应用的命令，只读
clashuwp-cli groups
clashuwp-cli logtail app 40
```

安全前提已核对过：`MainComponent.cs` 里只有三个按钮回调会走 `VpnSession.RecreateAsync / ConnectAsync / DisconnectAsync`，**没有启动即拨号的代码**，全仓也没有 `autoConnect` / `AutoInterconnect` 声明；启动那条路（`Diagnostics.MaybeRunProbesAsync`）默认**跳过**，要 `LocalState\settings.txt` 里写 `autoProbe=1` 才会跑探测 —— 而探测的动作形状是"建 `ClashUWP-Dataface` → 连 → 打流量 → 断 → 删"（Docs/09、项目记忆「API audit findings」第 3 条）。

★ 所以第一条 `clashuwp-cli status` 之前，先看一眼 `settings.txt` 有没有 `autoProbe=1`：CLI 的连接会**激活主应用进程**，那串启动逻辑就跑一遍。这一枪由人打，不由脚本打。碰隧道的 `connect / disconnect / recreate` 仍要人工明确下发；收工判据沿用项目记忆里的三条（没有自动重拨、`nrpt=0`、死接口路由=0）。

**当前状态（2026-10-18 03:42 更新）**：端到端**已打通**。`1.0.44.0` 装在本机（`421769c5-…_y0tg80em3rma2`，Status Ok），CLI `1.0.1.0` 别名可用，四条只读命令全过：`--selftest` 4/4 PASS、`--list` 打出 PFN、`status`/`groups` 打到主应用并由它回 `HttpRequestException … (127.0.0.1:9090)`（内核没起，控制器不在听 —— 这条失败恰恰证明命令进了 `OnBackgroundActivated`、执行了、结果原样回来了）、`logtail app 30` 拿到完整日志原文。中间踩到的 AOT 投影坑见 §6.2。

★ **主应用那份清单已经过 MakeAppx 校验**（2026-10-18 用项目自带的 `Tools\UwpBridge\uwp-bridge.exe -build ClashUWP\ClashUWP.csproj -configuration Release -platform x64 -target Publish` 走 VS 自己的 msbuild，91 秒成功，产出 `ClashUWP\AppPackages\ClashUWP_1.0.42.0_x64_Test\ClashUWP_1.0.42.0_x64.msix`，签名 Valid）。核对过包内 `AppxManifest.xml`：`<uap:Extension Category="windows.appService"><uap:AppService Name="ClashUWP.Debug" />` 被平台收下、Plugin 那条 `backgroundTasks` 的 `Executable` 已从 token 解析成 `ClashUWP.exe`、Package 级 `inProcessServer` 仍指向 `ClashUWP.dll`；payload 里 **没有** `ClashUWP.Vpn.dll` / `WinRT.Host.*` / `WinRT.Runtime.dll`（全 AOT + merge 形态符合预期），并且 `ClashUWP.dll` 里确实含 `DllGetActivationFactory` / `DllCanUnloadNow` 与 `VpnBackgroundTask` 符号 —— 声明与模块导出对得上。
教训仍然成立：**清单里的错是打包期错误，不是运行期错误**，改完清单必须真打一次包才算验过。

### 6.1 部署队列卡死（0x80073CF8）：一次中断的 Remove 会永久占住整条包家族队列

2026-10-18 装 `1.0.42.0` 时装不上：安装框一直停在"为系统做好安装准备…"，等了 8 分钟不动。**根因不在包，在部署队列**，而且这条判据以后每次装包前都该先跑一遍。

证据（全部只读取得，无需管理员）：

```
# 1) 已注册包的状态旗标——装机前先看清
Get-AppxPackage *421769c5* | Select Version,Status
#   1.0.38.0 | DeploymentInProgress, Servicing   ← 我点安装之前就是这样

# 2) 部署服务端日志：请求有没有"出队"
Get-WinEvent -LogName 'Microsoft-Windows-AppXDeploymentServer/Operational' -MaxEvents 800 |
  ? { $_.Message -match '421769c5|ClashUWP' } | Sort TimeCreated | Select TimeCreated,Id,Message
```

事件语义是关键：**603 = 请求已入队**，**607 = 已出队、正在跑**，**400/613 = 成功结束**，**404 = 失败**，**615 = 被调用方取消**，**10002 = 建 `.rslc` 韧性文件**。卡死的形状就是：

- 前一晚 23:39:55 有一次 `Remove … 1.0.38.0` 拿到过 607（正在跑），**之后再没有它的 400/404/613** —— 调用方进程早就不在了，服务端却仍认为它在跑，于是 `DeploymentInProgress` 一直挂着。
- 此后同族的每一条操作只有 603、永远等不到 607：23:46 / 23:49 / 23:51 三次 Add `1.0.39.0`，0:00 / 0:02 的 Remove（0:04、0:08 报 **0x80073CF8 = `ERROR_INSTALL_CANCEL`「用户取消了安装请求」**并配 615"被调用方取消" —— 是调用方自己等不下去放弃了，**不是**被队列拒绝），以及我这次 1:05:55 的 Add `1.0.42.0`。日志里那条 `Performance summary … Failed to reach state DeQueued` 才是"排不上队"的直接证据。
- 全机只有这一个包家族带 `DeploymentInProgress`，其它包照常 ⇒ 部署服务本身没挂，**堵的是这条家族的队列**，所以别去重启 `AppXSvc` 指望全局刷新，也别指望它自己超时。

两条踩过的弯路，记下来别再走：

1. **杀挂死的客户端解不开。** 23:55 我自己那条 `uwp-bridge.exe -install …1.0.39….msix` 连同三层 `bash` 包装确实还活着（`Get-CimInstance Win32_Process` 按 `CreationDate` 区间筛就能揪出来），杀掉它们、并在安装框点"取消"（会弹"是/否"，`cancelConfirmButton`＝是）后重新发一次 Add，依旧只有 603、没有 607。占用队列的是**服务端那条孤儿 Remove**，不是客户端。
2. **千万别用 Remove 来"解锁"。** 一是 Remove 同样排在死队列后面，二是本包的 Remove 会连带删掉 `LocalState`（`config.kernel.yaml`、`probe.txt`、`rasphone.pbk`、订阅）与 VPN profile —— 项目记忆里已经记过一次。而 1.0.38 → 1.0.42 的 **Add 不碰这些数据**，所以正确姿势是想办法让 Add 能出队，不是先卸再装。

**重启服务实测无效，只有开机恢复能解**（01:49–01:56 逐步做过，别再浪费时间）：

1. 提权 `Restart-Service AppXSvc -Force` —— 服务回到 Running，旗标纹丝不动，重发的 Add 依旧只有 603。
2. 提权 `Stop/Start AppXSvc` + `Stop/Start StateRepository`（按依赖顺序：先停 AppXSvc 再停 StateRepository，反向起）—— 两服务都 Running，`Get-AppxPackage` 能正常列出 236 个包（说明注册库没坏），但这条家族仍是 `DeploymentInProgress, Servicing`，Add 还是不出队。牵连面实测：`TermService` / `UmRdpService` 起来后自动回到 Running，`WpnUserService` / `WSAIFabricSvc` / `camsvc` 停在 Stopped（都是按需触发起动，不影响本机使用）。所以**动手前先查依赖**：`(Get-Service StateRepository).DependentServices` 里含 `TermService`，若有人在用远程桌面就别重启它。
3. 决定性证据（要管理员，只读）：`Get-ChildItem C:\ProgramData\Microsoft\Windows\AppRepository -Filter *.rslc -Force`（非管理员直接 `UnauthorizedAccess`）**只剩一个**文件，`…_S-1-5-21-…-1000_3.rslc`，`LastWriteTime = 2026-10-17 23:39:55` —— 正是事件 10002 为那条孤儿 Remove 建的韧性文件，还躺在盘上等恢复。而"运行中重启服务不会触发恢复"这条是**本机实测结论**（见上面第 1、2 步），不是文档原文：官方文档从没写恢复具体在哪个时机跑，只写了韧性文件机制的存在（`0x80073D03` / `0x80073D09` = "无法恢复该操作，因为恢复所需的数据已损坏"，见下面核对段）。
4. 同时 `Get-AppxPackage -AllUsers <GUID>` 显示 `PackageUserInformation : {…-1000 [gold]: Installed}` —— 包对用户仍是完整 Installed，**数据没被半个地拆掉**，卡住的只是队列。

⇒ 唯一剩下的杠杆是**重启整机**，让开机时的 `.rslc` 恢复把那条中断的 Remove 收掉（按韧性语义是回滚到上一个已知好状态，即保留 1.0.38 —— 这一条是推断，文档不保证究竟回滚还是补完），旗标随之清掉。重启前先给 `LocalState` 买份保险：把 `LocalState` 整目录（约 7.6 MB，含 `config.kernel.yaml` / `probe.txt` / `rasphone.pbk` / `logs`）连同 `%APPDATA%\Microsoft\Network\Connections\Pbk` 一起复制走 —— 本次已复制到 `D:\fluentapps\backups\ClashUWP-predial-20261018-0159\`。重启后先确认 `Status` 不再带 `DeploymentInProgress`，再双击装 `1.0.42.0`（Add 不动这些数据）。

### 官方口径核对（2026-10-18 联网，四篇 + 一条 Q&A）

- **`PackageStatus` 语义**（Inside MSIX「[Package Status](https://devblogs.microsoft.com/insidemsix/package-status/)」）：原文是 "DeploymentInProgress indicates that the package is currently participating in a deployment operation such as installation, repair, update, or removal."，`Servicing` 同组也表示"正在参与部署操作"。**这篇只定义状态、不给恢复步骤** —— 旗标卡住这件事没有文档化的运行中修法，我们的实测正好补上这一格。
- **[MSIX 部署故障排除](https://learn.microsoft.com/en-us/windows/msix/desktop/managing-your-msix-deployment-troubleshooting)**（2026-04-15 更新）整篇手段限于：读 `AppxDeployment-Server` 日志、`Get-AppxLog | Where EventId -eq 404`、按错误码表处置、`wsreset -i`、`sfc /scannow`、设置里的"修复"、补依赖、证书放**本机** Trusted People。**没有一条针对"请求排队不出队"**。码表里最接近的两个都不是本例：`0x80073D02 ERROR_PACKAGES_IN_USE` 官方修法是"关掉正在运行的实例"（本机 `Get-Process` 无任何 ClashUWP 进程），`0x80073CFA ERROR_REMOVE_FAILED` 是"关实例，状态损坏时在**干净启动**环境下重试"。
- **[打包、部署和查询故障排除](https://learn.microsoft.com/en-us/windows/win32/appxpkg/troubleshooting)** 全量码表把 `0x80073CF8` 定义为 `ERROR_INSTALL_CANCEL`（用户取消了安装请求）—— 我先前把它当成"部署已在进行中"是**读错**，已在上文改正。同表里 `0x80073D00 = ERROR_PACKAGE_UPDATING`（正在更新 ⇒ 应用起不来）、`0x80073D03 / 0x80073D09` = 恢复所需数据已损坏、`0x80073CFE = ERROR_PACKAGE_REPOSITORY_CORRUPTED` 与 `0x80073D07` 的官方恢复动作都是**"刷新电脑"**；`0x80073D01` 只在"用临时配置文件登录"这一情形下写了"注销并重新登录再试"，那是 roaming/temp profile 专用，不覆盖本例。
- **Q&A 里对"能不能手工修 StateRepository"的边界**（[AppX/MSIX deployment consistently fails at Indexed stage](https://learn.microsoft.com/en-nz/answers/questions/5983152/appx-msix-deployment-consistently-fails-at-indexed)）：明确写着"**没有文档化、受支持的命令可以只为一个包家族重建 StateRepository / stateExtension**；`Reset-AppxPackage` 只作用于已安装包、不重建部署元数据；不要手删数据库行、`PackageVolumes` 注册表键或受保护的 `WindowsApps` 目录。受支持的边界只有：包的移除/重注册、换 Windows 配置文件验证、或重置/重装 Windows。" ⇒ 所以**别去动 `WindowsApps` 和注册表**，那是官方点名禁止的方向；本例连"移除重注册"这条路都被队列本身堵死，只剩重启（必要时干净启动，即上面 `0x80073CFA` 那格的 official 说法）。

顺带一条与本节无关但省事的核对：`settings.txt` 是 `LocalState` 下的可选文件，**没有这个文件就是 `autoProbe=0`**（`AppSettings.LoadAsync` 先置默认再找文件，找不到直接返回），所以让安装框勾着"当准备就绪时启动"并不会自动拨号。

### 6.2 AOT 把 `TriggerDetails` 退化成了 `WinRT.IInspectable`：1.0.42 → 1.0.44 的三步取证

现象只有一行，`clashuwp-cli status` 回 **`响应状态异常: Failure`**。注意这是 **`AppServiceResponseStatus`**，不是 `AppServiceConnectionStatus` —— 即 `OpenAsync` 已经 `Success`（连接建起来了、平台也激活了服务端），**是响应没有产生**。所以别往"连不上"方向查。

第一轮猜测是"平台没投递 `OnBackgroundActivated`"（前台窗口在跑也一样没投递）。**1.0.43 只加诊断日志、不改逻辑**，为的就是把两种情况分开：

```
2026-10-18 03:25:52.294 pid=13120 app I appservice 激活到达 task= trigger=WinRT.IInspectable pid=13120
2026-10-18 03:25:52.294 pid=13120 app I appservice 触发细节不是 AppServiceTriggerDetails，忽略本次激活
```

两行 ⇒ 投递是**有的**，是 `args.TaskInstance.TriggerDetails is AppServiceTriggerDetails` 这个**人人都这么写的官方形状**在我们身上恒为 false。真实类型是 `WinRT.IInspectable`。

- 上游已开单：[microsoft/CsWinRT#1860 "Cannot cast some of the UWP activation arguments using dotnet as operator"](https://github.com/microsoft/CsWinRT/issues/1860)（open）。报告人的场景与我们**逐条同款**：UWP + Native AOT + in-app AppService + Win32 助手，`OpenAsync` 成功后连不上；`as` 与 `(T)` 都失败（`InvalidCastException`），`GetType().FullName` 在 AOT 发布下正是 `WinRT.IInspectable`、非 AOT 下正常。
- 维护者（`dongle-the-gadget` / `manodasanW`）给的口径：AOT 推不出 `object` 的真实类型，落到 `IDynamicInterfaceCastable`，而 `AppServiceTriggerDetails` 的 helper 类型是 struct 不是 interface ⇒ 转换失败；而 `As<TInterface>()` "works because it is making sure the type doesn't get trimmed"。**修法就是 `WinRT.CastExtensions.As<T>(this object)`**，2.3.1 里它是 public（反射核过：`WinRT.Runtime.dll` 静态方法表只有 `As` / `AsAgile`，**没有 `TryAs`**，别按 1.x 的直觉写 `TryAs`）。

1.0.44 的实现是"先 `as`，拿不到再 `As<T>()`，两条都记 `path=`"。装完打第一条命令：

```
2026-10-18 03:41:32.421 pid=16468 app I appservice 连接建立 path=as rawType=Windows.ApplicationModel.AppService.AppServiceTriggerDetails name=ClashUWP.Debug caller=ClashUWP.Cli_y0tg80em3rma2 pid=16468
2026-10-18 03:41:32.422 pid=16468 app I cmd status
```

★ 反直觉的一点：**救场的是 `as` 分支，不是 `As<T>()`**，而代码里 `As<T>()` 一行没被执行过。对上维护者那句"防止被修剪"就通了 —— 方法体里留着 `As<AppServiceTriggerDetails>()` 这个调用点，投影类型被根住，`TriggerDetails` 的 getter 这才返回真投影类型。所以那个兜底分支**同时是锚点**：把它删掉，`as` 会退回 `WinRT.IInspectable`、通道再次静默失效。要精简这段代码之前，先看这一条。

### 6.3 顺带量到的两个宿主行为（都影响"遥控"的语义，不是 bug 但要记）

- **主应用在跑时，连接确实落在前台进程**：03:41:32 那次由 UI 实例 `pid=16468`（有 `diag 装配完成` 那几行）服务 ⇒ §3 里"in-app 形态 = 同进程、`VpnSession` 状态可及"的前提成立。
- **主应用没在跑时，每次连接都会新起一个无窗口实例，命令跑完这个实例就没了**：`pid=11836`（03:41:42）、`pid=7692`（03:42:20）两次的启动日志都只到 `probe 启动探针完成`，**没有** `diag 装配完成` / `appsettings` / `dump` —— 因为那些在 `MainComponent` 里，无窗口实例走不到。收工查 `Get-Process ClashUWP` 是空的，不留残留进程。有窗口的实例会不会也一起退出，见下面「待验」那条。
  - 只读命令不受影响（`logtail` 读文件、`status`/`groups` 打 `127.0.0.1:9090` 是机器级端口）。
  - ⚠ 但 **`connect / disconnect / recreate` 在没有前台实例时，会在一个注定被回收的无窗口进程里执行**：那里的 `VpnSession` / 健康哨兵不是 UI 那份状态，UI 也看不到会话变化。**要遥控拨号，先把主应用开着**（这条是本次量到的事实推出来的操作纪律，不是官方文档）。
  - ⚠ `ServiceClosed` 的 `连接关闭 status=…` 一行在 1.0.44 实测**整份 app.log 里一次都没有**（`rg -c 连接关闭` = 0）。客户端进程直接退出时，这个事件要么没投递、要么宿主先被拆掉了来不及写。别把它当"连接还开着"的判据，也别在它没出现时下结论。
- ⚠ **待验（本机没能定因，别当结论用）**：两个**有窗口**的实例（`pid=16468` 由安装框"当准备就绪时启动"拉起、`pid=1848` 由 `shell:AppsFolder` 激活）都在服务完一条命令后十几秒内消失，`Get-Process ClashUWP` 归零，而 `Microsoft-Windows-AppModel-Runtime/Admin` 与 `Application Error` 里**没有任何对应的销毁或崩溃事件**。两种解释还没分开：① 人把窗口关了（当时确实在用这台机器）；② 连接生命周期真的把宿主拆了。干净的对照实验是"激活主应用、**不发任何 CLI 命令**、每 5 秒数一次进程数持续 30 秒" —— 这条要在别人的机器上跑之前先打招呼，它会反复弹出应用窗口。

---

## 7. 本轮改了哪些文件

| 文件 | 改动 |
| --- | --- |
| `ClashUWP\Package.appxmanifest` | 版本 1.0.41.0 → **1.0.42.0**；`<Application Id="App">` 下加 in-app `windows.appService` 声明（无 EntryPoint）。 |
| `ClashUWP\App.cs` | `OnBackgroundActivated` override：连接级/请求级 deferral、串行闸、`CallerPackageFamilyName` 审计、错误走 `["error"]`。 |
| `ClashUWP\Backend\DebugCommandLoop.cs` | 命令表从文件循环里抽成 `ExecuteAsync(string raw)`；`RunAsync` 退化成"读命令 → 执行 → 写结果"的薄壳。命令语义与条数未变。 |
| `ClashUWP.Cli\` | 新增工程：`Program.cs`（协议 + `--list/--selftest/--pfn/--timeout` + 退出码）、`ClashUWP.Cli.csproj`（AOT 发布形态）、`Package.appxmanifest`（别名 / console / 多实例 / AppListEntry=none）、`Assets\`（winapp 生成的图标）。 |
| `ClashUWP.slnx` | 加 `ClashUWP.Cli` 工程，x64/arm64 平台映射，**不加 `<Deploy/>`**（桌面客户端不属于 UWP 部署项）。 |
| `.gitignore` | `*.msix` / `*.msixbundle`：winapp 产物每次重打包都变，不入库。 |

1.0.42 → 1.0.44（端到端首测与修复，见 §6.2/§6.3）：

| 文件 | 改动 |
| --- | --- |
| `ClashUWP\App.cs` | 1.0.43 加"激活到达 / trigger="诊断（正是它把"没投递"和"投递了但类型不对"分开）；1.0.44 改成 `as` + `WinRT.CastExtensions.As<AppServiceTriggerDetails>()` 双路径并记 `path=`；`cmd` 值不是 `string` 时补一条类型日志（`ValueSet` 的值同样是 `object`，是同一个坑的高危点）。 |
| `ClashUWP\Package.appxmanifest` | 版本 1.0.42.0 → 1.0.43.0 → **1.0.44.0**（只为加日志与修投影，各发一版以便日志对得上版本号）。 |
| `ClashUWP.Cli\Program.cs` | 无改动：1.0.42 那两条超时闸（连接 15 s / 命令 `--timeout`）在首测里都按预期工作。 |
