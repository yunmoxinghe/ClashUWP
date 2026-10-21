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
        /// ★【1.0.81 · M8/Q12 · 可单测】把 <see cref="RunReloadAsync"/> 返回的文本判成
        /// "内核是否真的接受了这份配置"。
        ///
        /// 内核对非法配置返回非 2xx（4xx/5xx，body 里写着原因），<see cref="RunReloadAsync"/>
        /// 据此拼出三种文本：
        ///   · <c>重载成功（HTTP 2xx）：&lt;path&gt;</c>  —— 解析并应用成功；
        ///   · <c>重载失败（HTTP 4xx）：&lt;原因&gt;</c>   —— 内核明确拒绝（配置非法/路径不安全）；
        ///   · <c>reload 失败: &lt;异常&gt;</c> / <c>reload 需要参数：…</c> —— 连请求都没发出去。
        /// 这里只认"重载成功"前缀，其余一律算失败 —— 不让"落盘即成功"把核心拒绝伪装成成功。
        /// 纯函数（不碰网络），可被离线夹具直测（M1 同款思路）。
        /// </summary>
        internal static bool IsReloadSuccess(string reloadResult)
            => reloadResult != null &&
               reloadResult.StartsWith("重载成功", System.StringComparison.Ordinal);

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
        // ★【1.0.81 · Q11/Q13】internal（原为 private）：<see cref="SubscriptionView"/>
        //   也要用同一份保留名名单去查"用户节点/组名是否撞了内置策略名"（那也是一类
        //   命名冲突，mihomo 会折叠或报错，订阅侧就能先发现）。保留名是固定集合，
        //   两份口径必须同源，所以这里放开可见性而不是在订阅解析里重抄一份。
        internal static readonly string[] BuiltinProxyNames =
            { "GLOBAL", "DIRECT", "REJECT", "REJECT-DROP", "PASS", "PASS-RULE", "COMPATIBLE" };

        /// <summary>
        /// 是不是"可以选进去的组"：<c>Selector</c> / <c>URLTest</c> / <c>Fallback</c> /
        /// <c>LoadBalance</c> / <c>Relay</c> 五种之一（官方内核给的组类型）。
        /// ★ 类型名是内核给的<b>驼峰</b>形态（实测 <c>type=Selector</c>，而不是配置里的
        ///   <c>select</c>）—— 拿 yaml 的写法来比会永远比不中。两种形态都收，
        ///   免得以后内核改了序列化口径又变成"没有组"。
        /// ★【1.0.81 · M3/Q6】补 <c>Relay</c>/<c>relay</c>：relay 与 Selector 一样是<b>手动组</b>，
        ///   选择由用户决定且持久（不会像 url-test/fallback/load-balance 那样被内核改回）；
        ///   官方 Mihomo 同样支持 <c>PUT /proxies/{name}</c> 切换 relay 的选中项。
        ///   <c>LoadBalance</c> 维持"不可手选"（自动按策略分流，手选无意义）。
        /// </summary>
        private static bool IsSelectableGroupType(string type)
            => type is "Selector" or "URLTest" or "Fallback" or "LoadBalance" or "Relay"
               or "select" or "url-test" or "fallback" or "load-balance" or "relay";

        /// <summary>一个可切换组：<c>Members</c> 是 <c>all</c> 里的成员名原样列表。</summary>
        internal sealed record ProxyGroup(string Name, string Type, string Now,
                                           System.Collections.Generic.List<string> Members);

        /// <summary>
        /// <c>/proxies</c> 表里<b>每一个</b>条目的分类结果 —— 安卓版
        /// <c>convertProxies</c> 那句
        /// <c>_, isGroup := p.Adapter().(outboundgroup.ProxyGroup)</c> 在我们这边的等价物。
        ///
        /// 【★ 为什么必须在后端分，不能让 UI 猜】安卓的组/节点之分是<b>内核在 Go 里判好
        ///   再交给前端</b>的（<c>Proxy{IsGroup}</c>），前端只做呈现。我们以前的
        ///   <c>ReadProxiesTable</c> 只留下"是组的那些"，其余条目的 <c>type</c> 一律丢弃，
        ///   于是 UI 拿到的成员表里"嵌套组 / 内置策略 / 真节点"长得完全一样 ——
        ///   这就是"代理组成为节点"的根因（实测这份 iKuuu 订阅 11 个组里有 9 个
        ///   一个真节点都没有，成员全是别的组，界面却把它们渲染成同一种可点行）。
        /// 【判据】<c>IsGroup</c> = 该条目带 <c>all</c> 数组（实测真组都有、真节点都没有），
        ///   这是<b>结构判据</b>，比"按类型名枚举"稳；内置策略（DIRECT/REJECT…）没有
        ///   <c>all</c>，所以天然不是组，<c>IsBuiltin</c> 再单独标一份，
        ///   让 UI 能给它们照安卓那样显示类型副标题（"Direct"/"Reject"），
        ///   而不是像我们以前那样把它们整个从延迟表里藏掉。
        /// </summary>
        internal sealed record ProxyInfo(string Name, string Type, bool IsGroup,
                                          string Now, bool IsBuiltin, string ProviderName);

        /// <summary>
        /// 从 <c>GET /proxies</c> 的原文里一次拿全：可切换的组 + 每个节点最近一次测速 +
        /// 每张条目的分类表（组/节点/内置策略）。
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
        // ★【1.0.81 · Q11】返回值多了 <c>Duplicates</c>：<see cref="ByName"/> 是按名字
        //   扁平建表的 —— 一旦内核返回两个同名条目（mihomo 正常不会，但畸形的
        //   /proxies 响应、或订阅里重名节点被内核折叠后仍可能露出马脚），后写的会
        //   悄悄盖掉先写的，元数据（类型/延迟/是否策略）就会错位。这里把"盖掉"这个
        //   动作<b>记下来</b>而不是默默发生：<c>Duplicates</c> 计数，让上层诊断能
        //   明确说出来"内核里有 N 处同名条目，已按名字折叠"，而不是让用户对着错位的
        //   延迟/类型干瞪眼。切换本身以组作用域进行（PUT /proxies/{组} 在组内解析），
        //   所以同名不影响切得对，只影响"展示层"——这正是 Q11 残留的那一条。
        internal static (System.Collections.Generic.List<ProxyGroup> Groups,
                        System.Collections.Generic.Dictionary<string, long> LastDelay,
                        System.Collections.Generic.Dictionary<string, ProxyInfo> ByName,
                        int Duplicates)
            ReadProxiesTable(System.Text.Json.JsonElement root)
        {
            var groups = new System.Collections.Generic.List<ProxyGroup>();
            var delay = new System.Collections.Generic.Dictionary<string, long>(
                System.StringComparer.Ordinal);
            var byName = new System.Collections.Generic.Dictionary<string, ProxyInfo>(
                System.StringComparer.Ordinal);
            int duplicates = 0;
            if (!root.TryGetProperty("proxies", out var proxies) ||
                proxies.ValueKind != System.Text.Json.JsonValueKind.Object)
                return (groups, delay, byName, duplicates);

            foreach (var kv in proxies.EnumerateObject())
            {
                // 每个条目都先记最近一次测速（成员名去顶层表里查的就是这个）。
                long d = LastHistoryDelay(kv.Value);
                if (d >= 0) delay[kv.Name] = d;

                string type = GetString(kv.Value, "type");
                string now = GetString(kv.Value, "now");
                // ★【1.0.81 · M4/Q8】官方 GET /proxies 会把 <c>proxy-providers</c> 里的节点
                //   <b>提升为顶层条目</b>并附 <c>provider-name</c> 字段（见 Mihomo 源码
                //   proxyProviders 的 merge）。订阅直给的节点该字段为空，提供方动态拉来的
                //   节点带提供方名 —— 这是区分"节点来自哪"的唯一官方信号。
                string providerName = GetString(kv.Value, "provider-name");
                bool hasAll = kv.Value.TryGetProperty("all", out var allEl) &&
                              allEl.ValueKind == System.Text.Json.JsonValueKind.Array;

                bool builtin = false;
                foreach (string b in BuiltinProxyNames)
                    if (string.Equals(b, kv.Name, System.StringComparison.Ordinal)) { builtin = true; break; }

                // ★ 分类表对<b>每一个</b>条目都要记（下面两个 continue 只挡"能不能当组来切"，
                //   挡不住"UI 得知道这个成员是什么"）。
                // ★【1.0.81 · Q11】同名条目不再默默覆盖：若这个名字已经建过表，记一次碰撞
                //   （后写的会盖掉先写的，元数据可能错位——计数让上层能说出来）。
                if (byName.ContainsKey(kv.Name)) duplicates++;
                byName[kv.Name] = new ProxyInfo(kv.Name, type,
                    IsGroup: hasAll && !builtin, now, builtin, providerName);

                if (builtin) continue;
                if (!IsSelectableGroupType(type)) continue;

                var members = new System.Collections.Generic.List<string>();
                if (hasAll)
                {
                    foreach (var m in allEl.EnumerateArray())
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

                groups.Add(new ProxyGroup(kv.Name, type, now, members));
            }
            return (groups, delay, byName, duplicates);
        }

        // ===================== 代理提供方状态（M7 / Q10，官方 GET /providers/proxies） =====================

        /// <summary>
        /// 一个代理提供方的状态（<c>GET /providers/proxies</c> 解析结果）。
        ///
        /// 【★ 官方依据】Mihomo 暴露 <c>GET /providers/proxies</c>，返回每个
        ///   <c>proxy-providers:</c> 段提供方的：<c>vehicleType</c>（Http/File/Inline）、
        ///   <c>proxies</c>（已加载的节点数组）、<c>updatedAt</c>（最近一次拉取/更新时间）、
        ///   <c>subscriptionInfo</c>（upload/download/total/expire，仅 http 订阅有）。
        ///   这是 Q10 缺的那块能力：provider 级「刷没刷到、拉了多少节点、流量剩多少」在客户端
        ///   一直不可见，只能看到被提升到 <c>/proxies</c> 的节点（且那层不报节点总数）。
        /// 【与 <see cref="ReadProxiesTable"/> 同一风格】纯解析、internal，供 <see cref="KernelView"/>
        ///   与离线夹具共用同一份口径。
        /// </summary>
        internal sealed record ProviderInfo(
            string Name,
            string VehicleType,   // Http / File / Inline（官方 vehicleType 首字母大写）
            int NodeCount,        // proxies 数组长度
            string UpdatedAt,     // 原始时间戳字符串（RFC3339，缺则空）
            long SubUpload,
            long SubDownload,
            long SubTotal,
            long SubExpire);      // Unix 毫秒，0 表示无

        /// <summary>
        /// 解析 <c>GET /providers/proxies</c> 的 <c>providers</c> 对象。
        /// ★ 纯函数：离线夹具直接喂 JSON 字符串验证，不碰网络。
        /// 任一提供方缺字段都不抛，按"缺省值"处理 —— 内核返回的字段偶有不齐。
        /// </summary>
        internal static System.Collections.Generic.List<ProviderInfo> ReadProvidersTable(
            System.Text.Json.JsonElement root)
        {
            var list = new System.Collections.Generic.List<ProviderInfo>();
            if (!root.TryGetProperty("providers", out var providers) ||
                providers.ValueKind != System.Text.Json.JsonValueKind.Object)
                return list;

            foreach (var kv in providers.EnumerateObject())
            {
                var p = kv.Value;
                string name = GetString(p, "name");
                if (name.Length == 0) name = kv.Name;   // 兜底用对象 key
                string vehicle = GetString(p, "vehicleType");

                int nodeCount = 0;
                if (p.TryGetProperty("proxies", out var px) &&
                    px.ValueKind == System.Text.Json.JsonValueKind.Array)
                    nodeCount = px.GetArrayLength();

                string updatedAt = GetString(p, "updatedAt");

                long up = 0, down = 0, total = 0, expire = 0;
                if (p.TryGetProperty("subscriptionInfo", out var sub) &&
                    sub.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    up = ReadNumber(sub, "upload");
                    down = ReadNumber(sub, "download");
                    total = ReadNumber(sub, "total");
                    expire = ReadNumber(sub, "expire");
                }

                list.Add(new ProviderInfo(name, vehicle, nodeCount, updatedAt,
                    up, down, total, expire));
            }
            return list;
        }

        /// <summary>
        /// 取全部提供方状态（<c>GET /providers/proxies</c>）。
        /// ★ 失败返回<b>空列表</b>（不是抛）—— 提供方状态是<b>补充信息</b>，
        ///   拉不到不该让"代理页"整体报错；内核没起时本就空。
        /// </summary>
        internal static async System.Threading.Tasks.Task<
            System.Collections.Generic.List<ProviderInfo>> GetProvidersAsync()
        {
            var empty = new System.Collections.Generic.List<ProviderInfo>();
            try
            {
                using var http = NewLoopbackClient(10);
                string json = await http.GetStringAsync("http://127.0.0.1:9090/providers/proxies");
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                return ReadProvidersTable(doc.RootElement);
            }
            catch (Exception ex)
            {
                ClashUWP.Vpn.RollingLog.Info("providers",
                    $"GET /providers/proxies 失败（补充信息，忽略）: {ex.GetType().Name}");
                return empty;
            }
        }

        /// <summary>
        /// 触发某提供方的健康/节点刷新（<c>GET /providers/proxies/{name}/healthcheck</c>）。
        /// ★ 返回给人看的文本；204 表示已触发刷新（节点会在稍后陆续更新）。
        /// 失败的本体（如 404 提供方不存在）也返回文本，不抛。
        /// </summary>
        internal static async System.Threading.Tasks.Task<string> TriggerProviderHealthCheckAsync(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "healthcheck 需要参数：提供一个方名称";
            try
            {
                using var http = NewLoopbackClient(30);
                using var resp = await http.GetAsync(
                    "http://127.0.0.1:9090/providers/proxies/" +
                    System.Uri.EscapeDataString(name) + "/healthcheck");
                int code = (int)resp.StatusCode;
                // 官方：成功 204 No Content；节点会异步刷新，稍后 GET /proxies 可见新延迟。
                return code is 200 or 204
                    ? $"已触发「{name}」节点刷新（{code}），稍后自动重载延迟"
                    : $"「{name}」刷新失败（HTTP {code}）";
            }
            catch (Exception ex)
            {
                ClashUWP.Vpn.RollingLog.Error("providers-hc", ex);
                return $"「{name}」刷新失败: {ex.GetType().Name}";
            }
        }

        /// <summary>
        /// 组测速接口的 URL。<b>纯函数</b>，离线夹具直测。
        ///
        /// ★ 组名进路径<b>一律 percent-encode</b>：订阅里的组名普遍带 emoji、带空格，
        ///   甚至可能带 <c>/</c>（<c>🚀 加速/代理</c> 这类写法真有机场在用）。不编码的话
        ///   一个 <c>/</c> 就把路由切断了 —— 那不是"测速失败"，是打到别的接口上去了。
        ///   同 <see cref="SelectCoreAsync"/> 那条"组名进 URL 一律编码"的实测口径。
        /// </summary>
        internal static string GroupHealthCheckUrl(string group) =>
            "http://127.0.0.1:9090/proxies/" + Uri.EscapeDataString(group) + "/healthcheck";

        /// <summary>
        /// 让这个组立刻把成员全测一遍（安卓版那枚 ⚡ 的等价物：CMFA 是
        /// <c>healthCheck(groupName)</c> 之后重载这一组的数据，<c>ProxyActivity.kt 93-101</c>）。
        ///
        /// ★ 测速用的是<b>组自己在订阅里配的</b> <c>url:</c> / <c>timeout:</c> / <c>interval:</c>
        ///   （内核侧 <c>group.Healthcheck</c> 读的就是这几个字段），本函数不传测试地址、
        ///   不覆盖机场的设定 —— 与安卓一致。
        /// ★ 自动组（url-test / fallback）同样能测，而且这才是它的正事：手动组测完
        ///   只是把延迟刷新一下，自动组测完可能直接换赢家。所以这个按钮<b>不给
        ///   "能不能手选"门控</b>，每组都有。
        /// ★ 证据级别：<c>PUT /proxies/{name}/healthcheck</c> + 成功 204 这条按
        ///   <b>推断</b>记账（mihomo/clash REST 路由与 yacd 面板的用法一致如此；
        ///   官方 API 手册原文这一版没抓到 —— 沙箱无外网）。因此这里把内核回的
        ///   <b>状态码与响应体原样带回</b>：204=方法对了，405=方法不对（该换 GET），
        ///   404=路由不对。一次上机就定死，不搞"两种都试"的和稀泥。
        /// ★ 响应 204 是"已触发"，不是"测完了"：延迟是异步写回 <c>history</c> 的，
        ///   界面上的数会由 3 秒轮询带出来（与安卓"测完重载该组数据"同一效果）。
        /// </summary>
        internal static async Task<string> TriggerGroupHealthCheckAsync(string group)
        {
            if (string.IsNullOrEmpty(group))
                return "测速需要参数：给一个组名称";
            try
            {
                // 一次要拨完整组成员，最慢的那员就是这里的上限；30 秒够机场常见
                // （timeout 5000ms + 并发）的量级，也比"卡住不动"更容易看出来。
                using var http = NewLoopbackClient(30);
                string url = GroupHealthCheckUrl(group);
                using var req = new HttpRequestMessage(HttpMethod.Put, url);
                using var resp = await http.SendAsync(req);
                int code = (int)resp.StatusCode;
                string body = await resp.Content.ReadAsStringAsync();
                if (body.Length > 200) body = body[..200] + "…";

                if (code is 200 or 204)
                    return $"已触发「{group}」测速（{code}），延迟随下一次刷新出来";
                return $"「{group}」测速失败 HTTP {code}" +
                       (body.Length > 0 ? $" {body}" : "") +
                       "　※ 若这里是 405，说明本内核这版走的是 GET 而不是 PUT（见注释的证据级别）";
            }
            catch (Exception ex)
            {
                ClashUWP.Vpn.RollingLog.Error("group-hc", ex);
                return $"「{group}」测速失败: {ex.GetType().Name}";
            }
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
                var (groups, delay, byName, duplicates) = ReadProxiesTable(doc.RootElement);

                if (groups.Count == 0)
                    return "(内核里没有可切换的代理组：要么规则里没有组，要么订阅没加载。" +
                           "可先 debug conf 看 proxies 有没有落到内核。)";

                sb.Append($"可切换组={groups.Count}（* = 当前选中；delay=- 表示该成员还没被测过，不等于挂了；" +
                          "[组]=成员本身是嵌套组，[策略]=内置策略，二者都不是节点）");
                foreach (var g in groups)
                {
                    // ★ 成员数按<b>真节点</b>数（安卓口径：组不算进节点的堆里）。
                    //   两个数都给，因为"这个组其实是套壳"正是排障要看的事。
                    int realNodes = 0;
                    foreach (string name in g.Members)
                        if (!byName.TryGetValue(name, out var i) || (!i.IsGroup && !i.IsBuiltin))
                            realNodes++;

                    sb.Append($"\n组={g.Name} 类型={g.Type} 成员={g.Members.Count}" +
                              $"（其中节点={realNodes}） " +
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
                        string kind = "";
                        if (byName.TryGetValue(name, out var mi))
                        {
                            if (mi.IsGroup) kind = $" [组:{mi.Type}{(mi.Now.Length > 0 ? $"({mi.Now})" : "")}]";
                            else if (mi.IsBuiltin) kind = " [策略]";
                        }
                        sb.Append($"\n   {mark} {name}{kind}");
                        sb.Append(delay.TryGetValue(name, out long d) ? $" delay={d}ms" : " delay=-");
                    }
                }

                // ★【1.0.81 · Q11】同名条目不再吞掉：若内核响应里出现了同名条目
                //   （异常情形，正常 mihomo 不会），明确说出来，免得延迟/类型错位还查不出因。
                if (duplicates > 0)
                    sb.Append($"\n⚠ 内核返回了 {duplicates} 处同名代理条目，已按名字折叠：" +
                              "切换以组作用域仍正确，但同名节点的延迟/类型展示可能错位——" +
                              "多半是订阅里存在重名节点（见订阅元信息里的「重名」提示）。");

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
        /// 切换节点（<b>UI 专用入口</b>）：<c>PUT /proxies/{组}</c>，body
        /// <c>{"name":"节点名"}</c>，成功回 204。组名与节点名<b>各自成参</b>。
        ///
        /// 【★ 为什么不复用字符串入口】1.0.74 及以前，界面点节点走的是
        ///   <c>RunSelectAsync($"{group} {member}")</c> —— 把两个名字拼成一个字符串，
        ///   那边再用"第一个空格"拆回来。而订阅派生的组名<b>本身就有空格</b>
        ///   （实测这份 iKuuu 订阅 11 个组里 10 个带空格），拆出来只剩组名的第一段，
        ///   于是绝大多数组在界面上根本点不动，而代理页又不显示返回文本 ⇒ 全程静默失败。
        ///   安卓版没有这个坑，因为它传的是 <c>(组, 节点)</c> 两个值（<c>patchSelector</c>）。
        ///   ⇒ 这里对齐安卓：名字一旦被拼过就还原不回来，所以<b>不要拼</b>。
        /// </summary>
        internal static async Task<string> SelectAsync(string group, string node)
        {
            try
            {
                using var http = NewLoopbackClient(10);
                var groups = await FetchGroupsAsync(http);
                return await SelectCoreAsync(http, groups, group ?? "", node ?? "",
                    fromDebugText: false);
            }
            catch (Exception ex)
            {
                ClashUWP.Vpn.RollingLog.Error("mihomo-select", ex);
                return $"select 失败: {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>
        /// 切换节点（debug <c>select</c> 命令）：参数是一整行文本，先按组名做前缀匹配
        /// 还原出 <c>(组, 节点)</c>，再走 <see cref="SelectCoreAsync"/> —— 与 UI 同一条实现。
        ///
        /// 【★ 只有 PUT 生效】官方 v1.19.32 实测（Docs/12 §7）：同一条 URL
        ///   <c>PATCH</c> 回 <b>405</b>、<c>PUT</c> 回 <b>204</b>。记忆里"PATCH /proxies
        ///   切节点"那句是错的，已按实测改掉 —— 照它写会做出一个静默无效的开关。
        /// 【为什么先校验再发】节点名带中文、emoji、前导空格，手敲几乎必错；
        ///   直接把"不在该组成员表里"连同候选一起回出来，比让内核默默收下再发现
        ///   没变化要省一整轮。组名与节点名都<b>区分大小写、精确匹配</b>。
        ///   成员表就取自同一次 <c>GET /proxies</c>（见 <see cref="ReadProxiesTable"/>），
        ///   不再为校验多打一个接口。
        /// 【参数口径】<b>拿本次的组名做最长前缀匹配</b>（不是"第一个空格"，理由见
        ///   <see cref="SplitSelectArg"/>）；匹配不到才退回空格口径，好把"找不到组 X"
        ///   连同候选组名回出来。只给一段时当作节点名，并在"内核里只有一个可切换组"时自动补组名。
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
                using var http = NewLoopbackClient(10);
                var groups = await FetchGroupsAsync(http);
                var (group, node) = SplitSelectArg(arg.Trim(), groups);
                return await SelectCoreAsync(http, groups, group, node, fromDebugText: true);
            }
            catch (Exception ex)
            {
                ClashUWP.Vpn.RollingLog.Error("mihomo-select", ex);
                return $"select 失败: {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>取一次 <c>GET /proxies</c> 的组表（UI 与调试通道共用）。</summary>
        private static async Task<System.Collections.Generic.List<ProxyGroup>> FetchGroupsAsync(
            System.Net.Http.HttpClient http)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                await http.GetStringAsync("http://127.0.0.1:9090/proxies"));
            return ReadProxiesTable(doc.RootElement).Groups;
        }

        /// <summary>
        /// 把 <c>select</c> 的一行参数还原成 <c>(组名, 节点名)</c>。
        ///
        /// 【★ 判据是"组名做最长前缀匹配"，不是第一个空格】组名和节点名<b>都</b>可以带空格
        ///   （订阅原文决定的，我们改不了），所以"遇到空格就切"只能碰对一种情况。
        ///   而"哪个字符串是组名"这件事内核已经告诉过我们了 —— 就是本次
        ///   <c>GET /proxies</c> 的组表，拿它来匹配是<b>有依据的判据</b>。
        ///   取最长匹配：组名之间可能互为前缀（「美国 01」/「美国 01 直连」这类），
        ///   短的先中会把长的切坏。
        /// 【匹配不到时】退回老的空格口径，让 <see cref="SelectCoreAsync"/> 报
        ///   "找不到组 X（现有组：…）"。这里不猜：把拼错的组名原样报出来，比自动改成
        ///   "最像的那个组"有用 —— 自动改会把节点切到用户没打算切的组上。
        /// ★ 参数是 Trim 过的（<c>DebugCommandLoop</c> 就 Trim 过），所以<b>首尾真空格</b>的
        ///   节点名从这条路喂不进去 —— 那种成员用 UI 点（UI 走 <see cref="SelectAsync"/>，不拼串）。
        /// </summary>
        internal static (string Group, string Node) SplitSelectArg(
            string arg, System.Collections.Generic.List<ProxyGroup> groups)
        {
            string best = "";
            foreach (var g in groups)
            {
                if (g.Name.Length >= best.Length &&
                    arg.Length > g.Name.Length + 1 &&
                    arg.StartsWith(g.Name + " ", System.StringComparison.Ordinal))
                    best = g.Name;
            }
            if (best.Length > 0)
                return (best, arg.Substring(best.Length + 1).Trim());

            int sp = arg.IndexOf(' ');
            return sp < 0
                ? ("", arg.Trim())
                : (arg.Substring(0, sp).Trim(), arg.Substring(sp + 1).Trim());
        }

        /// <summary>
        /// 校验 + <c>PUT /proxies/{组}</c> + 回读 <c>now</c>。两条入口共用这一条，
        /// 保证"UI 点的"和"命令行打的"走同一份校验、同一种成功判据。
        /// </summary>
        /// <param name="fromDebugText">true = 参数来自一行文本（组名可能是猜的）；空节点名时报用法。</param>
        private static async Task<string> SelectCoreAsync(
            System.Net.Http.HttpClient http,
            System.Collections.Generic.List<ProxyGroup> groups,
            string group, string node, bool fromDebugText)
        {
            if (node.Length == 0)
                return fromDebugText
                    ? $"节点名为空。现有组：{string.Join(" | ", GroupNames(groups))}"
                    : "select 需要参数：select <组名> <节点名>（只有一个组时可只写节点名）。" +
                      "先用 groups 命令看确切的组名与节点名。";

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
            // ★【1.0.81 · M3】relay 与 Selector 同属手动组，选择持久，不给"自动组"提示；
            //   url-test / fallback / load-balance 是自动组，选了会被内核改回赢家。
            string groupNote = target.Type is "Selector" or "select" or "Relay" or "relay"
                ? "" : $"\n※ 该组类型是 {target.Type}（自动组）：选择会立刻生效，" +
                       "但测速/故障转移可能把它改回赢家 —— 不是 select 没成功。";

            if (!resp.IsSuccessStatusCode)
                return $"PUT /proxies/{group} -> {(int)resp.StatusCode} {respBody}；现值 now={now}" + groupNote;

            // ★【1.0.81 · Q4】手动组（Selector / relay）选择成功 → 客户端记下，
            //   供内核重启后由 ReplaySelectionsAsync 回放（home 只读，内核自己的
            //   cache.db 跨重启不保留，见本方法旧注释；客户端代记是本轮的非侵入修复）。
            //   自动组（url-test / fallback / load-balance）内核自管，不记。
            if (target.Type is "Selector" or "select" or "Relay" or "relay")
                SelectionStore.Record(group, node);

            string verdict = string.Equals(now, node, System.StringComparison.Ordinal)
                ? "★ 已选中" : "⚠ 现值与请求不一致，别当成功";
            return $"PUT /proxies/{group} -> {(int)resp.StatusCode}；now={now}（{verdict}）" +
                   $"\n※ 选择已记入客户端持久化（LocalState/selection.txt），内核重启后会自动回放；" +
                   $"若内核 home 日后迁到可写 LocalState（G10），这份客户端记录即作废、以内核为准。" +
                   groupNote;
        }

        /// <summary>
        /// ★【1.0.81 · Q4】内核每次（重新）起来后，回放客户端记下的组选择。
        ///
        /// 由 UI 轮询在 <c>KernelView.LoadAsync()</c> 成功（即内核当前可达）后调用；
        /// 用 <see cref="_kernelWasReachable"/> 做<b>边沿触发</b>：内核从不可达 → 可达时
        /// 只回放一次，避免每 3 秒轮询都打一遍 <c>PUT</c>。内核掉线后由
        /// <see cref="MarkKernelUnreachable"/> 复位，下次起来再回放。
        ///
        /// 回放规则：只针对<b>手动组</b>（Selector / relay）；自动组（url-test /
        /// fallback / load-balance）由内核自管，跳过；组或节点已不存在（订阅变了）
        /// 也跳过。回放失败不抛（不连累轮询），下个可达周期会重试（本次未清边沿标志）。
        /// </summary>
        private static bool _kernelWasReachable = false;

        internal static async Task<string> ReplaySelectionsAsync()
        {
            if (_kernelWasReachable)
                return "";                 // 本轮内核已回放过，跳过
            _kernelWasReachable = true;    // 先置位：即便后面失败也不重复刷

            var saved = SelectionStore.LoadAll();
            if (saved.Count == 0)
                return "";

            try
            {
                using var http = NewLoopbackClient(10);
                var groups = await FetchGroupsAsync(http);

                int applied = 0, skipped = 0;
                foreach (var kv in saved)
                {
                    ProxyGroup? g = null;
                    foreach (var x in groups)
                        if (string.Equals(x.Name, kv.Key, StringComparison.Ordinal)) { g = x; break; }
                    if (g == null)
                    {
                        skipped++;
                        continue;
                    }
                    // 自动组内核自管，回放无意义（且下一轮就被改回），跳过。
                    if (g.Type is "URLTest" or "url-test" or "Fallback" or "fallback" or
                        "LoadBalance" or "load-balance")
                    {
                        skipped++;
                        continue;
                    }
                    bool member = false;
                    foreach (var m in g.Members)
                        if (string.Equals(m, kv.Value, StringComparison.Ordinal)) { member = true; break; }
                    if (!member)
                    {
                        skipped++;          // 节点已不在该组（订阅变化）→ 跳过，不删除记录
                        continue;
                    }
                    if (string.Equals(g.Now, kv.Value, StringComparison.Ordinal))
                    {
                        applied++;         // 内核当前就选着它，无需再打 PUT
                        continue;
                    }
                    await SelectCoreAsync(http, groups, kv.Key, kv.Value, fromDebugText: false);
                    applied++;
                }

                return saved.Count == 0 ? "" :
                    $"已回放上次节点选择 {applied} 项，跳过 {skipped} 项（组/节点已不存在或非手动组）";
            }
            catch (Exception ex)
            {
                return $"回放节点选择失败: {ex.GetType().Name}（将在下次内核可达时重试）";
            }
        }

        /// <summary>
        /// 内核掉线时复位回放边沿（UI 轮询的 catch 分支调用）。
        /// 下次内核可达时 ReplaySelectionsAsync 会再回放一次。
        /// </summary>
        internal static void MarkKernelUnreachable() => _kernelWasReachable = false;

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
                // ★【1.0.89 · Docs/38 §十四 · 修 bug】这里原先把包目录名<b>写死</b>成
                //   <c>%LocalAppData%\Packages\421769c5-17f4-4287-b6c1-193222d21e14_y0tg80em3rma2\LocalState\…</c>，
                //   而那个家族名属于 1.0.75 <b>改名之前</b>的包（现读 <c>git log -S 421769c5 -- ClashUWP/Package.appxmanifest</c>：
                //   <c>3b11f69</c> 那笔把主清单 Identity Name 从 GUID 换成 <c>8B8CC4F4.ClashUWP</c>，
                //   换名同时换了发布者 hash）。⇒ 这一段从 1.0.75 起<b>每次</b>都走 <c>else</c>，
                //   诊断里那句"✗ 内核配置文件不存在"<b>从来不是事实</b>，而是一条读不到的假判据。
                //   现在统一走 <see cref="DebugCommandLoop.DefaultKernelConfigPath"/>（唯一算对的出口），
                //   并把路径本身打出来 —— 以后再漂，日志一行就能看出来是路径错还是文件真不在。
                var configPath = DebugCommandLoop.DefaultKernelConfigPath();

                if (File.Exists(configPath))
                {
                    var config = File.ReadAllText(configPath);
                    var lines = config.Split('\n');
                    sb.AppendLine($"\n✓ 内核配置文件存在 ({lines.Length} 行) path={configPath}");
                    
                    // ★【1.0.89 · Docs/38 §十四 · 同一批的第二条假判据】这里原先用
                    //   <c>config.Contains("external-controller: 127.0.0.1:9090")</c> 整串字面量判"配置正确"。
                    //   装机实测（本轮 C5，1.0.89 诊断出口原文）读到的真行是
                    //   <c>external-controller: '127.0.0.1:9090'</c> —— <b>带单引号</b>。这一行是订阅 yaml
                    //   原样带进来的（全仓现读：<c>grep -rn external-controller ClashUWP --include=*.cs</c> 里
                    //   除了本函数没有任何一处生成或改写它），所以引号风格、空格、端口<b>都由机场决定</b>，
                    //   字面量整串匹配在这种输入上必然漂。
                    //   ⇒ 上一行路径修好之后，旧判据仍然<b>假阴性</b>：文件里明明有这一行、REST 也明明可达
                    //     （同一次诊断的上面几行就是「✓ REST API 可达 {"meta":true,…}」），它照样报
                    //     「✗ external-controller 配置缺失或错误」。这种"读起来像故障、其实是判据写错"的行
                    //     比缺判据更坏，所以改成按行解析、把值<b>原样打出来</b>，只判两件事：有没有、
                    //     和前台 REST 客户端用的基址是不是同一个。
                    //   ★ 前台基址（<c>http://127.0.0.1:9090/…</c>，本文件与 KernelView 里 20+ 处字面量）
                    //     是另一笔债，本批不动，这里只负责把"两者不一致"如实报出来。
                    const string ControllerKey = "external-controller:";
                    string? controller = null;
                    foreach (var line in lines)
                    {
                        string t = line.Trim();
                        if (t.StartsWith(ControllerKey, StringComparison.OrdinalIgnoreCase))
                        {
                            controller = t.Substring(ControllerKey.Length).Trim().Trim('\'', '"');
                            break;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(controller))
                        sb.AppendLine("  ✗ 配置里没有 external-controller（内核不会开 REST 控制器）");
                    else if (string.Equals(controller, "127.0.0.1:9090", StringComparison.OrdinalIgnoreCase))
                        sb.AppendLine($"  ✓ external-controller={controller}（与前台 REST 基址一致）");
                    else
                        sb.AppendLine($"  ⚠ external-controller={controller}（前台 REST 客户端写的是 127.0.0.1:9090，" +
                                      "两者不一致 ⇒ 前台读不到内核）");
                }
                else
                {
                    sb.AppendLine($"\n✗ 内核配置文件不存在 path={configPath}");
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
