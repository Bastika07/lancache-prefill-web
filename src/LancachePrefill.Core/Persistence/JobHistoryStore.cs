using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace LancachePrefill.Core.Persistence;

/// <summary>A finished Job as kept in the history. See GLOSSARY.md → Job result.</summary>
public sealed record JobRecord(
    Guid JobId,
    string ModuleId,
    JobState State,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset FinishedAt,
    string? FailureReason,
    IReadOnlyList<GameOutcome> Games)
{
    public TimeSpan Duration => StartedAt is { } started ? FinishedAt - started : TimeSpan.Zero;
    public long BytesDownloaded => Games.Sum(g => g.BytesDownloaded);
    public long AverageBytesPerSecond => Duration > TimeSpan.Zero ? (long)(BytesDownloaded / Duration.TotalSeconds) : 0;
}

/// <summary>A Schedule firing as kept in the history.</summary>
public sealed record FiringRecord(string ScheduleName, DateTimeOffset At, IReadOnlyList<ModuleFiringRecord> Modules);

/// <summary>The Job enqueued for a Module, or none when it was skipped.</summary>
public sealed record ModuleFiringRecord(string ModuleId, Guid? JobId)
{
    public bool Skipped => JobId is null;
}

/// <summary>
/// Job history and Schedule firings. Fed by the Job queue (<see cref="IJobFinishedListener"/>)
/// and the Scheduler (<see cref="IScheduleFiredListener"/>).
/// </summary>
public interface IJobHistoryStore : IJobFinishedListener, IScheduleFiredListener
{
    /// <summary>Most recently finished first.</summary>
    Task<IReadOnlyList<JobRecord>> GetRecentAsync(int limit, CancellationToken ct);

    /// <summary>Most recent first.</summary>
    Task<IReadOnlyList<FiringRecord>> GetRecentFiringsAsync(int limit, CancellationToken ct);
}

internal sealed class JobHistoryStore(Func<PrefillDbContext> createContext) : IJobHistoryStore
{
    public async Task OnJobFinishedAsync(Job job, CancellationToken ct)
    {
        await using var context = createContext();
        context.JobRecords.Add(JobRecordEntity.From(job));
        await context.SaveChangesAsync(ct);
    }

    public async Task OnScheduleFiredAsync(ScheduleFiring firing, CancellationToken ct)
    {
        await using var context = createContext();
        context.FiringRecords.Add(FiringRecordEntity.From(firing));
        await context.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<FiringRecord>> GetRecentFiringsAsync(int limit, CancellationToken ct)
    {
        await using var context = createContext();
        var entities = await context.FiringRecords.AsNoTracking()
            .OrderByDescending(e => e.At)
            .Take(limit)
            .ToListAsync(ct);
        return [.. entities.Select(e => e.ToRecord())];
    }

    public async Task<IReadOnlyList<JobRecord>> GetRecentAsync(int limit, CancellationToken ct)
    {
        await using var context = createContext();
        var entities = await context.JobRecords.AsNoTracking()
            .OrderByDescending(e => e.FinishedAt)
            .Take(limit)
            .ToListAsync(ct);
        return [.. entities.Select(e => e.ToRecord())];
    }
}

internal sealed class JobRecordEntity
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public Guid JobId { get; set; }
    public required string ModuleId { get; set; }
    public JobState State { get; set; }
    public DateTimeOffset EnqueuedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public string? FailureReason { get; set; }
    public required string GamesJson { get; set; }

    public static JobRecordEntity From(Job job) => new()
    {
        JobId = job.Id,
        ModuleId = job.ModuleId,
        State = job.State,
        EnqueuedAt = job.EnqueuedAt,
        StartedAt = job.StartedAt,
        FinishedAt = job.FinishedAt ?? throw new InvalidOperationException($"Job {job.Id} has not finished."),
        FailureReason = job.FailureReason,
        GamesJson = JsonSerializer.Serialize(job.Result?.Games ?? [], Json),
    };

    public JobRecord ToRecord() => new(
        JobId, ModuleId, State, EnqueuedAt, StartedAt, FinishedAt, FailureReason,
        JsonSerializer.Deserialize<List<GameOutcome>>(GamesJson, Json) ?? []);
}

internal sealed class FiringRecordEntity
{
    public Guid Id { get; set; }
    public required string ScheduleName { get; set; }
    public DateTimeOffset At { get; set; }
    public required string ModulesJson { get; set; }

    public static FiringRecordEntity From(ScheduleFiring firing) => new()
    {
        Id = Guid.NewGuid(),
        ScheduleName = firing.ScheduleName,
        At = firing.At,
        ModulesJson = JsonSerializer.Serialize(firing.Modules.Select(m => new ModuleFiringRecord(m.ModuleId, m.Job?.Id)).ToList()),
    };

    public FiringRecord ToRecord() => new(
        ScheduleName, At, JsonSerializer.Deserialize<List<ModuleFiringRecord>>(ModulesJson) ?? []);
}
