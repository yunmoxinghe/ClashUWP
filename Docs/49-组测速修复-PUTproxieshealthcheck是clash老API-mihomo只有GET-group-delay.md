# 49 · 组测速修复（路由写成 clash premium 的老 API ⇒ 必然 404；顺带订正"延迟 0"的读法）

状态：**本批（应用版本 1.0.93）已在当前工作区落地，全量 Rebuild 0 警告 0 错误，离线夹具 11 条新判据全绿。**
**未打包、未签名、未装机 ⇒ 真机点一次 ⚡ 这件事没做，本机上主应用也没装机**（只有 `ClashUWP.Cli 1.0.4.0` 在注册）。
本文的判据全部取自**入仓的官方 mihomo 源码** `D:\fluentapps\repos\clashUWP研究\baseline\_mihomo`，
每条给到 文件:行，不靠 API 手册转述、不靠推断。

相关档案：分层与目录归属见 [Docs/48](48-分层与状态归属唯一数据源-目录等于归属进程-共享层准入与两个禁改清单.md)；
1.0.93 那条"删前台非官方 hook"的主线见 [Docs/46](46-删前台非官方hook-连接切换断开三路径对齐官方-1.0.93.md)；
测速按钮所属的订阅/节点系统审计见 [Docs/34](34-订阅与节点系统审计基线-1.0.81.md) / [Docs/35](35-订阅与节点系统修复与验收-1.0.81.md)。

---

## 〇、症状与一句话结论

症状：代理页每个组行末那枚「测速」点下去只回一句失败（旧文案还挂着"※ 若这里是 405…"那句），
界面上延迟也不刷新。

结论一句话：**这条路由在本内核（mihomo）里根本不存在**。旧实现打
`PUT /proxies/{组}/healthcheck` —— 那是 **clash（premium）时代的老 API**；
mihomo 把组级测速挪到了另一棵树上：`GET /group/{name}/delay?timeout=&url=`。
所以这不是"参数没调对"，是**方法 + 路径双双不存在** ⇒ 内核必然回 404，
404 被旧代码当成"失败状态码"原样带回界面，看起来就像"测速坏了"。

## 一、证据表（官方源码逐条）

