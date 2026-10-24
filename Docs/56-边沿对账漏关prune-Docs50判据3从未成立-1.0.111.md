# 56 · 边沿对账那轮漏关 prune —— Docs/50 §五 判据 #3 从 1.0.95 起从未成立（1.0.111）

发现方式：**回读代码**（承 Docs/55 §八 同一手法，那一次抓出第一段的"无会话"冒充，这一次抓出对账分工）。
用户口径是"都修好"，我把 1.0.110 那条"读不出当没有"的判据病顺着同一条链扫到账本/对账路径，扫出这一处。

## 一、结论速览

| 项 | 内容 |
| --- | --- |
| 缺陷 | 前台 3 秒轮询里的**会话边沿那一轮**调用写成 `SyncSystemProfilesAsync("边沿")`，而 `allowPrune` 的**默认值是 `true`** ⇒ 边沿那一轮一直在跑 prune（删系统条目） |
| 应然 | [Docs/50 §二.2 第 3 条](50-快速设置面板条目同步-切订阅推整份清单加会话边沿补对账-1.0.95.md)明写"边沿那一轮 `allowPrune=false`……prune 仍只留给挂载那一轮"；§五 第 3 条把它写成装机判据 |
| 影响面 | 1.0.95 ~ 1.0.110 **全部装机期**（这条设计一次都没生效过） |
| 危害等级 | **不是数据损坏**：prune 自己的条目级闸从 1.0.96 起是"读不出 ⇒ 保留"、活动会话 ⇒ 保留，所以它没删过不该删的（本机日志里 `删 0`，只有一次 `删 1` 且发生在切换那轮——那轮按设计就该 prune）。真正的代价是**分工被打破**：刚结束的会话与删除抢同一个 RPC，以及**判据 #3 是一句无法通过的空文** |
| 改法 | 调用点补 `allowPrune: false`；汇总行"不 prune"文案改成按 `trigger` 插值（边沿那句**逐字不变**，补账那轮不再冒充边沿） |
| 版本 | `1.0.110.0` → **`1.0.111.0`**，已编译、重签、装机 |
| 装机状态 | 二进制与挂载行为 ✅；**行为判据（边沿行尾必须是"本轮不 prune（…）"）⬜ 未验** —— 制造一次会话边沿要拨号，等用户点头（与 Docs/55 判据 B/C/F 同一道闸）；★ 试过的绕道「用 Windows Sandbox 造边沿」已探完：**本机沙盒初始化就失败（0x8007007E），给不了这条判据**，细则见 §七 |

## 二、一手证据：这两行就是"判据不成立"本身

`app.log`（`8B8CC4F4.ClashUWP_…_4tjvwvefxrz66\LocalState\logs`，10-23 装的是 1.0.109/1.0.110 那条线）：

```
2026-10-23 23:13:46.652 pid=1900  I profile-sub-sync 边沿对账：订阅 2 份 + 基础 profile 1 条，新增 0 / 更新 0 / 已就位 3 / 跳过 0 / 失败 0；prune 幽灵 profile：删 0 / 留 0
2026-10-23 23:16:22.560 pid=8080  I profile-sub-sync 边沿对账：订阅 2 份 + 基础 profile 1 条，新增 0 / 更新 0 / 已就位 3 / 跳过 0 / 失败 0；prune 幽灵 profile：删 0 / 留 0
```

Docs/50 §五 第 3 条要求的行尾是 `本轮不 prune（边沿只补条目，删除留给挂载那一轮）`，并且它自己规定：出现 `prune 幽灵 profile：删 N`（**它没限定 N>0 才算前缀不合格，但即使按最宽的读法，"这轮根本不该出现 prune 结果"也已经成立**）。

## 三、为什么十四版没人撞见它（判读法的问题，比代码缺陷更值得记）

