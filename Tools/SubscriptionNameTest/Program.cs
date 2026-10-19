// 订阅显示名离线夹具（Docs/25 §1.6 / §2.5）。
// 直接编译 ../../ClashUWP/Backend/SubscriptionName.cs，
// 覆盖：真机实测头、Profile-Title（明文/base64/url-safe）、filename*/filename、
// 优先级、中文与引号、后缀剥离、空白/控制字符收敛、限长、畸形输入。
// 用法：dotnet run --project Tools/SubscriptionNameTest
using System;
using System.Net.Http;
using System.Text;

using ClashUWP.Backend;

internal static class Program
{
    private static int _fail;

    private static void Check(string name, string actual, string expected)
    {
        bool ok = string.Equals(actual, expected, StringComparison.Ordinal);
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name +
                          (ok ? "" : $"  got=[{actual}] want=[{expected}]"));
        if (!ok) _fail++;
    }

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    private static int Main()
    {
        // ── 真机实测样本（2026-10-19，三份订阅只读 HEAD）──────────────
        Check("filename* UTF-8 ⇒ 星链机场",
              SubscriptionName.FromValues(
                  null, "attachment;filename*=UTF-8''%E6%98%9F%E9%93%BE%E6%9C%BA%E5%9C%BA"),
              "星链机场");
        Check("filename 普通 ⇒ 去 .yaml",
              SubscriptionName.FromValues(null, "attachment; filename=iKuuu_V2.yaml"),
              "iKuuu_V2");

        // ── Profile-Title 的两种形态 ─────────────────────────────────
        Check("Profile-Title 明文",
              SubscriptionName.FromValues("星链机场", null),
              "星链机场");
        Check("Profile-Title base64",
              SubscriptionName.FromValues("base64:" + B64("星链机场"), null),
              "星链机场");
        string urlSafe = B64("星链机场").Replace('+', '-').Replace('/', '_').TrimEnd('=');
        Check("Profile-Title base64 url-safe",
              SubscriptionName.FromValues("base64:" + urlSafe, null),
              "星链机场");

        // ── 优先级与常见变体 ─────────────────────────────────────────
        Check("Profile-Title 优先于 filename",
              SubscriptionName.FromValues("机场 A", "attachment; filename=fallback.yaml"),
              "机场 A");
        Check("filename* 优先于 filename",
              SubscriptionName.FromValues(
                  null,
                  "attachment; filename=old.yaml; filename*=UTF-8''%E6%96%B0%E5%90%8D%E5%AD%97"),
              "新名字");
        Check("原始中文 filename + 引号",
              SubscriptionName.FromValues(null, "attachment; filename=\"我的机场.yaml\""),
              "我的机场");
        Check(".yml 后缀",
              SubscriptionName.FromValues(null, "attachment; filename=abc.yml"),
              "abc");
        Check("大写后缀",
              SubscriptionName.FromValues(null, "attachment; filename=ABC.YAML"),
              "ABC");

        // ── 清洗：空白/控制字符/限长 ─────────────────────────────────
        Check("空白与控制字符收敛",
              SubscriptionName.FromValues("  星链\n\t机场  ", null),
              "星链 机场");
        Check("超长名字截到 48 字符",
              SubscriptionName.FromValues(new string('机', 60), null),
              new string('机', 48));

        // ── 空/畸形不抛、不产生垃圾 ──────────────────────────────────
        Check("全空 ⇒ 空串",
              SubscriptionName.FromValues(null, null),
              "");
        Check("只有 attachment ⇒ 空串",
              SubscriptionName.FromValues(null, "attachment"),
              "");
        Check("坏 base64 ⇒ 空串",
              SubscriptionName.FromValues("base64:!!!", null),
              "");
        Check("坏百分号保留原文（不抛）",
              SubscriptionName.FromValues(null, "attachment; filename*=UTF-8''%ZZ"),
              "%ZZ");

        // ── 头对象入口（验 Wiring：取值 + 顺序）──────────────────────
        using (var content = new ByteArrayContent(Array.Empty<byte>()))
        using (var resp = new HttpResponseMessage { Content = content })
        {
            resp.Headers.TryAddWithoutValidation("Profile-Title", "base64:" + B64("星链机场"));
            content.Headers.TryAddWithoutValidation(
                "Content-Disposition", "attachment; filename=ignored.yaml");
            Check("FromHeaders 取 Profile-Title",
                  SubscriptionName.FromHeaders(resp.Headers, content.Headers),
                  "星链机场");
        }

        using (var content = new ByteArrayContent(Array.Empty<byte>()))
        using (var resp = new HttpResponseMessage { Content = content })
        {
            content.Headers.TryAddWithoutValidation(
                "Content-Disposition",
                "attachment;filename*=UTF-8''%E6%98%9F%E9%93%BE%E6%9C%BA%E5%9C%BA");
            Check("FromHeaders 取 filename*",
                  SubscriptionName.FromHeaders(resp.Headers, content.Headers),
                  "星链机场");
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : _fail + " FAIL");
        return _fail == 0 ? 0 : 1;
    }
}
