// 探测原语 —— guard 与 doctor 共用的一把尺子。
//
// 【为什么单独成文件】"这个目标现在通不通"这个问题，guard 要反复问、doctor 要问一次。
//   两处各写一遍 TCP/HTTP 判定，就会漂成"guard 说通、doctor 说不通"——
//   而这两个命令的存在意义恰好是给人一个可信的答案，口径必须完全一致。
//
// 【fake-ip 下判连通不能看 DNS】隧道用的是 fake-ip：域名<b>一定解析得通</b>
//   （返回一个假池里的地址），所以"解析成功"毫无信息量。真正有意义的是
//   这个假地址上的 TCP 能不能建起来 / HTTP 有没有回 —— 那才说明包真的被隧道送到节点并回来了。
//   这就是目标默认走 tcp、以及 --expect 只卡状态码而不卡内容的原因。

using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;

namespace ClashUWP.Cli;

/// <summary>解析后的探测目标。</summary>
internal readonly record struct Target(string Scheme, string Host, int Port, string Path, bool PortGiven)
{
    /// <summary>用于显示的形态：带 scheme 就拼 URL，否则 host:port。</summary>
    public string Describe => Scheme.Length == 0
        ? $"{Host}:{Port}"
        : $"{Scheme}://{Host}{(Path.Length == 0 ? "/" : Path)}";
}

internal static class Probe
{
    /// <summary>
    /// 目标解析：支持 <c>host</c> / <c>host:port</c> / <c>https://host/path</c> / <c>[ip]:port</c>。
    ///
    /// 【为什么手工切而不用 <see cref="Uri"/>】最常见的写法 <c>1.1.1.1:443</c> 和
    ///   <c>www.google.com</c> 都没有 scheme，<c>new Uri("1.1.1.1:443")</c> 会把
    ///   "1.1.1.1" 当成<b>协议名</b>解析出一个空主机 —— 于是"目标写对了却报解析失败"。
    ///   这里按 host[:port][/path] 的规则自己切，端口默认交给调用方按 method 决定。
    /// 【portGiven 必须单独回传】否则 "--port 8443" 会被 https 的默认 443 挡住，
    ///   变成静默按 443 探测 —— 这种"参数被吃掉且不说话"的坑最难查。
    /// </summary>
    public static bool TryParse(string raw, out Target target, out string? error)
    {
        string scheme = "", host = "", path = "";
        int port = 0;
        bool portGiven = false;
        error = null;
        target = default;

        raw = raw.Trim();
        if (raw.Length == 0)
        {
            error = "需要一个目标：host / host:port / https://host/path";
            return false;
        }

        int schemeIdx = raw.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
        {
            scheme = raw.Substring(0, schemeIdx).ToLowerInvariant();
            if (scheme is not ("http" or "https"))
            {
                error = $"只支持 http/https（其它协议请用 host:port 形式走 tcp 探测），收到 {scheme}://";
                return false;
            }
            raw = raw.Substring(schemeIdx + 3);
            int cut = raw.IndexOfAny(new[] { '/', '?', '#' });
            if (cut >= 0)
            {
                path = raw.Substring(cut);
                raw = raw.Substring(0, cut);
            }
        }

        int colon = raw.LastIndexOf(':');
        if (colon > 0 && colon < raw.Length - 1 && !raw.Substring(colon + 1).Contains(']'))
        {
            // v6 字面量只在 "[..]:port" 形态下按最后一个冒号切端口。裸 v6（无方括号）会被
            // 误切成"端口=末段数字"，但本机基线是无 IPv6，不做过度设计；真踩到时报错文案会指出。
            string portText = raw.Substring(colon + 1);
            if (int.TryParse(portText, out int p) && p is > 0 and < 65536)
            {
                port = p;
                portGiven = true;
                raw = raw.Substring(0, colon);
            }
        }

        if (raw.StartsWith('[')) raw = raw.TrimEnd(']').TrimStart('[');
        host = raw.Trim();

        if (host.Length == 0)
        {
            error = "目标里解析不出主机名";
            return false;
        }
        if (host.Any(char.IsWhiteSpace))
        {
            error = $"目标含空白字符（整个参数需要加引号）：{host}";
            return false;
        }

        if (!portGiven)
            port = scheme.Length > 0 ? (scheme == "https" ? 443 : 80) : 443;

        target = new Target(scheme, host, port, path, portGiven);
        return true;
    }

    /// <summary>一次 TCP 连接探测。返回 (是否可达, 单行原因)。</summary>
    public static async Task<(bool Ok, string Detail)> TcpAsync(Target t, int timeoutSec)
    {
        using var client = new TcpClient();
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            await client.ConnectAsync(t.Host, t.Port, cts.Token);
            return (true, $"tcp 已建连 {t.Host}:{t.Port}（{sw.ElapsedMilliseconds}ms）");
        }
        catch (Exception ex)
        {
            string kind = ex is SocketException se
                ? $"socket={se.SocketErrorCode}"
                : ex is OperationCanceledException or TimeoutException ? "超时"
                : ex.GetType().Name;
            return (false, $"tcp 失败 {t.Host}:{t.Port} {kind}");
        }
    }

    /// <summary>
    /// 一次 HTTP 探测。<paramref name="expect"/> = 0 时"拿到任何响应就算通"
    /// （403/500 也算 —— 量的是路通不通，不是站点对不对）；给了具体码就按它卡，
    /// 用来验 generate_204 这类探针端点。
    /// </summary>
    public static async Task<(bool Ok, string Detail)> HttpAsync(Target t, int timeoutSec, int expect, bool noProxy)
    {
        string url = $"{t.Scheme}://{t.Host}{(t.Path.Length == 0 ? "/" : t.Path)}";
        var handler = new HttpClientHandler
        {
            UseProxy = !noProxy,
            AllowAutoRedirect = false,   // 302 也算"路是通的"，不去追跳转链
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSec) };

        var sw = Stopwatch.StartNew();
        try
        {
            using HttpResponseMessage resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            int code = (int)resp.StatusCode;
            bool ok = expect == 0 || code == expect;
            string detail = expect == 0
                ? $"http {code}（{sw.ElapsedMilliseconds}ms）"
                : ok ? $"http {code} 符合期望（{sw.ElapsedMilliseconds}ms）"
                     : $"http 收到 {code}，期望 {expect}";
            return (ok, detail);
        }
        catch (Exception ex)
        {
            string kind = ex is HttpRequestException hre && hre.InnerException is not null
                ? $"{ex.GetType().Name}/{hre.InnerException.GetType().Name}"
                : ex is TaskCanceledException or OperationCanceledException ? "超时"
                : ex.GetType().Name;
            return (false, $"http 失败 {kind}（{url}）");
        }
    }
}
