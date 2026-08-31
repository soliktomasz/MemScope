namespace MemoryProfiler.App.Navigation;

public sealed class InvestigationNavigationService
{
    private readonly Stack<InvestigationLocation> _back = new();
    private readonly Stack<InvestigationLocation> _forward = new();

    public event EventHandler? StateChanged;

    public InvestigationLocation? CurrentLocation { get; private set; }

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public void Reset(InvestigationLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        _back.Clear();
        _forward.Clear();
        CurrentLocation = location;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Navigate(InvestigationLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (location == CurrentLocation)
        {
            return;
        }

        if (CurrentLocation is not null)
        {
            _back.Push(CurrentLocation);
        }

        CurrentLocation = location;
        _forward.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void GoBack()
    {
        if (!CanGoBack)
        {
            return;
        }

        if (CurrentLocation is not null)
        {
            _forward.Push(CurrentLocation);
        }

        CurrentLocation = _back.Pop();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void GoForward()
    {
        if (!CanGoForward)
        {
            return;
        }

        if (CurrentLocation is not null)
        {
            _back.Push(CurrentLocation);
        }

        CurrentLocation = _forward.Pop();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
