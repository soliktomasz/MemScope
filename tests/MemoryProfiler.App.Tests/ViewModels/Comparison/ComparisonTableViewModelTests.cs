using System.Globalization;
using System.Windows.Input;
using MemoryProfiler.App.ViewModels.Comparison;
using MemoryProfiler.Contracts.Heap;
using Xunit;

namespace MemoryProfiler.App.Tests.ViewModels.Comparison;

public sealed class ComparisonTableViewModelTests
{
    private static TypeMemoryDelta Delta(
        string typeName,
        long countBefore,
        long countAfter,
        long sizeBefore,
        long sizeAfter,
        long? retainedDelta = null) =>
        new(
            typeName,
            countBefore,
            countAfter,
            countAfter - countBefore,
            sizeBefore,
            sizeAfter,
            sizeAfter - sizeBefore,
            retainedDelta);

    private static ComparisonTableViewModel TableWith(params TypeMemoryDelta[] deltas)
    {
        var table = new ComparisonTableViewModel();
        table.SetDeltas(deltas);
        return table;
    }

    [Fact]
    public void DefaultsToBiggestGrowthFirstAndResetsFilters()
    {
        var table = TableWith(
            Delta("System.String", 381_235, 461_576, 44_200_000, 56_200_000),
            Delta("MyApp.CacheEntry", 50_000, 100_000, 118_400_000, 236_800_000),
            Delta("System.Byte[]", 1_024, 10_000, 67_108_864, 655_360_000));

        Assert.Collection(
            table.FilteredDeltas,
            row => Assert.Equal("System.Byte[]", row.TypeName),
            row => Assert.Equal("MyApp.CacheEntry", row.TypeName),
            row => Assert.Equal("System.String", row.TypeName));
        Assert.Equal(3, table.TotalDeltaCount);
        Assert.Equal(3, table.FilteredDeltaCount);
        Assert.True(table.HasDeltas);
        Assert.True(table.HasFilteredDeltas);
        Assert.False(table.ShowGrowingOnly);
        Assert.False(table.ShowNewTypes);
        Assert.False(table.ShowDisappearedTypes);
        Assert.Equal(string.Empty, table.MinimumDeltaText);
        Assert.Equal(
            $"{3.ToString("N0", CultureInfo.CurrentCulture)} of {3.ToString("N0", CultureInfo.CurrentCulture)} types",
            table.ShownSummary);
    }

    [Fact]
    public void GrowingOnlyKeepsOnlyPositiveSizeDeltas()
    {
        var table = TableWith(
            Delta("System.Byte[]", 1, 10, 1_000, 20_000),
            Delta("System.String", 10, 9, 20_000, 18_000),
            Delta("System.Int32", 5, 5, 100, 100));

        table.ShowGrowingOnly = true;

        var typeName = Assert.Single(table.FilteredDeltas).TypeName;
        Assert.Equal("System.Byte[]", typeName);
    }

    [Fact]
    public void NewTypesKeepsOnlyTypesAbsentFromBefore()
    {
        var table = TableWith(
            Delta("MyApp.LeakedCache", 0, 4_000, 0, 268_000_000),
            Delta("System.String", 100, 110, 1_000, 1_100),
            Delta("MyApp.OldCache", 8_000, 0, 536_000_000, 0));

        table.ShowNewTypes = true;

        var typeName = Assert.Single(table.FilteredDeltas).TypeName;
        Assert.Equal("MyApp.LeakedCache", typeName);
    }

    [Fact]
    public void DisappearedTypesKeepsOnlyTypesAbsentFromAfter()
    {
        var table = TableWith(
            Delta("MyApp.LeakedCache", 0, 4_000, 0, 268_000_000),
            Delta("System.String", 100, 110, 1_000, 1_100),
            Delta("MyApp.OldCache", 8_000, 0, 536_000_000, 0));

        table.ShowDisappearedTypes = true;

        var typeName = Assert.Single(table.FilteredDeltas).TypeName;
        Assert.Equal("MyApp.OldCache", typeName);
    }

    [Fact]
    public void MinimumDeltaKeepsOnlyTypesWhoseSizeChangedByAtLeastTheThreshold()
    {
        var table = TableWith(
            Delta("System.Byte[]", 1, 2, 1_000, 200_000),
            Delta("System.String", 10, 11, 100_000, 100_500),
            Delta("System.Int32", 5, 5, 100, 100));

        table.MinimumDeltaText = "100 KB";

        var typeName = Assert.Single(table.FilteredDeltas).TypeName;
        Assert.Equal("System.Byte[]", typeName);

        // The threshold applies to the magnitude: a big shrink also passes.
        var shrinking = TableWith(
            Delta("MyApp.OldCache", 8_000, 0, 536_000_000, 0),
            Delta("System.String", 10, 11, 100_000, 100_500));
        shrinking.MinimumDeltaText = "100 MB";

        var shrunk = Assert.Single(shrinking.FilteredDeltas).TypeName;
        Assert.Equal("MyApp.OldCache", shrunk);
    }

