using Microsoft.UI.Reactor.Core;
using Reactor.Uwp.Hosting;

namespace ClashUWP
{
    /// <summary>
    /// Reactor 入口。
    ///
    /// 没有 App.xaml —— Reactor 是"用 C# 描述界面，不写 XAML"，所以这里不是
    /// <c>App : Application</c> + <c>InitializeComponent()</c>，而是
    /// <c>ReactorApplication&lt;T&gt;</c> + 手写 Main。
    ///
    /// 别改回 <c>Frame.Navigate(typeof(Page))</c>：纯 C# 的 Page 没有 XAML 生成的
    /// 元数据提供程序，XAML 框架按 Type 激活不了它，会直接访问冲突崩在 coreclr 里
    /// （事件日志是 Application Error 1000 / Faulting module coreclr.dll）。
    /// 要换页面就换 <c>ReactorApplication&lt;T&gt;</c> 的 T，或走框架自己的导航。
    /// </summary>
    public sealed partial class App : ReactorApplication<MainComponent>
    {
        public static void Main(string[] args) =>
            Windows.UI.Xaml.Application.Start(_ => new App());
    }
}
