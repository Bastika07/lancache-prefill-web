// Ported from SteamPrefill/Models/DepotInfo.cs (tpill90/steam-lancache-prefill, MIT). See NOTICE.
using System.Globalization;
using SteamKit2;

namespace LancachePrefill.Modules.Steam;

/// <summary>A Steam depot as described in an app's appinfo. Immutable; linked manifests resolve to a copy.</summary>
public sealed record SteamDepotInfo
{
    public required uint DepotId { get; init; }
    public required string Name { get; init; }

    /// <summary>Public-branch manifest; null for a depot linked from another app until resolved.</summary>
    public ulong? ManifestId { get; init; }

    public uint? DepotFromApp { get; init; }
    public uint? DlcAppId { get; init; }

    /// <summary>The app that owns the content: the DLC, the linked app, or the app it was listed in.</summary>
    public required uint ContainingAppId { get; init; }

    /// <summary>Empty = all operating systems.</summary>
    public IReadOnlyList<string> OperatingSystems { get; init; } = [];

    /// <summary>null = any architecture.</summary>
    public string? Architecture { get; init; }

    /// <summary>Empty = all languages.</summary>
    public IReadOnlyList<string> Languages { get; init; } = [];

    public bool LowViolence { get; init; }

    /// <summary>No manifest and not linked to another app's depot: nothing to download.</summary>
    internal bool IsInvalid => ManifestId is null && (DepotFromApp is null || DepotFromApp == DepotId);

    internal static SteamDepotInfo Parse(KeyValue depot, uint appId)
    {
        var depotId = uint.Parse(depot.Name!, CultureInfo.InvariantCulture);
        var manifestId = depot["manifests"]["public"]["gid"].AsUnsignedLongNullable()
                         ?? depot["manifests"]["public"].AsUnsignedLongNullable(); // older appinfo format
        var depotFromApp = depot["depotfromapp"].AsUnsignedIntNullable();
        var dlcAppId = depot["dlcappid"].AsUnsignedIntNullable();
        var arch = depot["config"]["osarch"].ToLowerCaseString();
        return new SteamDepotInfo
        {
            DepotId = depotId,
            Name = depot["name"].Value ?? $"Depot {depotId}",
            ManifestId = manifestId,
            DepotFromApp = depotFromApp,
            DlcAppId = dlcAppId,
            ContainingAppId = dlcAppId ?? depotFromApp ?? appId,
            OperatingSystems = depot["config"]["oslist"].SplitCommaDelimited(),
            Architecture = arch is "32" or "64" ? arch : null,
            Languages = depot["config"]["language"].SplitCommaDelimited(),
            LowViolence = depot["config"]["lowviolence"].Value is "1",
        };
    }

    public override string ToString() => $"{DepotId} - {Name}";
}
