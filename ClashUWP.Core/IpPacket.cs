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
    public static class IpPacket
    {
        public const byte ProtocolIcmp = 1;
        public const byte IcmpEchoRequest = 8;
        public const byte IcmpEchoReply = 0;
        public const byte Ipv4Version = 4;
        public const byte DefaultTtl = 64;

        private const int VersionAndIhlOffset = 0;
        private const int TotalLengthOffset = 2;
        private const int TtlOffset = 8;
        private const int ProtocolOffset = 9;
        private const int HeaderChecksumOffset = 10;
        private const int SourceAddressOffset = 12;
        private const int DestinationAddressOffset = 16;

        /// <summary>IP 头长度（IHL 字段 × 4，通常 20）。</summary>
        public static int HeaderLength(byte[] p) => (p[VersionAndIhlOffset] & 0x0F) * 4;

        public static byte Version(byte[] p) => (byte)(p[VersionAndIhlOffset] >> 4);

        public static int TotalLength(byte[] p) => ReadUInt16BigEndian(p, TotalLengthOffset);

        public static byte Protocol(byte[] p) => p[ProtocolOffset];

        public static uint SourceAddress(byte[] p) => ReadUInt32BigEndian(p, SourceAddressOffset);

        public static uint DestinationAddress(byte[] p) => ReadUInt32BigEndian(p, DestinationAddressOffset);

        /// <summary>把 32 位大端 IP 转成点分十进制，日志用。</summary>
        public static string AddressToString(uint address) =>
            $"{(address >> 24) & 0xFF}.{(address >> 16) & 0xFF}.{(address >> 8) & 0xFF}.{address & 0xFF}";

        /// <summary>
        /// 判断 <paramref name="packet"/> 的前 <paramref name="length"/> 字节是否是一个
        /// 完整的 IPv4 ICMP echo request。
        /// </summary>
        public static bool IsIpv4EchoRequest(byte[] packet, int length)
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
        public static bool TryBuildEchoReply(byte[] packet, int length, out byte[] reply)
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
        public static ushort Checksum(byte[] data, int offset, int length)
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

        public static ushort ReadUInt16BigEndian(byte[] p, int offset) =>
            (ushort)((p[offset] << 8) | p[offset + 1]);

        public static uint ReadUInt32BigEndian(byte[] p, int offset) =>
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
