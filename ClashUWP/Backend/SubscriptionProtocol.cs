using System;

namespace ClashUWP.Backend
{
    /// <summary>
    /// 【后端 · 订阅协议解析】把"一键导入"点出来的 URI 还原成订阅 URL。
    ///
    /// 【为什么单独一层，不写在 <c>App.OnActivated</c> 里】
    ///   协议形态有四种以上（<c>clash://install-config?url=</c>、<c>clashuwp://subscribe?url=</c>、
    ///   带 fragment 的、url 参数本身又被转义过的），而且**错一个字符就静默导入失败**。
    ///   单独一层可以离线单测（下面 <see cref="TryParse"/> 的返回值设计就是为这个）。
    ///
    /// 【★ 支持的形态（按机场实际用法）】
    ///   · <c>clash://install-config?url=https%3A%2F%2Fexample.com%2Fsub</c>   ← Clash 系通用，最常见
    ///   · <c>clash://install-config?url=https://example.com/sub</c>           ← 未转义也接
    ///   · <c>clashuwp://subscribe?url=…</c>                                    ← 自己的协议
    ///   · <c>clashuwp://import?url=…</c> / <c>clashuwp://fetch?url=…</c>      ← 同义别名
    ///   · <c>clashuwp://https://example.com/sub</c>                            ← 裸路径形态（少数面板这么发）
    ///
    /// 【★★ 安全边界（为什么必须自己收严）】
    ///   协议是<b>外部可触发的</b>：任何网页、任何聊天窗口里一个链接都能把本进程唤起。
    ///   而解析出来的 URL 会被 <see cref="SubscriptionManager.FetchAsync"/> 拿去发请求。
    ///   ① <b>只认 http/https</b> —— file:// / ms-appx:// / ms-appdata:// / 本地盘符一律拒；
    ///   ② <b>只取订阅 URL，不执行别的动作</b> —— 协议激活不连接 VPN、不改设置；
    ///   ③ 拒掉的都要返回<b>可展示的原因</b>，让 UI 能告诉用户"这个链接我没法用"，
    ///      而不是静默什么都不做。
    /// </summary>
    internal static class SubscriptionProtocol
    {
        internal sealed record Result(bool Ok, string Url, string Error)
        {
            internal static Result Fail(string why) => new(false, "", why);
        }

        /// <summary>
        /// 解析一个激活 URI。返回 <see cref="Result.Ok"/> 时 <see cref="Result.Url"/>
        /// 是<b>已解码</b>的订阅 URL；失败时 <see cref="Result.Error"/> 是给用户看的原因。
        /// ★ 永不抛：协议参数是不可信输入，任何异常都归一成 Fail。
        /// </summary>
        internal static Result TryParse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return Result.Fail("协议参数为空");

            string s = raw.Trim();

            // ① 先按 URI 整体解构（拿不到 scheme 就直接当裸 URL 试）
            if (!Uri.TryCreate(s, UriKind.Absolute, out Uri? uri) ||
                string.IsNullOrEmpty(uri.Scheme))
            {
                // 有些面板会漏掉 scheme，直接给 "https://…" 开头也能用
                return TryParseBareUrl(s);
            }

            string scheme = uri.Scheme.ToLowerInvariant();

            // ② 自己协议的两种形态
            if (scheme == "clashuwp")
            {
                // 2a) clashuwp://subscribe?url=…
                string fromQuery = ReadQuery(uri, "url") ?? ReadQuery(uri, "u") ?? "";
                if (fromQuery.Length > 0) return Normalize(fromQuery);

                // 2b) clashuwp://<订阅URL>（host+path 拼回去）
                //     例：clashuwp://https://example.com/sub
                //         → uri.Host = "https:" ，后面全在 AbsolutePath 里，畸形但真实存在
                string rebuilt = RebuildFromAuthority(uri);
                if (rebuilt.Length > 0) return Normalize(rebuilt);

                return Result.Fail("clashuwp:// 里没有 url 参数");
            }

            // ③ Clash 系通用协议：clash://install-config?url=…
            //    ★ 只接 install-config 这一个动作，别的（clash://update 之类）不认 ——
            //      它们语义不同，接错了比不接更糟。
            if (scheme == "clash")
            {
                string action = (uri.Host ?? "").ToLowerInvariant();
                if (action is not ("install-config" or "install" or "subscribe" or "import"))
                    return Result.Fail($"不支持的 clash:// 动作: {action}");

                string fromQuery = ReadQuery(uri, "url") ?? ReadQuery(uri, "u") ?? "";
                if (fromQuery.Length == 0)
                    return Result.Fail("clash://install-config 里没有 url 参数");
                return Normalize(fromQuery);
            }

