using System.Windows.Input;

namespace MemoryProfiler.App.ViewModels;

public sealed class StartViewModel : ViewModelBase, IDisposable
{
    private readonly AsyncCommand _attachToProcessCommand;
    private bool _isProcessPickerVisible;
    private bool _isDisposed;

    public StartViewModel(ProcessPickerViewModel processPicker)
    {
        ArgumentNullException.ThrowIfNull(processPicker);
        ProcessPicker = processPicker;
        _attachToProcessCommand = new AsyncCommand(ShowProcessPickerAsync);
    }

    public ProcessPickerViewModel ProcessPicker { get; }

    public ICommand AttachToProcessCommand => _attachToProcessCommand;

    public bool IsProcessPickerVisible
    {
        get => _isProcessPickerVisible;
        private set => SetProperty(ref _isProcessPickerVisible, value);
    }

    public async Task ShowProcessPickerAsync()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        IsProcessPickerVisible = true;
        await ProcessPicker.RefreshAsync();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        ProcessPicker.Dispose();
    }
}
