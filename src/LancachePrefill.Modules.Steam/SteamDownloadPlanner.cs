// Ported from SteamPrefill AppInfoHandler, DepotHandler and ManifestHandler
// (tpill90/steam-lancache-prefill, MIT). See NOTICE.
using System.Collections.Concurrent;

namespace LancachePrefill.Modules.Steam;

/// <summary>
/// Decides what prefilling Steam apps means: which apps are prefillable games, which of their depots
/// match the settings, whether those are up to date, and which chunks to request.
/// </summary>
public sealed class SteamDownloadPlanner(ISteamSession steam, IDownloadedDepotStore downloaded)
{
    /// <summary>Depots never worth prefilling (shared redistributables etc.), from upstream.</summary>
    private static readonly HashSet<uint> ExcludedDepotIds =
        [381450, 2347779, 1770480, 731, 732, 734, 228981, 228982, 228983, 228984, 228985, 228986, 228987, 228988, 228989, 228990];

    /// <summary>Spacewar: not playable, only a Steamworks multiplayer helper.</summary>
    private static readonly HashSet<uint> ExcludedAppIds = [480];

    private const int CategoryModsHl2 = 6;
    private const int CategoryMods = 19;
    private const int ManifestAttempts = 3;

    private readonly ConcurrentDictionary<uint, SteamAppInfo> _apps = new();
    private IReadOnlyList<CdnServer>? _cdnServers;

    /// <summary>
    /// Loads metadata for the apps (plus their owned DLC and linked apps) and returns the ones that are
    /// prefillable games for the chosen operating systems, ordered by name.
    /// </summary>
    public async Task<IReadOnlyList<SteamAppInfo>> GetAvailableGamesAsync(IEnumerable<uint> appIds, SteamSettings settings, CancellationToken ct)
    {
        var ids = appIds.Distinct().ToList();
        await LoadAppsAsync(ids, ct);
        await AddDlcDepotsAsync(ct);

        return [.. ids.Select(id => _apps.GetValueOrDefault(id))
            .OfType<SteamAppInfo>()
            .Where(a => a.Type is "game" or "beta" or "demo"
                        && a.ReleaseState is not ("unavailable" or "disabled")
                        && a.SupportsAny(settings.OperatingSystems)
                        && steam.OwnsApp(a.AppId)
                        && !ExcludedAppIds.Contains(a.AppId)
                        && !a.Categories.Contains(CategoryMods)
                        && !a.Categories.Contains(CategoryModsHl2))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<SteamAppPlan> PlanAsync(SteamAppInfo app, SteamSettings settings, bool force, CancellationToken ct)
    {
        var depots = await FilterDepotsAsync(app.Depots, settings, ct);
        if (depots.Count == 0)
        {
            return new SteamAppPlan(app, SteamPlanStatus.NothingToDownload, [], []);
        }
        depots = await ResolveLinkedManifestsAsync(depots, ct);

        // Re-download the whole app if any of its depots changed.
        if (!force && await AllDownloadedAsync(depots, ct))
        {
            return new SteamAppPlan(app, SteamPlanStatus.UpToDate, depots, []);
        }

        var chunks = new List<SteamChunkRequest>();
        foreach (var depot in depots)
        {
            var manifest = await GetManifestAsync(depot, ct);
            // Steam deduplicates at block level: several files can share a chunk.
            chunks.AddRange(manifest.Files
                .SelectMany(f => f.Chunks)
                .DistinctBy(c => c.ChunkId)
                .Select(c => new SteamChunkRequest(depot.DepotId, c.ChunkId, c.CompressedLength)));
        }
        return new SteamAppPlan(app, SteamPlanStatus.Download, depots, chunks);
    }

    /// <summary>Records the plan's depot manifests as prefilled, so the next plan reports them up to date.</summary>
    public Task MarkDownloadedAsync(SteamAppPlan plan, CancellationToken ct) =>
        downloaded.MarkDownloadedAsync(plan.Depots.Select(d => (d.DepotId, d.ManifestId!.Value)), ct);

    private async Task<List<SteamDepotInfo>> FilterDepotsAsync(IReadOnlyList<SteamDepotInfo> depots, SteamSettings settings, CancellationToken ct)
    {
        var result = new List<SteamDepotInfo>();
        foreach (var depot in depots)
        {
            if (ExcludedDepotIds.Contains(depot.DepotId)
                || !steam.OwnsDepot(depot.DepotId)
                || !steam.OwnsApp(depot.ContainingAppId)
                || (await GetAppAsync(depot.ContainingAppId, ct))?.Type is null)
            {
                continue;
            }
            if (depot.OperatingSystems.Count > 0 && !depot.OperatingSystems.Any(settings.OperatingSystems.Contains))
            {
                continue;
            }
            if (depot.Architecture is not null && depot.Architecture != settings.Architecture)
            {
                continue;
            }
            if (depot.Languages.Count > 0 && !depot.Languages.Contains(settings.Language))
            {
                continue;
            }
            if (depot.LowViolence)
            {
                continue;
            }
            result.Add(depot);
        }
        return result;
    }

    /// <summary>A depot listed with <c>depotfromapp</c> takes its manifest from the same depot in the linked app.</summary>
    private async Task<List<SteamDepotInfo>> ResolveLinkedManifestsAsync(List<SteamDepotInfo> depots, CancellationToken ct)
    {
        var resolved = new List<SteamDepotInfo>(depots.Count);
        foreach (var depot in depots)
        {
            if (depot.ManifestId is not null)
            {
                resolved.Add(depot);
                continue;
            }
            var linkedApp = await GetAppAsync(depot.DepotFromApp!.Value, ct)
                ?? throw new InvalidOperationException($"Linked app {depot.DepotFromApp} of depot {depot} not found.");
            var linked = linkedApp.Depots.First(d => d.DepotId == depot.DepotId);
            resolved.Add(depot with { ManifestId = linked.ManifestId });
        }
        return resolved;
    }

    private async Task<bool> AllDownloadedAsync(IEnumerable<SteamDepotInfo> depots, CancellationToken ct)
    {
        foreach (var depot in depots)
        {
            if (!await downloaded.IsDownloadedAsync(depot.DepotId, depot.ManifestId!.Value, ct))
            {
                return false;
            }
        }
        return true;
    }

    private async Task<SteamManifest> GetManifestAsync(SteamDepotInfo depot, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var code = await steam.GetManifestRequestCodeAsync(depot.DepotId, depot.ContainingAppId, depot.ManifestId!.Value, ct)
                    .WaitAsync(TimeSpan.FromSeconds(90), ct);
                if (code == 0)
                {
                    throw new InvalidOperationException($"No manifest request code for depot {depot}, manifest {depot.ManifestId}.");
                }
                var server = (await GetCdnServersAsync(ct))[0];
                return await steam.DownloadManifestAsync(depot.DepotId, depot.ManifestId.Value, code, server, ct);
            }
            catch (Exception ex) when (attempt < ManifestAttempts && ex is not OperationCanceledException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
            }
        }
    }

