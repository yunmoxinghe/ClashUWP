using System;

namespace ClashUWP.Backend
{
    /// <summary>
    /// ★【1.0.81 · M1/Q1/Q2】订阅正文形态校验（<b>纯函数</b>：无 IO、无 WinRT 依赖，可离线单测）。
    ///
    /// 官方 Clash/Mihomo 订阅（YAML）至少包含下列<b>顶层键</b>之一：
    ///   · <c>proxies:</c>          —— 显式节点列表（最常见）；
    ///   · <c>proxy-providers:</c>  —— 动态节点（http/file/inline 提供方，官方正确键名）；
    ///   · <c>proxy-groups:</c>     —— 代理组（依赖上面二者之一提供成员）。
    /// 三者都没有 ⇒ 不是 Clash 订阅（可能是机场首页/错误页 HTML、base64 正文、或纯规则集）。
    ///
    /// 同时<b>拒绝 HTML</b>：HTML 错误页（如订阅网关 403 页）常夹带 "proxies" 字眼，
    /// 会被旧判据误收并覆盖本地缓存（Q2 第三点）⇒ 这里先挡掉
    /// 首字符 <、&lt;html、&lt;!DOCTYPE 三种特征。
    /// 校验在落盘之前（<see cref="SubscriptionManager.FetchAsync"/> 调用点），失败不覆盖缓存。
    ///
    /// ★ 注意：base64 正文（部分机场对错误 UA 返回）必然没有上述顶层键，
    ///   会被这里判成"不像 Clash 订阅"而拒收 —— 这正是预期：本管理器只认
    ///   Mihomo 原生 YAML 订阅（直接写 config.yaml 给内核读），不做 base64 二次解码；
    ///   上层 <see cref="SubscriptionManager.FetchAsync"/> 那段 UA 注释（Clash.meta/1.16.0）
    ///   就是为了逼出 YAML 而非 base64。
    /// </summary>
    internal static class SubscriptionShape
    {
        /// <summary>订阅形态校验结果。</summary>
        internal sealed record Probe(bool Ok, string? Error);

        internal static Probe ProbeShape(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new Probe(false, "订阅内容为空");

            string trimmed = text.TrimStart();
            // ★ HTML 特征：错误页/网关页，必须先挡（避免误收覆盖缓存）。
            if (trimmed.Length > 0 && trimmed[0] == '<')
                return new Probe(false, "返回内容像 HTML 页面，不像 Clash 订阅（可能被拦截或需要登录）");
            if (text.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("<!doctype", StringComparison.OrdinalIgnoreCase) >= 0)
                return new Probe(false, "返回内容含 HTML 标签，不像 Clash 订阅（可能被拦截或需要登录）");

            // ★ 顶层键检测：至少一个才算"长得像 Clash 订阅"。
            //   行首锚定（含文件开头），避免把注释里/值里的同名串算进去。
            if (HasTopLevelKey(text, "proxies:") ||
                HasTopLevelKey(text, "proxy-providers:") ||
                HasTopLevelKey(text, "proxy-groups:"))
                return new Probe(true, null);

            return new Probe(false,
                "不是 Clash 订阅格式：找不到顶层 proxies: / proxy-providers: / proxy-groups: 键");
        }

        /// <summary>检测某个顶层键是否存在（位于文件开头，或某个无前导空格的行首）。</summary>
        private static bool HasTopLevelKey(string text, string key)
        {
            if (text.StartsWith(key, StringComparison.Ordinal))
                return true;
            return text.IndexOf("\n" + key, StringComparison.Ordinal) >= 0;
        }
    }
}
