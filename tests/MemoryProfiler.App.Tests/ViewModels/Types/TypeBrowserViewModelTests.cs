using System.Globalization;
using MemoryProfiler.App.ViewModels.Types;
using MemoryProfiler.Contracts.Heap;
using Xunit;

namespace MemoryProfiler.App.Tests.ViewModels.Types;

public sealed class TypeBrowserViewModelTests
{
    private static HeapTypeInfo Type(
        ulong methodTable,
        string name,
        string assembly,
        long count,
        ulong shallowSize,
        ulong? retainedSize = null) =>
        new(methodTable, name, assembly, count, shallowSize, retainedSize);

    private static readonly HeapTypeInfo[] SampleTypes =
    [
        Type(0x1000, "System.String", "System.Private.CoreLib", 381_235, 44_200_000, null),
        Type(0x2000, "System.Byte[]", "System.Private.CoreLib", 83_291, 91_800_000, null),
        Type(0x3000, "MyCompany.Cache.CacheEntry", "MyCompany.Cache", 50_000, 118_400_000, null),
        Type(0x4000, "MyCompany.Cache.Index", "MyCompany.Cache", 2_000, 1_500_000, null),
        Type(0x5000, "MyCompany.Core.Session", "MyCompany.Core", 120, 96_000, null)
    ];

    [Fact]
    public void DefaultSortShowsLargestShallowSizeFirst()
    {
        var browser = new TypeBrowserViewModel();
        browser.SetTypes(SampleTypes);

        Assert.Equal(TypeSortColumn.ShallowSize, browser.SortColumn);
        Assert.Equal(TypeSortDirection.Descending, browser.SortDirection);
        Assert.Equal(
            ["MyCompany.Cache.CacheEntry", "System.Byte[]", "System.String", "MyCompany.Cache.Index", "MyCompany.Core.Session"],
            browser.FilteredTypes.Select(row => row.TypeName));
        Assert.Equal(5, browser.TotalTypeCount);
        Assert.Equal(5, browser.FilteredTypeCount);
        Assert.Equal("5 of 5 types", browser.ShownSummary);
    }

    [Fact]
    public void SortingTogglesDirectionAndUpdatesHeaders()
    {
        var browser = new TypeBrowserViewModel();
        browser.SetTypes(SampleTypes);

        browser.SortBy(TypeSortColumn.ShallowSize);

        Assert.Equal(TypeSortDirection.Ascending, browser.SortDirection);
        Assert.Equal("Shallow size ascending", browser.ShallowSizeSortDescription);
        Assert.Equal("Shallow Size ↑", browser.ShallowSizeHeader);

        browser.SortBy(TypeSortColumn.ShallowSize);

        Assert.Equal(TypeSortDirection.Descending, browser.SortDirection);
        Assert.Equal("Shallow Size ↓", browser.ShallowSizeHeader);
    }

    [Fact]
    public void SortingByNameIsCaseInsensitiveAndStable()
    {
        var browser = new TypeBrowserViewModel();
        browser.SetTypes(
        [
            Type(0x1, "zeta.B", "A", 1, 1),
            Type(0x2, "Alpha.A", "A", 1, 1),
            Type(0x3, "alpha.B", "A", 1, 1)
        ]);

        browser.SortBy(TypeSortColumn.TypeName);

        Assert.Equal(["Alpha.A", "alpha.B", "zeta.B"], browser.FilteredTypes.Select(row => row.TypeName));
        Assert.Equal("Type ascending", browser.TypeNameSortDescription);
    }

    [Fact]
    public void SortingByCountUsesNumericOrder()
    {
        var browser = new TypeBrowserViewModel();
        browser.SetTypes(SampleTypes);

        browser.SortBy(TypeSortColumn.ObjectCount);

        Assert.Equal(
            ["MyCompany.Core.Session", "MyCompany.Cache.Index", "MyCompany.Cache.CacheEntry", "System.Byte[]", "System.String"],
            browser.FilteredTypes.Select(row => row.TypeName));
    }

    [Fact]
    public void SortingByAssemblyGroupsAssembliesThenTypes()
    {
        var browser = new TypeBrowserViewModel();
        browser.SetTypes(SampleTypes);

        browser.SortBy(TypeSortColumn.AssemblyName);

        Assert.Equal(
            ["MyCompany.Cache.CacheEntry", "MyCompany.Cache.Index", "MyCompany.Core.Session", "System.Byte[]", "System.String"],
            browser.FilteredTypes.Select(row => row.TypeName));
    }

    [Fact]
    public void SortingByRetainedSizeKeepsUnavailableRowsLast()
    {
        var browser = new TypeBrowserViewModel();
        browser.SetTypes(
        [
            Type(0x1, "WithRetained", "A", 1, 10, 500),
            Type(0x2, "WithoutRetained", "A", 1, 10, null)
        ]);

        browser.SortBy(TypeSortColumn.RetainedSize);

        Assert.Equal(["WithRetained", "WithoutRetained"], browser.FilteredTypes.Select(row => row.TypeName));
    }

