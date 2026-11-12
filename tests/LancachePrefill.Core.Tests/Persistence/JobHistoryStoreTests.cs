using LancachePrefill.Core.Persistence;

namespace LancachePrefill.Core.Tests.Persistence;

public sealed class JobHistoryStoreTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly DatabaseFixture _fixture = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _loops = [];

    private JobQueue StartQueue(PrefillDatabase db, params IPrefillModule[] modules)
    {
        var queue = new JobQueue(modules, _fixture.Time, [db.JobHistory]);
        _loops.Add(queue.RunAsync(_stop.Token));
        return queue;
    }

    [Fact]
    public async Task A_finished_job_is_kept_with_its_per_game_outcome_bytes_and_duration_across_a_restart()
    {
        var db = await _fixture.OpenAsync();
        var steam = new FakeModule("steam");
        var queue = StartQueue(db, steam);
        var enqueuedAt = _fixture.Time.GetUtcNow();
        var job = queue.Enqueue("steam", new PrefillRequest(["440", "570"]));
        await Eventually(() => steam.Runs.Count == 1);

        _fixture.Time.Advance(TimeSpan.FromSeconds(10));
        steam.Runs[0].Complete(new PrefillResult(
        [
            new GameOutcome("440", GameOutcomeStatus.Downloaded, BytesDownloaded: 50_000_000),
            new GameOutcome("570", GameOutcomeStatus.Failed, FailureReason: "No depots for windows"),
        ]));
        await EventuallyAsync(async () => (await db.JobHistory.GetRecentAsync(10, Ct)).Count == 1);

        var restarted = await _fixture.OpenAsync();
        var record = Assert.Single(await restarted.JobHistory.GetRecentAsync(10, Ct));

        Assert.Equal(job.Id, record.JobId);
        Assert.Equal("steam", record.ModuleId);
        Assert.Equal(JobState.Succeeded, record.State);
        Assert.Equal(enqueuedAt, record.EnqueuedAt);
        Assert.Equal(TimeSpan.FromSeconds(10), record.Duration);
        Assert.Equal(50_000_000, record.BytesDownloaded);
        Assert.Equal(5_000_000, record.AverageBytesPerSecond);
        Assert.Equal(
        [
            new GameOutcome("440", GameOutcomeStatus.Downloaded, BytesDownloaded: 50_000_000),
            new GameOutcome("570", GameOutcomeStatus.Failed, FailureReason: "No depots for windows"),
        ], record.Games);
    }

    private static Task Eventually(Func<bool> condition) => EventuallyAsync(() => Task.FromResult(condition()));

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Timeout);
        while (!await condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        foreach (var loop in _loops)
        {
            try { await loop.WaitAsync(Timeout); } catch (OperationCanceledException) { }
        }
        _stop.Dispose();
        await _fixture.DisposeAsync();
    }
}
