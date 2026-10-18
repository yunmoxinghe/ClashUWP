package bridge

import (
	"sync"

	"github.com/metacubex/gvisor/pkg/buffer"
	"github.com/metacubex/gvisor/pkg/tcpip"
	"github.com/metacubex/gvisor/pkg/tcpip/header"
	"github.com/metacubex/gvisor/pkg/tcpip/stack"
)

// VirtualEndpoint 是 gvisor netstack 的 LinkEndpoint，把 netstack 接到 VirtualTun。
// 形状照抄 sing-tun 的 WintunEndpoint（tun_windows_gvisor.go），只是把
// 「读 wintun」换成「读 VirtualTun」。
var _ stack.LinkEndpoint = (*VirtualEndpoint)(nil)

type VirtualEndpoint struct {
	tun        *VirtualTun
	mu         sync.RWMutex // 守护 dispatcher
	dispatcher stack.NetworkDispatcher
}

func (e *VirtualEndpoint) MTU() uint32 { return e.tun.mtu }

func (e *VirtualEndpoint) SetMTU(mtu uint32) {}

func (e *VirtualEndpoint) MaxHeaderLength() uint16 { return 0 }

func (e *VirtualEndpoint) LinkAddress() tcpip.LinkAddress { return "" }

func (e *VirtualEndpoint) SetLinkAddress(addr tcpip.LinkAddress) {}

// Capabilities 声明「收到的包，传输层校验和由对端负责」—— 与参考实现
// sing-tun 的 WintunEndpoint（tun_windows_gvisor.go）逐字一致。
//
// ★ 为什么这一行是黑洞的头号嫌疑（对照官方读出来的，不是猜的）：
//   nic.go:790  pkt.RXChecksumValidated = Capabilities()&CapabilityRXChecksumOffload != 0
//   conntrack.go:486 未置该标志时，对每个 TCP/UDP 包跑 header.TCPValid(...)，
//   校验和不对就 return nil（注释原文 "Just skip bad packets."）——
//   包进了内核、内核不回，正是实测到的形状（Encapsulated 7188 / 桌面 curl 全 000）。
//   而 Windows 交给 VPN 插件的 L3 包完全可能带<b>没填完的校验和</b>：
//   发送侧 checksum offload 本该由网卡补算，VPN 的 L3 接口没有网卡可补。
//   之前这里返回 0，等于要求内核去校验一批本来就没算完的校验和。
func (e *VirtualEndpoint) Capabilities() stack.LinkEndpointCapabilities {
	return stack.CapabilityRXChecksumOffload
}

// Attach 在启用 gvisor 栈时被 netstack 调用，起 dispatchLoop 消费出站包。
func (e *VirtualEndpoint) Attach(dispatcher stack.NetworkDispatcher) {
	e.mu.Lock()
	defer e.mu.Unlock()
	if dispatcher == nil && e.dispatcher != nil {
		e.dispatcher = nil
		return
	}
	if dispatcher != nil && e.dispatcher == nil {
		e.dispatcher = dispatcher
		go e.dispatchLoop()
	}
}

func (e *VirtualEndpoint) IsAttached() bool {
	e.mu.RLock()
	defer e.mu.RUnlock()
	return e.dispatcher != nil
}

func (e *VirtualEndpoint) Wait() {}

func (e *VirtualEndpoint) ARPHardwareType() header.ARPHardwareType { return header.ARPHardwareNone }

func (e *VirtualEndpoint) AddHeader(b *stack.PacketBuffer) {}

func (e *VirtualEndpoint) ParseHeader(ptr *stack.PacketBuffer) bool { return true }

// WritePackets 是 netstack 回包出口：把每个回包整体写回 VirtualTun（→ C# Inject）。
func (e *VirtualEndpoint) WritePackets(list stack.PacketBufferList) (int, tcpip.Error) {
	n := 0
	for _, pkt := range list.AsSlice() {
		if _, err := e.tun.WritePacket(pkt); err != nil {
			return n, &tcpip.ErrAborted{}
		}
		n++
	}
	return n, nil
}

func (e *VirtualEndpoint) Close() {}

func (e *VirtualEndpoint) SetOnCloseAction(f func()) {}

// dispatchLoop 从 VirtualTun.Read 拿原始 L3 包，判断 IPv4/IPv6 后投喂 netstack。
func (e *VirtualEndpoint) dispatchLoop() {
	buf := make([]byte, 65536)
	for {
		n, err := e.tun.Read(buf)
		if err != nil {
			return
		}
		if n < 1 {
			continue
		}

		var proto tcpip.NetworkProtocolNumber
		switch buf[0] >> 4 { // IP version 半字节
		case 4:
			proto = header.IPv4ProtocolNumber
		case 6:
			proto = header.IPv6ProtocolNumber
		default:
			continue // 非 IPv4/IPv6，丢弃
		}

		pkt := stack.NewPacketBuffer(stack.PacketBufferOptions{
			Payload:           buffer.MakeWithData(buf[:n]),
			IsForwardedPacket: true,
		})

		e.mu.RLock()
		dispatcher := e.dispatcher
		e.mu.RUnlock()
		if dispatcher == nil {
			pkt.DecRef()
			return
		}
		dispatcher.DeliverNetworkPacket(proto, pkt)
		pkt.DecRef()
	}
}