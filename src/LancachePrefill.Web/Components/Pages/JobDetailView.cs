using LancachePrefill.Core;
using LancachePrefill.Core.Persistence;

namespace LancachePrefill.Web.Components.Pages;

/// <summary>What the Job detail page shows, from a live <see cref="Job"/> or a history <see cref="JobRecord"/>.</summary>
public sealed record JobDetailView(
    Guid Id,
    string ModuleId,
    JobState State,
    DateTimeOffset? StartedAt,
    TimeSpan Duration,
    string? FailureReason,
    IReadOnlyList<GameOutcome> Games,
    int? ResolvedGameCount,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<JobLogLine> Log,
    PrefillProgress? Progress,
    bool Live)
{
    public long BytesDownloaded => Games.Sum(g => g.BytesDownloaded);
    public long AverageBytesPerSecond => Duration > TimeSpan.Zero ? (long)(BytesDownloaded / Duration.TotalSeconds) : 0;

    public static JobDetailView From(Job job, DateTimeOffset now) => new(
        job.Id, job.ModuleId, job.State, job.StartedAt,
        job.StartedAt is { } started ? (job.FinishedAt ?? now) - started : TimeSpan.Zero,
        job.FailureReason,
        job.Result?.Games ?? job.FinishedGames,
        job.ResolvedGameIds?.Count,
        job.Warnings, job.Log, job.Progress, Live: true);

    public static JobDetailView From(JobRecord record) => new(
        record.JobId, record.ModuleId, record.State, record.StartedAt, record.Duration, record.FailureReason,
        record.Games, null, record.Warnings, record.Log, null, Live: false);
}
