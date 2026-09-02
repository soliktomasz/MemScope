using MemoryProfiler.App.Services;
using MemoryProfiler.App.ViewModels.Objects;
using MemoryProfiler.App.ViewModels.Retainers;
using MemoryProfiler.App.ViewModels.Types;

namespace MemoryProfiler.App.ViewModels;

internal sealed class InvestigationClipboard(IClipboardService service)
{
    public Task CopyTypeNameAsync(
        TypeRowViewModel row,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        return service.SetTextAsync(row.TypeName, cancellationToken);
    }

    public Task CopyObjectAddressAsync(
        HeapObjectRowViewModel row,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        return service.SetTextAsync(row.AddressDisplay, cancellationToken);
    }

    public Task CopyObjectAddressAsync(
        ObjectReferenceRowViewModel row,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        return service.SetTextAsync(row.AddressDisplay, cancellationToken);
    }

    public Task CopyObjectAddressAsync(
        GcRootRowViewModel row,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        return service.SetTextAsync(row.AddressDisplay, cancellationToken);
    }

    public Task CopyObjectAddressAsync(
        TopRetainerRowViewModel row,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        return service.SetTextAsync(row.AddressDisplay, cancellationToken);
    }

    public Task CopyObjectAddressAsync(
        HeapFieldValueRowViewModel row,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        return service.SetTextAsync(row.ReferencedAddressDisplay, cancellationToken);
    }

    public Task CopyGcRootPathAsync(
        GcRootRowViewModel row,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        return service.SetTextAsync(row.RootPathDisplay, cancellationToken);
    }

    public Task CopyFieldValueAsync(
        HeapFieldValueRowViewModel row,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.CanCopyValue)
        {
            return Task.CompletedTask;
        }

        return service.SetTextAsync(row.CopyText, cancellationToken);
    }
}

internal sealed class NullClipboardService : IClipboardService
{
    public Task SetTextAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