| # | 判据 | 官方出处（`_mihomo`） | 对本项目的意义 |
|---|---|---|---|
| E1 | `/proxies/{name}` 只挂 `GET /`、`GET /delay`、`PUT /`、`DELETE /` | `hub/route/proxies.go:26-33` | **没有 `/healthcheck`** —— 旧路由证伪 |
| E2 | 组级路由是 `GET /group/{name}` 与 `GET /group/{name}/delay` | `hub/route/groups.go:21-26` | 新路的本体 |
| E3 | `timeout` 走 `strconv.ParseInt`，失败直接 400 | `hub/route/groups.go:69-72` | `timeout` 是**必填**，少它等于这一枪没打出去 |
| E4 | `expected` 解析失败也 400；空串/`*` 合法 | `groups.go:76-79` + `common/utils/ranges.go:15-31` | 本实现不传 `expected`（不筛状态码） |
| E5 | 成功响应是**裸对象** `map[string]uint16`（无外壳） | `groups.go:93`（`render.JSON(w, r, dm)`） | 解析层按"键=节点名、值=数字"读 |
| E6 | 表里**只写 `err == nil` 的成员** | `adapter/outboundgroup/groupbase.go:245-251` | 少列出来的 = 本轮不通，不是"内核漏返回" |
| E7 | 全批为空 ⇒ `get delay: all proxies timeout`，HTTP **504** | `groupbase.go:258-259` + `groups.go:86-90` | 全员不通不是 200 空表，是 504 + `{"error": …}` |
| E8 | 一个 ctx 罩**整批**，成员**并发**拨、`wg.Wait()` 之后才渲染 | `groups.go:82` + `groupbase.go:237-256` | `timeout` 的量级要按"最慢那员"给；且结果是**同步**的 |
| E9 | 节点级测速失败时往 history 写 **`Delay=0`**（不是"没有记录"） | `adapter/adapter.go:170-177`（defer：`alive=false` 时 `record.Delay` 保持 0）、`:190` | "0ms" 在界面上会被读成神速 ⇒ 必须单列一档 |
| E10 | 空 `url` ⇒ `urlToMetadata("")` 直接 error | `adapter/adapter.go:204-206` | 不传测速地址 ≠ 用默认值，是**全员判死** |
| E11 | `URLTest` 组**忽略**请求里的 `url`，改用它自己的 `u.testUrl` | `urltest.go:191-193` | 传什么地址都不会污染自动组的设定 |
| E12 | `Fallback` / `LoadBalance` 没有这个覆盖（用内嵌 `GroupBase.URLTest`） | `fallback.go`/`loadbalance.go` 无 `URLTest` 方法 + `groupbase.go:237` | 这两类**真会用我们传的 url** ⇒ 必须先读组自己的 `testUrl` |
| E13 | 组对象会把 `testUrl` 报出来 | `selector.go:72` / `urltest.go:174` / `fallback.go:90` / `loadbalance.go:230` | ① 步元数据可读，路上有据 |
| E14 | `Selector` 的值等于官方默认时**故意报空串**（"让面板 follow 自己的设置"） | `selector.go:63-67` | 空串不是异常 ⇒ 必须有兜底地址 |
| E15 | 订阅没写 `url:` 时内核自己补：先取提供方 health-check，再退官方默认 | `adapter/outboundgroup/parser.go:139-149`、`:166` | 空地址只可能来自 E14 那一种情形 |
| E16 | 命中 `SelectAble` 且类型不是 `Selector` ⇒ 先 `ForceSet("")` 并清缓存里的 selected | `groups.go:61-65` + `util.go:30-37` | **点 ⚡ 会把自动组的选择重置** —— 官方法定的副作用 |
| E17 | `ProxyGroup` 只有 `Selector`/`URLTest`/`Fallback`/`LoadBalance` 四家实现；`Relay` 不在内 | `util.go:25-28`（无 relay 断言）+ `groups.go:56-60` | **Relay 组点测速必然 404**，文案要指名道姓 |
| E18 | 提供方刷新 `GET /providers/proxies/{name}/healthcheck`（204） | `hub/route/provider.go:20-25` | 这一条**本来就是对的**，不动 —— 免得下一个人以为两处都错 |
| E19 | **本机一手佐证**：同一棵 `/proxies/{组}` 子树上，`PATCH` 回 **405**、`PUT` 回 **204** | [Docs/12 §7](12-后端功能缺口对照-安卓版.md) 那轮 `.local/proxyapi` 现场实验（真内核、真回环） | chi 只对"**pattern 存在但方法不对**"回 405；`/proxies/{name}/healthcheck` 这个 pattern 从未注册 ⇒ **404**。所以旧注释那句"405=方法不对（该换 GET）"的推理本身也不成立 —— 这条路从来就不是"方法选错了"，是"路径不存在" |

## 二、旧注释里那三条说法各自的下场

| 旧说法（`MihomoController.cs` 组测速注释） | 现在 |
|---|---|
| "`PUT /proxies/{name}/healthcheck` + 成功 204"，按**推断**记账（yacd 面板用法一致如此，官方手册原文没抓到） | **证伪**（E1/E2）。本轮按入仓官方源码定死，注释里把这条错的留在原地作为教训，不擦。 |
| "204 只代表已触发，延迟异步写回，要等 3 秒轮询才出数" | **随路由一起作废**（E8）：新路上内核等整批拨完才渲染，响应回来时 history 已经写好，UI 立刻刷一次就能看到新数。 |
| "测速用的是组自己在订阅里配的 `url:`，本函数不传测试地址、不覆盖机场设定" | 旧写法**并没有做到**：不传 `url` 按 E10 是"全员判死"，不是"沿用组设定"。现在先 `GET /group/{name}` 读它自己的 `testUrl`（E13/E14/E15），拿到就用、拿不到才兜底 —— 这句话第一次名副其实。 |

