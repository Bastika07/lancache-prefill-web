using LancachePrefill.Core;
using LancachePrefill.Core.Persistence;

namespace LancachePrefill.Web.Tests.JobView;

/// <summary>In-memory Job history, filled by the tests.</summary>
public sealed class FakeJobHistory : IJobHistoryStore
{
    public List<JobRecord> Records { get; } = [];

    public Task<IReadOnlyList<JobRecord>> GetRecentAsync(int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<JobRecord>>([.. Records.OrderByDescending(r => r.FinishedAt).Take(limit)]);

    public Task<JobRecord?> GetAsync(Guid jobId, CancellationToken ct) =>
        Task.FromResult(Records.FirstOrDefault(r => r.JobId == jobId));

    public Task<IReadOnlyList<FiringRecord>> GetRecentFiringsAsync(int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<FiringRecord>>([]);
    public Task PruneAsync(TimeSpan retention, CancellationToken ct) => Task.CompletedTask;
    public Task OnJobFinishedAsync(Job job, CancellationToken ct) => Task.CompletedTask;
    public Task OnScheduleFiredAsync(ScheduleFiring firing, CancellationToken ct) => Task.CompletedTask;
}
