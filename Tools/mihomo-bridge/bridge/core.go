package bridge

// core.go —— 桥接编排：加载 mihomo 配置 → 启动 gvisor 虚拟栈。
// 与 mihomo sing_tun.New 的差异（路线 A 的关键点）：
//   1. tunNew(tunOptions)（wintun 设备）替换为本包的 VirtualTun（内存队列）
//   2. 跳过 NetworkUpdateMonitor / DefaultInterfaceMonitor / auto-route /
//      auto-redirect —— 路由由 UWP VpnChannel 管理，mihomo 只做进程内终结
//   3. 配置中 tun.enable 必须为 false，避免 executor 走平台 tunNew

import (
	"context"
	"fmt"
	"net"
	"net/netip"
	"strings"
	"sync"
	"time"

	"github.com/metacubex/mihomo/adapter/inbound"
	"github.com/metacubex/mihomo/config"
	C "github.com/metacubex/mihomo/constant"
	"github.com/metacubex/mihomo/component/dialer"
	"github.com/metacubex/mihomo/hub"
	"github.com/metacubex/mihomo/hub/route"
	"github.com/metacubex/mihomo/listener/sing"
	"github.com/metacubex/mihomo/log"
	"github.com/metacubex/mihomo/tunnel"

	tun "github.com/metacubex/sing-tun"
)

// ErrAlreadyStarted 已经启动时重复 start。
var ErrAlreadyStarted = fmt.Errorf("bridge already started")

// Bridge 进程内单例（同一时间只有一条 VPN 连接，一份数据面足够）。
type Bridge struct {
	mu         sync.Mutex
	vtun       *VirtualTun
	stack      tun.Stack
	boundIface string // 实际绑定的物理网卡名，供诊断（快照/last_error 之外的观测）
}

// BoundInterface 返回最近一次 Start 实际绑定的物理网卡名（空=未绑定/失败）。
func (b *Bridge) BoundInterface() string {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.boundIface
}

// isVirtualNIC 判断接口名是否是虚拟网卡（Hyper-V/VMware/回环/隧道类）。
// VPN L3 接口靠参数 exclude 匹配，这里只管"非物理"这一类。
func isVirtualNIC(nameLower string) bool {
	for _, tag := range []string{
		"veth", "hyper-v", "vmware", "vbox", "virtual", "loopback",
		"isatap", "teredo", "wsl", "bluetooth", "microsoft wi-fi direct",
	} {
		if strings.Contains(nameLower, tag) {
			return true
		}
	}
	return false
}

// detectDefaultInterface 枚举网卡，挑出物理出口接口（非 VPN、非虚拟）。
//
// 【为什么不用路由探测】VPN 起来后 0.0.0.0/0 的默认路由选中的就是 VPN 接口自身
// （metric=1），排除后必然返回空 —— 旧实现在这条路径上返回 ""，绑定从未发生，
// 隧道依旧没有出口。改为枚举所有 UP 接口：排除 VPN L3 接口（名含 exclude）与
// 常见虚拟接口，返回第一个有 IPv4 的真实网卡名。
// 失败返回 ""（调用方只告警不致命）。
func detectDefaultInterface(exclude string) string {
	ifaces, err := net.Interfaces()
	if err != nil {
		return ""
	}
	excludeLower := strings.ToLower(exclude)
	for _, ifc := range ifaces {
		if ifc.Flags&net.FlagUp == 0 || ifc.Flags&net.FlagLoopback != 0 {
			continue
		}
		nameLower := strings.ToLower(ifc.Name)
		if excludeLower != "" && strings.Contains(nameLower, excludeLower) {
			continue // VPN L3 接口
		}
		if isVirtualNIC(nameLower) {
			continue // Hyper-V/VMware 等虚拟网卡
		}
		addrs, err := ifc.Addrs()
		if err != nil {
			continue
		}
		for _, a := range addrs {
			ipnet, ok := a.(*net.IPNet)
			if !ok {
				continue
			}
			if ipnet.IP.To4() != nil && !ipnet.IP.IsLoopback() {
				return ifc.Name // 有 IPv4 的非 VPN 真实网卡
			}
		}
	}
	return ""
}

