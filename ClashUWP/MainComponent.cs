using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;

namespace ClashUWP
{
    /// <summary>
    /// 最小可跑壳：只为验证 Reactor.Uwp 已接管渲染。
    /// 真正的界面（代理组卡片流 / 状态卡 / 日志页）后面堆在这里。
    /// </summary>
    public sealed class MainComponent : Component
    {
        public override Element Render()
        {
            var (count, setCount) = UseState(0);

            return VStack(
                TextBlock($"ClashUWP —— Reactor.Uwp 已接管（{count}）"),
                Button("点一下", () => setCount(count + 1)));
        }
    }
}
