namespace LancachePrefill.Modules.Steam;

/// <summary>The signed-in account's library, as needed by the dynamic Selection rules.</summary>
public interface ISteamLibrary
{
    /// <summary>Every owned app id with the time it was first licensed to the account.</summary>
    Task<IReadOnlyDictionary<uint, DateTimeOffset>> GetOwnedAppsAsync(CancellationToken ct);

    /// <summary>Apps with playtime in the last two weeks (Steam's own window).</summary>
    Task<IReadOnlyList<uint>> GetRecentlyPlayedAppIdsAsync(CancellationToken ct);
}
