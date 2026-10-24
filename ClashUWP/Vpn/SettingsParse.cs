using System;
using System.Collections.Generic;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// 【共享层 · 无状态纯函数】<c>settings.txt</c> 的<b>唯一一份</b>解析与取值口径。
    ///
    /// ══ 为什么这个类存在 ══
    ///   同一个 <c>LocalState\settings.txt</c> 有<b>两个读者</b>：
    ///     · 插件侧 <see cref="LocalSettings"/>（15 个数据面把手）；
    ///     · 前台侧 <c>Backend\AppSettings</c>（目前只有 <c>autoProbe</c> 一个）。
    ///   在 1.0.93 之前，这两处<b>各存了一份逐字相同的实现</b>：行切分循环两边各写一遍，
    ///   布尔真值表两边各写一遍，靠 <c>AppSettings.ParseBool</c> 的注释
    ///   「逐字照搬插件侧 <c>LocalSettings.ReadBool</c>」维系口径。
    ///   ⇒ 那是注释在当契约，不是代码在当契约：谁改了一边、另一边的注释不会跟着报警，
    ///     分叉的形态正是本类注释里那句「前台以为开了、插件以为关了」。
    ///
    /// ══ 统一的是哪一层，<b>不</b>统一哪一层 ══
    ///   ★ 统一：<b>文本 → 值</b>（行格式、布尔/整数字符串表、缺键与越界回落规则）。
    ///   ★ 不统一：<b>怎么把文本读出来</b> —— 两边各自的 I/O 方式必须保留：
    ///     · 插件在 <c>Connect</c> 这个<b>同步</b> WinRT 回调里取值，没有别的选择，
    ///       所以走 <c>GetAwaiter().GetResult()</c> 的同步等异步；
    ///     · 前台的读取在 <c>MainComponent.Render()</c> 的渲染线程上点火，
    ///       在那里阻塞一次 WinRT 存储调用就是把渲染线程挂住，所以必须全异步。
    ///     （推导与出处见 <c>Backend\AppSettings.cs</c> 类注释。）
    ///   ⇒ 本类只收 <see cref="string"/>，不碰 <c>StorageFile</c>，因此两边都能用、
    ///     且它<b>没有</b>任何可变状态 —— 两个进程各自持有什么是各自的缓存，
    ///     跨进程只靠这个文件传递（分层口径见 Docs/48）。
    ///
    /// 【回落纪律】缺键、空串、非数字、越界一律回落调用方给的 fallback ——
    ///   取证通道的缺失绝不该变成一次意外的 VPN 连接或一个静默换掉的实验臂。
    ///   整数区间<b>由调用方给</b>：这条是 Docs/09 C5 那个真缺陷换来的
    ///   （老实现硬编码 0..2，把 <c>encapReturn=3</c> 静默回落成 2，实验臂看着切了其实没切）。
    /// </summary>
    internal static class SettingsParse
    {
        /// <summary>
        /// 行格式：<c>key=value</c>，按 <c>\n</c> 切；每行先 <c>Trim()</c>；
        /// 空行与 <c>#</c> 开头的行跳过；没有 <c>=</c> 或 <c>=</c> 在首位的行跳过；
        /// 键与值各 <c>Trim()</c> 一次。<b>键大小写不敏感</b>（<c>OrdinalIgnoreCase</c>）。
        /// 与 1.0.93 之前两边那两份实现的差别：零 —— 这里是原样搬过来收成一份。
        /// </summary>
        internal static Dictionary<string, string> Parse(string text)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;
                int idx = line.IndexOf('=');
                if (idx <= 0)
                    continue;
                map[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
            }
            return map;
        }

        /// <summary>
        /// 布尔口径：<c>1/true/on/yes</c> 为真，<c>0/false/off/no</c> 为假，
        /// 其余（含缺键）回落 <paramref name="fallback"/>。
        /// ★ 值应当已经过 <see cref="Parse"/> 的 <c>Trim()</c>；这里按原文比较，不自己再清洗。
        /// </summary>
        internal static bool Bool(Dictionary<string, string> map, string key, bool fallback)
        {
            if (!map.TryGetValue(key, out string? v))
                return fallback;
            if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("yes", StringComparison.OrdinalIgnoreCase))
                return true;
            if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("no", StringComparison.OrdinalIgnoreCase))
                return false;
            return fallback;
        }

        /// <summary>整数口径：非数字或越界回落 fallback。默认区间 <c>0..2</c>（三档把手）。</summary>
        internal static int Int(Dictionary<string, string> map, string key, int fallback)
            => Int(map, key, fallback, 0, 2);

        /// <summary>整数口径，<b>合法区间由调用方给</b>（理由见类注释里 Docs/09 C5 那条）。</summary>
        internal static int Int(Dictionary<string, string> map, string key, int fallback,
                                int minInclusive, int maxInclusive)
        {
            if (!map.TryGetValue(key, out string? v))
                return fallback;
            return int.TryParse(v, out int parsed) &&
                   parsed >= minInclusive && parsed <= maxInclusive
                ? parsed
                : fallback;
        }

        /// <summary>字符串口径：<b>空串也回落</b> fallback（空 <c>HostName</c> 会让 <c>ConnectAsync</c> 抛）。</summary>
        internal static string String(Dictionary<string, string> map, string key, string fallback)
        {
            if (!map.TryGetValue(key, out string? v) || v.Length == 0)
                return fallback;
            return v;
        }
    }
}