    [Fact]
    public void SearchNarrowsTheTableImmediately()
    {
        var browser = new TypeBrowserViewModel();
        browser.SetTypes(SampleTypes);

        browser.SearchText = "MyCompany.Cache";

        Assert.Equal(2, browser.FilteredTypeCount);
        Assert.Equal(
            ["MyCompany.Cache.CacheEntry", "MyCompany.Cache.Index"],
            browser.FilteredTypes.Select(row => row.TypeName));
        Assert.True(browser.HasNoFilteredTypes == false);
    }

    [Fact]
    public void SearchIsCaseInsensitiveAndTrimsWhitespace()
    {
        var browser = new TypeBrowserViewModel();
        browser.SetTypes(SampleTypes);

        browser.SearchText = "  system.string  ";

        Assert.Equal("System.String", Assert.Single(browser.FilteredTypes).TypeName);
    }

    [Fact]
    public void AssemblyFilterRestrictsToTheSelectedAssembly()
    {
        var browser = new TypeBrowserViewModel();
        browser.SetTypes(SampleTypes);

        browser.SelectedAssemblyFilter = "MyCompany.Cache";

        Assert.Equal(2, browser.FilteredTypeCount);
        Assert.All(browser.FilteredTypes, row => Assert.Equal("MyCompany.Cache", row.AssemblyName));
    }

    [Fact]
    public void AssemblyFiltersListDistinctAssembliesAlphabetically()
    {
        var browser = new TypeBrowserViewModel();
        browser.SetTypes(SampleTypes);

        Assert.Equal(
            ["All assemblies", "MyCompany.Cache", "MyCompany.Core", "System.Private.CoreLib"],
            browser.AssemblyFilters);
    }

    [Fact]
    public void MinimumSizeFilterAcceptsBytesAndUnitSuffixes()
    {
        var browser = new TypeBrowserViewModel();
        browser.SetTypes(SampleTypes);

        browser.MinimumSizeText = "100 MB";

        Assert.Equal(
            ["MyCompany.Cache.CacheEntry"],
            browser.FilteredTypes.Select(row => row.TypeName));
    }

    [Fact]
    public void MinimumSizeFilterTreatsInvalidInputAsNoFilter()
    {
        var browser = new TypeBrowserViewModel();
        browser.SetTypes(SampleTypes);

        browser.MinimumSizeText = "not-a-size";

        Assert.Equal(5, browser.FilteredTypeCount);
    }

    [Fact]
    public void FiltersCombine()
    {
        var browser = new TypeBrowserViewModel();
        browser.SetTypes(SampleTypes);

        browser.SearchText = "MyCompany";
        browser.SelectedAssemblyFilter = "MyCompany.Cache";
        browser.MinimumSizeText = "50 MB";

        Assert.Equal("MyCompany.Cache.CacheEntry", Assert.Single(browser.FilteredTypes).TypeName);
    }

    [Fact]
    public void NoMatchesStateIsExposed()
    {
        var browser = new TypeBrowserViewModel();
        browser.SetTypes(SampleTypes);

        browser.SearchText = "NoSuchNamespace";

        Assert.True(browser.HasTypes);
        Assert.False(browser.HasFilteredTypes);
        Assert.True(browser.HasNoFilteredTypes);
        Assert.Equal("0 of 5 types", browser.ShownSummary);
    }

    [Fact]
    public void EmptySnapshotExposesNoTypesState()
    {
        var browser = new TypeBrowserViewModel();

        browser.SetTypes([]);

        Assert.True(browser.HasNoTypes);
        Assert.False(browser.HasFilteredTypes);
        Assert.Equal(string.Empty, browser.ShownSummary);
        Assert.Equal(["All assemblies"], browser.AssemblyFilters);
    }

    [Fact]
    public void RowsFormatCountSizesAndUnavailableRetainedSize()
    {
        var browser = new TypeBrowserViewModel();
        browser.SetTypes([Type(0x1000, "MyApp.CacheEntry", "MyApp", 50_000, 118_400_000, null)]);

        var row = Assert.Single(browser.FilteredTypes);

        Assert.Equal(50_000.ToString("N0", CultureInfo.CurrentCulture), row.CountDisplay);
        Assert.Equal(FormatBytes(118_400_000), row.ShallowSizeDisplay);
        Assert.True(row.IsRetainedSizeUnavailable);
        Assert.Equal("N/A", row.RetainedSizeDisplay);
        Assert.Equal("MyApp", row.AssemblyName);
    }

    [Fact]
    public void RowsFormatAvailableRetainedSize()
    {
        var browser = new TypeBrowserViewModel();
        browser.SetTypes([Type(0x1000, "MyApp.GlobalCache", "MyApp", 1, 4_096, 440_401_920)]);

        var row = Assert.Single(browser.FilteredTypes);

        Assert.True(row.IsRetainedSizeAvailable);
        Assert.Equal(FormatBytes(440_401_920), row.RetainedSizeDisplay);
    }