// rebindInterface 探测并刷新出站绑定的物理网卡，记录到 b.boundIface。
// ★ 调用方必须已持有 b.mu（Start 的两条路径都满足）。
//
// 探测：枚举 UP 网卡，排除自己的 VPN L3 接口（名为 ClashUWP）与虚拟网卡，
// 取第一个有 IPv4 的真实网卡（见 detectDefaultInterface 注释——旧的路由探测法
// 在 VPN 起来后必然选中自己，是绑定从未发生的根因）。
func (b *Bridge) rebindInterface() {
	if name := detectDefaultInterface("ClashUWP"); name != "" {
		dialer.DefaultInterface.Store(name)
		b.boundIface = name // 记录到 Bridge，供 BoundInterface() 诊断
		log.Infoln("[ClashUWP] 出站绑定物理网卡: %s（防全路由自环）", name)
	} else {
		b.boundIface = ""
		log.Warnln("[ClashUWP] 未能探测物理网卡，出站可能被全路由回环")
	}
}

// Default 全局单例。
var Default = &Bridge{}

// Start 解析 mihomo 配置并启动 gvisor 虚拟栈。
// configJSON：完整 mihomo YAML 配置；mtu 为 0 时取配置 tun.mtu，再缺省 1400。
// mmdbDir 非空时设为 mihomo home 目录（需含 Country.mmdb）——GEOIP 规则在
// config.Parse 阶段就加载 MMDB，必须在 Parse 之前设置。
func (b *Bridge) Start(configJSON []byte, mtu uint32, mmdbDir string) error {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.stack != nil {
		// C# 侧把这条错误当作「复用现桥」处理（新回程泵挂到新 channel）。
		// 物理网卡可能已换（比如刚从 Wi-Fi 切到有线），这里顺手刷新绑定再返回。
		b.rebindInterface()
		return ErrAlreadyStarted
	}

	if mmdbDir != "" {
		C.SetHomeDir(mmdbDir)
	}

	cfg, err := config.Parse(configJSON)
	if err != nil {
		return fmt.Errorf("parse mihomo config: %w", err)
	}

	// ★ 防自环（关键）：全路由（0.0.0.0/0）下，mihomo 拨号代理节点服务器的流量
	//   会被 UWP VPN 的 L3 接口路由回自己的隧道 —— 隧道等拨号、拨号等隧道，死锁，
	//   表现为整机断网。标准解法（同 Clash Verge 的 interface-name）：把 mihomo
	//   所有出站拨号强制绑定物理网卡。Windows 实现为 IP_UNICAST_IF
	//   （component/dialer/bind_windows.go）。
	b.rebindInterface()

	// 装载代理/规则/DNS 等全局状态；tun.enable=false → ReCreateTun 直接返回。
	// ★ 用 hub.ApplyConfig 而非 executor.ApplyConfig：后者注释明确
	//   "without ExternalController"，不装配 RESTful 控制器；
	//   hub 版本 = applyRoute(ExternalController) + executor.ApplyConfig，
	//   这样配置里的 external-controller: 127.0.0.1:9090 才真正监听，
	//   调试时可用 /proxies /connections /logs 实时观测内核状态。
	hub.ApplyConfig(cfg)

	tunCfg := cfg.General.Tun
	if mtu == 0 {
		mtu = tunCfg.MTU
	}
	if mtu == 0 {
		mtu = 1400
	}

	// DNS 劫持地址表（与 sing_tun.New 的 dnsAdds 构造一致）
	var dnsAdds []netip.AddrPort
	for _, d := range tunCfg.DNSHijack {
		if _, after, ok := strings.Cut(d, "://"); ok {
			d = after
		}
		d = strings.Replace(d, "any", "0.0.0.0", 1)
		addrPort, err := netip.ParseAddrPort(d)
		if err != nil {
			return fmt.Errorf("parse dns-hijack %q: %w", d, err)
		}
		dnsAdds = append(dnsAdds, addrPort)
	}
	for _, a := range tunCfg.Inet4Address {
		dnsAdds = append(dnsAdds, netip.AddrPortFrom(a.Addr().Next(), 53))
	}
	for _, a := range tunCfg.Inet6Address {
		dnsAdds = append(dnsAdds, netip.AddrPortFrom(a.Addr().Next(), 53))
	}

	h, err := sing.NewListenerHandler(sing.ListenerConfig{
		Tunnel:     tunnel.Tunnel,
		Type:       C.TUN,
		Additions:  []inbound.Addition{inbound.WithInName("DEFAULT-TUN"), inbound.WithSpecialRules("")},
		UDPTimeout: sing.UDPTimeout,
	})
	if err != nil {
		return fmt.Errorf("new listener handler: %w", err)
	}
	handler := &TunHandler{
		ListenerHandler: h,
		DnsAddrPorts:    dnsAdds,
		Inet4Address:    tunCfg.Inet4Address,
		Inet6Address:    tunCfg.Inet6Address,
	}

	vtun := NewVirtualTun(mtu)
	tunOptions := tun.Options{
		Name:                  "ClashUWP",
		MTU:                   mtu,
		Inet4Address:          tunCfg.Inet4Address,
		Inet6Address:          tunCfg.Inet6Address,
		Inet4LoopbackAddress:  filterAddrFamily(tunCfg.LoopbackAddress, true),
		Inet6LoopbackAddress:  filterAddrFamily(tunCfg.LoopbackAddress, false),
		// EndpointIndependentNat 属于 StackOptions（下方），tun.Options 没有该字段。
	}

	udpTimeout := sing.UDPTimeout
	if tunCfg.UDPTimeout != 0 {
		udpTimeout = time.Second * time.Duration(tunCfg.UDPTimeout)
	}
	icmpTimeout := sing.ICMPTimeout
	if tunCfg.ICMPTimeout != 0 {
		icmpTimeout = time.Second * time.Duration(tunCfg.ICMPTimeout)
	}

	stackOptions := tun.StackOptions{
		Context:                context.Background(),
		Tun:                    vtun,
		TunOptions:             tunOptions,
		EndpointIndependentNat: tunCfg.EndpointIndependentNat,
		UDPTimeout:             udpTimeout,
		ICMPTimeout:            icmpTimeout,
		Handler:                handler,
		Logger:                 log.SingLogger,
	}

	st, err := tun.NewStack("gvisor", stackOptions)
	if err != nil {
		_ = vtun.Close()
		return fmt.Errorf("new gvisor stack: %w", err)
	}
	if err := st.Start(); err != nil {
		_ = vtun.Close()
		return fmt.Errorf("start gvisor stack: %w", err)
	}

	b.vtun = vtun
	b.stack = st
	log.Infoln("[ClashUWP] mihomo bridge started, mtu=%d, inet4=%v", mtu, tunCfg.Inet4Address)
	return nil
}

