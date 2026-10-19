using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ClashUWP.Vpn
{
    /// <summary>
    /// <c>clash_bridge.dll</c>（Go c-shared，路线 A 数据面）的 P/Invoke 面。
    ///
    /// ABI（与 Tools/mihomo-bridge/main.go 的 //export 一一对应，
    /// 详见 Docs/03-mihomo集成方案与ABI设计.md）：
    ///   int clash_bridge_start(const char* config, int length, int mtu)
    ///   int clash_bridge_stop()
    ///   int clash_bridge_put_packet(const char* data, int len)
    ///   int clash_bridge_get_packet(char* buf, int cap, int* outLen)
    ///   int clash_bridge_last_error(char* buf, int cap)
    ///
    /// 调用约定 cdecl（Go c-shared 默认）。
    ///
    /// ★★【1.0.77 必读：为什么这里是 <c>[LibraryImport]</c> 而不是 <c>[DllImport]</c>】
    ///   本文件从组件工程搬进主工程的那一次（1.0.76）装机即炸，实机原文：
    ///       bridge-start StartMihomoBridge 异常: AggregateException:
    ///       Method '[ClashUWP]ClashUWP.Vpn.MihomoBridge.clash_bridge_start(
    ///         uint8[],int32,int32,uint8[])' requires marshalling that is not yet
    ///         supported by this compiler.
    ///   根因不是搬文件，是<b>它所在的 assembly 变了</b>：
    ///   <c>DisableRuntimeMarshalling</c> 是<b>逐 assembly</b> 生效的
    ///   （编译成 <c>[module: DisableRuntimeMarshalling]</c>）。
    ///     · 旧形态：这份源码编在组件 assembly <c>ClashUWP.Vpn</c> 里，
    ///       那个工程没开这个属性 ⇒ <c>[DllImport]</c> 的 <c>byte[]</c> 走运行时封送桩，能用；
    ///     · 新形态：它进了主 assembly <c>ClashUWP</c>，本工程为了 UseUwp/AOT 的
    ///       WinRT 投影一直开着 <c>DisableRuntimeMarshalling=true</c> ⇒
    ///       marshalling-free 模式下 <c>[DllImport]</c> 的数组参数不被支持，
    ///       ilc 直接把桩编成"抛 NotSupported"。
    ///   官方解法只有两条：调用方自己把 <c>byte[]</c> 换成指针，或用<b>源生成的
    ///   <c>[LibraryImport]</c></b>（它对 blittable 元素数组生成 <c>fixed</c> 指针传参，
    ///   marshalling-free 与 NativeAOT 都支持）。这里选后者 —— 语义与原来的
    ///   <c>byte[]</c> 完全一致（传入的是数组首地址，Go 侧收到的还是 <c>char*</c>），
    ///   调用点一行都不用改。
    ///   ★ 别再用 <c>[DllImport]</c> 写数组参数：这条只在 AOT 运行到那一行才报错，
    ///     构建期一个字都不提示，1.0.76 就是这么把"插件能激活"换成了"隧道起不来"的。
    ///
    /// DLL 加载：与宿主同目录（包安装目录），按包目录搜索。
    /// </summary>
    /// <summary>
    /// 调用约定怎么写：原 <c>[DllImport]</c> 时代这里显式写了 <c>CallingConvention.Cdecl</c>，
    /// 换成 <c>[LibraryImport]</c> 后在主工程里<b>编不过</b>：
    ///     error CS1069: 未能在命名空间"System.Runtime.InteropServices"中找到类型名
    ///     "CallingConvention"。此类型已转发到程序集"System.Runtime.InteropServices, Version=10.0.0.0"
    /// —— 同一个写法在旧的组件工程里能编，主工程的引用集（UseUwp + CsWinRT 投影）缺那份转发用的
    /// 实际引用程序集。这里<b>不去补引用</b>，而是直接省掉该属性，因为省掉是等价的：
    /// 本工程只出 <c>x64</c> 与 <c>arm64</c>（<c>Platforms=x64;arm64</c>，原生桥也只有这两个架构），
    /// 而这两个架构在 Windows 上<b>只有一种</b>外部函数调用约定（x64 home/scratch 寄存器 ABI、
    /// ARM64 C ABI），<code>Cdecl</code> / <code>StdCall</code> / 默认 <code>Winapi</code> 生成的
    /// 机器码完全相同 —— 调用约定差异只在 x86 才存在。Go 侧 <c>c-shared</c> 导出的就是这套。
    /// ★ 若哪天要支持 x86，必须先把这个属性写回来（并解决 CS1069 的引用问题），不能这样省。
    /// </summary>
    internal static partial class MihomoBridge
    {
        // 不带 .dll 后缀：会按平台惯例补全，x64/arm64 各自命中。
        private const string DllName = "clash_bridge";

        /// <summary>启动 mihomo 数据面。返回 0 成功；-1 参数非法；-2 启动失败。
        /// mmdbDir 为 Country.mmdb 所在目录（UTF-8，空 = mihomo 默认 home）。</summary>
        [LibraryImport(DllName)]
        internal static partial int clash_bridge_start(byte[] configJson, int length, int mtu, byte[]? mmdbDir);

        /// <summary>停止数据面并释放资源；可再次 start。</summary>
        [LibraryImport(DllName)]
        internal static partial int clash_bridge_stop();

        /// <summary>送入一个原始 L3 包（Encapsulate → gvisor 栈）。返回 0 成功；-1 未启动。</summary>
        [LibraryImport(DllName)]
        internal static partial int clash_bridge_put_packet(byte[] data, int length);

        /// <summary>取一个回程原始 L3 包。返回 1 取到（outLen 为字节数）；0 无包；-1 参数非法。</summary>
        [LibraryImport(DllName)]
        internal static partial int clash_bridge_get_packet(byte[] buffer, int capacity, out int outLen);

        /// <summary>取最近一次 start 失败的错误描述（UTF-8），返回字节数。</summary>
        [LibraryImport(DllName)]
        internal static partial int clash_bridge_last_error(byte[] buffer, int capacity);

        /// <summary>取最近一次 start 实际绑定的物理网卡名（UTF-8），返回字节数。
        /// 空 = 未绑定（探测失败）。防自环修复是否生效的权威观测点。</summary>
        [LibraryImport(DllName)]
        internal static partial int clash_bridge_bound_iface(byte[] buffer, int capacity);

        /// <summary>以 UTF-8 启动数据面。mmdbDir 为空时传 null（用 mihomo 默认 home）。</summary>
        internal static int Start(string configJson, int mtu, string? mmdbDir)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(configJson);
            // 含结尾 0：Go 侧 C.GoString 依赖 NUL 结尾。
            byte[]? dir = string.IsNullOrEmpty(mmdbDir) ? null : Encoding.UTF8.GetBytes(mmdbDir + "\0");
            return clash_bridge_start(bytes, bytes.Length, mtu, dir);
        }

        /// <summary>取最近一次 start 失败的错误描述（UTF-8）。</summary>
        internal static string LastError()
        {
            byte[] buf = new byte[2048];
            int n = clash_bridge_last_error(buf, buf.Length);
            if (n <= 0)
                return string.Empty;
            return Encoding.UTF8.GetString(buf, 0, Math.Min(n, buf.Length));
        }

        /// <summary>取最近一次 start 实际绑定的物理网卡名（UTF-8）。
        /// 空串 = 未绑定（探测失败）——快照里为空说明防自环修复没生效。</summary>
        internal static string BoundInterface()
        {
            byte[] buf = new byte[512];
            int n = clash_bridge_bound_iface(buf, buf.Length);
            if (n <= 0)
                return string.Empty;
            return Encoding.UTF8.GetString(buf, 0, Math.Min(n, buf.Length));
        }
    }
}
