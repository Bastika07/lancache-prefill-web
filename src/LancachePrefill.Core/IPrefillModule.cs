namespace LancachePrefill.Core;

/// <summary>One game platform integration (Steam, Battle.net, Epic). See GLOSSARY.md → Module.</summary>
public interface IPrefillModule
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>The dynamic Selection rules this Module can resolve. None by default.</summary>
    IReadOnlySet<SelectionRuleKind> SupportedRules => new HashSet<SelectionRuleKind>();

    /// <summary>Resolves one dynamic Selection rule to Game ids. Called by the Job queue when a Job starts.</summary>
    Task<IReadOnlyList<string>> ResolveRuleAsync(SelectionRule rule, CancellationToken ct) =>
        throw new NotSupportedException($"Module '{Id}' does not support the rule {rule}.");

    /// <summary>Prefills the requested Games through the Lancache and reports the per-Game outcome.</summary>
    Task<PrefillResult> PrefillAsync(PrefillRequest request, IProgress<PrefillProgress> progress, CancellationToken ct);
}

/// <summary>
/// What one Job asks a Module to prefill: explicit Games plus dynamic rules (a Selection).
/// The Job queue resolves the rules when the Job starts; the Module receives only resolved GameIds.
/// </summary>
public sealed record PrefillRequest(IReadOnlyList<string> GameIds, bool Force = false, IReadOnlyList<SelectionRule>? Rules = null)
{
    public IReadOnlyList<SelectionRule> Rules { get; init; } = Rules ?? [];
}

public enum SelectionRuleKind
{
    AllOwned,
    RecentlyPlayed,
    RecentlyPurchased,
    TopPopular,
}

/// <summary>A dynamic Selection rule. See GLOSSARY.md → Dynamic selection.</summary>
public sealed record SelectionRule(SelectionRuleKind Kind, int Count = 0)
{
    public static SelectionRule AllOwned { get; } = new(SelectionRuleKind.AllOwned);
    public static SelectionRule RecentlyPlayed { get; } = new(SelectionRuleKind.RecentlyPlayed);
    public static SelectionRule RecentlyPurchased { get; } = new(SelectionRuleKind.RecentlyPurchased);
    public static SelectionRule TopPopular(int count) => new(SelectionRuleKind.TopPopular, count);

    public override string ToString() => Kind == SelectionRuleKind.TopPopular ? $"TopPopular({Count})" : Kind.ToString();
}

/// <summary>Module-neutral progress report.</summary>
public sealed record PrefillProgress(string Phase, string? CurrentGameId = null, long BytesDone = 0, long BytesTotal = 0);

/// <summary>Per-Game outcome of a prefill. See GLOSSARY.md → Job result.</summary>
public sealed record PrefillResult(IReadOnlyList<GameOutcome> Games)
{
    public static PrefillResult Empty { get; } = new([]);
}

public sealed record GameOutcome(string GameId, GameOutcomeStatus Status, long BytesDownloaded = 0, string? FailureReason = null);

public enum GameOutcomeStatus
{
    UpToDate,
    Downloaded,
    Failed,

    /// <summary>Nothing to do by design, e.g. not a prefillable game or every depot filtered out. Reason in FailureReason.</summary>
    Skipped,
}
