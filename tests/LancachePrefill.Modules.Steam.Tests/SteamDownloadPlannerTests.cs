using LancachePrefill.Modules.Steam;

namespace LancachePrefill.Modules.Steam.Tests;

public sealed class SteamDownloadPlannerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly FakeSteamSession _steam = new();
    private readonly InMemoryDownloadedDepots _downloaded = new();

    private SteamDownloadPlanner Planner() => new(_steam, _downloaded);

    private async Task<SteamAppPlan> PlanExampleGameAsync(SteamSettings settings, bool force = false)
    {
        var planner = Planner();
        var game = Assert.Single(await planner.GetAvailableGamesAsync([1000], Ct));
        return await planner.PlanAsync(game, settings, force, Ct);
    }

    [Fact]
    public async Task With_default_settings_the_plan_has_the_windows_64_bit_english_depots_including_linked_and_owned_dlc_depots()
    {
        var plan = await PlanExampleGameAsync(SteamSettings.Default);

        // 1003/1004 other OS, 1005 German, 1006 low violence, 1007 32-bit, 1009 unlicensed,
        // 1201 unowned DLC, 228986 excluded redistributable.
        Assert.Equal<uint>([1001, 1002, 1008, 1010, 1101], [.. plan.Depots.Select(d => d.DepotId).Order()]);
    }
}
