using System;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// 最小 IPv4 / ICMPv4 处理器 —— 骨架期的「假内核」。
    ///
    /// 存在的唯一目的：<b>证明 VpnChannel 这条链路是通的</b>。
    /// 隧道起来后，在本机 <c>ping 10.7.0.1</c>（任一走 VPN 的地址），
    /// 如果能收到 reply，说明
    ///   Start 建 L3 网卡 → Encapsulate 拿到原始 IP 包 → 主动注入 → 系统协议栈收到
    /// 这整条路径都成立。此时再往里换真内核（mihomo dll），风险就只剩内核本身了。
    ///
    /// 为什么用 ICMP 而不是 TCP：TCP 需要完整的用户态 TCP/IP 栈（seq 重排、滑动窗口、
    /// 重传），那是几万行的活；ICMP echo 只需改 4 个字节、重算两个校验和，约 120 行。
    /// 验证链路不需要先实现协议栈。
    ///
    /// 注意 byte[] 里全是<b>网络字节序</b>（大端），BitConverter 在小端机上会读反，
    /// 所以全部手写位移，不用 BitConverter。
    /// </summary>
    internal static class IpPacket
    {
        internal const byte ProtocolIcmp = 1;
        internal const byte IcmpEchoRequest = 8;
        internal const byte IcmpEchoReply = 0;
        internal const byte Ipv4Version = 4;
        internal const byte DefaultTtl = 64;

        private const int VersionAndIhlOffset = 0;
        private const int TotalLengthOffset = 2;
        private const int TtlOffset = 8;
        private const int ProtocolOffset = 9;
        private const int HeaderChecksumOffset = 10;
        private const int SourceAddressOffset = 12;
        private const int DestinationAddressOffset = 16;

        /// <summary>IP 头长度（IHL 字段 × 4，通常 20）。</summary>
        internal static int HeaderLength(byte[] p) => (p[VersionAndIhlOffset] & 0x0F) * 4;

        internal static byte Version(byte[] p) => (byte)(p[VersionAndIhlOffset] >> 4);

        internal static int TotalLength(byte[] p) => ReadUInt16BigEndian(p, TotalLengthOffset);

        internal static byte Protocol(byte[] p) => p[ProtocolOffset];

        internal static uint SourceAddress(byte[] p) => ReadUInt32BigEndian(p, SourceAddressOffset);

        internal static uint DestinationAddress(byte[] p) => ReadUInt32BigEndian(p, DestinationAddressOffset);

        /// <summary>把 32 位大端 IP 转成点分十进制，日志用。</summary>
        internal static string AddressToString(uint address) =>
            $"{(address >> 24) & 0xFF}.{(address >> 16) & 0xFF}.{(address >> 8) & 0xFF}.{address & 0xFF}";

        /// <summary>
        /// 判断 <paramref name="packet"/> 的前 <paramref name="length"/> 字节是否是一个
        /// 完整的 IPv4 ICMP echo request。
        /// </summary>
        internal static bool IsIpv4EchoRequest(byte[] packet, int length)
        {
            if (length < 20 + 8)                      // 最短 IP 头 + ICMP 头
                return false;
            if (Version(packet) != Ipv4Version)
                return false;

            int headerLength = HeaderLength(packet);
            if (headerLength < 20 || headerLength > length)
                return false;

            // TotalLength 是 IP 头 + 载荷的总长；用它而不是 length，
            // 因为 VpnPacketBuffer 的 buffer 可能比实际包长（池化 buffer 按 MTU 分配）。
            int totalLength = TotalLength(packet);
            if (totalLength < headerLength + 8 || totalLength > length)
                return false;

            if (Protocol(packet) != ProtocolIcmp)
                return false;

            return packet[headerLength] == IcmpEchoRequest;   // ICMP Type
        }

        /// <summary>
        /// 就地把 echo request 改成 echo reply。
        /// 做法：交换源/目的 IP，ICMP Type 改 0，然后重算 ICMP 与 IP 两个校验和。
        /// 载荷原样保留 —— 这样 ping 的 identifier / sequence / 时间戳都对得上。
        /// </summary>
        internal static bool TryBuildEchoReply(byte[] packet, int length, out byte[] reply)
        {
            reply = Array.Empty<byte>();
            if (!IsIpv4EchoRequest(packet, length))
                return false;

            int headerLength = HeaderLength(packet);
            int totalLength = TotalLength(packet);

            reply = new byte[totalLength];
            Array.Copy(packet, reply, totalLength);

            // 1) 交换源 / 目的地址
            uint source = SourceAddress(reply);
            uint destination = DestinationAddress(reply);
            WriteUInt32BigEndian(reply, SourceAddressOffset, destination);
            WriteUInt32BigEndian(reply, DestinationAddressOffset, source);

            // 2) TTL 给个固定值（原值可能是 128 / 64 / 1，回包不该照抄）
            reply[TtlOffset] = DefaultTtl;

            // 3) ICMP Type: 8 (request) → 0 (reply)
            reply[headerLength] = IcmpEchoReply;

            // 4) 重算 ICMP 校验和：覆盖 ICMP 头 + 载荷，即 IP 头之后的所有字节
            WriteUInt16BigEndian(reply, headerLength + 2, 0);
            ushort icmpChecksum = Checksum(reply, headerLength, totalLength - headerLength);
            WriteUInt16BigEndian(reply, headerLength + 2, icmpChecksum);

            // 5) 重算 IP 头校验和：只覆盖 IP 头，且计算前必须把校验和字段清零
            WriteUInt16BigEndian(reply, HeaderChecksumOffset, 0);
            ushort ipChecksum = Checksum(reply, 0, headerLength);
            WriteUInt16BigEndian(reply, HeaderChecksumOffset, ipChecksum);

            return true;
        }

        /// <summary>
        /// RFC 1071 的 16 位反码和。奇数长度时最后一个字节按「后面补 0 字节」处理，
        /// 即加到高 8 位（大端序下的自然结果）。
        /// </summary>
        /// <summary>
        /// 一行概述一个包（只读，不改包）。数据面取证专用：
        /// 黑洞时最需要知道的是「谁的包进来了、内核回了什么」，而不是猜。
        ///
        /// ★ 必须带校验和结论，因为它就是本轮的头号嫌疑：
        ///   我们的 gvisor 端点 <c>Capabilities()</c> 返回 0，而参考实现
        ///   （sing-tun 的 WintunEndpoint）返回 <c>CapabilityRXChecksumOffload</c>。
        ///   gvisor 的 nic.go:790 据此决定 <c>pkt.RXChecksumValidated</c>；conntrack.go:486
        ///   在没有该标志时会对每个 TCP/UDP 包做 <c>TCPValid</c>，校验和不对就直接
        ///   「skip bad packets」—— 包进了内核、内核一个字节都不回，正是实测到的黑洞形状。
        ///   而 Windows 交给 VPN 插件的 L3 包<b>可能带着尚未填写的校验和</b>（发送侧
        ///   checksum offload 由网卡完成，虚拟 L3 接口没有网卡可完成它）。
        ///   所以采样行必须打印「存的校验和是多少、按伪头重算对不对」，一次采样即可判定。
        /// </summary>
        internal static string Describe(byte[] p, int length)
        {
            if (length < 20) return $"短包 {length}B";
            if (Version(p) != Ipv4Version) return $"非IPv4 v={Version(p)} {length}B";

            int hl = HeaderLength(p);
            byte proto = Protocol(p);
            uint srcAddr = SourceAddress(p);
            uint dstAddr = DestinationAddress(p);
            string src = AddressToString(srcAddr);
            string dst = AddressToString(dstAddr);

            // IP 总长才是这个包的真实长度，buf 里可能带填充；校验和都按总长算。
            int ipTotal = TotalLength(p);
            int n = Math.Min(length, ipTotal);
            if (n < hl) return $"{src} -> {dst} 头长越界 hl={hl} total={ipTotal} buf={length}";

            // IP 头校验和：把字段清零后重算，和存的值比。
            ushort ipStored = ReadUInt16BigEndian(p, HeaderChecksumOffset);
            string ipCk = "ipck=?";
            if (hl >= 20)
            {
                ushort recomputed = ChecksumWithZeroedField(p, 0, hl, HeaderChecksumOffset);
                ipCk = recomputed == ipStored ? "ipck=ok" : $"ipck=BAD存=0x{ipStored:X4}算=0x{recomputed:X4}";
            }

            string l4 = "";
            if (proto == 6 && n >= hl + 20)
            {
                int sport = ReadUInt16BigEndian(p, hl);
                int dport = ReadUInt16BigEndian(p, hl + 2);
                byte fl = p[hl + 13];
                string f = "";
                if ((fl & 0x02) != 0) f += "S";
                if ((fl & 0x10) != 0) f += "A";
                if ((fl & 0x01) != 0) f += "F";
                if ((fl & 0x04) != 0) f += "R";
                l4 = $"tcp {sport}->{dport} [{f}] {TransportChecksumNote(p, n, hl, hl + 16, 6, srcAddr, dstAddr, "tcpck")}";
            }
            else if (proto == 17 && n >= hl + 8)
            {
                int sport = ReadUInt16BigEndian(p, hl);
                int dport = ReadUInt16BigEndian(p, hl + 2);
                // UDP 校验和为 0 = 发送方按 RFC 768 合法地没算，gvisor 也不会因此丢包。
                ushort stored = ReadUInt16BigEndian(p, hl + 6);
                string note = stored == 0 ? "udpck=none(未计算,合法)"
                    : TransportChecksumNote(p, n, hl, hl + 6, 17, srcAddr, dstAddr, "udpck");
                l4 = $"udp {sport}->{dport} {note}";
            }
            else if (proto == ProtocolIcmp && n >= hl + 2)
            {
                l4 = $"icmp type={p[hl]}";
            }

            return $"{src} -> {dst} proto={proto} ip={ipTotal}/buf={length} hl={hl} {l4} {ipCk}";
        }

        /// <summary>
        /// 目的地址是否「不该进隧道」：组播 224.0.0.0/4、受限广播 255.255.255.255、
        /// 全零 0.0.0.0/8。这几类在真实 TUN 上会被内核原样回写（sing-tun 的
        /// LinkEndpointFilter 就干这事），等于把 Windows 自己发的 LLMNR/SSDP 组播
        /// 注回它自己 —— 白烧包数，还把采样淹了。
        /// </summary>
        internal static bool IsNonUnicastDestination(byte[] p, int length)
        {
            if (length < 20 || Version(p) != Ipv4Version)
                return false;
            uint dst = DestinationAddress(p);
            return (dst >> 28) == 0xE          // 224.0.0.0/4 组播（含 239.x 管理域）
                || dst == 0xFFFFFFFF           // 受限广播
                || (dst >> 24) == 0;           // 0.0.0.0/8
        }

        /// <summary><see cref="FixChecksums"/> 的改写结论（哪些字段本来是错的）。</summary>
        [Flags]
        internal enum ChecksumFix
        {
            None = 0,
            IpHeader = 1,
            Transport = 2,
        }

        /// <summary>
        /// 把交给内核之前的校验和补算齐（就地改 <paramref name="p"/>，调用方给的是副本）。
        ///
        /// ★ 这是 1.0.15 针对隧道黑洞的主修，理由分三层，每层都有出处：
        ///
        /// 1) 内核会验校验和。我们的 gvisor 端点 <c>Capabilities()</c> 返回 0，
        ///    <c>sing-tun@v0.4.27/stack_gvisor.go</c> 因此不会置
        ///    <c>pkt.RXChecksumValidated</c>（见 <c>gvisor/pkg/tcpip/stack/nic.go:790</c>），
        ///    而 <c>conntrack.go:486</c> 在未置该标志时对每个 TCP/UDP 包跑
        ///    <c>header.TCPValid(...)</c>，不对就 <c>return nil</c>（注释原文
        ///    "Just skip bad packets."）—— 包进了内核、内核一个字节都不回。
        ///    这与实测形状完全吻合：Encapsulated=7188 而桌面 curl 全 000。
        ///
        /// 2) 本机交给插件的 L3 包<b>很可能带没算完的校验和</b>。Windows 的 TCP/IP
        ///    默认把校验和交给网卡做 TX offload，而 VPN 的 L3 接口背后没有网卡可补算，
        ///    于是包到了插件手里时校验和字段可能是 0 或半值。真实 wintun 那套
        ///    （Clash Verge）不踩这个坑，是因为参考端点直接声明
        ///    <c>CapabilityRXChecksumOffload</c> 让内核跳过校验
        ///    （<c>sing-tun/tun_windows_gvisor.go</c> 的 WintunEndpoint 就是这么写的）。
        ///
        /// 3) 改哪一层由谁说了算：<c>clash_bridge.dll</c> 需要 Go + MinGW 重新编译，
        ///    本机这两个工具链已经不在了（<c>go</c>/<c>gcc</c> 均找不到，只剩
        ///    <c>C:\tools\mingw64.7z</c>）。装工具属于「先征得同意」的范围，
        ///    所以这一版把补算放在 C# 侧 —— 效果等价且不依赖重编桥：
        ///    内核收到的一定是校验和正确的包，验得过去也验得对。
        ///    桥源码里的 <c>Capabilities()</c> 对齐改动保留着，下次重建 DLL 时自然带上。
        ///
        /// 幂等性：本来正确的包重算出来还是同一个值，改写等于没改；
        /// UDP 存的 0 是 RFC 768 允许的「未计算」，gvisor 也不丢，因此保持 0 不动。
        /// ICMP 无 offload 通道（Windows 总是算好），不动。
        /// </summary>
        internal static ChecksumFix FixChecksums(byte[] p, int length)
        {
            if (length < 20 || Version(p) != Ipv4Version)
                return ChecksumFix.None;

            int hl = HeaderLength(p);
            if (hl < 20 || hl > length)
                return ChecksumFix.None;

            var fix = ChecksumFix.None;

            // ── IP 头校验和：只覆盖头，字段本身视为 0 ──────────────────────
            ushort ipStored = ReadUInt16BigEndian(p, HeaderChecksumOffset);
            ushort ipWant = ChecksumWithZeroedField(p, 0, hl, HeaderChecksumOffset);
            if (ipWant != ipStored)
            {
                WriteUInt16BigEndian(p, HeaderChecksumOffset, ipWant);
                fix |= ChecksumFix.IpHeader;
            }

            // 真实包长：buf 尾部可能有填充，伪头长度必须按 IP 总长算。
            int total = TotalLength(p);
            int n = Math.Min(length, total);
            byte proto = Protocol(p);

            if (proto == 6 && n >= hl + 20)
            {
                int off = hl + 16;                       // TCP checksum 字段偏移
                ushort stored = ReadUInt16BigEndian(p, off);
                ushort want = TransportChecksum(p, n, hl, off, proto);
                if (want != stored)
                {
                    WriteUInt16BigEndian(p, off, want);
                    fix |= ChecksumFix.Transport;
                }
            }
            else if (proto == 17 && n >= hl + 8)
            {
                int off = hl + 6;                        // UDP checksum 字段偏移
                ushort stored = ReadUInt16BigEndian(p, off);
                if (stored != 0)                         // 0 = 合法的「未计算」，保持不动
                {
                    ushort want = TransportChecksum(p, n, hl, off, proto);
                    if (want != stored)
                    {
                        if (want == 0) want = 0xFFFF;    // RFC 768：UDP 校验和不得为 0
                        WriteUInt16BigEndian(p, off, want);
                        fix |= ChecksumFix.Transport;
                    }
                }
            }

            return fix;
        }

        /// <summary>
        /// 按伪头（源址、目的址、协议、L4 长度）算传输层校验和；
        /// <paramref name="checksumOffset"/> 处的两字节视为 0（计算前的应有状态）。
        /// 返回的就是应当写回去的值。
        /// </summary>
        private static ushort TransportChecksum(byte[] p, int total, int hl,
            int checksumOffset, byte protocol)
        {
            uint sum = 0;
            uint srcAddr = SourceAddress(p);
            uint dstAddr = DestinationAddress(p);
            sum += (srcAddr >> 16) & 0xFFFF;
            sum += srcAddr & 0xFFFF;
            sum += (dstAddr >> 16) & 0xFFFF;
            sum += dstAddr & 0xFFFF;
            sum += protocol;
            sum += (uint)(total - hl);                   // 伪头里的 L4 长度

            int end = total;
            for (int i = hl; i + 1 < end; i += 2)
            {
                if (i == checksumOffset)                 // 跳过校验和字段（视为 0）
                    continue;
                sum += (uint)((p[i] << 8) | p[i + 1]);
            }
            if (((end - hl) & 1) == 1)                   // 奇数字节：尾部补 0
                sum += (uint)(p[end - 1] << 8);

            while ((sum >> 16) != 0)
                sum = (sum & 0xFFFF) + (sum >> 16);

            return (ushort)~sum;
        }

        /// <summary>
        /// TCP/UDP 校验和结论：按 RFC 1071 伪头（源址、目的址、协议、L4 长度）重算比对。
        /// </summary>
        private static string TransportChecksumNote(byte[] p, int total, int hl,
            int checksumOffset, byte protocol, uint srcAddr, uint dstAddr, string tag)
        {
            ushort stored = ReadUInt16BigEndian(p, checksumOffset);
            int l4Len = total - hl;
            if (l4Len < 4)
                return $"{tag}=?";

            uint sum = 0;
            // 伪头：src(4) dst(4) zero(1) proto(1) l4Len(2)
            sum += (srcAddr >> 16) & 0xFFFF;
            sum += srcAddr & 0xFFFF;
            sum += (dstAddr >> 16) & 0xFFFF;
            sum += dstAddr & 0xFFFF;
            sum += protocol;
            sum += (uint)l4Len;

            int end = total;
            for (int i = hl; i + 1 < end; i += 2)
            {
                if (i == checksumOffset)          // 计算时校验和字段视为 0
                    continue;
                sum += (uint)((p[i] << 8) | p[i + 1]);
            }
            if (((end - hl) & 1) == 1)            // 奇数长度：末字节补 0
                sum += (uint)(p[end - 1] << 8);

            while ((sum >> 16) != 0)
                sum = (sum & 0xFFFF) + (sum >> 16);

            ushort recomputed = (ushort)~sum;
            return recomputed == stored ? $"{tag}=ok" : $"{tag}=BAD存=0x{stored:X4}算=0x{recomputed:X4}";
        }

        /// <summary>把 offset 处两字节视为 0 后重算 [start,start+length) 的校验和。</summary>
        private static ushort ChecksumWithZeroedField(byte[] p, int start, int length, int offset)
        {
            uint sum = 0;
            int end = start + length;
            for (int i = start; i + 1 < end; i += 2)
            {
                if (i == offset)
                    continue;
                sum += (uint)((p[i] << 8) | p[i + 1]);
            }
            if (((end - start) & 1) == 1)
                sum += (uint)(p[end - 1] << 8);
            while ((sum >> 16) != 0)
                sum = (sum & 0xFFFF) + (sum >> 16);
            return (ushort)~sum;
        }

        /// <summary>
        /// RFC 1071 的 16 位反码和。奇数长度时最后一个字节按「后面补 0 字节」处理，
        /// 即加到高 8 位（大端序下的自然结果）。
        /// </summary>
        internal static ushort Checksum(byte[] data, int offset, int length)
        {
            uint sum = 0;
            int end = offset + length;

            for (int i = offset; i + 1 < end; i += 2)
                sum += (uint)((data[i] << 8) | data[i + 1]);

            if (((end - offset) & 1) == 1)          // 落单的最后一个字节
                sum += (uint)(data[end - 1] << 8);

            while ((sum >> 16) != 0)                // 回卷进位
                sum = (sum & 0xFFFF) + (sum >> 16);

            return (ushort)~sum;
        }

        internal static ushort ReadUInt16BigEndian(byte[] p, int offset) =>
            (ushort)((p[offset] << 8) | p[offset + 1]);

        internal static uint ReadUInt32BigEndian(byte[] p, int offset) =>
            ((uint)p[offset] << 24) | ((uint)p[offset + 1] << 16) |
            ((uint)p[offset + 2] << 8) | p[offset + 3];

        private static void WriteUInt16BigEndian(byte[] p, int offset, ushort value)
        {
            p[offset] = (byte)(value >> 8);
            p[offset + 1] = (byte)(value & 0xFF);
        }

        private static void WriteUInt32BigEndian(byte[] p, int offset, uint value)
        {
            p[offset] = (byte)((value >> 24) & 0xFF);
            p[offset + 1] = (byte)((value >> 16) & 0xFF);
            p[offset + 2] = (byte)((value >> 8) & 0xFF);
            p[offset + 3] = (byte)(value & 0xFF);
        }
    }
}
