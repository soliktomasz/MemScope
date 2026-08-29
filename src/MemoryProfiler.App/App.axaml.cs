using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MemoryProfiler.App.ViewModels;
using MemoryProfiler.Diagnostics.Processes;

namespace MemoryProfiler.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var processPicker = new ProcessPickerViewModel(new DotNetProcessDiscovery());
            desktop.MainWindow = new MainWindow(new StartViewModel(processPicker));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
