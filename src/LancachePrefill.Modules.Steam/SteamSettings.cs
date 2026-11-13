namespace LancachePrefill.Modules.Steam;

/// <summary>Steam Module settings (spec → Module contract).</summary>
/// <param name="OperatingSystems">Any of <c>windows</c>, <c>macos</c>, <c>linux</c>; depots for any of them are prefilled.</param>
/// <param name="Architecture">Steam's <c>osarch</c>: <c>64</c> or <c>32</c>.</param>
/// <param name="Language">Steam language name, e.g. <c>english</c>, <c>german</c>, <c>schinese</c>.</param>
public sealed record SteamSettings(
    IReadOnlyList<string> OperatingSystems,
    int MaxConcurrentRequests,
    string Architecture = "64",
    string Language = "english")
{
    /// <summary>Upstream defaults: Windows, 64-bit, English, 30 concurrent requests.</summary>
    public static SteamSettings Default { get; } = new(["windows"], 30);

    public static IReadOnlyList<string> KnownOperatingSystems { get; } = ["windows", "macos", "linux"];

    /// <summary>Steam's language names (from upstream's Language enum).</summary>
    public static IReadOnlyList<string> KnownLanguages { get; } =
    [
        "english", "arabic", "brazilian", "bulgarian", "chinese", "schinese", "tchinese", "czech", "danish", "dutch",
        "finnish", "french", "german", "greek", "hungarian", "italian", "indonesian", "japanese", "koreana", "latam",
        "norwegian", "polish", "portuguese", "spanish", "romanian", "russian", "thai", "turkish", "swedish", "ukrainian",
        "vietnamese",
    ];

    public const int MinConcurrentRequests = 1;
    public const int MaxConcurrentRequestsLimit = 100;
}
