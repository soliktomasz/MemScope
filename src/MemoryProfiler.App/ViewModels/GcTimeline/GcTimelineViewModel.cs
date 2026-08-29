using System.Collections.ObjectModel;
using MemoryProfiler.Contracts.Live;

namespace MemoryProfiler.App.ViewModels.GcTimeline;

public sealed class GcTimelineViewModel : ViewModelBase
{
    public const string AllGenerations = "All generations";

    private static readonly IReadOnlyList<string> AvailableGenerationFilters =
        [AllGenerations, "Gen 0", "Gen 1", "Gen 2"];

    private readonly int _maximumEvents;
    private readonly List<GcEventRowViewModel> _events = [];
    private ObservableCollection<GcEventRowViewModel> _filteredEvents = [];
    private ReadOnlyObservableCollection<GcEventRowViewModel> _filteredEventsView;
    private string _selectedGenerationFilter = AllGenerations;
    private double _minimumPauseMilliseconds;
    private GcEventRowViewModel? _selectedEvent;
    private int _generation2EventCount;
    private int _ineffectiveEventCount;

    public GcTimelineViewModel(int maximumEvents)
    {
        if (maximumEvents <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEvents));
        }

        _maximumEvents = maximumEvents;
        _filteredEventsView = new ReadOnlyObservableCollection<GcEventRowViewModel>(_filteredEvents);
    }

    public IReadOnlyList<string> GenerationFilters => AvailableGenerationFilters;

    public ReadOnlyObservableCollection<GcEventRowViewModel> FilteredEvents => _filteredEventsView;

    public int EventCount => _events.Count;

    public int FilteredEventCount => _filteredEvents.Count;

    public int Generation2EventCount => _generation2EventCount;

    public int IneffectiveEventCount => _ineffectiveEventCount;

    public bool HasEvents => EventCount > 0;

    public bool HasNoEvents => !HasEvents;

    public bool HasFilteredEvents => _filteredEvents.Count > 0;

    public bool HasNoFilteredEvents => HasEvents && !HasFilteredEvents;

    public bool HasSelection => SelectedEvent is not null;

    public bool HasNoSelection => !HasSelection;

    public string SelectedGenerationFilter
    {
        get => _selectedGenerationFilter;
        set
        {
            var filter = AvailableGenerationFilters.Contains(value) ? value : AllGenerations;
            if (SetProperty(ref _selectedGenerationFilter, filter))
            {
                RebuildFilter();
            }
        }
    }

    public double MinimumPauseMilliseconds
    {
        get => _minimumPauseMilliseconds;
        set
        {
            var threshold = double.IsFinite(value) ? Math.Max(0, value) : 0;
            if (SetProperty(ref _minimumPauseMilliseconds, threshold))
            {
                RebuildFilter();
            }
        }
    }

    public GcEventRowViewModel? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (SetProperty(ref _selectedEvent, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(HasNoSelection));
            }
        }
    }

    public void Apply(GcEvent gcEvent)
    {
        ArgumentNullException.ThrowIfNull(gcEvent);
        var row = new GcEventRowViewModel(gcEvent);
        _events.Add(row);
        if (row.Generation == 2)
        {
            _generation2EventCount++;
        }

        if (row.IsIneffective)
        {
            _ineffectiveEventCount++;
        }

        if (Matches(row))
        {
            _filteredEvents.Add(row);
        }

        if (_events.Count > _maximumEvents)
        {
            var evicted = _events[0];
            _events.RemoveAt(0);
            if (evicted.Generation == 2)
            {
                _generation2EventCount--;
            }

            if (evicted.IsIneffective)
            {
                _ineffectiveEventCount--;
            }

            _filteredEvents.Remove(evicted);
            if (ReferenceEquals(SelectedEvent, evicted))
            {
                SelectedEvent = null;
            }
        }

        OnPropertyChanged(nameof(EventCount));
        OnPropertyChanged(nameof(FilteredEventCount));
        OnPropertyChanged(nameof(Generation2EventCount));
        OnPropertyChanged(nameof(IneffectiveEventCount));
        OnPropertyChanged(nameof(HasEvents));
        OnPropertyChanged(nameof(HasNoEvents));
        OnPropertyChanged(nameof(HasFilteredEvents));
        OnPropertyChanged(nameof(HasNoFilteredEvents));
    }

    private void RebuildFilter()
    {
        _filteredEvents = new ObservableCollection<GcEventRowViewModel>(_events.Where(Matches));
        _filteredEventsView = new ReadOnlyObservableCollection<GcEventRowViewModel>(_filteredEvents);
        OnPropertyChanged(nameof(FilteredEvents));

        if (SelectedEvent is not null && !_filteredEvents.Contains(SelectedEvent))
        {
            SelectedEvent = null;
        }

        OnPropertyChanged(nameof(HasFilteredEvents));
        OnPropertyChanged(nameof(HasNoFilteredEvents));
        OnPropertyChanged(nameof(FilteredEventCount));
    }

    private bool Matches(GcEventRowViewModel row)
    {
        var generationMatches = SelectedGenerationFilter == AllGenerations ||
                                SelectedGenerationFilter == row.GenerationDisplay;
        return generationMatches && row.PauseMilliseconds >= MinimumPauseMilliseconds;
    }
}
