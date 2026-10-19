// IpPacket 校验和离线夹具（Docs/22 §4.7）。
// 独立实现 RFC 1071 与 IPv4/IPv6 伪头，对拍 IpPacket.FixChecksums 与
// IsNonUnicastDestination；直接编译 ../../ClashUWP.Vpn/IpPacket.cs（产品源文件）。
// 用法：dotnet run --project Tools/IpPacketChecksumTest
using System;
using ClashUWP.Vpn;

internal static class Program
{
    // ---- 独立的 RFC 1071 实现：按字节数组显式拼伪头，不复用被测代码 ----
    private static ushort Sum(byte[] data)
    {
        uint s = 0;
        int i = 0;
        for (; i + 1 < data.Length; i += 2)
            s += (uint)((data[i] << 8) | data[i + 1]);
        if (i < data.Length)
            s += (uint)(data[i] << 8);
        while ((s >> 16) != 0)
            s = (s & 0xFFFF) + (s >> 16);
        return (ushort)~s;
    }

    private static byte[] Pseudo6(byte[] src, byte[] dst, int l4Len, byte next)
    {
        var ph = new byte[40];
        Array.Copy(src, 0, ph, 0, 16);
        Array.Copy(dst, 0, ph, 16, 16);
        ph[32] = (byte)(l4Len >> 24);
        ph[33] = (byte)(l4Len >> 16);
        ph[34] = (byte)(l4Len >> 8);
        ph[35] = (byte)l4Len;
        ph[39] = next;
        return ph;
    }

    private static byte[] Pseudo4(uint src, uint dst, int l4Len, byte proto)
    {
        var ph = new byte[12];
        ph[0] = (byte)(src >> 24); ph[1] = (byte)(src >> 16); ph[2] = (byte)(src >> 8); ph[3] = (byte)src;
        ph[4] = (byte)(dst >> 24); ph[5] = (byte)(dst >> 16); ph[6] = (byte)(dst >> 8); ph[7] = (byte)dst;
        ph[9] = proto;
        ph[10] = (byte)(l4Len >> 8); ph[11] = (byte)l4Len;
        return ph;
    }

    private static byte[] Slice(byte[] a, int start, int len)
    {
        var r = new byte[len];
        Array.Copy(a, start, r, 0, len);
        return r;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Array.Copy(a, 0, r, 0, a.Length);
        Array.Copy(b, 0, r, a.Length, b.Length);
        return r;
    }

    private static int _fail;

