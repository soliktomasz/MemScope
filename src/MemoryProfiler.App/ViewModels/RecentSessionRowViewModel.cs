using System.Globalization;
using System.Windows.Input;

namespace MemoryProfiler.App.ViewModels;

public enum RecentSessionKind
{
    Snapshot,
    Comparison
}

public sealed class RecentSessionRowViewModel
{
    private readonly Func<Task> _open;

    internal RecentSessionRowViewModel(
        RecentSessionKind kind,
        string title,
        string details,
        string pathDisplay,
        DateTimeOffset timestamp,
        Func<Task> open)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(open);
        Kind = kind;
        Title = title;
        Details = details;
        PathDisplay = pathDisplay;
        Timestamp = timestamp;
        TimestampDisplay = timestamp
            .ToLocalTime()
            .ToString("g", CultureInfo.CurrentCulture);
        AutomationName = $"Open {title}";
        _open = open;
        OpenCommand = new AsyncCommand(OpenAsync);
    }

    public RecentSessionKind Kind { get; }

    public string Title { get; }

    public string Details { get; }

    public string PathDisplay { get; }

    public DateTimeOffset Timestamp { get; }

    public string TimestampDisplay { get; }

    public string AutomationName { get; }

    public ICommand OpenCommand { get; }

    public Task OpenAsync() => _open();
}
