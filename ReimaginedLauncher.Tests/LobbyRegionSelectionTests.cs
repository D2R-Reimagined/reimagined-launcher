using ReimaginedLauncher.HttpClients.Models;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class LobbyRegionSelectionTests
{
    private static readonly LobbyRegionResponse[] Regions =
    [
        new("ap-south", "AP South", true, ["10.0.0.4"]),
        new("eu-west", "EU West", true, ["10.0.0.2"]),
        new("na-east", "NA East", true, ["10.0.0.1"]),
        new("na-west", "NA West", true, ["10.0.0.3"])
    ];

    [Fact]
    public void AutomaticPicksTheTwoFastestAvailableRegions()
    {
        var pings = new Dictionary<string, double?>
        {
            ["ap-south"] = 180, ["eu-west"] = 95, ["na-east"] = 22, ["na-west"] = 61
        };

        Assert.Equal(["na-east", "na-west"], LobbyRegionSelection.Automatic(Regions, pings));
    }

    [Fact]
    public void AutomaticFillsWithAvailableRegionsByNameWhenTooFewArePinged()
    {
        var pings = new Dictionary<string, double?> { ["na-west"] = 40, ["eu-west"] = null };

        Assert.Equal(["na-west", "ap-south"], LobbyRegionSelection.Automatic(Regions, pings));
        Assert.Equal(["ap-south", "eu-west"], LobbyRegionSelection.Automatic(Regions, new Dictionary<string, double?>()));
    }

    [Fact]
    public void AutomaticNeverPicksAnOfflineRegion()
    {
        LobbyRegionResponse[] regions =
        [
            new("eu-west", "EU West", true, ["10.0.0.2"]),
            new("na-east", "NA East", false, ["10.0.0.1"]),
            new("na-west", "NA West", false, ["10.0.0.3"])
        ];
        var pings = new Dictionary<string, double?> { ["na-east"] = 5, ["na-west"] = 9, ["eu-west"] = 120 };

        Assert.Equal(["eu-west"], LobbyRegionSelection.Automatic(regions, pings));
    }

    [Fact]
    public void AnExplicitSelectionIsKeptOverThePings()
    {
        var pings = new Dictionary<string, double?> { ["na-east"] = 10, ["na-west"] = 20, ["eu-west"] = 90 };

        var selected = LobbyRegionSelection.Effective(Regions, pings, ["eu-west"]);

        Assert.Equal(["eu-west"], selected);
        Assert.True(LobbyRegionSelection.IsCustom(Regions, ["eu-west"]));
        Assert.False(LobbyRegionSelection.IsCustom(Regions, null));
    }

    [Fact]
    public void RegionsTheApiNoLongerReturnsAreDropped()
    {
        var pings = new Dictionary<string, double?> { ["na-east"] = 10, ["na-west"] = 20 };

        Assert.Equal(["na-east"], LobbyRegionSelection.Effective(Regions, pings, ["gone", "na-east"]));
        // Nothing left of the explicit set means automatic again.
        Assert.Equal(["na-east", "na-west"], LobbyRegionSelection.Effective(Regions, pings, ["gone"]));
        Assert.False(LobbyRegionSelection.IsCustom(Regions, ["gone"]));
    }

    [Fact]
    public void TogglingStoresTheExplicitSetAndKeepsAtLeastOneRegion()
    {
        Assert.Equal(["na-east", "eu-west"], LobbyRegionSelection.Toggle(["na-east"], "eu-west", true));
        Assert.Equal(["na-east"], LobbyRegionSelection.Toggle(["na-east", "eu-west"], "eu-west", false));
        Assert.Null(LobbyRegionSelection.Toggle(["na-east"], "na-east", false));
    }

    [Fact]
    public void LaunchOrderIsClosestFirstWithUnmeasuredLastByName()
    {
        var pings = new Dictionary<string, double?> { ["na-west"] = 80, ["eu-west"] = 35, ["na-east"] = null };

        var order = LobbyRegionSelection.OrderForLaunch(
            ["na-east", "ap-south", "na-west", "eu-west", "removed"], Regions, pings);

        Assert.Equal(["eu-west", "na-west", "ap-south", "na-east"], order);
    }

    [Fact]
    public void ServerPinLabelsTagAnAdminTestingServer()
    {
        var names = new Dictionary<string, string> { ["eu-west"] = "EU West" };
        var open = new LobbyAdminServerResponse("eu-1", "EU 1", ["eu-west"], "10.0.0.2", true, 3, 5, 32);
        var lab = open with { Id = "lab-1", Name = "Lab", AdminOnly = true, Online = false };

        Assert.Equal("EU 1 — EU West — 3/32", LobbyServerChoice.From(open, names).Label);
        Assert.Equal("Lab — EU West — 3/32 (admin only) (offline)", LobbyServerChoice.From(lab, names).Label);
    }
}
