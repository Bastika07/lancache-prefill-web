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
        var game = Assert.Single(await planner.GetAvailableGamesAsync([1000], SteamSettings.Default, Ct));
        return await planner.PlanAsync(game, settings, force, Ct);
    }

    [Fact]
    public async Task With_default_settings_the_plan_has_the_windows_64_bit_english_depots_including_linked_and_owned_dlc_depots()
    {
        var plan = await PlanExampleGameAsync(SteamSettings.Default);

        // 1003/1004 other OS, 1005 German, 1006 low violence, 1007 32-bit, 1009 unlicensed,
        // 1011 + 1201 unowned DLC, 228986 excluded redistributable (owned, but on the exclusion list).
        Assert.Equal<uint>([1001, 1002, 1008, 1010, 1101], [.. plan.Depots.Select(d => d.DepotId).Order()]);
    }

    private static uint[] DepotIds(SteamAppPlan plan) => [.. plan.Depots.Select(d => d.DepotId).Order()];

    [Fact]
    public async Task A_dlc_depot_listed_in_the_base_game_is_prefilled_only_when_the_dlc_is_owned()
    {
        _steam.OwnedApps.Add(1200);

        var plan = await PlanExampleGameAsync(SteamSettings.Default);

        Assert.Contains(1011u, DepotIds(plan));
    }

    [Fact]
    public async Task Several_operating_systems_prefill_the_union_of_their_depots()
    {
        var plan = await PlanExampleGameAsync(SteamSettings.Default with { OperatingSystems = ["windows", "linux"] });

        Assert.Equal<uint>([1001, 1002, 1004, 1008, 1010, 1101], DepotIds(plan));
    }

    [Fact]
    public async Task The_language_setting_picks_the_matching_language_depots()
    {
        var plan = await PlanExampleGameAsync(SteamSettings.Default with { Language = "german" });

        Assert.Equal<uint>([1001, 1002, 1005, 1008, 1101], DepotIds(plan));
    }

    [Fact]
    public async Task The_32_bit_setting_picks_32_bit_depots_and_keeps_architecture_neutral_ones()
    {
        var plan = await PlanExampleGameAsync(SteamSettings.Default with { Architecture = "32" });

        Assert.Equal<uint>([1001, 1002, 1007, 1008, 1010, 1101], DepotIds(plan));
    }

    [Fact]
    public async Task A_linked_depot_uses_the_manifest_of_the_same_depot_in_the_linked_app()
    {
        var plan = await PlanExampleGameAsync(SteamSettings.Default);

        Assert.Equal(5008ul, plan.Depots.Single(d => d.DepotId == 1008).ManifestId);
        Assert.Contains((1008u, 5008ul), _steam.ManifestDownloads);
    }

    [Fact]
    public async Task Chunks_shared_by_files_of_one_depot_are_requested_once_but_each_depot_gets_its_own()
    {
        _steam.SetManifest(1001, 5001,
            new SteamFile([new SteamChunk("aaaa", 100), new SteamChunk("bbbb", 200)]),
            new SteamFile([new SteamChunk("aaaa", 100), new SteamChunk("cccc", 300)]));
        _steam.SetManifest(1002, 5002, new SteamFile([new SteamChunk("aaaa", 100)]));

        var plan = await PlanExampleGameAsync(SteamSettings.Default);

        Assert.Equal(SteamPlanStatus.Download, plan.Status);
        Assert.Equal(["aaaa", "bbbb", "cccc"], plan.Chunks.Where(c => c.DepotId == 1001).Select(c => c.ChunkId));
        Assert.Equal(["aaaa"], plan.Chunks.Where(c => c.DepotId == 1002).Select(c => c.ChunkId));
        Assert.Equal(100 + 200 + 300 + 100 + (3 * 1_048_576), plan.TotalBytes);
    }

    [Fact]
    public async Task After_a_successful_prefill_the_game_is_up_to_date_without_fetching_manifests()
    {
        var planner = Planner();
        var game = Assert.Single(await planner.GetAvailableGamesAsync([1000], SteamSettings.Default, Ct));
        await planner.MarkDownloadedAsync(await planner.PlanAsync(game, SteamSettings.Default, force: false, Ct), Ct);
        _steam.ManifestDownloads.Clear();

        var plan = await planner.PlanAsync(game, SteamSettings.Default, force: false, Ct);

        Assert.Equal(SteamPlanStatus.UpToDate, plan.Status);
        Assert.Empty(plan.Chunks);
        Assert.Empty(_steam.ManifestDownloads);
    }

    [Fact]
    public async Task Force_downloads_an_up_to_date_game_again()
    {
        var planner = Planner();
        var game = Assert.Single(await planner.GetAvailableGamesAsync([1000], SteamSettings.Default, Ct));
        await planner.MarkDownloadedAsync(await planner.PlanAsync(game, SteamSettings.Default, force: false, Ct), Ct);

        var plan = await planner.PlanAsync(game, SteamSettings.Default, force: true, Ct);

        Assert.Equal(SteamPlanStatus.Download, plan.Status);
        Assert.Equal(5, plan.Chunks.Count);
    }

    [Fact]
    public async Task One_updated_depot_makes_the_whole_game_download_again()
    {
        await _downloaded.MarkDownloadedAsync([(1001, 5001), (1002, 4999), (1008, 5008), (1010, 5010), (1101, 5101)], Ct);

        var plan = await PlanExampleGameAsync(SteamSettings.Default);

        Assert.Equal(SteamPlanStatus.Download, plan.Status);
        Assert.Equal<uint>([1001, 1002, 1008, 1010, 1101], [.. plan.Chunks.Select(c => c.DepotId).Distinct().Order()]);
    }

    [Fact]
    public async Task A_game_whose_depots_are_all_filtered_out_has_nothing_to_download()
    {
        _steam.UnownedDepots.Add(3301); // the only depot of "Another Game"
        var planner = Planner();
        var game = Assert.Single(await planner.GetAvailableGamesAsync([3300], SteamSettings.Default, Ct));

        var plan = await planner.PlanAsync(game, SteamSettings.Default, force: false, Ct);

        Assert.Equal(SteamPlanStatus.NothingToDownload, plan.Status);
        Assert.Empty(plan.Depots);
        Assert.Empty(_steam.ManifestDownloads);
    }

    [Fact]
    public async Task Only_owned_released_non_mod_games_are_prefillable_and_they_come_sorted_by_name()
    {
        // 3000 is a tool, 3100 a mod, 3200 delisted, 480 Spacewar, 9999 unknown to Steam.
        var games = await Planner().GetAvailableGamesAsync([1000, 3000, 3100, 3200, 480, 3300, 9999], SteamSettings.Default, Ct);

        Assert.Equal(["Another Game", "Example Game"], games.Select(g => g.Name));
    }

    [Fact]
    public async Task A_game_for_another_operating_system_is_prefillable_only_when_that_system_is_chosen()
    {
        Assert.Empty(await Planner().GetAvailableGamesAsync([3400], SteamSettings.Default, Ct));

        var linux = await Planner().GetAvailableGamesAsync([3400, 1000], SteamSettings.Default with { OperatingSystems = ["linux"] }, Ct);

        Assert.Equal(["Example Game", "Linux Only Game"], linux.Select(g => g.Name));
    }

    [Fact]
    public async Task An_unowned_game_is_not_prefillable()
    {
        _steam.OwnedApps.Remove(3300);

        Assert.Empty(await Planner().GetAvailableGamesAsync([3300], SteamSettings.Default, Ct));
    }
}