先把事实分清：**1.0.95 那批根本没装机**（Docs/50 §一 明写"全是代码级，未打包、未装机、未拨号"）⇒ 判据 #3 从来没有被正式判读过，谈不上"判读判错了"。真正发生的是后面这十四版：`边沿对账` 行反复出现在 `app.log` 里（上面那两行就是 1.0.109/1.0.110 装机期），每次都被读到，但**没有人拿 §五 #3 去对它** —— 因为那条判据要"两条同名订阅 + app 内 A→B 切换"这个前置形状（§五 #2），本机一直没造出来，于是整条 #3 被当成"还没轮到验"，而不是"验了但不成立"。

而它其实**每次出现都可以当场判**：判据问的是"这一轮跑没跑 prune"，答案就在行尾形状里，不在 `删 N` 的数字里。数字为 0 有两种来路（没跑 / 跑了但没有可删的），只看数字分不开。
⇒ 口径订正（这条是本轮真正要留下的）：
1. **"某一轮不该做某件事"的判据，按该轮输出里有没有这件事的产物来判**，不许按产物计数为 0 来判；
2. **装机时读到的判据行，当场对着 §五 逐条打勾或打叉**，不许因为"这条的前置形状还没造"就整体顺延 —— 前置造不出来只影响"能不能验正向"，不影响"能不能验出违反"。同族坑见 Docs/45 §八.5（"声称改了盘、只看自己写的日志行"）。

## 四、改法（两处，逐字可回读）

