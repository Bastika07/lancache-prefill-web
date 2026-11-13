namespace LancachePrefill.Modules.Steam;

public enum SteamPlanStatus
{
    /// <summary>Settings filtered out every depot.</summary>
    NothingToDownload,

    /// <summary>Every depot's current manifest was prefilled before.</summary>
    UpToDate,

    Download,
}

/// <summary>One chunk request: <c>GET /depot/{DepotId}/chunk/{ChunkId}</c>.</summary>
public sealed record SteamChunkRequest(uint DepotId, string ChunkId, uint CompressedLength);

/// <summary>What prefilling one Steam app means right now.</summary>
public sealed record SteamAppPlan(
    SteamAppInfo App,
    SteamPlanStatus Status,
    IReadOnlyList<SteamDepotInfo> Depots,
    IReadOnlyList<SteamChunkRequest> Chunks)
{
    public long TotalBytes => Chunks.Sum(c => (long)c.CompressedLength);
}
