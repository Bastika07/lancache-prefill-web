using LancachePrefill.Modules.Steam;
using SteamKit2;

namespace LancachePrefill.Modules.Steam.Tests;

/// <summary>
/// Steam as seen through <see cref="ISteamSession"/>, backed by the appinfo fixtures.
/// Owns everything in the fixtures except depot 1009 and DLC 1200 / depot 1201.
/// Manifests not set explicitly get one 1&#160;MB chunk named after the depot.
/// </summary>
internal sealed class FakeSteamSession : ISteamSession
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private readonly Dictionary<(uint Depot, ulong Manifest), SteamManifest> _manifests = [];

    public HashSet<uint> OwnedApps { get; } = [1000, 1100, 2000, 3000, 3100, 3200, 3300, 480];
    public HashSet<uint> UnownedDepots { get; } = [1009, 1201];
    public List<(uint Depot, ulong Manifest)> ManifestDownloads { get; } = [];

    public void SetManifest(uint depotId, ulong manifestId, params SteamFile[] files) =>
        _manifests[(depotId, manifestId)] = new SteamManifest(depotId, manifestId, files);

    public Task<IReadOnlyDictionary<uint, KeyValue>> GetAppInfoAsync(IReadOnlyCollection<uint> appIds, CancellationToken ct)
    {
        var result = new Dictionary<uint, KeyValue>();
        foreach (var appId in appIds)
        {
            var file = Path.Combine(FixtureDir, $"app_{appId}.vdf");
            if (File.Exists(file))
            {
                result[appId] = KeyValue.LoadFromString(File.ReadAllText(file))!;
            }
        }
        return Task.FromResult<IReadOnlyDictionary<uint, KeyValue>>(result);
    }

    public bool OwnsApp(uint appId) => OwnedApps.Contains(appId);
    public bool OwnsDepot(uint depotId) => !UnownedDepots.Contains(depotId);

    public Task<ulong> GetManifestRequestCodeAsync(uint depotId, uint appId, ulong manifestId, CancellationToken ct) =>
        Task.FromResult(manifestId * 7);

    public Task<SteamManifest> DownloadManifestAsync(uint depotId, ulong manifestId, ulong requestCode, CdnServer server, CancellationToken ct)
    {
        Assert.Equal(manifestId * 7, requestCode);
        ManifestDownloads.Add((depotId, manifestId));
        return Task.FromResult(_manifests.TryGetValue((depotId, manifestId), out var manifest)
            ? manifest
            : new SteamManifest(depotId, manifestId, [new SteamFile([new SteamChunk($"{depotId:x}aa", 1_048_576)])]));
    }

    public Task<IReadOnlyList<CdnServer>> GetCdnServersAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CdnServer>>([new CdnServer("cache1-fra1.steamcontent.com", Load: 10)]);
}

/// <summary>Remembers downloaded depot manifests in memory, standing in for the settings-backed store.</summary>
internal sealed class InMemoryDownloadedDepots : IDownloadedDepotStore
{
    private readonly HashSet<(uint, ulong)> _done = [];

    public Task<bool> IsDownloadedAsync(uint depotId, ulong manifestId, CancellationToken ct) => Task.FromResult(_done.Contains((depotId, manifestId)));

    public Task MarkDownloadedAsync(IEnumerable<(uint DepotId, ulong ManifestId)> depots, CancellationToken ct)
    {
        _done.UnionWith(depots);
        return Task.CompletedTask;
    }
}