1. [MainComponent.cs:358](file:///D:/fluentapps/repos/ClashUWP/ClashUWP/Frontend/MainComponent.cs#L358) —— 边沿那轮补参数，同时把那段注释里"因为读数毒最容易在此刻成立"这条**过期理由**换成 1.0.96 之后的**分工**理由，并写明它从前是空头话：

```csharp
if (pendingRealign && !VpnSession.SwitchInProgress)
{
    pendingRealign = false;
    await SubscriptionManager.SyncSystemProfilesAsync("边沿", allowPrune: false);
}
```

2. [SubscriptionManager.cs:404](file:///D:/fluentapps/repos/ClashUWP/ClashUWP/Backend/SubscriptionManager.cs#L404) —— "本轮不 prune"文案从前硬写了"边沿"二字，于是 `补账` 那一轮（1.0.101 起就带 `allowPrune:false`）也在冒充边沿。改成按触发点插值：

```csharp
string prune = allowPrune
    ? await VpnProfileSetup.PruneOrphanProfilesAsync(list.Select(p => p.Id).ToList())
    : $"本轮不 prune（{trigger}只补条目，删除留给挂载那一轮）";
```

⇒ `边沿` 那一轮产出的字符串与 Docs/50 §五 #3 抄的**逐字相同**（判据字面匹配不破），`补账` 那轮从此如实写自己。

四个触发点归位后：`启动`=prune（唯一的删除点）/ `切换`=prune（三段式里它跑在"台上没有会话"的时刻，[Docs/55 §三](55-切订阅改走先断开后修改再重连三段式-反向修订1.0.93的切换不收尾-1.0.109.md)）/ `边沿`=只补 / `补账`=只补。

## 五、同一条链上扫完的其余"读不出当没有"口子（本轮没改，因为方向是对的）

| 位置 | 形状 | 判定 |
| --- | --- | --- |
| [VpnProfileSetup.cs:1108](file:///D:/fluentapps/repos/ClashUWP/ClashUWP/Backend/VpnProfileSetup.cs#L1108)（订阅条目更新闸） | `status != "Disconnected" ⇒ 跳过 + 记欠账` | 读不出 ≠ `Disconnected` ⇒ **保守方向正确**，不会在中毒条目上写 |
| [VpnProfileSetup.cs:1214](file:///D:/fluentapps/repos/ClashUWP/ClashUWP/Backend/VpnProfileSetup.cs#L1214)（基础条目） | 同上 | 同上 |
| [VpnProfileSetup.cs:1300](file:///D:/fluentapps/repos/ClashUWP/ClashUWP/Backend/VpnProfileSetup.cs#L1300)（prune） | `status != "Disconnected" ⇒ 保留`，1.0.96 已把放行改掉 | 同上 |
| [VpnProfileSetup.cs:1660](file:///D:/fluentapps/repos/ClashUWP/ClashUWP/Backend/VpnProfileSetup.cs#L1660)（`ConnectedSubIdAsync`） | 有条目活着但读不出 ⇒ 回 `""` | **假空**。危害面查过：账本回填 [MainComponent.cs:400](file:///D:/fluentapps/repos/ClashUWP/ClashUWP/Frontend/MainComponent.cs#L400) 有 `connectedSub.Length > 0` 前置闸 ⇒ 假空**不会**把生效订阅往"没会话"的方向改；它只让 UI 那一格显示没连、并多产生一次边沿 ⇒ 多跑一轮对账（本轮改完，那一轮只补条目不删）。这条属于 Docs/45/51 已定性的中毒后果，不是新缺陷，**不假装修好了** |
| [VpnProfileSetup.cs:1610](file:///D:/fluentapps/repos/ClashUWP/ClashUWP/Backend/VpnProfileSetup.cs#L1610)（并发观测） | 只报 `Connected` 的条目 | 假阴性，且只落日志不动手 ⇒ 可接受 |

## 六、装机记录（2026-10-24，全程零拨号）

| 项 | 结果 |
| --- | --- |
| 构建 | VS 桥 `uwp-bridge.exe -build …\ClashUWP.csproj -configuration Release -platform x64 -target Publish`：`ok:True exitCode:0 252 870 ms PackageSuccessfullyCreated`（日志 `clashuwp-build-1111-publish.log`） |
| 陈旧清单处置 | 按 [Docs/42 §二.4](42-商店提交实机填写-上传包内层版本被Upload陈旧清单钉死.md)：`Upload\AppxManifest.xml`（钉在 `1.0.96.0`，时间戳 10-23 04:55）改名 `AppxManifest.xml.stale-1.0.96.bak` 挪走 → 重建 → 重新生成 `02:06`、`Version="1.0.111.0"`、22 043 B（比陈旧那份大 6.8 KB，因为 1.0.106 加的 `VpnHost` 入口进来了） |
| 包内 identity 回读 | `AppPackages\ClashUWP_1.0.111.0_x64_Test\…msix` = **1.0.111.0** ✅；`Upload\ClashUWP_1.0.111.0_x64\…msix` = **1.0.111.0** ✅（两处都开包读 `<Identity>`，不信目录名）；`.msixupload` 内层就是 Upload 那份（体积 51 653 133 B 逐字对上） |
| 重签 | `signtool sign /fd SHA256 /sha1 FB3309A6…` → `Successfully signed`；`verify /pa` → `Successfully verified`（需 `MSYS2_ARG_CONV_EXCL='*'`） |
| 装机前置 | `rasdial` = **No connections**（绝不带会话杀进程）→ `Stop-Process -Name ClashUWP -Force` → 残留进程数 0 → `Add-AppxPackage`（位置参数，5.1 无 `-Package`） |
| 装后 | `8B8CC4F4.ClashUWP_1.0.111.0_x64__4tjvwvefxrz66`（**同家族** `4tjvwvefxrz66` ⇒ `LocalState` 与已注册 profile 不动）；包内 `ClashUWP.dll` 时间戳 02:09（证明装的是本次二进制，不是 1.0.110 的残留） |
| 行为核对（零拨号部分） | pid 19964 挂载：`autoProbe=0` ⇒ 探测跳过（不建 profile、不连 VPN）、`启动对账 … 已就位 3 / 跳过 0 / 失败 0；prune 幽灵 profile：删 0 / 留 0`（挂载那轮按设计就该 prune）；无 `0x80070006`、无 `拒绝`、无 `本轮放弃`；台账 `profile-reconcile-owed.txt` 仍 0 字节；NRPT 规则 0 条；`debug-cmd.txt` 无残留命令；收工时 `rasdial` 仍 No connections |
| **未验** | 判据「每条 `边沿对账：…` 行尾 = `本轮不 prune（边沿只补条目，删除留给挂载那一轮）`」⬜ —— 要产生一次会话边沿（`connectedSub` 变化）必须真拨一次隧道；本机现在 `[星链机场]` 条目读数中毒 ⇒ 不拨号时 `connectedSub` 恒为 `""`，边沿出不来。与 Docs/55 §五 判据 B/C/F 一起等"可以拨号"的明确指令。**沙盒绕道已排除，理由见 §七** |
| 离线套件 | `Tools\run-offline-tests.cmd` → `ALL OFFLINE TESTS PASSED`，exit 0（`offline-tests-1111.log`） |

## 七、"用 sandbox 测"这条路已探完：走不通（2026-10-24）

§六 那行**未验**挂上之后，指令是「用 sandbox 测试」——即不碰主机网络、在隔离环境里造出会话边沿。
本轮把它探到了底，结论是**这条路在本机断的，且断在应用之外**，记下来免得下次再走。

### 7.1 三条把启动方式修对的中间结论（每一条都曾被我误读成"沙盒不可用"）

| 现象 | 真因 | 可用形状 |
| --- | --- | --- |
| 直接跑 `C:\Windows\System32\WindowsSandbox.exe`（带不带 `.wsb` 都一样）8 s 内 **exit 0、不留进程、事件日志零命中** | 26100/26300 上沙盒客户端已改成**商店包**投递：真身在 `C:\Program Files\WindowsApps\MicrosoftWindows.WindowsSandbox_0.8.107.0_x64__cw5n1h2txyewy\`（`WindowsSandboxServer.exe` + `WindowsSandboxRemoteSession.exe`），System32 那个只是启动器 | `Start-Process explorer.exe -ArgumentList '…\probe.wsb'`（走 explorer 才有会话上下文） |
| 客户端窗口停在 **405×217 的黑壳**（`PrintWindow` 拍回来只有标题栏 + 一条黑带），`<LogonCommand>` 永不执行、映射目录等不到输出 | `.wsb` 里写了 `<VGpu>Disable</VGpu>`。**机制是推断**（本 build 上访客系统起不到 logon），但现象可复现：删掉 VGpu 节点后同一个 `.wsb` 换成 620×320 的错误对话框，形状明确变了 | 探针配置**删掉 VGpu 节点**、`MemoryInMB` 给 4096 |
| 三次启动堆出三个 `WindowsSandboxRemoteSession`（6:50 / 7:08 / 7:24），且 `vmmemWindowsSandbox` 停在 13 MB / 0 CPU | 同时只允许一台沙盒 VM（**推断**，依据同上：后两次只留黑壳、拿不到 VM）；★ 确定的一条是 **杀客户端不会带走 server 和 VM** | 重开前 `Get-Process WindowsSandbox* \| Stop-Process -Force` 杀干净，再起一次 |

顺带订正一条更早的误判：CBS 里 `Containers-DisposableClientVM-Package…10.0.26100.9550` 的 `CurrentState = 112`
= **已安装**（80/64 才是 staged/partial）⇒ 功能一直是开着的，**不需要 `dism`、不需要重启**。
我上一轮"可选功能未真启用"那句是错的。

### 7.2 修对启动方式之后撞的硬错误

> **Windows 沙盒无法初始化。找不到指定的模块。(0x8007007E)**

`0x8007007E = ERROR_MOD_NOT_FOUND`，弹窗在 `WindowsSandboxRemoteSession` 那个 620×320 客户端窗口里（`PrintWindow` 拍回来的）。缺哪个模块，静态手段查到这就到边界了：

- **不是包依赖缺**：该包声明的 `Microsoft.VCLibs.140.00.UWPDesktop`、`Microsoft.VCLibs.140.00` 与四个 resource split 逐个 `Get-AppxPackage` 回读，全部 `installed=True`。
- **不是静态导入缺**：`dumpbin /dependents` 扫 `WindowsSandboxServer.exe` / `WindowsSandboxRemoteSession.exe` / `SandboxUDK.dll` / `SandboxCommon.dll` / `WindowsSandboxClient.exe`，非 api-set 依赖在 System32 全找得到。
  （`ext-ms-win-com-ole32-l1-1-0.dll` 那三条是 API set 虚拟名，**不是**缺失文件，别当线索。）
- ⇒ 缺的是**运行时 `LoadLibrary` 的模块**。本机没有 ETW / Process Monitor 类工具（逆向装备只有 ilspycmd + dumpbin，见 memory `env-win-dev-tool-locations`），再往下只能靠猜。
- 可疑形状（**未证**，别当结论用）：System32 侧 `WindowsSandbox.exe`（文件时间 9/24）与 `WindowsSandboxClient.exe`（9/10）属 CBS 老载荷，商店包是 0.8.107，两边不同代。真要修得动 SFC / DISM / 就地升级 —— 那是改系统组件，属于动手前必须先问用户的那一档，本轮没做。

### 7.3 对 §六 那行未验意味着什么

沙盒给不了的不只是"装不上我们的包"，还有一层结构性障碍就算 VM 起来了也在：外发/侧载自签 MSIX 进容器只能靠 `.wsb` 的 `<LogonCommand>` 挂一条自制安装脚本，**这正撞 AGENTS.md 第 2 条**（部署不得绕开 VS、不得自制 ps1），而 VS 不可能往容器里部署。所以：

- 判据「边沿行尾 = 本轮不 prune」与 Docs/55 判据 B/C/F **仍然只有主机一条路**：重启清掉 `[星链机场]` 中毒条目 → 由用户点名要拨的那一条隧道 → 现场收判据。
- 在那之前，本批收工状态不变：**代码改完、离线套件全绿、装机成功、零拨号**，行为判据按 §六 明记**未验**，不许读成"已修好并验证"。
- 收工核对（沙盒这一趟没脏主机）：`rasdial` = **No connections**、NRPT 规则 **0** 条、`ClashUWP` 进程 **1** 个（pid 19964 未动）、台账仍 0 字节、沙盒进程残留 0 个；本轮为截图临时写的 `cap.ps1` / `cap2.ps1` / `sandbox*.png` 已删，`D:\fluentapps\sandbox-probe\` 只留 `probe.wsb` + `probe.cmd`（功能一旦可用即可复用）。

## 八、这笔账记给谁

- **代码**：Docs/50 §二.2 第 3 条的设计落地了（此前只有参数、没有调用点）。
- **文档**：Docs/50 §二.2 第 3 条与 §五 第 3 条各加了一段 1.0.111 订正（含"当时为什么没抓到"的判读法订正）；Docs/55 §四 版本链与 §八.⑦ 的出处一并订正（那条 `Upload\` 旧清单**不是 1.0.110 新坑**，根因与处置早在 Docs/42 §二，本轮只是按 §二.4 真做了一次）。
- **下一手**：判据 #3 的实机闭合、Docs/55 判据 B/C/F，全部卡在同一道"要不要拨号"的闸上；§七 已经把"不拨号也能造边沿"的唯一候选（沙盒）证伪，所以出口只剩两条 —— ① 主机重启清掉 `[星链机场]` 中毒条目后由用户点名拨一条隧道；② 这几条一直记为**未验**。
- **环境账**：`0x8007007E` 这条不是我们的缺陷，是这台机器的沙盒载荷坏了；它同时否证了我本轮先前两次判断（"功能未启用"、"直接起 exe 没进程=崩了"），判读法订正一并写进 §七.1 的表里。
