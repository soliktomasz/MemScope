using System.Globalization;
using MemoryProfiler.Analysis.Dominators;
using MemoryProfiler.App.ViewModels;
using MemoryProfiler.App.ViewModels.Retainers;
using MemoryProfiler.Contracts.Heap;
using Xunit;

namespace MemoryProfiler.App.Tests.ViewModels.Retainers;

public sealed class TopRetainersViewModelTests
{
    [Fact]
    public async Task SetResultPublishesTheFirstWindowAndLoadMoreAppends()
    {
        var viewModel = new TopRetainersViewModel(ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await viewModel.SetResultAsync(Result(1_201));

        Assert.Equal(500, viewModel.Retainers.Count);
        Assert.Equal(1_201, viewModel.TotalRetainerCount);
        Assert.True(viewModel.ShowTable);

        viewModel.LoadMoreCommand.Execute(null);
        Assert.Equal(1_000, viewModel.Retainers.Count);
    }

    [Fact]
    public async Task TypeSearchIsOrdinalIgnoreCase()
    {
        var viewModel = new TopRetainersViewModel(ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await viewModel.SetResultAsync(Result(100));
        viewModel.SearchText = "cacheentry42";
        await viewModel.ApplySearchAsync();

        var row = Assert.Single(viewModel.Retainers);
        Assert.Equal("MyApp.CacheEntry42", row.TypeName);
        Assert.True(viewModel.ShowTable);
    }

    [Fact]
    public async Task AddressSearchMatchesTheCanonicalAddress()
    {
        var viewModel = new TopRetainersViewModel(ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        var result = Result(100);
        await viewModel.SetResultAsync(result);
        var target = result.Dominators[0];
        var canonical = target.ObjectAddress.ToString("X12", CultureInfo.InvariantCulture);

        viewModel.SearchText = canonical;
        await viewModel.ApplySearchAsync();

        var row = Assert.Single(viewModel.Retainers);
        Assert.Equal(target.ObjectAddress, row.Address);
    }

    [Fact]
    public async Task SearchWithNoMatchesExposesTheEmptyState()
    {
        var viewModel = new TopRetainersViewModel(ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await viewModel.SetResultAsync(Result(100));
        viewModel.SearchText = "no-such-type";
        await viewModel.ApplySearchAsync();

        Assert.Empty(viewModel.Retainers);
        Assert.True(viewModel.ShowEmpty);
        Assert.False(viewModel.ShowTable);
    }

    [Fact]
    public async Task BeginLoadingPublishesTheLoadingStateAndClearsRows()
    {
        var viewModel = new TopRetainersViewModel(ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;
        await viewModel.SetResultAsync(Result(10));

        await viewModel.BeginLoadingAsync();

        Assert.True(viewModel.IsLoading);
        Assert.True(viewModel.ShowLoading);
        Assert.False(viewModel.ShowTable);
        Assert.Empty(viewModel.Retainers);
    }

    [Fact]
    public async Task SetUnavailablePublishesTheUnavailableStateAndClearsRows()
    {
        var viewModel = new TopRetainersViewModel(ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;
        await viewModel.SetResultAsync(Result(10));

        await viewModel.SetUnavailableAsync();

        Assert.True(viewModel.IsUnavailable);
        Assert.True(viewModel.ShowUnavailable);
        Assert.False(viewModel.ShowTable);
        Assert.Empty(viewModel.Retainers);
    }

    [Fact]
    public async Task ClearAsyncDropsTheResultAndSelection()
    {
        var viewModel = new TopRetainersViewModel(ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;
        await viewModel.SetResultAsync(Result(10));
        viewModel.SelectedRetainer = viewModel.Retainers[0];

        await viewModel.ClearAsync();

        Assert.Empty(viewModel.Retainers);
        Assert.Null(viewModel.SelectedRetainer);
        Assert.False(viewModel.ShowTable);
        Assert.True(viewModel.ShowIdle);
    }

    [Fact]
    public async Task SupersededSearchCannotPublishAStaleResult()
    {
        var viewModel = new TopRetainersViewModel(ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;
        await viewModel.SetResultAsync(Result(200));

        var first = viewModel.ApplySearchAsync();
        viewModel.SearchText = "cacheentry199";
        var second = viewModel.ApplySearchAsync();
        await second;
        await first;

        var row = Assert.Single(viewModel.Retainers);
        Assert.Equal("MyApp.CacheEntry199", row.TypeName);
    }

    private static DominatorAnalysisResult Result(int count)
    {
        var dominators = Enumerable.Range(0, count)
            .Select(index => new DominatorInfo(
                (ulong)(0x1000 + index * 0x100),
                $"MyApp.CacheEntry{index}",
                (ulong)(64 + index),
                (ulong)(800 + index * 10),
                index + 1))
            .OrderByDescending(item => item.RetainedSize)
            .ThenBy(item => item.TypeName, StringComparer.Ordinal)
            .ToArray();
        return new DominatorAnalysisResult(dominators, []);
    }
}
