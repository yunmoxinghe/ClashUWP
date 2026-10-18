// Package bridge 是 mihomo 桥接层的纯 Go 核心（不含 cgo）。
// 它把 mihomo 的 gvisor tun 栈跑在「内存队列」上，而不是真实网卡上：
//
//	C# Encapsulate ──PutPacket──▶ 出站队列 ──Read──▶ gvisor netstack ──代理──▶ 出网
//	                                                            │
//	C# Inject ◀──GetPacket── 入站队列 ◀──Write/WritePacket ──────┘
//
// 这样 UWP VPN 插件抓到的原始 L3 包被 mihomo 在进程内终结/代理，回包再主动注入本机协议栈，
// 全程不需要 wintun 设备（对应 sing-tun 的 Tun 接口 = io.ReadWriter + Close）。
package bridge

import (
	"io"
	"sync"

	"github.com/metacubex/gvisor/pkg/tcpip/stack"
)

// VirtualTun 实现 sing-tun 的 tun.Tun（io.ReadWriter + Close）与 tun.GVisorTun
// （WritePacket + NewEndpoint）。它不操作任何真实网卡。
type VirtualTun struct {
	mu       sync.Mutex
	cond     *sync.Cond
	outbound [][]byte // C# PutPacket 投喂的待处理出站包（FIFO），Read 从这里取
	inbound  chan []byte // gvisor 写回、要 C# GetPacket 取走注入本机的回包
	closed   bool
	mtu      uint32
}

// NewVirtualTun 建一个虚拟 Tun。mtu 用于回报给 gvisor 端点。
func NewVirtualTun(mtu uint32) *VirtualTun {
	t := &VirtualTun{
		outbound: make([][]byte, 0, 64),
		inbound:  make(chan []byte, 256),
		mtu:      mtu,
	}
	t.cond = sync.NewCond(&t.mu)
	return t
}

// Read 阻塞直到有出站包（供 gvisor 的 dispatchLoop 调用）。
// 语义与 net.Conn.Read 一致：返回的字节拷进 p，调用方不持有内部 buffer。
func (t *VirtualTun) Read(p []byte) (int, error) {
	t.mu.Lock()
	for len(t.outbound) == 0 && !t.closed {
		t.cond.Wait()
	}
	if t.closed {
		t.mu.Unlock()
		return 0, io.EOF
	}
	pkt := t.outbound[0]
	t.outbound = t.outbound[1:]
	t.mu.Unlock()

	if len(pkt) > len(p) {
		pkt = pkt[:len(p)]
	}
	return copy(p, pkt), nil
}

// Write 是 io.Writer：把一个回包整体放入入站队列（C# 侧 GetPacket 取走）。
// p 是 netstack 复用的 buffer，必须拷贝。
func (t *VirtualTun) Write(p []byte) (int, error) {
	if t.closed {
		return 0, io.EOF
	}
	buf := make([]byte, len(p))
	copy(buf, p)
	select {
	case t.inbound <- buf:
	default:
		// 入站队列满则丢弃：本机 TCP 会重传，不该阻塞 netstack goroutine。
	}
	return len(p), nil
}

// Close 关闭隧道：唤醒阻塞的 Read。
// 注意：不能 close(inbound) —— Write 侧存在「检查 closed 与发送之间」的窗口，
// 关闭 channel 会在该窗口触发 send on closed channel panic（Stop 与 netstack
// goroutine 并发时必现）。GetPacket 永不阻塞，不依赖 channel 关闭。
func (t *VirtualTun) Close() error {
	t.mu.Lock()
	if !t.closed {
		t.closed = true
		t.cond.Broadcast()
	}
	t.mu.Unlock()
	return nil
}

// ── C# 桥（c-shared 边界调用）──────────────────────────────────────

// PutPacket 由 C# Encapsulate 调用：投喂一个出站原始 L3 包。
func (t *VirtualTun) PutPacket(pkt []byte) {
	if t == nil {
		return
	}
	t.mu.Lock()
	if t.closed {
		t.mu.Unlock()
		return
	}
	cp := make([]byte, len(pkt))
	copy(cp, pkt)
	t.outbound = append(t.outbound, cp)
	t.cond.Signal()
	t.mu.Unlock()
}

// GetPacket 非阻塞取一个回包；没有则 ok=false。C# 侧 get_packet 调用。
func (t *VirtualTun) GetPacket() ([]byte, bool) {
	select {
	case pkt, ok := <-t.inbound:
		return pkt, ok
	default:
		return nil, false
	}
}

// ── GVisorTun 接口（sing-tun stack_gvisor.go）──────────────────────

// WritePacket 满足 tun.GVisorTun：把 gvisor 的 PacketBuffer（可能分片）拼成一个
// 完整 IP 包，作为单一回包入队。slices 属于同一个包，不能拆成多条写。
func (t *VirtualTun) WritePacket(pkt *stack.PacketBuffer) (int, error) {
	slices := pkt.AsSlices()
	total := 0
	for _, s := range slices {
		total += len(s)
	}
	buf := make([]byte, 0, total)
	for _, s := range slices {
		buf = append(buf, s...)
	}
	return t.Write(buf)
}

// NewEndpoint 满足 tun.GVisorTun：返回把 netstack 接到本 VirtualTun 的 LinkEndpoint。
func (t *VirtualTun) NewEndpoint() (stack.LinkEndpoint, stack.NICOptions, error) {
	return &VirtualEndpoint{tun: t}, stack.NICOptions{}, nil
}