顺带记一笔**同一类**的坑：还有一句"CMFA 是 `healthCheck(groupName)` 之后重载这一组数据（`ProxyActivity.kt 93-101`）"。
本机 **没有 CMFA 源码**（`find` 全仓无 `ProxyActivity.kt`），所以本轮起**不再用"与安卓一致"当依据**，
只引官方 mihomo。安卓那颗延迟胶囊对 `delay==0` 究竟怎么画，属**未证**。

## 三、改动清单（文件:行 → 改成什么）

| 位置 | 改法 |
|---|---|
| `Backend\Mihomo\MihomoController.cs:876` | 新增 `GroupTestBatchTimeoutMs = 8000`：官方 `timeout` 罩整批（E3/E8），所以不能照抄单节点那条的 5000 |
| `:883` | 新增 `DefaultGroupTestUrl = "http://www.gstatic.com/generate_204"` —— 与 `OneDelayAsync`（`:582`）同一个地址、与我们 conf 里 url-test 段写的那个一致，不另立标准 |
| `:919` `GroupHealthCheckUrl(group, testUrl, timeoutMs)` | 路由改 `GET /group/{组}/delay?timeout=&url=`；组名继续 `EscapeDataString`（一个 `/` 就能把路由切成两段，这条原判据留着）；`url` 也编码（组自己的地址可能带 `&`/`?`，编码后与单节点那条的裸写法**解码同值**） |
| `:942` `ReadGroupDelayMap(JsonElement)` | 新纯函数：裸对象 ⇒ `名字→延迟`；**值不是数字的一律跳过**，于是 E7 的错误体（`{"error": "字符串"}`）一个节点都不算，两种形状靠值类型一次分开 |
| `:990` `TriggerGroupHealthCheckAsync` | ① `GET /group/{name}` 读 `testUrl` / `type` / `all` 成员数（读不到不判死，退兜底地址）；② `GET …/delay`；③ 按状态码分档出话：200 ⇒ `测速完成：N/M 通，最快 …，没列出的 K 员本轮不通` + 用了哪个测速地址 + 自动组的选择会被重置；404 ⇒ 指名"不是可测的组，Relay 不在这张表里"；400 ⇒ 指名 timeout/expected；504 ⇒ 指名全员不通 |
| ↳ 同一个函数里的**一次自纠** | 第一版把"响应体超 200 字符就截断"留在了**解析之前** —— 32 员的一组那张 map 轻松超过 200 字符，等于修完还是坏（表现为"测速回来了但解析失败"）。现在**原文进解析器**，截断只用在非 200 那两条要带回错误体的话上。这条没有离线夹具兜住（在 async 路径里），靠的是复核代码时想了一遍"真订阅的响应长什么样" |
| `:1126`、`:1183`（debug `groups` 文本通道） | 延迟三档写全：`-` = 没测过、`0（本轮不通）` = 挂了、`Nms` = 数；表头那句同步补"delay=0 才是本轮不通" |
| `Frontend\MainComponent.cs:1035` | `MemberSubtitle` 的 `m.DelayMs >= 0 ? $"{n}ms" : ""` 改成三档：`>0` 写 ms、`==0` 写 **"不通"**、`-1` 不写 |
| `MainComponent.cs:1423` `HealthCheckAsync` | 注释按 E8 订正（不再说"当场不出数"）；进行中那句改成"最慢决定这一轮的成败"（原"几秒到几十秒"是照着错的异步语义写的） |
| `Backend\Mihomo\KernelView.cs:26`、`:91` | 顶部口径与 `Member` 注释补 0 这一档，并写明"只引官方源码，安卓那条未证" |
| `Tools\YamlShapeTest\Program.cs:927` | 夹具重写（见 §四） |
| `Tools\YamlShapeTest\YamlShapeTest.csproj:36` | **补 link** `Vpn\SettingsParse.cs` —— Docs/48 §四 那次"解析收成一份实现"把产品工程编译过了，但夹具是 link 单文件编译的，没跟着补就整条编译失败。这正是 Docs/48 里"产品 Rebuild 过 ≠ 夹具过"那条的实证 |

## 四、离线夹具新增/改写的 11 条判据

