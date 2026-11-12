using LancachePrefill.Core.Persistence;

namespace LancachePrefill.Modules.Steam;

/// <summary>
/// Keeps prefilled depot manifests (depot → manifest ids) as one JSON document in the settings store.
/// Replaces upstream's successfullyDownloadedDepots.json. Only one Job runs at a time (ADR 0004),
/// so read-modify-write is safe.
/// </summary>
public sealed class SettingsDownloadedDepotStore(ISettingsStore settings) : IDownloadedDepotStore
{
    private const string Key = "steam.downloaded-depots";

    public async Task<bool> IsDownloadedAsync(uint depotId, ulong manifestId, CancellationToken ct)
    {
        var state = await LoadAsync(ct);
        return state.TryGetValue(depotId, out var manifests) && manifests.Contains(manifestId);
    }

    public async Task MarkDownloadedAsync(IEnumerable<(uint DepotId, ulong ManifestId)> depots, CancellationToken ct)
    {
        var state = await LoadAsync(ct);
        foreach (var (depotId, manifestId) in depots)
        {
            if (!state.TryGetValue(depotId, out var manifests))
            {
                state[depotId] = manifests = [];
            }
            manifests.Add(manifestId);
        }
        await settings.SaveModuleAsync(Key, state, ct);
    }

    private Task<Dictionary<uint, HashSet<ulong>>> LoadAsync(CancellationToken ct) =>
        settings.GetModuleAsync<Dictionary<uint, HashSet<ulong>>>(Key, [], ct);
}
