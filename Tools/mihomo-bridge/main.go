// Package main 是 mihomo 桥接层的 c-shared 导出面（C ABI），
// 供 ClashUWP VPN 插件（C# P/Invoke）调用。
package main

/*
#include <stdint.h>
*/
import "C"

import (
	"sync"
	"unsafe"

	"clashuwp/mihomo-bridge/bridge"
)

func main() {}

var (
	errMu   sync.Mutex
	lastErr string
)

func setErr(msg string) {
	errMu.Lock()
	lastErr = msg
	errMu.Unlock()
}

// clash_bridge_start 启动 mihomo 数据面。
// 返回 0 成功；-1 参数非法；-2 启动失败（详情见 clash_bridge_last_error）。
//
//export clash_bridge_start
func clash_bridge_start(configJSON *C.char, length C.int, mtu C.int, mmdbDir *C.char) C.int {
	if configJSON == nil || length <= 0 {
		setErr("empty config buffer")
		return -1
	}
	cfg := C.GoBytes(unsafe.Pointer(configJSON), length)
	dir := ""
	if mmdbDir != nil {
		dir = C.GoString(mmdbDir)
	}
	if err := bridge.Default.Start(cfg, uint32(mtu), dir); err != nil {
		setErr(err.Error())
		return -2
	}
	setErr("")
	return 0
}

// clash_bridge_stop 停止数据面并释放资源（可再次 start）。
//
//export clash_bridge_stop
func clash_bridge_stop() C.int {
	_ = bridge.Default.Stop()
	return 0
}

// clash_bridge_put_packet 送入一个原始 L3 包（C# Encapsulate → gvisor 栈）。
// 返回 0 成功；-1 未启动或参数非法。
//
//export clash_bridge_put_packet
func clash_bridge_put_packet(data *C.char, length C.int) C.int {
	if data == nil || length <= 0 {
		return -1
	}
	pkt := C.GoBytes(unsafe.Pointer(data), length)
	return C.int(bridge.Default.PutPacket(pkt))
}

// clash_bridge_get_packet 取一个回程原始 L3 包（gvisor 栈 → C# Inject）。
// 返回 1 取到包（outLen 为字节数）；0 当前无包；-1 参数非法。
//
//export clash_bridge_get_packet
func clash_bridge_get_packet(buffer *C.char, capacity C.int, outLen *C.int) C.int {
	if buffer == nil || capacity <= 0 || outLen == nil {
		return -1
	}
	dst := unsafe.Slice((*byte)(unsafe.Pointer(buffer)), int(capacity))
	n, ok := bridge.Default.GetPacket(dst)
	if !ok {
		return 0
	}
	*outLen = C.int(n)
	return 1
}

// clash_bridge_last_error 取最近一次 start 失败的错误描述（UTF-8）。
// 返回描述字节数（不含结尾 0）。
//
//export clash_bridge_last_error
func clash_bridge_last_error(buffer *C.char, capacity C.int) C.int {
	errMu.Lock()
	msg := lastErr
	errMu.Unlock()
	if buffer == nil || capacity <= 0 {
		return C.int(len(msg))
	}
	return C.int(copy(unsafe.Slice((*byte)(unsafe.Pointer(buffer)), int(capacity)), msg))
}

// clash_bridge_bound_iface 取最近一次 start 实际绑定的物理网卡名（UTF-8），
// 空串 = 未绑定（探测失败）。这是防自环修复是否生效的权威观测点：
// 快照里 BoundIface 为空就说明绑定从未发生。
// 返回名字字节数。
//
//export clash_bridge_bound_iface
func clash_bridge_bound_iface(buffer *C.char, capacity C.int) C.int {
	name := bridge.Default.BoundInterface()
	if buffer == nil || capacity <= 0 {
		return C.int(len(name))
	}
	return C.int(copy(unsafe.Slice((*byte)(unsafe.Pointer(buffer)), int(capacity)), name))
}