            // ④ 已经是 http/https —— 用户可能直接把这个链接拖进来
            if (scheme is "http" or "https")
                return Normalize(uri.AbsoluteUri);

            return Result.Fail($"不认识的协议: {scheme}://");
        }

        // ── 实现细节 ──────────────────────────────────────────────────────

        /// <summary>
        /// 收口：解码 + 校验 scheme + 去掉首尾空白。
        /// ★ 二次解码：机场有时会把 url 参数<b>转义两次</b>（%253A → %3A → :），
        ///   所以最多解两轮，两轮后还不对就放弃（无限解会把 "https%3A%2F%2F" 之外的
        ///   东西也解坏，而恶意 URL 正好可以藏在这上面）。
        /// </summary>
        private static Result Normalize(string candidate)
        {
            string u = candidate.Trim();

            for (int round = 0; round < 2; round++)
            {
                // ★ 顺序很关键：先试"能不能直接当 URL"，不能当<b>且还有 % 残留</b>才解一轮。
                //   旧写法是"TryCreate 失败就 return Fail"，双次转义
                //   （https%253A%252F%252F…）第一轮解出 https%3A%2F%2F… 时 TryCreate 仍失败，
                //   于是被误判成"不是合法 URL"—— 而它解第二轮就对了。
                if (Uri.TryCreate(u, UriKind.Absolute, out Uri? parsed))
                {
                    string sch = parsed.Scheme.ToLowerInvariant();
                    if (sch is "http" or "https")
                        return new Result(true, parsed.AbsoluteUri, "");
                    return Result.Fail($"只支持 http/https 订阅（收到 {sch}://）");
                }

                if (!u.Contains('%')) return Result.Fail("不是合法的 URL");
                try { u = Uri.UnescapeDataString(u); }
                catch { return Result.Fail("URL 解码失败"); }
            }

            return Result.Fail("解码两轮后仍不是合法 URL");
        }

        /// <summary>没有 scheme 的裸 URL（"example.com/sub" 这种）。</summary>
        private static Result TryParseBareUrl(string s)
        {
            if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return Normalize(s);

            // 形如 "example.com/sub" —— 补 https 再试
            return Normalize("https://" + s);
        }

        /// <summary>读 query 参数；不存在返回 null。★ 参数名大小写不敏感。</summary>
        private static string? ReadQuery(Uri uri, string key)
        {
            string q = uri.Query;
            if (q.Length == 0) return null;
            if (q[0] == '?') q = q.Substring(1);

            foreach (string pair in q.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0) continue;
                string k = pair.Substring(0, eq);
                string v = pair.Substring(eq + 1);
                if (string.Equals(Uri.UnescapeDataString(k), key, StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(v);
            }
            return null;
        }

        /// <summary>
        /// 从 <c>clashuwp://https://example.com/sub</c> 这种畸形 URI 里还原订阅 URL。
        /// ★ 实测形态（本机 .NET 10，非文档推断）：<c>Scheme=clashuwp</c>、<c>Host=https</c>
        ///   （<b>不带冒号</b>）、<c>AbsolutePath=//example.com/sub</c> ——
        ///   内层 scheme 被吞进 Host，其余全在 Path 里。Host 带/不带尾冒号都接。
        /// </summary>
        private static string RebuildFromAuthority(Uri uri)
        {
            string host = (uri.Host ?? "").TrimEnd(':');
            string path = uri.AbsolutePath ?? "";
            string query = uri.Query ?? "";

            string innerScheme = host.ToLowerInvariant();
            if (innerScheme is not ("http" or "https")) return "";

            // path 形如 "//example.com/sub"
            string rest = path;
            if (rest.StartsWith("//")) rest = rest.Substring(2);
            if (rest.Length == 0) return "";

            return $"{innerScheme}://{rest}{query}";
        }

        /// <summary>给人看的一行说明（UI 与日志共用）。</summary>
        internal static string Usage() =>
            "一键导入：clashuwp://subscribe?url=<订阅链接> 或 clash://install-config?url=<订阅链接>（只支持 http/https）";
    }
}