// Stop 关闭 gvisor 栈与虚拟 tun；可再次 Start。
func (b *Bridge) Stop() error {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.stack == nil {
		return nil
	}
	stack := b.stack
	vtun := b.vtun
	b.stack = nil
	b.vtun = nil
	err := stack.Close()
	_ = vtun.Close()
	// 关闭 RESTful 控制器：ReCreateServer 收到全空 Config 会先 Close 旧
	// httpServer 且不再监听（server.go start()：Addr 为空则只关不开）。
	route.ReCreateServer(&route.Config{})
	log.Infoln("[ClashUWP] mihomo bridge stopped")
	return err
}

// PutPacket 把 VpnChannel Encapsulate 收到的原始 IP 包送进 gvisor 栈。
// 返回 0 成功；-1 未启动。
func (b *Bridge) PutPacket(pkt []byte) int {
	b.mu.Lock()
	vtun := b.vtun
	b.mu.Unlock()
	if vtun == nil {
		return -1
	}
	vtun.PutPacket(pkt)
	return 0
}

// GetPacket 取一个 gvisor 栈发出的原始 IP 包（回程流量）拷入 dst。
// 返回拷贝字节数；ok=false 表示当前无包（或 dst 太小已丢弃）。
func (b *Bridge) GetPacket(dst []byte) (n int, ok bool) {
	b.mu.Lock()
	vtun := b.vtun
	b.mu.Unlock()
	if vtun == nil {
		return 0, false
	}
	pkt, has := vtun.GetPacket()
	if !has {
		return 0, false
	}
	if len(pkt) > len(dst) {
		log.Warnln("[ClashUWP] get_packet: packet %d > buffer %d, dropped", len(pkt), len(dst))
		return 0, false
	}
	return copy(dst, pkt), true
}

func filterAddrFamily(addrs []netip.Addr, want4 bool) []netip.Addr {
	var out []netip.Addr
	for _, a := range addrs {
		if a.Is4() == want4 {
			out = append(out, a)
		}
	}
	return out
}
