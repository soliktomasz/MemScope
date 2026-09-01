using MemoryProfiler.App.Services;
using MemoryProfiler.App.ViewModels;
using MemoryProfiler.App.ViewModels.Objects;
using MemoryProfiler.App.ViewModels.Types;
using MemoryProfiler.Contracts.Heap;
using Xunit;

namespace MemoryProfiler.App.Tests.ViewModels;

public sealed class InvestigationClipboardTests
{
    [Fact]
    public async Task CopiesTypeName()
    {
        var service = new RecordingClipboardService();
        var clipboard = new InvestigationClipboard(service);
        var row = new TypeRowViewModel(
            new HeapTypeInfo(0x1000, "Example.Widget", "Example", 1, 24, null));

        await clipboard.CopyTypeNameAsync(row);

        Assert.Equal("Example.Widget", service.Text);
    }

    [Fact]
    public async Task CopiesCanonicalObjectAddress()
    {
        var service = new RecordingClipboardService();
        var clipboard = new InvestigationClipboard(service);
        var row = new HeapObjectRowViewModel(
            new HeapObjectInfo(0x1234, 0x1000, "Example.Widget", 24, "Gen0"));

        await clipboard.CopyObjectAddressAsync(row);

        Assert.Equal("0x000000001234", service.Text);
    }

    [Fact]
    public async Task CopiesCompleteGcRootPath()
    {
        var service = new RecordingClipboardService();
        var clipboard = new InvestigationClipboard(service);
        var row = new GcRootRowViewModel(
            0, true, false, "GC Root", "Static field", "root", "Example.Root",
            0, string.Empty, false,
            "GC Root: Example.Root\n0x000000001234 Example.Widget");

        await clipboard.CopyGcRootPathAsync(row);

        Assert.Equal(
            "GC Root: Example.Root\n0x000000001234 Example.Widget",
            service.Text);
    }

    private sealed class RecordingClipboardService : IClipboardService
    {
        public string? Text { get; private set; }

        public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Text = text;
            return Task.CompletedTask;
        }
    }
}
