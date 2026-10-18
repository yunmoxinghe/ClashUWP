using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClashUWP.Backend.Mihomo
{
    /// <summary>
    /// 【后端 · 内核控制器】mihomo REST API（127.0.0.1:9090）的包内客户端。
    ///
    /// 【为什么必须在 app 内查】控制器监听在**插件进程**内，UWP AppContainer 有回环隔离：
    ///   桌面 curl/PowerShell 到它的 TCP SYN 永远不被应答（实测 listening=True 但
    ///   外部 connect 超时）。前台 app 与插件同包同 AppContainer SID，包内回环互通。
    ///
    /// 【★ 两条硬口径】
    ///   1) HttpClient 必须 <c>UseProxy = false</c>，否则请求被自己下发的代理劫走；
    ///   2) 改配置只走 <c>PATCH /configs</c>（逐字段即时生效），不能用 PUT ——
    ///      PUT 的 path/payload 从 JSON body 取，我们只把 path 拼在 query 上，
    ///      所以当年那个 TryHotReload 从来没生效过（1.0.16 的反面教材）。
    ///
    /// 本类全部是**运行时**通道：绑卡 / 模式 / 测速 / 日志流都不动 config.yaml，
    /// 也就不必重装包 —— 单变量 A/B 靠的就是这个。
    /// </summary>
    internal static class MihomoController
    {
        /// <summary>
        /// 连接期自查 mihomo 控制器（debug controller 命令）。
        ///
        /// 【为什么必须在 app 内查】mihomo 控制器（127.0.0.1:9090）监听在<b>插件
        /// 进程</b>内，UWP AppContainer 有回环隔离：桌面 curl/PowerShell 到它的 TCP SYN
        /// 永远不被应答（实测 listening=True 但外部 connect 超时）。前台 app 与插件
        /// 同包同 AppContainer SID，包内回环互通，所以在 app 内用 HttpClient 查。
        /// </summary>
        internal static async Task<string> RunControllerProbeAsync()
        {
            try
            {
                // 不走系统代理，直连本机（Proxy 在 handler 上，不在 DefaultRequestHeaders）。
                using var http = new System.Net.Http.HttpClient(
                    new System.Net.Http.HttpClientHandler { UseProxy = false });
                http.Timeout = TimeSpan.FromSeconds(6);

                string sb = "";

                // 选中的节点：★ 组名不能写死。以前找 "ClashUWP-Auto"，而订阅派生出来的
                //   实际组叫「星链机场」，于是每次自查都报 "(无 ClashUWP-Auto 组)"，
                //   把内核明明活着的事实读成了"没选到节点"。这里改成按结构挑组：
                //   凡是带 all 数组且长度 > 1 的就是代理组，GLOBAL/PROVIDER 之外一般只有一个。
                string proxiesJson = await http.GetStringAsync("http://127.0.0.1:9090/proxies");
                using (var doc = System.Text.Json.JsonDocument.Parse(proxiesJson))
                {
                    bool reported = false;
                    if (doc.RootElement.TryGetProperty("proxies", out var proxies) &&
                        proxies.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        foreach (var kv in proxies.EnumerateObject())
                        {
                            if (kv.Name is "GLOBAL" or "DIRECT" or "REJECT" or "PASS") continue;
                            if (!kv.Value.TryGetProperty("all", out var all) ||
                                all.ValueKind != System.Text.Json.JsonValueKind.Array ||
                                all.GetArrayLength() < 2) continue;
                            string type = kv.Value.TryGetProperty("type", out var t)
                                ? t.GetString() ?? "?" : "?";
                            string now = kv.Value.TryGetProperty("now", out var n)
                                ? n.GetString() ?? "?" : "?";
                            sb += $"组={kv.Name} 类型={type} 成员={all.GetArrayLength()} 选中={now}";
                            reported = true;
                            break;
                        }
                    }
                    if (!reported) sb += "(没找到代理组)";
                }

                // 活动连接：目标 + 链路（可看出走没走代理）。
                string connJson = await http.GetStringAsync("http://127.0.0.1:9090/connections");
                using (var doc = System.Text.Json.JsonDocument.Parse(connJson))
                {
                    if (doc.RootElement.TryGetProperty("connections", out var conns) &&
                        conns.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        sb += $"\n活动连接数={conns.GetArrayLength()}";
                        int shown = 0;
                        foreach (var c in conns.EnumerateArray())
                        {
                            if (shown++ >= 8) break;
                            string dst = "?", net = "?";
                            if (c.TryGetProperty("metadata", out var meta))
                            {
                                dst = meta.TryGetProperty("destinationIP", out var dip)
                                    ? dip.GetString() ?? "?"
                                    : meta.TryGetProperty("host", out var h) ? h.GetString() ?? "?" : "?";
                                if (meta.TryGetProperty("destinationPort", out var dport))
                                    dst += ":" + dport.GetString();
                                if (meta.TryGetProperty("network", out var n))
                                    net = n.GetString() ?? "?";
                                // 域名比 IP 好认（黑洞排查时"这是哪条连接"必须一眼看出来）。
                                string? mh = meta.TryGetProperty("host", out var hh) ? hh.GetString() : null;
                                if (!string.IsNullOrEmpty(mh) && mh != dst)
                                    dst += $"({mh})";
                            }
                            string chain = "?";
                            if (c.TryGetProperty("chains", out var ch) &&
                                ch.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                var parts = new System.Collections.Generic.List<string>();
                                foreach (var e in ch.EnumerateArray()) parts.Add(e.GetString() ?? "?");
                                chain = string.Join(" > ", parts);
                            }
                            // ★ 双向字节数是本任务（隧道内证据判健康）的关键字段：
                            //   upload>0 说明本机→内核→节点这条路通了，
                            //   download>0 说明回程也真的在动。两个都 0 的连接
                            //   就是「SYN 进去了、什么都没回来」的黑洞形状。
                            string traffic = "";
                            long up = ReadNumber(c, "upload"), down = ReadNumber(c, "download");
                            if (up >= 0 || down >= 0)
                                traffic = $" ↑{up}B ↓{down}B";
                            else
                            {
                                long upt = ReadNumber(c, "uploadTraffic");
                                long dwt = ReadNumber(c, "downloadTraffic");
                                if (upt >= 0 || dwt >= 0) traffic = $" ↑{upt}B ↓{dwt}B";
                            }
                            sb += $"\n  {dst} [{net}] => {chain}{traffic}";
                        }
                    }
                }
                return sb;
            }
            catch (Exception ex)
            {
                ClashUWP.Vpn.RollingLog.Error("mihomo-controller", ex);
                return $"controller 查询失败: {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>
        /// GET /configs 原样回显（debug conf 命令）。
        ///
        /// 【为什么要】1.0.16 实测：往 LocalState/config.yaml 写
        /// <c>interface-name: WLAN</c> 之后，桥进程在流量压力下<b>一条外呼 TCP
        /// socket 都不产生</b>（measure-10.log：10 轮采样只见 127.0.0.1:9090
        /// 与哨兵自己的 10.7.0.2 回程）。要么这个键根本没落到
        /// <c>cfg.General.Interface</c>，要么落到了但 <c>iface.ResolveInterface</c>
        /// 失败让拨号直接报错。两种情况的修法完全不同，必须先看内核自己
        /// 认为的 interface-name 是什么，别再靠猜。
        /// </summary>
        internal static async Task<string> RunConfigsDumpAsync()
        {
            try
            {
                using var http = NewLoopbackClient(6);
                string json = await http.GetStringAsync("http://127.0.0.1:9090/configs");
                var sb = new StringBuilder();
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    foreach (var kv in doc.RootElement.EnumerateObject())
                    {
                        switch (kv.Value.ValueKind)
                        {
                            case System.Text.Json.JsonValueKind.Object:
                            {
                                var keys = new System.Collections.Generic.List<string>();
                                foreach (var k in kv.Value.EnumerateObject()) keys.Add(k.Name);
                                sb.Append($"{kv.Name}={{{string.Join(",", keys)}}} ");
                                break;
                            }
                            case System.Text.Json.JsonValueKind.Array:
                            {
                                var items = new System.Collections.Generic.List<string>();
                                foreach (var e in kv.Value.EnumerateArray())
                                {
                                    items.Add(e.ValueKind == System.Text.Json.JsonValueKind.String
                                        ? e.GetString() ?? "?" : e.ToString());
                                    if (items.Count >= 6) break;
                                }
                                sb.Append($"{kv.Name}=[{string.Join(",", items)}] ");
                                break;
                            }
                            default:
                                sb.Append($"{kv.Name}={kv.ToString()} ");
                                break;
                        }
                    }
                }
                catch (Exception parseEx)
                {
                    // 解析失败也别把原文丢了——interface-name 有没有在内，看一眼就知道。
                    sb.Append($"(解析失败 {parseEx.GetType().Name}: {parseEx.Message}) 原文: {json}");
                }
                string s = sb.ToString();
                return s.Length > 1600 ? s[..1600] + "…" : s;
            }
            catch (Exception ex)
            {
                ClashUWP.Vpn.RollingLog.Error("mihomo-conf", ex);
                return $"conf 查询失败: {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>
        /// ★【1.0.48】主动热重载磁盘上的内核配置：<c>PUT /configs?force=true</c> + body <c>{"path":"…"}</c>。
        ///
        /// 【为什么需要这条命令 —— 2026-10-18 实测出来的真缺口】
        ///   内核是<b>常驻</b>的（<c>residentBridge=1</c>）：<c>Disconnect</c> 不停桥，
        ///   下一次 <c>Connect</c> 走"复用常驻内核"分支 ⇒ <b>内核不会重新读磁盘上的
        ///   <c>config.kernel.yaml</c></b>。实测形态：改了磁盘文件的 <c>tolerance</c>，
        ///   断开重连后 plugin.log 记的是
        ///   <c>bridge-start 复用常驻内核（排空 50 包，<b>配置未变</b>）</c> ——
        ///   那个"配置未变"是拿<b>进程内存里那份 config 字符串</b>算的指纹，
        ///   磁盘上改了什么它根本不知道 ⇒ <b>改了不生效，而且没有任何提示</b>。
        ///   ⇒ 必须有一条能<b>主动</b>让内核重载的命令。
        ///
        /// 【口径与 <c>ClashVpnPlugin.TryHotReload</c> 逐字一致】
        ///   URL <c>PUT /configs?force=true</c>、body <c>{"path":"&lt;绝对路径&gt;"}</c>、
        ///   <b>手写 JSON 转义</b>（Windows 路径全是反斜杠；AOT 里 <c>JsonSerializer.Serialize</c> 直接抛）。
        ///   两条路径的 body 必须完全一样，否则"插件侧重载"与"前台主动重载"会得到不同结果。
        ///
        /// 【★ 这条命令碰什么】只让内核重新读一份配置文件（改出站规则/测速参数），
        ///   <b>不碰隧道</b>（隧道由平台管，插件侧 <c>Encapsulate</c> 那条数据面不受影响）。
        ///   但它<b>会</b>改内核运行态 ⇒ 与 <c>select</c>/<c>mode</c>/<c>bind</c> 同级，
        ///   复盘网络问题时记得它被用过。
        /// </summary>
        internal static async Task<string> RunReloadAsync(string configPath)
        {
            if (string.IsNullOrWhiteSpace(configPath))
                return "reload 需要参数：reload <config.kernel.yaml 的绝对路径>";

            try
            {
                using var http = NewLoopbackClient(10);

                // 手写 JSON（与 ClashVpnPlugin.TryHotReload 同形）：转义 " \ 与控制字符。
                var sb = new StringBuilder();
                sb.Append("{\"path\":\"");
                foreach (char ch in configPath)
                {
                    if (ch is '"' or '\\') sb.Append('\\').Append(ch);
                    else if (ch == '\n') sb.Append("\\n");
                    else if (ch == '\r') sb.Append("\\r");
                    else if (ch == '\t') sb.Append("\\t");
                    else if (ch < 0x20) sb.Append($"\\u{(int)ch:x4}");
                    else sb.Append(ch);
                }
                sb.Append("\"}");

                using var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Put,
                    "http://127.0.0.1:9090/configs?force=true")
                {
                    Content = new System.Net.Http.StringContent(
                        sb.ToString(), Encoding.UTF8, "application/json")
                };
                using var resp = await http.SendAsync(req);
                int code = (int)resp.StatusCode;
                string body = "";
                if (!resp.IsSuccessStatusCode)
                {
                    // 400 的原文就写着"为什么"（path is not a absolute path / not safe path /
                    // parse error）—— 只报状态码等于把问题变成猜谜。
                    try { body = await resp.Content.ReadAsStringAsync(); }
                    catch { /* 读不到原文也别把命令带崩 */ }
                    if (body.Length > 300) body = body[..300] + "…";
                }

                ClashUWP.Vpn.RollingLog.Info("reload", $"PUT /configs?force=true -> {code} {body}");
                return code >= 200 && code < 300
                    ? $"重载成功（HTTP {code}）：{configPath}"
                    : $"重载失败（HTTP {code}）：{body}";
            }
            catch (Exception ex)
            {
                ClashUWP.Vpn.RollingLog.Error("mihomo-reload", ex);
                return $"reload 失败: {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>
        /// GET /configs 取内核运行态三要素：mode / log-level / interface-name（debug status 命令）。
        ///
        /// 【为什么要】G8 "模式切换与生效值回读进 UI"的最小可用通道。
        /// 相比 `RunConfigsDumpAsync` 的整表 dump，这一条只取三项，文本短、适合常驻显示。
        /// </summary>
        internal static async Task<string> RunStatusAsync()
        {
            try
            {
                using var http = NewLoopbackClient(5);
                string json = await http.GetStringAsync("http://127.0.0.1:9090/configs");
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                string mode = "(缺)";
                if (root.TryGetProperty("mode", out var m))
                    mode = m.ValueKind == System.Text.Json.JsonValueKind.String ? m.GetString() ?? "?" : m.ToString();

                string logLevel = "(缺)";
                if (root.TryGetProperty("log-level", out var ll))
                    logLevel = ll.ValueKind == System.Text.Json.JsonValueKind.String ? ll.GetString() ?? "?" : ll.ToString();

                string iface = "(未绑)";
                if (root.TryGetProperty("interface-name", out var i))
                    iface = i.ValueKind == System.Text.Json.JsonValueKind.String
                        ? (string.IsNullOrEmpty(i.GetString()) ? "(未绑)" : $"\"{i.GetString()}\"")
                        : i.ToString();

                return $"mode={mode} | log-level={logLevel} | interface-name={iface}";
            }
            catch (Exception ex)
            {
                ClashUWP.Vpn.RollingLog.Error("mihomo-status", ex);
                return $"status 查询失败: {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>
        /// 抓 <c>GET /logs</c> 事件流若干秒（debug logs 命令）。
        ///
        /// 【为什么要】/logs 是<b>只推新事件</b>的流式接口，没有快照语义，
        /// 桌面侧也连不进容器（见 RunControllerProbeAsync 注释），所以只能
        /// 在 app 内挂住流、同时在外面制造流量，才能捞到内核拨号失败的原文。
        /// 拨号错误原文是本轮唯一的判据：
        ///   「can not find controller interface by name …」→ 绑卡名字口径问题，改 C# 即可；
        ///   「bind4: …」/setsockopt 被拒 → AppContainer 不让绑网卡，Go 侧也救不了；
        ///   只有 url-test/healthcheck 噪音 → 说明拨号根本没发起，问题在上游（DNS/路由/规则）。
        /// </summary>
        internal static async Task<string> RunLogStreamAsync(int seconds)
        {
            var sb = new StringBuilder();
            int n = 0;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            using var http = NewLoopbackClient(seconds + 15);
            bool levelUp = false;
            try
            {
                // ★ 先把日志等级抬到 debug 再挂流：拨号失败原文（dial … error / bind4: …）
                //   在 mihomo 里是 Debug 级，log-level: info 下 /logs 一条都不会推
                //   —— 实测 12 s 抓到 0 行（measure-11.log）。PATCH 是逐字段生效
                //   （hub/route/configs.go patchConfigs：LogLevel 只调 log.SetLevel），
                //   不会把 interface-name 之类的别的状态冲掉，finally 再恢复 info。
                string lvlPatch = await PatchConfigsAsync(http, "{\"log-level\":\"debug\"}");
                levelUp = lvlPatch.StartsWith("2");
                sb.Append($"\n(抬 debug={lvlPatch})");

                using var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Get, "http://127.0.0.1:9090/logs");
                using var resp = await http.SendAsync(req,
                    System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!resp.IsSuccessStatusCode)
                    return $"GET /logs -> {(int)resp.StatusCode} {resp.ReasonPhrase}";

                using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
                using var reader = new System.IO.StreamReader(stream);
                while (!cts.IsCancellationRequested && n < 80)
                {
                    string? line;
                    try { line = await reader.ReadLineAsync(cts.Token); }
                    catch (OperationCanceledException) { break; }
                    if (line == null) break; // 流被关闭（内核重启/断链）
                    if (line.Length == 0) continue;
                    // ★ /logs 的非 structured 形态是<b>逐行 JSON</b>（server.go getLogs：
                    //   Content-Type: application/json + w.Write(json+"\n") + Flush），
                    //   没有 SSE 的 "data: " 前缀 —— 上一版只认 "data:"，12 s 全被丢掉
                    //   （measure-12.log「抓到 0 行」就是这么来的，不是内核没日志）。
                    if (line.StartsWith("data:")) line = line.Substring(5).Trim();
                    if (!line.StartsWith("{")) continue;
                    string body = line;
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(body);
                        var root = doc.RootElement;
                        string type = root.TryGetProperty("type", out var l)
                            ? l.GetString() ?? "?" : "?";
                        string pay = root.TryGetProperty("payload", out var p)
                            ? p.GetString() ?? "" : body;
                        if (pay.Length > 220) pay = pay[..220] + "…";
                        sb.Append($"\n[{type}] {pay}");
                    }
                    catch
                    {
                        sb.Append($"\n{body}");
                    }
                    n++;
                }
                return $"日志流 {seconds}s，抓到 {n} 行" + sb;
            }
            catch (OperationCanceledException)
            {
                return $"日志流 {seconds}s（到时），抓到 {n} 行" + sb;
            }
            catch (Exception ex)
            {
                ClashUWP.Vpn.RollingLog.Error("mihomo-logs", ex);
                return $"logs 流读取失败: {ex.GetType().Name}: {ex.Message}（已抓 {n} 行）" + sb;
            }
            finally
            {
                if (levelUp)
                {
                    try { await PatchConfigsAsync(http, "{\"log-level\":\"info\"}"); }
                    catch (Exception ex)
                    {
                        // 恢复不了就留着 debug —— 但要在日志里留一句：
                        // 内核长期 debug 级会自己刷出大量事件，下次看日志的人得知道
                        // 这不是我们的本意。
                        ClashUWP.Vpn.RollingLog.Warn("mihomo-logs", "log-level 未能恢复 info：" + ex.Message);
                    }
                }
            }
        }

        /// <summary>
        /// 运行时改 mihomo 的出站绑卡（debug bind 命令）：
        /// <c>PATCH /configs {"interface-name":"WLAN"}</c>，再 GET 回来核对。
        ///
        /// 【为什么要】它是唯一<b>不用改 config.yaml、不用重装包</b>就能开关绑定的通道，
        /// 于是「绑物理网卡」和「被自家全路由隧道吃掉」这两件事能做严格的单变量 A/B：
        ///   bind WLAN → bind - → bind WLAN，同一进程、同一内核、同一路由表。
        /// 【为什么走 PATCH 而不是 PUT】PUT /configs 的 path/payload 从 JSON body 取
        /// （hub/route/configs.go updateConfigs），PATCH 才是逐字段即时生效：
        /// patchConfigs 里 <c>if general.InterfaceName != nil { dialer.DefaultInterface.Store(...) }</c>。
        /// </summary>
        internal static async Task<string> RunBindAsync(string ifaceName)
        {
            try
            {
                using var http = NewLoopbackClient(8);
                // ★ 手写 JSON：JsonSerializer.Serialize 在本包（Native AOT + 反射序列化
                //   被关闭）里直接抛 InvalidOperationException —— 实测 measure-12.log 的
                //   bind 两步都栽在这。网卡名里不会有需要转义的字符，逐个转义足够安全。
                var sb = new StringBuilder();
                sb.Append("{\"interface-name\":\"");
                foreach (char ch in ifaceName)
                {
                    if (ch is '"' or '\\') sb.Append('\\').Append(ch);
                    else if (ch < 0x20) sb.Append($"\\u{(int)ch:x4}");
                    else sb.Append(ch);
                }
                sb.Append("\"}");
                string body = sb.ToString();
                string patch = await PatchConfigsAsync(http, body);
                string now = "(读不到)";
                try
                {
                    string json = await http.GetStringAsync("http://127.0.0.1:9090/configs");
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("interface-name", out var v))
                        now = v.ValueKind == System.Text.Json.JsonValueKind.String
                            ? $"“{v.GetString()}”" : v.ToString();
                }
                catch (Exception readEx)
                {
                    now = "(回读失败 " + readEx.Message + ")";
                }
                return $"PATCH {body} -> {patch}；内核现值 interface-name={now}";
            }
            catch (Exception ex)
            {
                ClashUWP.Vpn.RollingLog.Error("mihomo-bind", ex);
                return $"bind 失败: {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>
        /// 运行时改内核分流模式（debug mode 命令）——和 <see cref="RunBindAsync"/> 同一套路：
        /// 手写 JSON、PATCH <c>/configs</c>、再回读现值，全程不碰 config.yaml，
        /// 所以换臂不用重装包。
        ///
        /// 【为什么要这个开关】run 13 的内核原文里，死掉的两类拨号是
        ///   ① <c>dial DIRECT (match GeoIP/cn) → connectex: unreachable host</c>
        ///   ② <c>dial 星链机场 (match Match/)</c> 经节点出国。
        /// 「DIRECT 分支本身在毒化路径上」这个假设，只有把模式切成 global
        /// （所有流量都走节点，不再出现 DIRECT 拨号）或 direct（全直连）才能单变量验证。
        /// 【口径】只接受 rule / global / direct 三个值，其余一律拒绝 —— 免得把
        ///   一个拼错的 mode 打进内核后还以为换了臂。
        /// </summary>
        internal static async Task<string> RunModeAsync(string mode)
        {
            if (mode is not ("rule" or "global" or "direct"))
                return $"mode 参数无效: “{mode}”（只支持 rule / global / direct）";

            try
            {
                using var http = NewLoopbackClient(8);
                string patch = await PatchConfigsAsync(http, $"{{\"mode\":\"{mode}\"}}");
                string now = "(读不到)";
                try
                {
                    string json = await http.GetStringAsync("http://127.0.0.1:9090/configs");
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("mode", out var v))
                        now = v.ValueKind == System.Text.Json.JsonValueKind.String
                            ? $"“{v.GetString()}”" : v.ToString();
                }
                catch (Exception readEx)
                {
                    now = "(回读失败 " + readEx.Message + ")";
                }
                return $"PATCH mode={mode} -> {patch}；内核现值 mode={now}";
            }
            catch (Exception ex)
            {
                ClashUWP.Vpn.RollingLog.Error("mihomo-mode", ex);
                return $"mode 失败: {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>
        /// 让内核立刻拨一次指定节点，把 REST 原文带回来（debug delay 命令）。
        ///
        /// 【为什么要】这是「拨号死在哪一步」最短的一条路：
        /// <c>GET /proxies/{name}/delay?timeout=..&amp;url=..</c> 走的正是
        /// <c>dialer → proxy.Dial</c> 这条真实出站路径，失败时 mihomo 会把
        /// 错误原文放进响应体（<c>{"message":"..."}</c>）返回。
        /// 相比 /logs 的流式时序，它一次请求一个结论，不需要等事件推送。
        /// 【两个一起测】选中节点（要拨出国）和 <c>DIRECT</c>（国内直连）同测，
        /// 就能把「节点不可达」和「内核出站整体不可达」分开。
        /// </summary>
        internal static async Task<string> RunDelayAsync(string node)
        {
            try
            {
                using var http = NewLoopbackClient(25);

                if (string.IsNullOrEmpty(node))
                {
                    string proxiesJson = await http.GetStringAsync("http://127.0.0.1:9090/proxies");
                    using var doc = System.Text.Json.JsonDocument.Parse(proxiesJson);
                    if (doc.RootElement.TryGetProperty("proxies", out var proxies) &&
                        proxies.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        foreach (var kv in proxies.EnumerateObject())
                        {
                            if (kv.Name is "GLOBAL" or "DIRECT" or "REJECT" or "PASS") continue;
                            if (!kv.Value.TryGetProperty("all", out var all) ||
                                all.ValueKind != System.Text.Json.JsonValueKind.Array ||
                                all.GetArrayLength() < 2) continue;
                            if (kv.Value.TryGetProperty("now", out var n))
                                node = n.GetString() ?? "";
                            if (string.IsNullOrEmpty(node)) node = kv.Name; // 没选中就用组名
                            break;
                        }
                    }
                }
                if (string.IsNullOrEmpty(node)) node = "DIRECT";

                var sb = new StringBuilder();
                sb.Append(await OneDelayAsync(http, node));
                if (node != "DIRECT") sb.Append('\n').Append(await OneDelayAsync(http, "DIRECT"));
                return sb.ToString();
            }
            catch (Exception ex)
            {
                ClashUWP.Vpn.RollingLog.Error("mihomo-delay", ex);
                return $"delay 失败: {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>打一个节点的 delay 探测；非 2xx 也要把响应体带回来（错误原文就在那里）。</summary>
        private static async Task<string> OneDelayAsync(
            System.Net.Http.HttpClient http, string name)
        {
            string url = "http://127.0.0.1:9090/proxies/" + Uri.EscapeDataString(name) +
                "/delay?timeout=5000&url=http://www.gstatic.com/generate_204";
            using var resp = await http.GetAsync(url);
            string body = await resp.Content.ReadAsStringAsync();
            if (body.Length > 300) body = body[..300] + "…";
            return $"{name}: HTTP {(int)resp.StatusCode} {body}";
        }

        /// <summary>
        /// 容器内回环用的 HttpClient：★ 必须关系统代理，否则请求被自己下发的代理劫走。
        /// ★ <b>internal</b>（原为 private）：<see cref="KernelView"/> 也要用同一条通道查
        ///   <c>/configs</c> / <c>/proxies</c> / <c>/connections</c>，
        ///   而"必须 <c>UseProxy=false</c>"这条是硬约束 —— 不能出现第二份实现。
        /// </summary>
        internal static System.Net.Http.HttpClient NewLoopbackClient(int timeoutSeconds)
            => new(new System.Net.Http.HttpClientHandler { UseProxy = false })
            {
                Timeout = TimeSpan.FromSeconds(timeoutSeconds)
            };

        // ===================== 选节点（G2，Docs/12 §5 第 3 项） =====================

        /// <summary>
        /// mihomo 在 <c>/proxies</c> 表里和真组并排列出来的<b>内置策略名</b>。
        ///
        /// 【实测】官方 v1.19.32（本轮 <c>.local/proxyapi</c> 现场，非文档转述）：
        ///   <c>COMPATIBLE / DIRECT / PASS / PASS-RULE / REJECT / REJECT-DROP</c> 的
        ///   <c>type</c> 是 <c>Compatible/Direct/Pass/PassRule/Reject/RejectDrop</c>
        ///   且<b>没有</b> <c>all</c> 数组 —— 它们已经被 <see cref="IsSelectableGroupType"/>
        ///   挡掉了；真正需要按名字排除的只有 <c>GLOBAL</c>（它就是
        ///   <c>type=Selector</c> + <c>all</c>，结构判据拿它没办法）。
        /// 【为什么还留整张名单】① 只防 GLOBAL 是"跟着这一版内核的形状走"，
        ///   名单是便宜的冗余；② 列出来之后，<c>groups</c> 的输出和
        ///   <c>select</c> 的报错都能说清"这个名字是内置策略，不是组"。
        /// </summary>
        private static readonly string[] BuiltinProxyNames =
            { "GLOBAL", "DIRECT", "REJECT", "REJECT-DROP", "PASS", "PASS-RULE", "COMPATIBLE" };

        /// <summary>
        /// 是不是"可以选进去的组"：<c>Selector</c> / <c>URLTest</c> / <c>Fallback</c> /
        /// <c>LoadBalance</c> 四种之一。
        /// ★ 类型名是内核给的<b>驼峰</b>形态（实测 <c>type=Selector</c>，而不是配置里的
        ///   <c>select</c>）—— 拿 yaml 的写法来比会永远比不中。两种形态都收，
        ///   免得以后内核改了序列化口径又变成"没有组"。
        /// </summary>
        private static bool IsSelectableGroupType(string type)
            => type is "Selector" or "URLTest" or "Fallback" or "LoadBalance"
               or "select" or "url-test" or "fallback" or "load-balance";

        /// <summary>一个可切换组：<c>Members</c> 是 <c>all</c> 里的成员名原样列表。</summary>
        internal sealed record ProxyGroup(string Name, string Type, string Now,
                                           System.Collections.Generic.List<string> Members);

        /// <summary>
        /// 从 <c>GET /proxies</c> 的原文里一次拿全：可切换的组 + 每个节点最近一次测速。
        ///
        /// 【★ 一次请求就够，实测口径】组的 <c>all</c> 在 <c>GET /proxies</c> 和
        ///   <c>GET /proxies/{组}</c> 里<b>都是字符串数组</b>（成员名），不会展开成对象 ——
        ///   所以"逐组再打一次明细接口拿延迟"是<b>白做的</b>。测速历史挂在
        ///   <b>每个节点自己的顶层条目</b>上（<c>proxies[节点名].history[]</c>，
        ///   实测 <c>GET /proxies/DIRECT/delay</c> 之后 DIRECT 那条立刻多出一个元素，
        ///   而组的 history 始终是 <c>[]</c>）。一次 <c>GET /proxies</c> 同时拿到两者。
        /// 【<c>Now</c> 缺失】给空串：没测过/没选过的组真的可能没有 <c>now</c>。
        /// ★ <b>internal</b>（原为 private）</b>：<see cref="KernelView"/> 要用同一份解析口径
        ///   产出 UI 用的结构化视图 —— 解析规则（组类型判定、延迟取顶层 history、内置策略过滤）
        ///   只能有一份，两边各写一遍必然分叉。
        /// </summary>
        internal static (System.Collections.Generic.List<ProxyGroup> Groups,
                        System.Collections.Generic.Dictionary<string, long> LastDelay)
            ReadProxiesTable(System.Text.Json.JsonElement root)
        {
            var groups = new System.Collections.Generic.List<ProxyGroup>();
            var delay = new System.Collections.Generic.Dictionary<string, long>(
                System.StringComparer.Ordinal);
            if (!root.TryGetProperty("proxies", out var proxies) ||
                proxies.ValueKind != System.Text.Json.JsonValueKind.Object)
                return (groups, delay);

            foreach (var kv in proxies.EnumerateObject())
            {
                // 每个条目都先记最近一次测速（成员名去顶层表里查的就是这个）。
                long d = LastHistoryDelay(kv.Value);
                if (d >= 0) delay[kv.Name] = d;

                bool builtin = false;
                foreach (string b in BuiltinProxyNames)
                    if (string.Equals(b, kv.Name, System.StringComparison.Ordinal)) { builtin = true; break; }
                if (builtin) continue;

                string type = GetString(kv.Value, "type");
                if (!IsSelectableGroupType(type)) continue;

                var members = new System.Collections.Generic.List<string>();
                if (kv.Value.TryGetProperty("all", out var all) &&
                    all.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var m in all.EnumerateArray())
                    {
                        // 实测是字符串；对象形态留一手（将来某版展开成对象也不至于读空）。
                        if (m.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            string? s = m.GetString();
                            if (!string.IsNullOrEmpty(s)) members.Add(s!);
                        }
                        else if (m.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            string s = GetString(m, "name");
                            if (s.Length > 0) members.Add(s);
                        }
                    }
                }

                groups.Add(new ProxyGroup(kv.Name, type, GetString(kv.Value, "now"), members));
            }
            return (groups, delay);
        }

        /// <summary>读一个字符串字段；缺失/类型不对给空串（组名与节点名都必须原样，不做任何加工）。</summary>
        private static string GetString(System.Text.Json.JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v)) return "";
            return v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
        }

        /// <summary>
        /// 该条目 <c>history</c> 里<b>最后一个</b> <c>delay</c>（实测是追加式的，末尾就是最近一次）；
        /// 没有历史给 <c>-1</c> —— 调用方必须把它显示成"没测过"，<b>不能</b>显示成 0 或"挂了"。
        /// </summary>
        private static long LastHistoryDelay(System.Text.Json.JsonElement e)
        {
            if (!e.TryGetProperty("history", out var h) ||
                h.ValueKind != System.Text.Json.JsonValueKind.Array)
                return -1;
            long last = -1;
            foreach (var item in h.EnumerateArray())
            {
                long d = ReadNumber(item, "delay");
                if (d >= 0) last = d;
            }
            return last;
        }

        /// <summary>
        /// 列出可切换的组与成员（debug <c>groups</c> 命令）—— 选节点 UI 的最小前身。
        ///
        /// 【为什么要】<c>controller</c> 命令只回<b>第一个</b>组的一行摘要（它当初就是为
        ///   "内核活着吗"设计的），而选节点必须看到"有哪些组、每组现在选了谁、成员叫什么"。
        ///   订阅派生的组名是中文（实测「测试分组」，真订阅里是「星链机场」），节点名
        ///   还带中文与前导空格，<b>靠猜是猜不出可用的节点名的</b> —— 必须原样列出来再抄进 select。
        /// 【★ 延迟列怎么读】<c>delay=-</c> = 这个成员<b>从没被测过</b>。select 组带
        ///   <c>lazy: true</c> 时启动不测速，整列都是 <c>-</c> 是正常现象，不是"全挂了"；
        ///   要数就紧接着打 <c>delay</c> 命令或让 <c>mode global</c> 造流量。
        ///   （同一份数据里 <c>alive</c> 全是 <c>true</c>，实测未测速时不可信，故不列。）
        /// </summary>
        internal static async Task<string> RunGroupsAsync()
        {
            try
            {
                using var http = NewLoopbackClient(10);
                string json = await http.GetStringAsync("http://127.0.0.1:9090/proxies");
                var sb = new StringBuilder();

                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var (groups, delay) = ReadProxiesTable(doc.RootElement);

                if (groups.Count == 0)
                    return "(内核里没有可切换的代理组：要么规则里没有组，要么订阅没加载。" +
                           "可先 debug conf 看 proxies 有没有落到内核。)";

                sb.Append($"可切换组={groups.Count}（* = 当前选中；delay=- 表示该成员还没被测过，不等于挂了）");
                foreach (var g in groups)
                {
                    sb.Append($"\n组={g.Name} 类型={g.Type} 成员={g.Members.Count} " +
                              $"选中={(g.Now.Length == 0 ? "(空：未选中/未测速)" : g.Now)}");
                    if (g.Members.Count == 0)
                    {
                        sb.Append("  (all 是空的：这个组没有可选成员)");
                        continue;
                    }
                    int shown = 0;
                    foreach (string name in g.Members)
                    {
                        if (shown++ >= 40) { sb.Append("\n    …（成员过多，只列前 40）"); break; }
                        string mark = string.Equals(name, g.Now, System.StringComparison.Ordinal)
                            ? "*" : " ";
                        sb.Append($"\n   {mark} {name}");
                        sb.Append(delay.TryGetValue(name, out long d) ? $" delay={d}ms" : " delay=-");
                    }
                }

                string s = sb.ToString();
                return s.Length > 2600 ? s[..2600] + "…" : s;
            }
            catch (Exception ex)
            {
                ClashUWP.Vpn.RollingLog.Error("mihomo-groups", ex);
                return $"groups 查询失败: {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>
        /// 切换节点（debug <c>select</c> 命令）：<c>PUT /proxies/{组}</c>，body
        /// <c>{"name":"节点名"}</c>，成功回 204。
        ///
        /// 【★ 只有 PUT 生效】官方 v1.19.32 实测（Docs/12 §7）：同一条 URL
        ///   <c>PATCH</c> 回 <b>405</b>、<c>PUT</c> 回 <b>204</b>。记忆里"PATCH /proxies
        ///   切节点"那句是错的，已按实测改掉 —— 照它写会做出一个静默无效的开关。
        /// 【为什么先校验再发】节点名带中文、emoji、前导空格，手敲几乎必错；
        ///   直接把"不在该组成员表里"连同候选一起回出来，比让内核默默收下再发现
        ///   没变化要省一整轮。组名与节点名都<b>区分大小写、精确匹配</b>。
        ///   成员表就取自同一次 <c>GET /proxies</c>（见 <see cref="ReadProxiesTable"/>），
        ///   不再为校验多打一个接口。
        /// 【参数口径】第一个空格前是组名，其余全是节点名（节点名里可以有空格）。
        ///   只给一段时当作节点名，并在"内核里只有一个可切换组"时自动补组名；
        ///   多个组则报错列出组名，绝不猜。
        /// 【★ 持久化的真话】切完之后内核会把选择写进 <c>&lt;home&gt;/cache.db</c>
        ///   （实测：改选择 → 强杀进程 → 重启，<c>now</c> 仍是改后的值），
        ///   但我们的 home 现在是 <c>Package.Current.InstalledLocation</c>（只读），
        ///   所以<b>重启/重装后不保留</b>。那条路属于 G10（home 迁 LocalState），
        ///   不是这条命令能顺手解决的 —— 这里把它写进返回值，免得把"没持久化"
        ///   当成 select 失败再查一遍。
        /// </summary>
        internal static async Task<string> RunSelectAsync(string arg)
        {
            if (string.IsNullOrWhiteSpace(arg))
                return "select 需要参数：select <组名> <节点名>（只有一个组时可只写节点名）。" +
                       "先用 groups 命令看确切的组名与节点名。";

            try
            {
                int sp = arg.IndexOf(' ');
                string group = sp < 0 ? "" : arg.Substring(0, sp).Trim();
                string node = sp < 0 ? arg.Trim() : arg.Substring(sp + 1).Trim();
                if (node.Length == 0)
                    return $"select 参数里节点名为空: “{arg}”";

                using var http = NewLoopbackClient(10);
                System.Collections.Generic.List<ProxyGroup> groups;
                using (var doc = System.Text.Json.JsonDocument.Parse(
                           await http.GetStringAsync("http://127.0.0.1:9090/proxies")))
                    groups = ReadProxiesTable(doc.RootElement).Groups;

                if (groups.Count == 0)
                    return "内核里没有可切换的代理组（先 groups / conf 确认订阅真的加载了）";

                if (group.Length == 0)
                {
                    if (groups.Count > 1)
                        return $"有 {groups.Count} 个组，必须指名：select <组名> <节点名>。组名：" +
                               string.Join(" | ", GroupNames(groups));
                    group = groups[0].Name;
                }

                ProxyGroup? target = null;
                foreach (var g in groups)
                    if (string.Equals(g.Name, group, System.StringComparison.Ordinal)) { target = g; break; }
                if (target == null)
                {
                    // 用户可能把内置策略名当组名打进来（"select DIRECT xxx"）—— 值得单说一句，
                    // 因为内置策略在 /proxies 表里和真组并排，看着就像组。
                    bool builtin = false;
                    foreach (string b in BuiltinProxyNames)
                        if (string.Equals(b, group, System.StringComparison.Ordinal)) { builtin = true; break; }
                    return $"找不到组 “{group}”（区分大小写，原样抄 groups 的输出）" +
                           (builtin ? "：它是内核的内置策略，不是可切换的代理组。" : "") +
                           "现有组：" + string.Join(" | ", GroupNames(groups));
                }

                bool ok = false;
                foreach (string m in target.Members)
                    if (string.Equals(m, node, System.StringComparison.Ordinal)) { ok = true; break; }
                if (!ok)
                {
                    // 前 12 个成员原样回显，够抄了；全量给会把 debug-out.txt 撑爆。
                    int n = target.Members.Count < 12 ? target.Members.Count : 12;
                    return $"组 “{group}” 里没有节点 “{node}”（区分大小写、空格与 emoji 都算）。" +
                           $"前 {n} 个成员：{string.Join(" | ", target.Members.GetRange(0, n))}" +
                           (target.Members.Count > n ? " …（全量见 groups）" : "");
                }

                // 组名进 URL 一律 percent-encode。实测 PUT 吃原始 UTF-8（中文组名直接拼
                // 也能 204），但那条"中文进路径就丢 query"的坑只发生在带 query 的
                // /delay 上 —— 两种形态别赌，统一编码。节点名走 JSON body，不进 URL。
                string url = "http://127.0.0.1:9090/proxies/" + Uri.EscapeDataString(group);
                using var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Put, url)
                {
                    Content = new System.Net.Http.StringContent(
                        "{\"name\":\"" + JsonEscape(node) + "\"}", Encoding.UTF8, "application/json")
                };
                using var resp = await http.SendAsync(req);
                string respBody = await resp.Content.ReadAsStringAsync();
                if (respBody.Length > 200) respBody = respBody[..200] + "…";

                // 回读 now：204 不等于"选上了"，只有这一格能证明。
                string now = "(读不到)";
                try
                {
                    using var d3 = System.Text.Json.JsonDocument.Parse(
                        await http.GetStringAsync(url));
                    now = GetString(d3.RootElement, "now");
                    if (now.Length == 0) now = "(空)";
                }
                catch (Exception readEx)
                {
                    now = "(回读失败 " + readEx.GetType().Name + ")";
                }

                // 自动组（url-test / fallback / load-balance）的"选中"由内核自己维护：
                // PUT 一个成员名会被接受，但下一轮测速/故障转移就把名字改回赢家。
                // 必须当场说出来，否则"我明明选了它，怎么又回去了"会被读成 select 失败。
                string groupNote = target.Type is "Selector" or "select"
                    ? "" : $"\n※ 该组类型是 {target.Type}（自动组）：选择会立刻生效，" +
                           "但测速/故障转移可能把它改回赢家 —— 不是 select 没成功。";

                if (!resp.IsSuccessStatusCode)
                    return $"PUT /proxies/{group} -> {(int)resp.StatusCode} {respBody}；现值 now={now}" + groupNote;

                string verdict = string.Equals(now, node, System.StringComparison.Ordinal)
                    ? "★ 已选中" : "⚠ 现值与请求不一致，别当成功";
                return $"PUT /proxies/{group} -> {(int)resp.StatusCode}；now={now}（{verdict}）" +
                       $"\n※ 选择由内核写进 <home>/cache.db 持久化，但当前 home 是只读的 " +
                       $"InstalledLocation ⇒ 重启/重装后不保留（G10 修 home 之后才有持久化）。" +
                       groupNote;
            }
            catch (Exception ex)
            {
                ClashUWP.Vpn.RollingLog.Error("mihomo-select", ex);
                return $"select 失败: {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>组名列表（报错时把候选原样回出来，供照抄）。</summary>
        private static System.Collections.Generic.IEnumerable<string> GroupNames(
            System.Collections.Generic.List<ProxyGroup> groups)
        {
            foreach (var g in groups) yield return g.Name;
        }

        /// <summary>
        /// 手写 JSON 字符串转义（★ 不能用 <c>JsonSerializer.Serialize</c>：本包
        /// Native AOT + 反射序列化被关闭，它直接抛 <c>InvalidOperationException</c>，
        /// 实测 measure-12.log 的 bind 两步就栽在这）。
        /// 与 <c>RunBindAsync</c> 里那段内联转义同一口径，只是抽成了函数 ——
        /// 节点名里能出现引号和反斜杠（"xxx\" 这种），不能像网卡名那样想当然。
        /// </summary>
        private static string JsonEscape(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                if (ch is '"' or '\\') sb.Append('\\').Append(ch);
                else if (ch == '\n') sb.Append("\\n");
                else if (ch == '\r') sb.Append("\\r");
                else if (ch == '\t') sb.Append("\\t");
                else if (ch < 0x20) sb.Append($"\\u{(int)ch:x4}");
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>
        /// PATCH /configs —— mihomo 的<b>运行时</b>改配置通道（逐字段生效，不重载整份配置）。
        /// 成功回 204。这条同时是 1.0.16 那个 <c>TryHotReload</c> 的反面教材：
        /// PUT /configs 的 path/payload 是从 <b>JSON body</b> 里取的，
        /// 我们只把 path 拼在 query 上，所以热重载从来没生效过。
        /// </summary>
        private static async Task<string> PatchConfigsAsync(
            System.Net.Http.HttpClient http, string jsonBody)
        {
            using var req = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Patch, "http://127.0.0.1:9090/configs")
            {
                Content = new System.Net.Http.StringContent(
                    jsonBody, Encoding.UTF8, "application/json")
            };
            using var resp = await http.SendAsync(req);
            return $"{(int)resp.StatusCode}{(resp.IsSuccessStatusCode ? "" : " " + await resp.Content.ReadAsStringAsync())}";
        }

        /// <summary>
        /// 查看/关闭活动连接（debug connections / kill 命令）。
        ///
        /// 【为什么要】这是 G4「连接视图 / 强制断开」的后端实现：
        ///   · <c>connections</c> —— 列出所有活动连接的目标、链路、流量；
        ///   · <c>kill &lt;id&gt;</c> —— 强断指定连接（DELETE /connections/{id}）；
        ///   · <c>kill-all</c> —— 清空所有连接（DELETE /connections）。
        ///
        /// 相比 <see cref="RunControllerProbeAsync"/> 的快照式 dump，这个通道支持
        /// <b>主动干预</b>：当某个连接卡死或占着资源不放时，可以手动把它踢掉。
        /// </summary>
        internal static async Task<string> RunConnectionsAsync(string action)
        {
            try
            {
                using var http = NewLoopbackClient(10);

                // kill-all: 清空所有连接
                if (action == "kill-all")
                {
                    var req = new HttpRequestMessage(HttpMethod.Delete, "http://127.0.0.1:9090/connections");
                    using var resp = await http.SendAsync(req);
                    string status = resp.IsSuccessStatusCode ? "204" : $"{(int)resp.StatusCode}";
                    return $"清空所有连接 → {status}";
                }

                // kill <id>: 强断单个连接
                if (action.StartsWith("kill ", StringComparison.OrdinalIgnoreCase))
                {
                    string id = action.Substring(5).Trim();
                    if (string.IsNullOrEmpty(id))
                        return "kill 需要参数：kill <connection-id>（先用 connections 命令看 ID）";

                    var req = new HttpRequestMessage(HttpMethod.Delete, $"http://127.0.0.1:9090/connections/{id}");
                    using var resp = await http.SendAsync(req);
                    string status = resp.IsSuccessStatusCode ? "204" : $"{(int)resp.StatusCode}";
                    return $"删除连接 {id} → {status}";
                }

                // 默认：列出所有连接
                string connJson = await http.GetStringAsync("http://127.0.0.1:9090/connections");
                using var doc = System.Text.Json.JsonDocument.Parse(connJson);

                var sb = new StringBuilder();
                if (!doc.RootElement.TryGetProperty("connections", out var conns) ||
                    conns.ValueKind != System.Text.Json.JsonValueKind.Array)
                    return "(内核里没有活动连接列表)";

                int total = conns.GetArrayLength();
                sb.AppendLine($"活动连接数={total}");
                if (total == 0)
                    return sb.ToString().TrimEnd();

                sb.AppendLine("(id | 目标 | 协议 | 链路 | 流量)");
                int shown = 0;
                foreach (var c in conns.EnumerateArray())
                {
                    if (shown++ >= 20) break; // 最多列 20 条

                    string id = "?", dst = "?", net = "?", chain = "?";
                    long up = -1, down = -1;

                    // ID
                    if (c.TryGetProperty("id", out var idEl))
                        id = idEl.GetString() ?? "?";

                    // 目标
                    if (c.TryGetProperty("metadata", out var meta))
                    {
                        dst = meta.TryGetProperty("destinationIP", out var dip)
                            ? dip.GetString() ?? "?"
                            : meta.TryGetProperty("host", out var h) ? h.GetString() ?? "?" : "?";
                        if (meta.TryGetProperty("destinationPort", out var dport))
                            dst += ":" + dport.GetString();
                        if (meta.TryGetProperty("network", out var n))
                            net = n.GetString() ?? "?";

                        // 域名比 IP 好认
                        string? mh = meta.TryGetProperty("host", out var hh) ? hh.GetString() : null;
                        if (!string.IsNullOrEmpty(mh) && mh != dst)
                            dst += $"({mh})";
                    }

                    // 链路
                    if (c.TryGetProperty("chains", out var ch) &&
                        ch.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        var parts = new System.Collections.Generic.List<string>();
                        foreach (var e in ch.EnumerateArray())
                            parts.Add(e.GetString() ?? "?");
                        chain = string.Join(" > ", parts);
                    }

                    // 流量
                    up = ReadNumber(c, "upload");
                    down = ReadNumber(c, "download");
                    if (up < 0) up = ReadNumber(c, "uploadTraffic");
                    if (down < 0) down = ReadNumber(c, "downloadTraffic");

                    string traffic = up >= 0 || down >= 0 ? $"↑{up}B ↓{down}B" : "";
                    sb.AppendLine($"{id} | {dst} | {net} | {chain} | {traffic}");
                }

                if (total > 20)
                    sb.AppendLine($"... 还有 {total - 20} 条未显示");

                sb.AppendLine("\n操作提示：");
                sb.AppendLine("  kill <id>     —— 强断指定连接");
                sb.AppendLine("  kill-all      —— 清空所有连接");

                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                ClashUWP.Vpn.RollingLog.Error("mihomo-connections", ex);
                return $"connections 失败: {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>读一个数字字段；不存在或类型不对给 -1（数字可能是 Number 也可能是字符串）。</summary>
        private static long ReadNumber(System.Text.Json.JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v))
                return -1;
            if (v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetInt64(out long n))
                return n;
            if (v.ValueKind == System.Text.Json.JsonValueKind.String &&
                long.TryParse(v.GetString(), out long s))
                return s;
            return -1;
        }

        /// <summary>
        /// ★ 诊断内核启动状态：检查 REST API、读取插件日志、探测端口监听。
        /// </summary>
        internal static async Task<string> DiagnoseKernelAsync()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== 内核诊断 ===");

            // 1. 尝试访问 REST API
            try
            {
                using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
                http.Timeout = TimeSpan.FromSeconds(3);
                string version = await http.GetStringAsync("http://127.0.0.1:9090/version");
                sb.AppendLine($"✓ REST API 可达: {version}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"✗ REST API 不可达: {ex.GetType().Name}: {ex.Message}");
            }

            // 2. 读取插件日志最后 20 行
            try
            {
                var pluginLog = await LocalState.ReadIfExistsByPathAsync("logs/plugin.log");
                if (!string.IsNullOrEmpty(pluginLog))
                {
                    var lines = pluginLog.Split('\n');
                    var tail = lines.Length > 20 ? lines[^20..] : lines;
                    sb.AppendLine("\n--- 插件日志（最后 20 行）---");
                    foreach (var line in tail)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            sb.AppendLine(line.Trim());
                    }
                }
                else
                {
                    sb.AppendLine("\n✗ 插件日志不存在或为空");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"\n✗ 读取插件日志失败: {ex.Message}");
            }

            // 3. 检查 LocalState 里的内核配置
            try
            {
                var configPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Packages", "421769c5-17f4-4287-b6c1-193222d21e14_y0tg80em3rma2",
                    "LocalState", "config.kernel.yaml");
                
                if (File.Exists(configPath))
                {
                    var config = File.ReadAllText(configPath);
                    var lines = config.Split('\n');
                    sb.AppendLine($"\n✓ 内核配置文件存在 ({lines.Length} 行)");
                    
                    // 检查关键配置项
                    if (config.Contains("external-controller: 127.0.0.1:9090"))
                        sb.AppendLine("  ✓ external-controller 配置正确");
                    else
                        sb.AppendLine("  ✗ external-controller 配置缺失或错误");
                }
                else
                {
                    sb.AppendLine("\n✗ 内核配置文件不存在");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"\n✗ 检查配置文件失败: {ex.Message}");
            }

            return sb.ToString();
        }
    }
}
