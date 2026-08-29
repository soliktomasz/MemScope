using Avalonia.Controls;
using MemoryProfiler.App.ViewModels;

namespace MemoryProfiler.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(StartViewModel viewModel)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }
}
