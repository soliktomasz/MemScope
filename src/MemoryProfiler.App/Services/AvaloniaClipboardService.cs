using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace MemoryProfiler.App.Services;

public sealed class AvaloniaClipboardService(Func<TopLevel?> topLevel) : IClipboardService
{
    public async Task SetTextAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        var clipboard = topLevel()?.Clipboard ??
            throw new InvalidOperationException("The clipboard is not available.");
        await clipboard.SetTextAsync(text);
    }
}
