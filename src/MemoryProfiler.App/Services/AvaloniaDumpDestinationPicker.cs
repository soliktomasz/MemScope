using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace MemoryProfiler.App.Services;

internal sealed class AvaloniaDumpDestinationPicker(
    Func<TopLevel?> topLevelAccessor) : IDumpDestinationPicker
{
    public async Task<string?> PickAsync()
    {
        var topLevel = topLevelAccessor()
            ?? throw new InvalidOperationException("The application window is not available.");
        var storageProvider = topLevel.StorageProvider;
        if (!storageProvider.CanPickFolder)
        {
            throw new NotSupportedException(
                "The current platform does not provide a destination folder picker.");
        }

        var folders = await storageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                AllowMultiple = false,
                Title = "Choose snapshot destination"
            });
        try
        {
            return folders.FirstOrDefault()?.Path.LocalPath;
        }
        finally
        {
            foreach (var folder in folders)
            {
                folder.Dispose();
            }
        }
    }
}
