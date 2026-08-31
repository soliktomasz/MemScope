using MemoryProfiler.App.Navigation;
using MemoryProfiler.App.Models;
using Xunit;

namespace MemoryProfiler.App.Tests.Navigation;

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
}
