// Ported from SteamPrefill/Models/AppInfo.cs (tpill90/steam-lancache-prefill, MIT). See NOTICE.
using System.Globalization;
using SteamKit2;

namespace LancachePrefill.Modules.Steam;

/// <summary>A Steam app (game, DLC, tool, …) as described by its appinfo.</summary>
public sealed record SteamAppInfo
{
    public required uint AppId { get; init; }
    public required string Name { get; init; }

    /// <summary>Lower-case Steam type (<c>game</c>, <c>dlc</c>, <c>tool</c>, …); null if missing or unknown.</summary>
    public string? Type { get; init; }

    public string? ReleaseState { get; init; }
    public IReadOnlyList<string> OsList { get; init; } = [];

    /// <summary>Owned DLC only.</summary>
    public IReadOnlyList<uint> DlcAppIds { get; init; } = [];

    public IReadOnlyList<SteamDepotInfo> Depots { get; init; } = [];
    public IReadOnlyList<int> Categories { get; init; } = [];

    /// <summary>Upstream only knew Windows; an app without an oslist runs everywhere.</summary>
    public bool SupportsAny(IEnumerable<string> operatingSystems) => OsList.Count == 0 || operatingSystems.Any(OsList.Contains);

    private static readonly HashSet<string> KnownTypes =
        ["application", "beta", "config", "demo", "dlc", "game", "guide", "hardware", "media", "music", "series", "tool", "video"];

    internal static SteamAppInfo Parse(uint appId, KeyValue root, Func<uint, bool> ownsApp)
    {
        var type = root["common"]["type"].ToLowerCaseString();
        return new SteamAppInfo
        {
            AppId = appId,
            Name = root["common"]["name"].Value ?? $"App {appId}",
            Type = type is not null && KnownTypes.Contains(type) ? type : null,
            ReleaseState = root["common"]["releasestate"].ToLowerCaseString(),
            OsList = root["common"]["oslist"].SplitCommaDelimited(),
            Depots = root["depots"] == KeyValue.Invalid
                ? []
                : [.. root["depots"].Children
                        .Where(d => uint.TryParse(d.Name, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                        .Select(d => SteamDepotInfo.Parse(d, appId))
                        .Where(d => !d.IsInvalid)],
            DlcAppIds = [.. root["extended"]["listofdlc"].SplitCommaDelimited()
                            .Select(id => uint.Parse(id, CultureInfo.InvariantCulture))
                            .Where(ownsApp)],
            Categories = [.. root["common"]["category"].Children
                             .Select(c => int.Parse(c.Name!.Replace("category_", "", StringComparison.Ordinal), CultureInfo.InvariantCulture))],
        };
    }

    public override string ToString() => Name;
}