    private async Task<IReadOnlyList<CdnServer>> GetCdnServersAsync(CancellationToken ct)
    {
        if (_cdnServers is null)
        {
            var servers = await steam.GetCdnServersAsync(ct);
            if (servers.Count == 0)
            {
                throw new InvalidOperationException("Steam returned no CDN servers.");
            }
            _cdnServers = [.. servers.OrderBy(s => s.Load)];
        }
        return _cdnServers;
    }

    private async Task<SteamAppInfo?> GetAppAsync(uint appId, CancellationToken ct)
    {
        if (!_apps.ContainsKey(appId))
        {
            await LoadAppsAsync([appId], ct);
        }
        return _apps.GetValueOrDefault(appId);
    }

    /// <summary>Loads appinfo for apps not loaded yet, in up to ten parallel batches.</summary>
    private async Task LoadAppsAsync(IReadOnlyCollection<uint> appIds, CancellationToken ct)
    {
        var missing = appIds.Where(id => !_apps.ContainsKey(id)).Distinct().ToList();
        if (missing.Count == 0)
        {
            return;
        }
        var batchSize = (missing.Count / 10) + 1;
        await Task.WhenAll(missing.Chunk(batchSize).Select(async batch =>
        {
            foreach (var (appId, keyValues) in await steam.GetAppInfoAsync(batch, ct))
            {
                _apps.TryAdd(appId, SteamAppInfo.Parse(appId, keyValues, steam.OwnsApp));
            }
        }));
    }

    /// <summary>Loads owned DLC and linked apps, then adds each game's DLC depots to the game.</summary>
    private async Task AddDlcDepotsAsync(CancellationToken ct)
    {
        var games = _apps.Values.Where(a => a.Type == "game").ToList();
        var dlcIds = _apps.Values.SelectMany(a => a.DlcAppIds);
        var containingIds = games.SelectMany(g => g.Depots).Select(d => d.ContainingAppId);
        await LoadAppsAsync([.. dlcIds.Union(containingIds)], ct);

        foreach (var game in games)
        {
            var dlcDepots = game.DlcAppIds
                .Select(id => _apps.GetValueOrDefault(id))
                .OfType<SteamAppInfo>()
                .SelectMany(dlc => dlc.Depots);
            _apps[game.AppId] = game with { Depots = [.. game.Depots.Concat(dlcDepots).DistinctBy(d => d.DepotId)] };
        }
    }
}
