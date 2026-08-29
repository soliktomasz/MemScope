using Avalonia.Controls;
using MemoryProfiler.App.ViewModels;

namespace MemoryProfiler.App;

public partial class MainWindow : Window
{
    private StartViewModel? _viewModel;
    private bool _shutdownStarted;
    private bool _shutdownComplete;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(StartViewModel viewModel)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    protected override void OnClosing(WindowClosingEventArgs eventArgs)
    {
        if (_viewModel is not null && !_shutdownComplete)
        {
            eventArgs.Cancel = true;
            if (!_shutdownStarted)
            {
                _shutdownStarted = true;
                _ = CompleteShutdownAsync();
            }
        }

        base.OnClosing(eventArgs);
    }

    private async Task CompleteShutdownAsync()
    {
        try
        {
            if (_viewModel is not null)
            {
                await _viewModel.DisposeAsync();
            }
        }
        catch
        {
            // Shutdown must continue even if the target disappears during cleanup.
        }
        finally
        {
            _shutdownComplete = true;
            Close();
        }
    }
}
