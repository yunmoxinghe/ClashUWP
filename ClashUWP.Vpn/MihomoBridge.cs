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
    ///   int clash_bridge_start(const char* config, int len, int mtu)
    ///   int clash_bridge_stop()
    ///   int clash_bridge_put_packet(const char* data, int len)
    ///   int clash_bridge_get_packet(char* buf, int cap, int* outLen)
    ///   int clash_bridge_last_error(char* buf, int cap)
    ///
    /// 调用约定 cdecl（Go c-shared 默认）；所有参数均为可 blittable 类型
    /// （byte[]/int/out int），NativeAOT 源生成封送可直接处理，无手工封送开销。
    ///
    /// DLL 加载：与插件组件同目录（包安装目录），DllImport 按包目录搜索。
    /// </summary>
    internal static class MihomoBridge
    {
        // 不带 .dll 后缀：DllImport 会按平台惯例补全，x64/arm64 各自命中。
        private const string DllName = "clash_bridge";

        /// <summary>启动 mihomo 数据面。返回 0 成功；-1 参数非法；-2 启动失败。
        /// mmdbDir 为 Country.mmdb 所在目录（UTF-8，空 = mihomo 默认 home）。</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int clash_bridge_start(byte[] configJson, int length, int mtu, byte[]? mmdbDir);

        /// <summary>停止数据面并释放资源；可再次 start。</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int clash_bridge_stop();

        /// <summary>送入一个原始 L3 包（Encapsulate → gvisor 栈）。返回 0 成功；-1 未启动。</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int clash_bridge_put_packet(byte[] data, int length);

        /// <summary>取一个回程原始 L3 包。返回 1 取到（outLen 为字节数）；0 无包；-1 参数非法。</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int clash_bridge_get_packet(byte[] buffer, int capacity, out int outLen);

        /// <summary>取最近一次 start 失败的错误描述（UTF-8），返回字节数。</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int clash_bridge_last_error(byte[] buffer, int capacity);

        /// <summary>取最近一次 start 实际绑定的物理网卡名（UTF-8），返回字节数。
        /// 空 = 未绑定（探测失败）。防自环修复是否生效的权威观测点。</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int clash_bridge_bound_iface(byte[] buffer, int capacity);

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
