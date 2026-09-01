using System.Runtime.CompilerServices;
using MemoryProfiler.App.Navigation;
using MemoryProfiler.App.Models;
using MemoryProfiler.TestInfrastructure;
using Xunit;

namespace MemoryProfiler.App.Tests.Navigation;

[Collection("Profiler memory")]
public sealed class InvestigationNavigationServiceTests
{
    [Fact]
    public void NavigateBuildsBackHistoryAndRaisesStateChanged()
    {
        var navigation = new InvestigationNavigationService();
        var changes = 0;
        navigation.StateChanged += (_, _) => changes++;

        navigation.Navigate(new TypesLocation());
        navigation.Navigate(new TypeLocation(0x1000));

        Assert.Equal(new TypeLocation(0x1000), navigation.CurrentLocation);
        Assert.True(navigation.CanGoBack);
        Assert.False(navigation.CanGoForward);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void BackAndForwardRestoreLocations()
    {
        var navigation = new InvestigationNavigationService();
        var types = new TypesLocation();
        var type = new TypeLocation(0x1000);
        var references = new ObjectReferencesLocation(
            0x2000,
            "MyApp.Cache",
            ReferenceDirection.Outgoing);
        navigation.Navigate(types);
        navigation.Navigate(type);
        navigation.Navigate(references);

        navigation.GoBack();
        Assert.Equal(type, navigation.CurrentLocation);
        Assert.True(navigation.CanGoBack);
        Assert.True(navigation.CanGoForward);

        navigation.GoForward();
        Assert.Equal(references, navigation.CurrentLocation);
        Assert.True(navigation.CanGoBack);
        Assert.False(navigation.CanGoForward);
    }

    [Fact]
    public void NewNavigationAfterBackClearsForwardHistory()
    {
        var navigation = new InvestigationNavigationService();
        navigation.Navigate(new TypesLocation());
        navigation.Navigate(new TypeLocation(0x1000));
        navigation.GoBack();

        navigation.Navigate(new GcRootsLocation(0x2000, "MyApp.Cache"));

        Assert.False(navigation.CanGoForward);
        Assert.Equal(
            new GcRootsLocation(0x2000, "MyApp.Cache"),
            navigation.CurrentLocation);
    }

    [Fact]
    public void NavigatingToCurrentLocationIsIgnored()
    {
        var navigation = new InvestigationNavigationService();
        var changes = 0;
        navigation.StateChanged += (_, _) => changes++;
        var location = new TypeLocation(0x1000);

        navigation.Navigate(location);
        navigation.Navigate(location);

        Assert.False(navigation.CanGoBack);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void ResetStartsANewHistoryAtTheGivenLocation()
    {
        var navigation = new InvestigationNavigationService();
        navigation.Navigate(new TypesLocation());
        navigation.Navigate(new TypeLocation(0x1000));
        navigation.GoBack();

        navigation.Reset(new TypesLocation());

        Assert.Equal(new TypesLocation(), navigation.CurrentLocation);
        Assert.False(navigation.CanGoBack);
        Assert.False(navigation.CanGoForward);
    }

    [Fact]
    public void RepeatedNavigationReleasesResetHistoryAndKeepsMemoryBounded()
    {
        var navigation = new InvestigationNavigationService();
        navigation.Reset(new TypesLocation());
        var before = ProfilerMemoryProbe.MeasureRetainedBytes();

        var references = ExerciseNavigation(navigation, locationCount: 20_000);
        var peak = GC.GetTotalMemory(forceFullCollection: false);
        var after = ProfilerMemoryProbe.MeasureRetainedBytes();

        Assert.Equal(new TypesLocation(), navigation.CurrentLocation);
        Assert.False(navigation.CanGoBack);
        Assert.False(navigation.CanGoForward);
        Assert.InRange(references.Count(reference => reference.IsAlive), 0, 1);
        Assert.True(
            ProfilerMemoryProbe.IsGrowthWithin(
                before,
                after,
                peak,
                fixedAllowanceBytes: 8 * 1024 * 1024),
            $"Retained navigation memory grew by {after - before:N0} bytes.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IReadOnlyList<WeakReference> ExerciseNavigation(
        InvestigationNavigationService navigation,
        int locationCount)
    {
        var references = new List<WeakReference>(locationCount);
        for (var index = 1; index <= locationCount; index++)
        {
            var location = new TypeLocation((ulong)index);
            references.Add(new WeakReference(location));
            navigation.Navigate(location);
        }

        while (navigation.CanGoBack)
        {
            navigation.GoBack();
        }

        Assert.Equal(new TypesLocation(), navigation.CurrentLocation);

        while (navigation.CanGoForward)
        {
            navigation.GoForward();
        }

        Assert.Equal(new TypeLocation((ulong)locationCount), navigation.CurrentLocation);
        navigation.Reset(new TypesLocation());
        return references;
    }
}
