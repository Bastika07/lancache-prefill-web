namespace LancachePrefill.Modules.Steam;

/// <summary>Which depot manifests were fully prefilled already (spec: up-to-date state per Depot).</summary>
public interface IDownloadedDepotStore
{
    Task<bool> IsDownloadedAsync(uint depotId, ulong manifestId, CancellationToken ct);
    Task MarkDownloadedAsync(IEnumerable<(uint DepotId, ulong ManifestId)> depots, CancellationToken ct);
}