    [Fact]
    public void SetRetainedSizesFillsRowsInPlaceWithoutResettingFilters()
    {
        var browser = new TypeBrowserViewModel();
        browser.SetTypes(SampleTypes);
        browser.SearchText = "MyCompany";
        browser.SortBy(TypeSortColumn.ObjectCount);

        browser.SetRetainedSizes(
        [
            new TypeRetainedSize(0x1000, "System.String", 44_200_000),
            new TypeRetainedSize(0x2000, "System.Byte[]", 91_800_000),
            new TypeRetainedSize(0x3000, "MyCompany.Cache.CacheEntry", 118_400_000),
            new TypeRetainedSize(0x4000, "MyCompany.Cache.Index", 1_500_000),
            new TypeRetainedSize(0x5000, "MyCompany.Core.Session", 96_000),
        ]);

        Assert.Equal("MyCompany", browser.SearchText);
        Assert.Equal(TypeSortColumn.ObjectCount, browser.SortColumn);
        var row = browser.FilteredTypes.Single(type => type.TypeName == "MyCompany.Cache.CacheEntry");
        Assert.True(row.IsRetainedSizeAvailable);
        Assert.Equal(FormatBytes(118_400_000), row.RetainedSizeDisplay);
    }

    [Fact]
    public void SetRetainedSizesReordersAnActiveRetainedSizeSort()
    {
        var browser = new TypeBrowserViewModel();
        browser.SetTypes(
        [
            Type(0x1, "Large", "A", 1, 10, null),
            Type(0x2, "Small", "A", 1, 10, null),
        ]);
        browser.SortBy(TypeSortColumn.RetainedSize);
        browser.SortBy(TypeSortColumn.RetainedSize);
        Assert.Equal(TypeSortDirection.Descending, browser.SortDirection);
        Assert.Equal(["Large", "Small"], browser.FilteredTypes.Select(row => row.TypeName));

        browser.SetRetainedSizes(
        [
            new TypeRetainedSize(0x1, "Large", 500),
            new TypeRetainedSize(0x2, "Small", 5_000),
        ]);

        Assert.Equal(["Small", "Large"], browser.FilteredTypes.Select(row => row.TypeName));
    }

    [Fact]
    public void SetRetainedSizesLeavesTypesWithoutResultsUnavailable()
    {
        var browser = new TypeBrowserViewModel();
        browser.SetTypes([Type(0x1, "Known", "A", 1, 10)]);

        browser.SetRetainedSizes([new TypeRetainedSize(0x9999, "Other", 100)]);

        var row = Assert.Single(browser.FilteredTypes);
        Assert.True(row.IsRetainedSizeUnavailable);
        Assert.Equal("N/A", row.RetainedSizeDisplay);
    }

    private static string FormatBytes(ulong value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var amount = (double)value;
        var unitIndex = 0;
        while (amount >= 1024 && unitIndex < units.Length - 1)
        {
            amount /= 1024;
            unitIndex++;
        }

        var format = amount >= 100 || unitIndex == 0 ? "N0" : "N1";
        return $"{amount.ToString(format, CultureInfo.CurrentCulture)} {units[unitIndex]}";
    }

    [Fact]
    public void UnknownAssemblyDisplaysAsUnknownAssembly()
    {
        var browser = new TypeBrowserViewModel();
        browser.SetTypes([new HeapTypeInfo(0x1000, "Some.Type", null, 1, 16, null)]);

        Assert.Equal("Unknown assembly", Assert.Single(browser.FilteredTypes).AssemblyName);
        Assert.Equal(["All assemblies", "Unknown assembly"], browser.AssemblyFilters);
    }

    [Fact]
    public void SetTypesResetsFiltersAndSort()
    {
        var browser = new TypeBrowserViewModel();
        browser.SetTypes(SampleTypes);
        browser.SearchText = "MyCompany";
        browser.SelectedAssemblyFilter = "MyCompany.Cache";
        browser.MinimumSizeText = "50 MB";
        browser.SortBy(TypeSortColumn.ObjectCount);

        browser.SetTypes(
        [
            Type(0x9000, "Other.Widget", "Other", 10, 1_000)
        ]);

        Assert.Equal(string.Empty, browser.SearchText);
        Assert.Equal(string.Empty, browser.MinimumSizeText);
        Assert.Equal(TypeBrowserViewModel.AllAssemblies, browser.SelectedAssemblyFilter);
        Assert.Equal(TypeSortColumn.ShallowSize, browser.SortColumn);
        Assert.Equal(TypeSortDirection.Descending, browser.SortDirection);
        Assert.Equal("Other.Widget", Assert.Single(browser.FilteredTypes).TypeName);
    }

    [Fact]
    public void SelectionSurvivesAReSortButIsClearedWhenFilteredOut()
    {
        var browser = new TypeBrowserViewModel();
        browser.SetTypes(SampleTypes);
        var selected = browser.FilteredTypes.Single(row => row.TypeName == "System.String");
        browser.SelectedType = selected;

        browser.SortBy(TypeSortColumn.ObjectCount);

        Assert.Same(selected, browser.SelectedType);

        browser.SearchText = "MyCompany.Cache";

        Assert.Null(browser.SelectedType);
    }
}