`dotnet run --project Tools/YamlShapeTest` ⇒ **ALL OFFLINE TESTS PASSED**（含以下 11 条，全绿）：

路由拼装 7 条：形状是 `/group/{组}/delay` 且不再出现 `/proxies/` 与 `healthcheck`；`timeout` 原样进 query；
没配 `testUrl` 时落兜底地址；emoji+空格组名按 `EscapeDataString` 不多编不漏编；组名里的 `/` 编成 `%2F`；
组自己配的 `testUrl` 优先；带 `&` 的测速地址编码后整条 query 只剩一个裸 `&`。

响应解析 4 条：裸对象按 `名字→延迟` 收下（节点名原样）；错误体 `{"error": …}` 不算任何节点；
数字值原样保留（含 `0`，渲染归上层）+ 非数字跳过；不是对象（异常形状）给空表且不抛。

## 五、为什么"延迟 0"这一档是**测速修复的一部分**而不是顺手美化

E9 决定：节点这一轮拨不通，内核往 `history` 末尾写的是 **`Delay=0`**（`alive=false`），不是"没有记录"。
旧 UI 把 `>= 0` 一律拼成 `"Nms"` ⇒ 点了 ⚡ 之后，不通的成员在界面上显示 **"0ms"** ——
在一列 123ms / 456ms 里它长得像"这员最快"，判据读反。
新路（E6）本来就只回拨通的节点，所以不通的成员**必然**以 0 落进 history，
这是主路径不是边角情形 ⇒ 0 单独渲染成"不通"（三档：没测过不写 / 0 写不通 / >0 写 ms）。

## 六、验证到哪一步、没到哪一步

**做到了**：
* 全量 Rebuild（`Tools\UwpBridge\uwp-bridge.exe -Build …\ClashUWP.csproj -Target Rebuild -Configuration Debug -Platform x64`）：
  `ok: True`、`exitCode: 0`、警告行 0、错误行 0。共跑三次（改路由后、补夹具 link 后、
  上面那条"截断在解析之前"自纠后），最后一次 169.6 s，全 AOT 链路走到 `Generating native code`。
* 离线夹具 `dotnet run --project Tools/YamlShapeTest`：**ALL OFFLINE TESTS PASSED**（11 条新判据在内）。

**没做到（不许把它说成已验）**：
1. **真机点一次 ⚡** —— 主应用没装机（1.0.93 从未出包），这一条只能等 VS 出包后复验。
   判据见 §七。
2. **`url` 参数 percent-encode 后的上机读法** —— 依据是 Go 标准库 `query.Get()` 会 percent-decode
   （E3/E10 那条路径），本机没实测。裸写与编码两种形态**解码后是同一个值**。
3. **Relay 组必然 404** —— 依据 E17/E18 的路由表，本机没有 Relay 组的真订阅可点。
4. **`ForceSet("")` 重置自动组选择**这一副作用（E16）在界面上具体表现成什么样，未实测；
   本档只保证文案里说了"现值等它下一轮重新定"。

## 七、上机复验清单（照这个读，别再靠猜）

1. 点 ⚡ → hint 应该是 `「组名」测速完成：N/M 通，最快 节点名 Xms　※ 测速地址：…`，
   随后这一列的延迟**当场**刷新（E8：同步返回，不用等 3 秒轮询）。
2. `HTTP 400` ⇒ `timeout` 没进 query（夹具钉过，理论上不该出现；出现了就看组名编码是不是把 `?` 吞了）。
3. `HTTP 404` ⇒ 这个名字不是可测的组（`debug groups` 看它的 类型；Relay 组属正常 404）。
4. `HTTP 504` ⇒ 全员不通：先按 [Docs/32](32-退出Verge纯净复测-国内站点全废与哨兵探针误杀.md) 那条线确认本机常驻 Verge 没抢路由、订阅没过期，再看节点列表里是不是全 `不通`。
5. 自动组（URLTest/Fallback）点完"现值"变空/变兜底 ⇒ **官方行为**（E16），不是掉线。
6. 收工照老规矩：不写碰隧道的 `debug-cmd`、确认没有自动重拨。