    [Fact]
    public void FiltersCombineAndEmptyResultsAreTracked()
    {
        var table = TableWith(
            Delta("MyApp.LeakedCache", 0, 4_000, 0, 268_000_000),
            Delta("MyApp.OldCache", 8_000, 0, 536_000_000, 0));

        table.ShowNewTypes = true;
        table.ShowDisappearedTypes = true;

        Assert.Empty(table.FilteredDeltas);
        Assert.True(table.HasNoFilteredDeltas);
        Assert.Equal(
            $"{0.ToString("N0", CultureInfo.CurrentCulture)} of {2.ToString("N0", CultureInfo.CurrentCulture)} types",
            table.ShownSummary);
    }

    [Fact]
    public void SortingTogglesDirectionPerColumn()
    {
        var table = TableWith(
            Delta("System.String", 10, 20, 10_000, 20_000),
            Delta("System.Byte[]", 5, 15, 50_000, 150_000));

        table.SortBy(DeltaSortColumn.SizeDelta);

        // Same column toggles to ascending (smallest growth first).
        Assert.Collection(
            table.FilteredDeltas,
            row => Assert.Equal("System.String", row.TypeName),
            row => Assert.Equal("System.Byte[]", row.TypeName));
        Assert.Equal(DeltaSortDirection.Ascending, table.SortDirection);
        Assert.Equal("Size delta ascending", table.SizeDeltaSortDescription);
        Assert.Equal("Size Δ ↑", table.SizeDeltaHeader);

        table.SortBy(DeltaSortColumn.TypeName);

        Assert.Collection(
            table.FilteredDeltas,
            row => Assert.Equal("System.Byte[]", row.TypeName),
            row => Assert.Equal("System.String", row.TypeName));
    }

    [Fact]
    public void RetainedDeltaSortsNullsLast()
    {
        var table = TableWith(
            Delta("System.String", 1, 2, 1, 2, retainedDelta: 100),
            Delta("System.Byte[]", 1, 2, 1, 2, retainedDelta: null),
            Delta("System.Int32", 1, 2, 1, 2, retainedDelta: 500));

        table.SortBy(DeltaSortColumn.RetainedDelta);

        // Ascending retained delta with nulls last: 100, 500, then N/A.
        Assert.Collection(
            table.FilteredDeltas,
            row => Assert.Equal("System.String", row.TypeName),
            row => Assert.Equal("System.Int32", row.TypeName),
            row => Assert.Equal("System.Byte[]", row.TypeName));
    }

    [Fact]
    public void SetDeltasRaisesCanExecuteChangedOnAllSortCommands()
    {
        var table = new ComparisonTableViewModel();
        var commands = new (ICommand Command, int Raised)[]
        {
            (table.SortByTypeNameCommand, 0),
            (table.SortByCountDeltaCommand, 0),
            (table.SortBySizeDeltaCommand, 0),
            (table.SortByRetainedDeltaCommand, 0),
        };
        foreach (var (command, _) in commands)
        {
            command.CanExecuteChanged += (_, _) =>
            {
                var index = Array.FindIndex(commands, pair => ReferenceEquals(pair.Command, command));
                commands[index] = (command, commands[index].Raised + 1);
            };
        }

        Assert.False(table.SortBySizeDeltaCommand.CanExecute(null));

        table.SetDeltas([Delta("System.String", 10, 20, 10_000, 20_000)]);

        Assert.True(table.SortBySizeDeltaCommand.CanExecute(null));
        Assert.All(
            commands,
            pair => Assert.True(
                pair.Raised > 0,
                $"{pair.Command} did not raise CanExecuteChanged when deltas were set."));
    }

    [Fact]
    public void SetDeltasResetsFiltersAndSortToDefaults()
    {
        var table = TableWith(Delta("System.String", 10, 20, 10_000, 20_000));
        table.ShowGrowingOnly = true;
        table.MinimumDeltaText = "1 MB";
        table.SortBy(DeltaSortColumn.TypeName);

        table.SetDeltas(
        [
            Delta("MyApp.CacheEntry", 50_000, 100_000, 118_400_000, 236_800_000),
            Delta("System.Byte[]", 1_024, 10_000, 67_108_864, 655_360_000),
        ]);

        Assert.False(table.ShowGrowingOnly);
        Assert.False(table.ShowNewTypes);
        Assert.False(table.ShowDisappearedTypes);
        Assert.Equal(string.Empty, table.MinimumDeltaText);
        Assert.Collection(
            table.FilteredDeltas,
            row => Assert.Equal("System.Byte[]", row.TypeName),
            row => Assert.Equal("MyApp.CacheEntry", row.TypeName));
    }
}
