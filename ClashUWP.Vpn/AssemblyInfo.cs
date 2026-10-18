using System.Runtime.CompilerServices;

// 前台工程（ClashUWP.exe / AssemblyName=ClashUWP）需要复用组件内的"优雅收尾"实现
// （GracefulTeardown：先等平台把会话收干净，再删 profile）。
//
// ★ 为什么走 InternalsVisibleTo 而不是把 GracefulTeardown 设成 public：
//   本工程是 CsWinRT 组件，<b>所有 public 类型都会被投影进 winmd</b>，
//   投影要求成员全部可 WinRT 化 —— 而 GracefulTeardown 用了值元组
//   （<(bool, string)>）和可空返回（VpnPlugInProfile?），投影会直接报错。
//   和 ClashVpnPlugin / IpPacket 同一个处理办法：internal + 不投影，
//   再对本包的另一侧开放可见性。
//   两个程序集都在同一个 MSIX 包里，不存在"泄露给第三方"的问题。
[assembly: InternalsVisibleTo("ClashUWP")]
