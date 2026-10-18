package bridge

// 本文件复刻 mihomo listener/sing_tun 的 ListenerHandler 包装层
// （dns.go + prepare.go），在 sing.ListenerHandler 之上补充：
//   1. DNS 劫持（ShouldHijackDns + relay）
//   2. tun.Handler 的 PrepareConnection
// 差异点：UWP 沙箱内无法创建原始 ICMP 连接，PrepareConnection 恒返回
// (nil, nil)，让 gvisor ICMP forwarder 全部走本地 fake ping echo。

import (
	"context"
	"net"
	"net/netip"
	"sync"
	"time"

	"github.com/metacubex/mihomo/component/resolver"
	"github.com/metacubex/mihomo/listener/sing"
	"github.com/metacubex/mihomo/log"

	tun "github.com/metacubex/sing-tun"
	"github.com/metacubex/sing/common/buf"
	"github.com/metacubex/sing/common/bufio"
	M "github.com/metacubex/sing/common/metadata"
	N "github.com/metacubex/sing/common/network"
)

// TunHandler 满足 sing-tun 的 Handler 接口（PrepareConnection +
// TCPConnectionHandler + PacketHandler + E.Handler）。
type TunHandler struct {
	*sing.ListenerHandler
	DnsAddrPorts []netip.AddrPort
	Inet4Address []netip.Prefix
	Inet6Address []netip.Prefix
}

var _ tun.Handler = (*TunHandler)(nil)

// PrepareConnection 恒返回 (nil, nil)：不建立直连 ICMP 通道，
// 由 gvisor ICMP forwarder 本地应答 echo（fake ping）。
func (h *TunHandler) PrepareConnection(network string, source M.Socksaddr, destination M.Socksaddr, routeContext tun.DirectRouteContext, timeout time.Duration) (tun.DirectRouteDestination, error) {
	if network == N.NetworkICMP {
		log.Debugln("[ICMP] %s %s --> %s using fake ping echo", network, source, destination)
	}
	return nil, nil
}

func (h *TunHandler) ShouldHijackDns(targetAddr netip.AddrPort) bool {
	for _, addrPort := range h.DnsAddrPorts {
		if addrPort == targetAddr || (addrPort.Addr().IsUnspecified() && targetAddr.Port() == 53) {
			return true
		}
	}
	return false
}

func (h *TunHandler) NewConnection(ctx context.Context, conn net.Conn, metadata M.Metadata) error {
	if h.ShouldHijackDns(metadata.Destination.AddrPort()) {
		log.Debugln("[DNS] hijack tcp:%s", metadata.Destination.String())
		return resolver.RelayDnsConn(ctx, conn, resolver.DefaultDnsReadTimeout)
	}
	return h.ListenerHandler.NewConnection(ctx, conn, metadata)
}

func (h *TunHandler) NewPacket(ctx context.Context, key netip.AddrPort, buffer *buf.Buffer, metadata M.Metadata, init func(natConn N.PacketConn) N.PacketWriter) {
	if h.ShouldHijackDns(metadata.Destination.AddrPort()) {
		log.Debugln("[DNS] hijack udp:%s from %s", metadata.Destination.String(), metadata.Source.String())
		writer := init(nil)
		rwOptions := N.ReadWaitOptions{
			FrontHeadroom: N.CalculateFrontHeadroom(writer),
			RearHeadroom:  N.CalculateRearHeadroom(writer),
			MTU:           resolver.SafeDnsPacketSize,
		}
		go relayDnsPacket(ctx, buffer, rwOptions, metadata.Destination, nil, &writer)
		return
	}
	h.ListenerHandler.NewPacket(ctx, key, buffer, metadata, init)
}

func (h *TunHandler) NewPacketConnection(ctx context.Context, conn N.PacketConn, metadata M.Metadata) error {
	if h.ShouldHijackDns(metadata.Destination.AddrPort()) {
		log.Debugln("[DNS] hijack udp:%s from %s", metadata.Destination.String(), metadata.Source.String())
		defer func() { _ = conn.Close() }()
		mutex := sync.Mutex{}
		var writer N.PacketWriter = conn
		defer func() {
			mutex.Lock()
			defer mutex.Unlock()
			writer = nil
		}()
		rwOptions := N.ReadWaitOptions{
			FrontHeadroom: N.CalculateFrontHeadroom(conn),
			RearHeadroom:  N.CalculateRearHeadroom(conn),
			MTU:           resolver.SafeDnsPacketSize,
		}
		readWaiter, isReadWaiter := bufio.CreatePacketReadWaiter(conn)
		if isReadWaiter {
			readWaiter.InitializeReadWaiter(rwOptions)
		}
		for {
			var (
				readBuff *buf.Buffer
				dest     M.Socksaddr
				err      error
			)
			_ = conn.SetReadDeadline(time.Now().Add(resolver.DefaultDnsReadTimeout))
			readBuff = nil
			if isReadWaiter {
				readBuff, dest, err = readWaiter.WaitReadPacket()
			} else {
				readBuff = rwOptions.NewPacketBuffer()
				dest, err = conn.ReadPacket(readBuff)
				if readBuff != nil {
					rwOptions.PostReturn(readBuff)
				}
			}
			if err != nil {
				if readBuff != nil {
					readBuff.Release()
				}
				if sing.ShouldIgnorePacketError(err) {
					break
				}
				return err
			}
			go relayDnsPacket(ctx, readBuff, rwOptions, dest, &mutex, &writer)
		}
		return nil
	}
	return h.ListenerHandler.NewPacketConnection(ctx, conn, metadata)
}

func relayDnsPacket(ctx context.Context, readBuff *buf.Buffer, rwOptions N.ReadWaitOptions, dest M.Socksaddr, mutex *sync.Mutex, writer *N.PacketWriter) {
	ctx, cancel := context.WithTimeout(ctx, resolver.DefaultDnsRelayTimeout)
	defer cancel()
	inData := readBuff.Bytes()
	writeBuff := readBuff
	writeBuff.Resize(writeBuff.Start(), 0)
	if len(writeBuff.FreeBytes()) < resolver.SafeDnsPacketSize {
		writeBuff = rwOptions.NewPacketBuffer()
	}
	msg, err := resolver.RelayDnsPacket(ctx, inData, writeBuff.FreeBytes())
	if writeBuff != readBuff {
		readBuff.Release()
	}
	if err != nil {
		writeBuff.Release()
		return
	}
	writeBuff.Truncate(len(msg))
	if mutex != nil {
		mutex.Lock()
		defer mutex.Unlock()
	}
	conn := *writer
	if conn == nil {
		writeBuff.Release()
		return
	}
	err = conn.WritePacket(writeBuff, dest)
	if err != nil {
		writeBuff.Release()
		return
	}
}
