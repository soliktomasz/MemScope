using Avalonia.Controls;
using Avalonia.Input;
using MemoryProfiler.App.ViewModels;
using MemoryProfiler.App.ViewModels.Objects;

namespace MemoryProfiler.App.Views;

public partial class SnapshotView : UserControl
{
    public SnapshotView()
    {
        InitializeComponent();
    }

    private void OnReferencesListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control { DataContext: ObjectReferenceRowViewModel { CanNavigate: true } row } &&
            DataContext is SnapshotViewModel viewModel)
        {
            viewModel.ShowOutgoingReferences(row);
        }
    }

    private void OnPathsListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control { DataContext: GcRootRowViewModel { CanNavigate: true } row } &&
            DataContext is SnapshotViewModel viewModel)
        {
            viewModel.ShowOutgoingReferences(row);
        }
    }
}
