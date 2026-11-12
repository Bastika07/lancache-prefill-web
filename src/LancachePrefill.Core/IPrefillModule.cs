namespace LancachePrefill.Core;

/// <summary>One game platform integration (Steam, Battle.net, Epic). See GLOSSARY.md → Module.</summary>
public interface IPrefillModule
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>Prefills the requested Games through the Lancache and reports the per-Game outcome.</summary>
    Task<PrefillResult> PrefillAsync(PrefillRequest request, IProgress<PrefillProgress> progress, CancellationToken ct);
}

/// <summary>What one Job asks a Module to prefill.</summary>
public sealed record PrefillRequest(IReadOnlyList<string> GameIds, bool Force = false);

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
}
