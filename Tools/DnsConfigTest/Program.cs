// DnsConfig 离线夹具（Docs/23 §4/§6）。
// 直接编译 ../../ClashUWP.Vpn/DnsConfig.cs、LocalSettings.cs、VpnPluginConfig.cs，
// 覆盖：解析/拒绝原因、逐键校验矩阵、假 IP 池派生值、v6 前缀归一、模板往返。
// 用法：dotnet run --project Tools/DnsConfigTest
using System;
using ClashUWP.Vpn;

internal static class Program
{
    private static int _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name +
                          (detail.Length > 0 ? "  " + detail : ""));
        if (!ok) _fail++;
    }

    private static int Main()
    {
        ParseBasics();
        Rejections();
        Validation();
        PoolMath();
        V6Normalize();
        TemplateRoundTrip();
        Ipv6ArmFallback();

        Console.WriteLine(_fail == 0 ? "ALL PASS" : _fail + " FAIL");
        return _fail == 0 ? 0 : 1;
    }

    private static void ParseBasics()
    {
        var cfg = DnsConfig.Parse(
            "# 注释\r\n" +
            "\r\n" +
            " Nameserver = 223.5.5.5, 119.29.29.29 \r\n" +
            "fake-ip-range=198.18.0.1/16\r\n" +
            "nameserver = 1.1.1.1\r\n");

        Check("parse: 注释/空行被忽略", cfg.Entries.Count == 3, "count=" + cfg.Entries.Count);
        Check("parse: 键归一小写", cfg.Has("nameserver") && cfg.Has("fake-ip-range"));
        Check("parse: 同名后写的赢", cfg.Get("nameserver") == "1.1.1.1", cfg.Get("nameserver"));
        Check("parse: 值去首尾空白", cfg.Get("fake-ip-range") == "198.18.0.1/16");
        Check("parse: 无拒绝", cfg.Rejected.Count == 0, Join(cfg));
        Check("parse: 空文本 = 空配置",
              DnsConfig.Parse("").IsEmpty && DnsConfig.Parse(null).Rejected.Count == 0);
    }

    private static void Rejections()
    {
        var cfg = DnsConfig.Parse(
            "nameserver\n" +                       // 没有 =
            "foo = 1\n" +                          // 白名单外
            "nameserver =\n" +                     // 空值
            "enhanced-mode = redir-host\n" +       // 校验失败
            "ipv6 = 2\n" +                         // 布尔校验失败
            "nrpt-dns = dns.google\n");            // 非 IP

        Check("reject: 五类非法行全部被拒", cfg.Rejected.Count == 6, "count=" + cfg.Rejected.Count);
        Check("reject: 全部被拒时 Entries 为空", cfg.IsEmpty);
        foreach (string r in cfg.Rejected) Console.WriteLine("      · " + r);

        var over = new System.Text.StringBuilder();
        for (int i = 0; i < DnsConfig.MaxEntries + 1; i++)
            over.Append("nameserver = 223.5.5.5\n");
        var capped = DnsConfig.Parse(over.ToString());
        Check("reject: 超过 40 行被截断",
              capped.Entries.Count == DnsConfig.MaxEntries && capped.Rejected.Count == 1,
              capped.Entries.Count + "/" + capped.Rejected.Count);

        // 行内注释不是语法：整段值会进入校验并被拒绝（文件头写明 # 只在行首）。
        var inline = DnsConfig.Parse("nameserver = 1.1.1.1 # 备注\n");
        Check("reject: 行内 # 注释会被拒（不是静默截断）", inline.IsEmpty && inline.Rejected.Count == 1);
    }

    private static void Validation()
    {
        Expect("enhanced-mode: fake-ip", "enhanced-mode", "fake-ip", true);
        Expect("enhanced-mode: 大小写不敏感", "enhanced-mode", "FAKE-IP", true);
        Expect("enhanced-mode: redir-host 拒绝", "enhanced-mode", "redir-host", false);

        Expect("fake-ip-range: /16", "fake-ip-range", "198.18.0.1/16", true);
        Expect("fake-ip-range: 无掩码拒绝", "fake-ip-range", "198.18.0.1", false);
        Expect("fake-ip-range: /33 拒绝", "fake-ip-range", "198.18.0.1/33", false);
        Expect("fake-ip-range: v6 拒绝", "fake-ip-range", "2001:db8::/32", false);

        Expect("fake-ip-range6: ULA /64", "fake-ip-range6", "fdfe:dcba:9876::/64", true);
        Expect("fake-ip-range6: 主机位会被归一", "fake-ip-range6", "fdfe:dcba:9876::1234/64", true);
        Expect("fake-ip-range6: 全局 v6 拒绝", "fake-ip-range6", "2000::/64", false);
        Expect("fake-ip-range6: /47 拒绝", "fake-ip-range6", "fd00::/47", false);
        Expect("fake-ip-range6: /65 拒绝", "fake-ip-range6", "fd00::/65", false);

        Expect("fake-ip-filter: 通配写法", "fake-ip-filter", "*.lan, +.msftconnecttest.com", true);
        Expect("fake-ip-filter: 单引号拒绝", "fake-ip-filter", "a'b", false);

        Expect("fake-ip-ttl: 60", "fake-ip-ttl", "60", true);
        Expect("fake-ip-ttl: 0 拒绝", "fake-ip-ttl", "0", false);
        Expect("fake-ip-ttl: 86401 拒绝", "fake-ip-ttl", "86401", false);
        Expect("fake-ip-ttl: 非数字拒绝", "fake-ip-ttl", "abc", false);

        Expect("default-nameserver: 两个 IP", "default-nameserver", "223.5.5.5, 119.29.29.29", true);
        Expect("default-nameserver: system", "default-nameserver", "system", true);
        Expect("default-nameserver: scheme 拒绝", "default-nameserver", "tls://1.1.1.1", false);

        Expect("nameserver: 裸 IP", "nameserver", "223.5.5.5", true);
        Expect("nameserver: DoH", "nameserver", "https://dns.alidns.com/dns-query", true);
        Expect("nameserver: rcode 合法", "nameserver", "rcode://success", true);
        Expect("nameserver: rcode 非法", "nameserver", "rcode://bogus", false);
        Expect("nameserver: ts 拒绝", "nameserver", "ts://x", false);
        Expect("nameserver: dhcp 拒绝", "nameserver", "dhcp://auto", false);
        Expect("nameserver: 未知 scheme 拒绝", "nameserver", "ftp://1.1.1.1", false);
        Expect("nameserver: 空主机拒绝", "nameserver", "udp://", false);
        Expect("nameserver: 单引号拒绝", "nameserver", "a'b", false);

        Expect("ipv6: 1", "ipv6", "1", true);
        Expect("ipv6: off", "ipv6", "off", true);
        Expect("ipv6: 2 拒绝", "ipv6", "2", false);

        Expect("ipv6-timeout: 0", "ipv6-timeout", "0", true);
        Expect("ipv6-timeout: 60001 拒绝", "ipv6-timeout", "60001", false);
        Expect("ipv6-timeout: 负数拒绝", "ipv6-timeout", "-1", false);

        Expect("nrpt-dns: v4", "nrpt-dns", "223.5.5.5", true);
        Expect("nrpt-dns: v6", "nrpt-dns", "2001:4860:4860::8888", true);
        Expect("nrpt-dns: 主机名拒绝", "nrpt-dns", "dns.google", false);
    }

    private static void Expect(string name, string key, string value, bool pass)
    {
        // ★【1.0.131】本 csproj 的 Nullable 从 disable 改成 enable（与产品工程逐字同一上下文，
        //   被测的 DnsConfig.cs 里那些 `string?` 注释这才不是"无上下文的注释"）。
        //   这一行的类型跟着收紧：Validate 的契约就是"没问题返回 null"，
        //   写成 string 在新上下文里是 CS8600，而不是"少个 null 分支"。
        string? error = DnsConfig.Validate(key, value);
        Check(name, (error == null) == pass, error == null ? "" : "reason=" + error);
    }

    private static void PoolMath()
    {
        var empty = new DnsConfig();
        Check("pool: 默认 YAML 写法", empty.FakeIpRange == "198.18.0.1/16", empty.FakeIpRange);
        Check("pool: 默认路由写法", empty.FakeIpRoute == "198.18.0.0/16", empty.FakeIpRoute);
        Check("pool: 默认网络号", empty.FakeIpNetwork == "198.18.0.0", empty.FakeIpNetwork);
        Check("pool: 默认前缀", empty.FakeIpPrefix == 16, empty.FakeIpPrefix.ToString());
        Check("pool: 池内命中", empty.IsInFakeIpPool("198.18.5.5"));
        Check("pool: 池外不命中", !empty.IsInFakeIpPool("198.19.5.5"));
        Check("pool: v6 地址不冒充 v4", !empty.IsInFakeIpPool("2001:db8::1"));

        var cfg = DnsConfig.Parse("fake-ip-range = 198.19.5.6/24\n");
        Check("pool: 自定义路由归一", cfg.FakeIpRoute == "198.19.5.0/24", cfg.FakeIpRoute);
        Check("pool: 自定义网络号", cfg.FakeIpNetwork == "198.19.5.0", cfg.FakeIpNetwork);
        Check("pool: 自定义前缀", cfg.FakeIpPrefix == 24, cfg.FakeIpPrefix.ToString());
        Check("pool: 自定义池内命中", cfg.IsInFakeIpPool("198.19.5.200"));
        Check("pool: 自定义池外不命中", !cfg.IsInFakeIpPool("198.19.6.1"));

        var zero = DnsConfig.Parse("fake-ip-range = 0.0.0.0/0\n");
        Check("pool: /0 合法（内核允许）", zero.FakeIpRoute == "0.0.0.0/0", zero.FakeIpRoute);
        Check("pool: /0 匹配任意 v4", zero.IsInFakeIpPool("8.8.8.8"));

        Check("pool: 非法 CIDR 回落默认",
              DnsConfig.ToRouteCidr("x", "198.18.0.1/16") == "198.18.0.0",
              DnsConfig.ToRouteCidr("x", "198.18.0.1/16"));

        Check("v6pool: 未配时用 fallback",
              empty.EffectiveFakeIpRange6("fdfe:dcba:9876::/64") == "fdfe:dcba:9876::/64");
        var v6 = DnsConfig.Parse("fake-ip-range6 = fdfe:1::1234/64\n");
        Check("v6pool: 覆盖值归一后优先",
              v6.EffectiveFakeIpRange6("fdfe:dcba:9876::/64") == "fdfe:1::/64",
              v6.EffectiveFakeIpRange6("fdfe:dcba:9876::/64"));
    }

    private static void V6Normalize()
    {
        Check("v6: 网络号原样", LocalSettings.TryNormalizeV6Prefix("fdfe:dcba:9876::/64", out string a) && a == "fdfe:dcba:9876::/64", a);
        Check("v6: 主机位清零", LocalSettings.TryNormalizeV6Prefix("fdfe:dcba:9876::1234/64", out string b) && b == "fdfe:dcba:9876::/64", b);
        Check("v6: 非零末段清零", LocalSettings.TryNormalizeV6Prefix("fd66:6:6600::2/64", out string c) && c == "fd66:6:6600::/64", c);
        Check("v6: 全局地址拒绝", !LocalSettings.TryNormalizeV6Prefix("2000::/64", out _));
        Check("v6: /48 允许", LocalSettings.TryNormalizeV6Prefix("fd00::/48", out _));
        Check("v6: /47 拒绝", !LocalSettings.TryNormalizeV6Prefix("fd00::/47", out _));
        Check("v6: /65 拒绝", !LocalSettings.TryNormalizeV6Prefix("fd00::/65", out _));
        Check("v6: 无掩码拒绝", !LocalSettings.TryNormalizeV6Prefix("fd00::", out _));
        Check("v6: v4 拒绝", !LocalSettings.TryNormalizeV6Prefix("10.0.0.0/8", out _));
    }

    private static void TemplateRoundTrip()
    {
        var template = new DnsConfig().ToText();
        var parsedTemplate = DnsConfig.Parse(template);
        Check("template: 模板自身解析为 0 项 0 拒",
              parsedTemplate.IsEmpty && parsedTemplate.Rejected.Count == 0,
              "entries=" + parsedTemplate.Entries.Count + " rejected=" + parsedTemplate.Rejected.Count);

        var src = DnsConfig.Parse(
            "nameserver = 223.5.5.5, 119.29.29.29\n" +
            "fake-ip-range = 198.19.0.1/16\n" +
            "fake-ip-filter = *.lan, +.msftconnecttest.com\n" +
            "nrpt-dns = 223.5.5.5\n");
        var again = DnsConfig.Parse(src.ToText());
        Check("template: ToText 往返项数不变", src.Entries.Count == again.Entries.Count,
              src.Entries.Count + " -> " + again.Entries.Count);
        Check("template: 往返后值不变", again.Get("nameserver") == src.Get("nameserver") &&
              again.Get("fake-ip-filter") == src.Get("fake-ip-filter"));

        Check("describe: 空配置", new DnsConfig().Describe().StartsWith("无覆盖"),
              new DnsConfig().Describe());
        Check("describe: 有条目", src.Describe().Contains("nameserver=223.5.5.5"), src.Describe());

        Check("split: 去空白与空段",
              string.Join("|", DnsConfig.SplitList(" a , b ,, c ")) == "a|b|c");
        Check("bool: 写 YAML 字面量",
              DnsConfig.Parse("ipv6 = on\n").GetBoolAsYaml("ipv6") == "true");
        Check("inline: 行内流式序列",
              DnsConfig.ToInlineList("a, b") == "['a', 'b']", DnsConfig.ToInlineList("a, b"));
        Check("listkey: 分类", DnsConfig.IsListKey("nameserver") &&
              DnsConfig.IsListKey("nrpt-dns") && !DnsConfig.IsListKey("fake-ip-range"));
    }

    /// <summary>
    /// ★【1.0.132】IPv6 档位的回落真值表（Docs/71）。
    /// 触发它的是 10-28 01:42 的装机实物：<c>settings.txt</c> 里没有 <c>ipv6</c> 键时，
    /// 隧道按 2 跑而前台读成 0 —— 因为"回落成哪一档"在两个读者手里各写了一遍。
    /// 现在键名、区间、缺省档、认不出的回落全部住进 <see cref="LocalSettings.ParseIpv6Arm"/>，
    /// 口径对照 Clash Verge：缺键 = 没人表过态 = 取权威默认（模板 <c>ipv6: true</c> 那一步），
    /// 不是另写一个保守数。
    /// </summary>
    private static void Ipv6ArmFallback()
    {
        int Arm(string text) => LocalSettings.ParseIpv6Arm(SettingsParse.Parse(text));

        // ① 缺键 = 权威默认档；这一条就是装机上分叉的那一格
        Check("1.0.132 缺 ipv6 键 → 默认档 2（不再回落 0）",
              Arm("subName=x\n") == LocalSettings.Ipv6ArmDefault && Arm("") == 2,
              "缺键读到 " + Arm("subName=x\n"));
        Check("1.0.132 默认档只有一个定义处（字段初值 == 常量）",
              LocalSettings.Ipv6 == LocalSettings.Ipv6ArmDefault,
              "Ipv6=" + LocalSettings.Ipv6 + " Ipv6ArmDefault=" + LocalSettings.Ipv6ArmDefault);

        // ② 用户主动选的三档必须原样读回 —— 回落改成 2 不能把显式的 0 吃掉
        Check("1.0.132 显式 0/1/2 原样（主动回档仍然有效）",
              Arm("ipv6=0\n") == 0 && Arm("ipv6=1\n") == 1 && Arm("ipv6=2\n") == 2,
              $"0->{Arm("ipv6=0\n")} 1->{Arm("ipv6=1\n")} 2->{Arm("ipv6=2\n")}");
        Check("1.0.132 同键多行仍是最后一行胜出（追加语义不变）",
              Arm("ipv6=1\nipv6=0\n") == 0 && Arm("ipv6=0\nipv6=2\n") == 2,
              "末行 " + Arm("ipv6=1\nipv6=0\n") + "/" + Arm("ipv6=0\nipv6=2\n"));

        // ③ 键在但值不可用 = 同样当"没人表过态"，并且不得压成 0（0 是已实测会旁路隧道那一档）
        Check("1.0.132 越界值 → 默认档 2（不是 0）",
              Arm("ipv6=3\n") == 2 && Arm("ipv6=-1\n") == 2,
              "3->" + Arm("ipv6=3\n") + " -1->" + Arm("ipv6=-1\n"));
        Check("1.0.132 认不出/空值 → 默认档 2（不是 0）",
              Arm("ipv6=abc\n") == 2 && Arm("ipv6=\n") == 2,
              "abc->" + Arm("ipv6=abc\n") + " 空->" + Arm("ipv6=\n"));

        // ④ 写侧归一与读侧同一个回落（写得进去 3、读回来 2 这种形状不再出现）
        Check("1.0.132 键名与区间只有 LocalSettings 一处定义",
              LocalSettings.Ipv6Key == "ipv6" &&
              LocalSettings.Ipv6ArmMin == 0 && LocalSettings.Ipv6ArmMax == 2,
              $"key={LocalSettings.Ipv6Key} 区间={LocalSettings.Ipv6ArmMin}..{LocalSettings.Ipv6ArmMax}");
    }

    private static string Join(DnsConfig cfg) => string.Join(" / ", cfg.Rejected);
}
