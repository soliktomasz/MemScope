using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace MemoryProfiler.App.Services;

internal sealed class AvaloniaDumpFilePicker(
    Func<TopLevel?> topLevelAccessor) : IDumpFilePicker
{
    public async Task<string?> PickAsync()
    {
        var topLevel = topLevelAccessor()
            ?? throw new InvalidOperationException("The application window is not available.");
        var storageProvider = topLevel.StorageProvider;
        if (!storageProvider.CanOpen)
        {
            throw new NotSupportedException(
                "The current platform does not provide a file picker.");
        }

        var files = await storageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                AllowMultiple = false,
                Title = "Open memory dump",
                FileTypeFilter =
                [
                    new FilePickerFileType("Dump files")
                    {
                        Patterns = ["*.dmp", "*.core", "*.dmp.gz"]
                    },
                    FilePickerFileTypes.All
                ]
            });
        try
        {
            return files.FirstOrDefault()?.Path.LocalPath;
        }
        finally
        {
            foreach (var file in files)
            {
                file.Dispose();
            }
        }
    }
}
