using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.Objects;
using MemoryProfiler.App.Services;
using MemoryProfiler.App.ViewModels;
using MemoryProfiler.Diagnostics.Dumps;
using MemoryProfiler.Diagnostics.Processes;
using MemoryProfiler.Diagnostics.Sessions;

namespace MemoryProfiler.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var processPicker = new ProcessPickerViewModel(new DotNetProcessDiscovery());
            MainWindow? mainWindow = null;
            var destinationPicker = new AvaloniaDumpDestinationPicker(() => mainWindow);
            var dumpFilePicker = new AvaloniaDumpFilePicker(() => mainWindow);
            var viewModel = new StartViewModel(
                processPicker,
                new LiveDiagnosticsSessionFactory(),
                AvaloniaUiDispatcher.Instance,
                new DumpCaptureService(),
                destinationPicker,
                new ClrMdHeapSnapshotLoader(),
                dumpFilePicker,
                new ClrMdHeapObjectRepository());
            mainWindow = new MainWindow(viewModel);
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
