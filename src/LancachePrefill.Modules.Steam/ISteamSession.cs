using SteamKit2;

namespace LancachePrefill.Modules.Steam;

/// <summary>
/// Everything the Steam module needs from a logged-in Steam session. The SteamKit2 implementation
/// (connection, login, licenses) comes with #9; tests use a fake backed by fixtures.
/// </summary>
public interface ISteamSession
{
    /// <summary>PICS product info (appinfo) per app id; apps Steam doesn't return are absent.</summary>
    Task<IReadOnlyDictionary<uint, KeyValue>> GetAppInfoAsync(IReadOnlyCollection<uint> appIds, CancellationToken ct);

    bool OwnsApp(uint appId);
    bool OwnsDepot(uint depotId);

    Task<ulong> GetManifestRequestCodeAsync(uint depotId, uint appId, ulong manifestId, CancellationToken ct);
    Task<SteamManifest> DownloadManifestAsync(uint depotId, ulong manifestId, ulong requestCode, CdnServer server, CancellationToken ct);
    Task<IReadOnlyList<CdnServer>> GetCdnServersAsync(CancellationToken ct);
}

public sealed record CdnServer(string Host, int Load);

public sealed record SteamManifest(uint DepotId, ulong ManifestId, IReadOnlyList<SteamFile> Files);

public sealed record SteamFile(IReadOnlyList<SteamChunk> Chunks);

/// <param name="ChunkId">Lower-case hex SHA-1, as used in <c>/depot/{depot}/chunk/{chunk}</c>.</param>
public sealed record SteamChunk(string ChunkId, uint CompressedLength);