    private static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + "  " + detail);
        if (!ok) _fail++;
    }

    private static byte[] BuildV6(byte next, byte[] src, byte[] dst, byte[] l4)
    {
        var p = new byte[40 + l4.Length];
        p[0] = 0x60;
        p[4] = (byte)(l4.Length >> 8);
        p[5] = (byte)l4.Length;
        p[6] = next;
        p[7] = 64;
        Array.Copy(src, 0, p, 8, 16);
        Array.Copy(dst, 0, p, 24, 16);
        Array.Copy(l4, 0, p, 40, l4.Length);
        return p;
    }

    private static void Main()
    {
        byte[] src6 = { 0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 };
        byte[] dst6 = { 0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2 };

        // ── v6 TCP，校验和故意留 0 ─────────────────────────────
        var tcp = new byte[20 + 5];
        tcp[0] = 0x04; tcp[1] = 0xD2;             // 1234
        tcp[2] = 0x00; tcp[3] = 0x50;             // 80
        tcp[12] = 0x50;                            // data offset 5
        tcp[13] = 0x02;                            // SYN
        tcp[14] = 0xFF; tcp[15] = 0xFF;            // window
        for (int i = 0; i < 5; i++) tcp[20 + i] = (byte)('a' + i);

        byte[] p6tcp = BuildV6(6, src6, dst6, tcp);
        ushort want6tcp = Sum(Concat(Pseudo6(src6, dst6, tcp.Length, 6), tcp));
        var fixed6tcp = IpPacket.FixChecksums(p6tcp, p6tcp.Length);
        int off6tcp = 40 + 16;
        ushort got6tcp = (ushort)((p6tcp[off6tcp] << 8) | p6tcp[off6tcp + 1]);
        Check("v6-tcp-zero", fixed6tcp == IpPacket.ChecksumFix.Transport && got6tcp == want6tcp,
            $"fixed={fixed6tcp} got=0x{got6tcp:X4} want=0x{want6tcp:X4}");
        // 全包复算 = 0xFFFF 是接收方的验收口径
        var whole6tcp = Concat(Pseudo6(src6, dst6, tcp.Length, 6), Slice(p6tcp, 40, p6tcp.Length - 40));
        Check("v6-tcp-verify", Sum(whole6tcp) == 0, $"fold=0x{Sum(whole6tcp):X4}");
        Check("v6-tcp-idempotent", IpPacket.FixChecksums(p6tcp, p6tcp.Length) == IpPacket.ChecksumFix.None,
            "second pass");

        // ── v6 UDP，校验和留 0（IPv6 必填 ⇒ 必须补）────────────
        var udp = new byte[8 + 4];
        udp[0] = 0x04; udp[1] = 0xD2;
        udp[2] = 0x00; udp[3] = 0x35;             // 53
        udp[4] = 0x00; udp[5] = 0x0C;             // len 12
        for (int i = 0; i < 4; i++) udp[8 + i] = (byte)(0x10 + i);

        byte[] p6udp = BuildV6(17, src6, dst6, udp);
        ushort want6udp = Sum(Concat(Pseudo6(src6, dst6, udp.Length, 17), udp));
        if (want6udp == 0) want6udp = 0xFFFF;
        var fixed6udp = IpPacket.FixChecksums(p6udp, p6udp.Length);
        int off6udp = 40 + 6;
        ushort got6udp = (ushort)((p6udp[off6udp] << 8) | p6udp[off6udp + 1]);
        Check("v6-udp-zero", fixed6udp == IpPacket.ChecksumFix.Transport && got6udp == want6udp,
            $"fixed={fixed6udp} got=0x{got6udp:X4} want=0x{want6udp:X4}");
        var whole6udp = Concat(Pseudo6(src6, dst6, udp.Length, 17), Slice(p6udp, 40, p6udp.Length - 40));
        Check("v6-udp-verify", Sum(whole6udp) == 0, $"fold=0x{Sum(whole6udp):X4}");

        // ── v6 非 TCP/UDP（ICMPv6 58）不动 ─────────────────────
        var icmp6 = new byte[8];
        icmp6[0] = 128;
        var p6icmp = BuildV6(58, src6, dst6, icmp6);
        byte[] copyIcmp = (byte[])p6icmp.Clone();
        Check("v6-icmp-untouched", IpPacket.FixChecksums(p6icmp, p6icmp.Length) == IpPacket.ChecksumFix.None
            && p6icmp[40] == copyIcmp[40], "no change");

        // ── v4 回归：TCP 校验和留 0，应补成期望值 ─────────────
        var tcp4 = new byte[20 + 5];
        tcp4[0] = 0x04; tcp4[1] = 0xD2;
        tcp4[2] = 0x00; tcp4[3] = 0x50;
        tcp4[12] = 0x50;
        tcp4[13] = 0x02;
        tcp4[14] = 0xFF; tcp4[15] = 0xFF;
        for (int i = 0; i < 5; i++) tcp4[20 + i] = (byte)('a' + i);

        var p4 = new byte[20 + tcp4.Length];
        p4[0] = 0x45;
        p4[2] = (byte)(p4.Length >> 8); p4[3] = (byte)p4.Length;
        p4[8] = 64; p4[9] = 6;
        p4[12] = 10; p4[13] = 7; p4[14] = 0; p4[15] = 2;
        p4[16] = 198; p4[17] = 18; p4[18] = 0; p4[19] = 4;
        Array.Copy(tcp4, 0, p4, 20, tcp4.Length);
        uint s4 = 0x0A070002, d4 = 0xC6120004;
        ushort want4 = Sum(Concat(Pseudo4(s4, d4, tcp4.Length, 6), tcp4));
        var fixed4 = IpPacket.FixChecksums(p4, p4.Length);
        ushort got4 = (ushort)((p4[36] << 8) | p4[37]);
        Check("v4-tcp-regression", (fixed4 & IpPacket.ChecksumFix.Transport) != 0 && got4 == want4,
            $"fixed={fixed4} got=0x{got4:X4} want=0x{want4:X4}");

        // ── v6 组播判定 ─────────────────────────────────────────
        var mc = BuildV6(58, src6, dst6, icmp6);
        mc[24] = 0xFF; mc[25] = 0x02;
        Check("v6-multicast-drop", IpPacket.IsNonUnicastDestination(mc, mc.Length), "ff02::");
        Check("v6-unicast-keep", !IpPacket.IsNonUnicastDestination(p6tcp, p6tcp.Length), "2001:db8::2");
        var v4mc = new byte[20]; v4mc[0] = 0x45; v4mc[16] = 224; v4mc[17] = 0; v4mc[18] = 0; v4mc[19] = 251;
        Check("v4-multicast-regression", IpPacket.IsNonUnicastDestination(v4mc, v4mc.Length), "224.0.0.251");

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
        Environment.Exit(_fail == 0 ? 0 : 1);
    }
}
