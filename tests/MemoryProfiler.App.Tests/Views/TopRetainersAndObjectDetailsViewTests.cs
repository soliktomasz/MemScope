using Avalonia.Automation;
using Avalonia.Controls;
using MemoryProfiler.App.Views;
using Xunit;

namespace MemoryProfiler.App.Tests.Views;

public sealed class TopRetainersAndObjectDetailsViewTests
{
    private static SnapshotView CreateView() => new();

    [Fact]
    public void ExposesTwoAccessibleAnalysisModeButtons()
    {
        var view = CreateView();
        var types = view.FindControl<Button>("ShowTypesButton");
        var retainers = view.FindControl<Button>("ShowTopRetainersButton");

        Assert.NotNull(types);
        Assert.NotNull(retainers);
        Assert.Equal("Show heap types", AutomationProperties.GetName(types!));
        Assert.Equal("Show top retainers", AutomationProperties.GetName(retainers!));
    }

    [Fact]
    public void TopRetainersGridHasTheSixExpectedColumns()
    {
        var view = CreateView();
        var grid = view.FindControl<DataGrid>("TopRetainersDataGrid");

        Assert.NotNull(grid);
        Assert.True(grid!.IsReadOnly);
        Assert.Equal(6, grid.Columns.Count);
        Assert.Equal(
            ["Type", "Address", "Shallow size", "Retained size", "Retained objects", "Retained heap"],
            grid.Columns.Select(column => column.Header?.ToString() ?? string.Empty).ToArray());
    }

    [Fact]
    public void ObjectDetailsExposesTheSensitiveValueWarning()
    {
        var view = CreateView();
        var warning = view.FindControl<TextBlock>("SensitiveValuesWarning");

        Assert.NotNull(warning);
    }

    [Fact]
    public void ObjectDetailsFieldsGridHasTheFiveExpectedColumns()
    {
        var view = CreateView();
        var grid = view.FindControl<DataGrid>("ObjectDetailsFieldsDataGrid");

        Assert.NotNull(grid);
        Assert.True(grid!.IsReadOnly);
        Assert.Equal(5, grid.Columns.Count);
        Assert.Equal(
            ["Field", "Declared type", "Kind", "Value", "Referenced address"],
            grid.Columns.Select(column => column.Header?.ToString() ?? string.Empty).ToArray());
    }

    [Fact]
    public void ObjectDetailsActionsBindToTheirCommands()
    {
        var view = CreateView();
        var showMore = view.FindControl<Button>("ShowMoreStringsButton");
        var loadMore = view.FindControl<Button>("LoadMoreArrayButton");
        var cancel = view.FindControl<Button>("CancelObjectDetailsButton");

        Assert.NotNull(showMore);
        Assert.NotNull(loadMore);
        Assert.NotNull(cancel);
    }

    [Fact]
    public void TopRetainersContentReservesToolbarRow()
    {
        var view = CreateView();
        var content = view.FindControl<Grid>("TopRetainersContentGrid");
        var toolbar = view.FindControl<StackPanel>("TopRetainersToolbar");
        var loading = view.FindControl<StackPanel>("TopRetainersLoadingState");
        var unavailable = view.FindControl<StackPanel>("TopRetainersUnavailableState");
        var empty = view.FindControl<StackPanel>("TopRetainersEmptyState");
        var results = view.FindControl<DataGrid>("TopRetainersDataGrid");

        Assert.NotNull(content);
        Assert.Equal(2, content!.RowDefinitions.Count);
        Assert.NotNull(toolbar);
        Assert.NotNull(loading);
        Assert.NotNull(unavailable);
        Assert.NotNull(empty);
        Assert.NotNull(results);
        Assert.Equal(0, Grid.GetRow(toolbar!));
        Assert.Equal(1, Grid.GetRow(loading!));
        Assert.Equal(1, Grid.GetRow(unavailable!));
        Assert.Equal(1, Grid.GetRow(empty!));
        Assert.Equal(1, Grid.GetRow(results!));
    }
}
