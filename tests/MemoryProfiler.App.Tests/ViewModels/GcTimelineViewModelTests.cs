using MemoryProfiler.App.ViewModels.GcTimeline;
using MemoryProfiler.Contracts.Live;
using Xunit;

namespace MemoryProfiler.App.Tests.ViewModels;

public sealed class GcTimelineViewModelTests
{
    [Fact]
    public void EmptyTimelineDoesNotAlsoReportAnEmptyFilterResult()
    {
        var viewModel = new GcTimelineViewModel(maximumEvents: 10);

        Assert.True(viewModel.HasNoEvents);
        Assert.False(viewModel.HasNoFilteredEvents);
    }

    [Fact]
    public void FiltersEventsByGenerationAndMinimumPause()
    {
        var viewModel = new GcTimelineViewModel(maximumEvents: 10);
        viewModel.Apply(CreateEvent(generation: 0, pauseMilliseconds: 4));
        viewModel.Apply(CreateEvent(generation: 2, pauseMilliseconds: 18));
        viewModel.Apply(CreateEvent(generation: 2, pauseMilliseconds: 72));

        viewModel.SelectedGenerationFilter = "Gen 2";
        viewModel.MinimumPauseMilliseconds = 20;

        var visible = Assert.Single(viewModel.FilteredEvents);
        Assert.Equal(2, visible.Generation);
        Assert.Equal(72, visible.PauseMilliseconds);
        Assert.Equal(1, viewModel.FilteredEventCount);
    }

    [Fact]
    public void FilterRefreshReplacesTheSnapshotWithOnePropertyNotification()
    {
        var viewModel = new GcTimelineViewModel(maximumEvents: 10);
        viewModel.Apply(CreateEvent(generation: 0, pauseMilliseconds: 4));
        viewModel.Apply(CreateEvent(generation: 2, pauseMilliseconds: 18));
        var originalSnapshot = viewModel.FilteredEvents;
        var notifications = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(GcTimelineViewModel.FilteredEvents))
            {
                notifications++;
            }
        };

        viewModel.SelectedGenerationFilter = "Gen 2";

        Assert.NotSame(originalSnapshot, viewModel.FilteredEvents);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void SummaryCountsSurfaceGen2AndIneffectiveCollections()
    {
        var viewModel = new GcTimelineViewModel(maximumEvents: 10);
        viewModel.Apply(CreateEvent(generation: 2, pauseMilliseconds: 18, heapBefore: 1000, heapAfter: 990));
        viewModel.Apply(CreateEvent(generation: 2, pauseMilliseconds: 48, heapBefore: 1000, heapAfter: 700));
        viewModel.Apply(CreateEvent(generation: 0, pauseMilliseconds: 4, heapBefore: 1000, heapAfter: 980));

        Assert.Equal(2, viewModel.Generation2EventCount);
        Assert.Equal(2, viewModel.IneffectiveEventCount);
        Assert.Equal(3, viewModel.FilteredEventCount);
    }

    [Fact]
    public void EventHistoryEvictsTheOldestEventsAtItsCapacity()
    {
        var viewModel = new GcTimelineViewModel(maximumEvents: 3);

        viewModel.Apply(CreateEvent(generation: 0, pauseMilliseconds: 1));
        viewModel.Apply(CreateEvent(generation: 1, pauseMilliseconds: 2));
        viewModel.Apply(CreateEvent(generation: 2, pauseMilliseconds: 3));
        viewModel.Apply(CreateEvent(generation: 0, pauseMilliseconds: 4));

        Assert.Equal([2d, 3d, 4d], viewModel.FilteredEvents.Select(x => x.PauseMilliseconds));
        Assert.Equal(3, viewModel.EventCount);
    }

    [Fact]
    public void FilteringOutTheSelectedEventClearsTheInspector()
    {
        var viewModel = new GcTimelineViewModel(maximumEvents: 10);
        viewModel.Apply(CreateEvent(generation: 0, pauseMilliseconds: 4));
        viewModel.Apply(CreateEvent(generation: 2, pauseMilliseconds: 18));
        viewModel.SelectedEvent = viewModel.FilteredEvents[0];

        viewModel.SelectedGenerationFilter = "Gen 2";

        Assert.Null(viewModel.SelectedEvent);
        Assert.False(viewModel.HasSelection);
    }

    [Fact]
    public void EventRowExposesHeapReductionForTheInspectorChart()
    {
        var row = new GcEventRowViewModel(CreateEvent(
            generation: 2,
            pauseMilliseconds: 48,
            heapBefore: 540 * 1024 * 1024,
            heapAfter: 391 * 1024 * 1024));

        Assert.Equal(2, row.Generation);
        Assert.Equal(48, row.PauseMilliseconds);
        Assert.Equal(149UL * 1024 * 1024, row.ReclaimedBytes);
        Assert.Equal(1d, row.HeapBeforeRatio);
        Assert.InRange(row.HeapAfterRatio, 0.724, 0.725);
        Assert.Contains("540", row.HeapBeforeDisplay);
        Assert.Contains("391", row.HeapAfterDisplay);
    }

    [Fact]
    public void InspectorChartNormalizesHeapGrowthAgainstTheLargerValue()
    {
        var row = new GcEventRowViewModel(CreateEvent(
            generation: 2,
            pauseMilliseconds: 48,
            heapBefore: 400,
            heapAfter: 500));

        Assert.Equal(0.8, row.HeapBeforeRatio);
        Assert.Equal(1, row.HeapAfterRatio);
        Assert.True(row.IsIneffective);
        Assert.Equal("Low", row.EffectivenessDisplay);
    }

    private static GcEvent CreateEvent(
        int generation,
        double pauseMilliseconds,
        ulong heapBefore = 1024,
        ulong heapAfter = 768) =>
        new(
            new DateTimeOffset(2026, 8, 29, 12, 1, 4, TimeSpan.Zero),
            generation,
            TimeSpan.FromMilliseconds(pauseMilliseconds),
            heapBefore,
            heapAfter,
            "Allocation");
}
