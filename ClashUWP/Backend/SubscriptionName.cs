using System;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;

namespace ClashUWP.Backend
{
    /// <summary>
    /// 【订阅显示名】从机场响应头里取一个给人看的名字。
    ///
    /// 【为什么单独成文件】这是纯字符串处理，没有任何 WinRT/存储依赖；
    ///   放进 <c>SubscriptionManager</c> 会让离线夹具没法 source-link 它
    ///   （那类会连带把 Storage/HttpClient 依赖拖进来）。拆出来之后
    ///   <c>Tools\SubscriptionNameTest</c> 编译的就是本文件<b>逐字同一份</b>。
    ///
    /// 【取值顺序】
    ///   ① <c>Profile-Title</c>（Clash 生态惯例，常见 <c>base64:</c> 前缀）；
    ///   ② <c>Content-Disposition</c> 的 <c>filename*</c>（RFC 5987，UTF-8 百分号编码）；
    ///   ③ 普通 <c>filename</c>（去掉 .yaml/.yml 后缀）。
    /// 全部为空/畸形就返回空串，由调用方退回 host ——
    /// <b>名字取不到绝不能影响订阅拉取本身</b>。
    ///
    /// 【实测样本（本机 2026-10-19）】
    ///   <c>attachment;filename*=UTF-8''%E6%98%9F%E9%93%BE%E6%9C%BA%E5%9C%BA</c>
    ///   ⇒ 星链机场；
    ///   <c>attachment; filename=iKuuu_V2.yaml</c> ⇒ iKuuu_V2。
    /// </summary>
    internal static class SubscriptionName
    {
        /// <summary>从响应头对象取值。头读取本身失败只当成"没有名字"。</summary>
        internal static string FromHeaders(HttpHeaders headers, HttpContentHeaders contentHeaders)
        {
            string? title = null;
            string? disposition = null;
            try
            {
                if (headers.TryGetValues("Profile-Title", out var titles))
                    title = titles.FirstOrDefault();
                if (contentHeaders.TryGetValues("Content-Disposition", out var cds))
                    disposition = cds.FirstOrDefault();
            }
            catch
            {
                // 头畸形/实现差异都不影响正文；继续按"没有名字"处理。
            }
            return FromValues(title, disposition);
        }

        /// <summary>纯函数入口（离线夹具直接测这一层）。</summary>
        internal static string FromValues(string? profileTitle, string? contentDisposition)
        {
            string title = DecodeProfileTitle(profileTitle?.Trim() ?? "");
            string clean = CleanName(title);
            if (clean.Length > 0) return clean;

            string name = ParseContentDispositionName(contentDisposition);
            return CleanName(name);
        }

        /// <summary>
        /// <c>Profile-Title</c> 的两种形态：明文，或 <c>base64:&lt;...&gt;</c>。
        /// 后者在 Clash 生态很常见（值本身是 UTF-8 名字的 base64）。
        /// </summary>
        private static string DecodeProfileTitle(string raw)
        {
            const string prefix = "base64:";
            if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return raw;

            try
            {
                string s = raw.Substring(prefix.Length).Trim()
                    .Replace('-', '+').Replace('_', '/');
                int pad = s.Length % 4;
                if (pad == 2) s += "==";
                else if (pad == 3) s += "=";
                else if (pad == 1) return "";
                return Encoding.UTF8.GetString(Convert.FromBase64String(s));
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 解析 <c>Content-Disposition</c> 里的文件名。
        /// ★ 手工按分号切而不用 <c>ContentDispositionHeaderValue</c>：
        ///   实测部分机场的写法（<c>attachment;filename*=UTF-8''…</c>）在
        ///   .NET 的强类型解析里拿不到 FileNameStar；手工解析更稳。
        /// </summary>
        private static string ParseContentDispositionName(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";

            string? star = ParameterValue(raw, "filename*");
            if (star != null)
            {
                // RFC 5987: charset'lang'percent-encoded。机场实际写法是 UTF-8''…
                int q = star.IndexOf("''", StringComparison.Ordinal);
                if (q >= 0) star = star.Substring(q + 2);
                star = star.Trim().Trim('"');
                try { star = Uri.UnescapeDataString(star); } catch { }
                return StripYamlExt(star);
            }

            string? plain = ParameterValue(raw, "filename");
            if (plain != null)
            {
                plain = plain.Trim().Trim('"');
                // 有些服务器把名字百分号编码后塞进普通 filename。
                try { plain = Uri.UnescapeDataString(plain); } catch { }
                return StripYamlExt(plain);
            }
            return "";
        }

        /// <summary>取 <c>; name=value</c> 里的 value（大小写不敏感，到下一个分号为止）。</summary>
        private static string? ParameterValue(string header, string name)
        {
            int i = 0;
            while (i < header.Length)
            {
                int semi = header.IndexOf(';', i);
                int end = semi < 0 ? header.Length : semi;
                string part = header.Substring(i, end - i).Trim();
                int eq = part.IndexOf('=');
                if (eq > 0 && part.Substring(0, eq).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return part.Substring(eq + 1).Trim();
                if (semi < 0) break;
                i = semi + 1;
            }
            return null;
        }

        private static string StripYamlExt(string name)
        {
            name = name.Trim().Trim('"', '\'');
            if (name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 5);
            else if (name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);
            return name.Trim();
        }

        /// <summary>名字只用于显示：去控制字符、收敛空白、限长，保留中文/emoji。</summary>
        private static string CleanName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";

            var sb = new StringBuilder(name.Length);
            bool lastSpace = false;
            foreach (char c in name)
            {
                char ch = char.IsControl(c) ? ' ' : c;
                bool isSpace = char.IsWhiteSpace(ch);
                if (isSpace)
                {
                    if (!lastSpace) sb.Append(' ');
                }
                else
                {
                    sb.Append(ch);
                }
                lastSpace = isSpace;
            }

            string clean = sb.ToString().Trim();
            return clean.Length > 48 ? clean.Substring(0, 48).TrimEnd() : clean;
        }
    }
}
