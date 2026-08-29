using Avalonia.Threading;

namespace MemoryProfiler.App.ViewModels;

internal interface IUiDispatcher
{
    Task InvokeAsync(Action action);
}

internal sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public static AvaloniaUiDispatcher Instance { get; } = new();

    private AvaloniaUiDispatcher()
    {
    }

    public async Task InvokeAsync(Action action) =>
        await Dispatcher.UIThread.InvokeAsync(action);
}

internal sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public static ImmediateUiDispatcher Instance { get; } = new();

    private ImmediateUiDispatcher()
    {
    }

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
