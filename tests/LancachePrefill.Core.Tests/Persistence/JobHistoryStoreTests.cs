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

    [Fact]
    public async Task Selection_warnings_of_a_job_are_kept_across_a_restart()
    {
        var db = await _fixture.OpenAsync();
        var steam = new FakeModule("steam") { AutoComplete = true };
        steam.FailingRules[SelectionRule.TopPopular(5)] = new HttpRequestException("SteamCharts unreachable");
        var queue = StartQueue(db, steam);

        queue.Enqueue("steam", new PrefillRequest(["440"], Rules: [SelectionRule.TopPopular(5)]));
        await EventuallyAsync(async () => (await db.JobHistory.GetRecentAsync(10, Ct)).Count == 1);

        var restarted = await _fixture.OpenAsync();
        var warning = Assert.Single(Assert.Single(await restarted.JobHistory.GetRecentAsync(10, Ct)).Warnings);
        Assert.Contains("SteamCharts unreachable", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Schedule_firings_including_skipped_modules_are_kept_across_a_restart()
    {
        var db = await _fixture.OpenAsync();
        var steam = new FakeModule("steam");
        var epic = new FakeModule("epic");
        var queue = StartQueue(db, steam, epic);
        var scheduler = new Scheduler(queue, [new Schedule("Nightly", "0 3 * * *", ["steam", "epic"])],
            TimeZoneInfo.Utc, _fixture.Time, id => new PrefillRequest([id]), [db.JobHistory]);
        _loops.Add(scheduler.RunAsync(_stop.Token));
        queue.Enqueue("steam", new PrefillRequest(["440"]));
        await Eventually(() => steam.Runs.Count == 1);

        _fixture.Time.Advance(TimeSpan.FromHours(3));
        await EventuallyAsync(async () => (await db.JobHistory.GetRecentFiringsAsync(10, Ct)).Count == 1);

        var restarted = await _fixture.OpenAsync();
        var firing = Assert.Single(await restarted.JobHistory.GetRecentFiringsAsync(10, Ct));

        var inMemory = Assert.Single(scheduler.Firings);
        Assert.Equal("Nightly", firing.ScheduleName);
        Assert.Equal(_fixture.Time.GetUtcNow(), firing.At);
        Assert.True(Assert.Single(firing.Modules, m => m.ModuleId == "steam").Skipped);
        Assert.Equal(
            inMemory.Modules.Single(m => m.ModuleId == "epic").Job!.Id,
            Assert.Single(firing.Modules, m => m.ModuleId == "epic").JobId);
    }

    [Fact]
    public async Task Pruning_removes_jobs_and_firings_older_than_the_retention_and_keeps_newer_ones()
    {
        var db = await _fixture.OpenAsync();
        var steam = new FakeModule("steam") { AutoComplete = true };
        var queue = StartQueue(db, steam);
        // Fires only on Jan 7 and Feb 7, so moving the clock to day 91 adds nothing new.
        var scheduler = new Scheduler(queue, [new Schedule("Twice", "0 3 7 1,2 *", ["steam"])],
            TimeZoneInfo.Utc, _fixture.Time, id => new PrefillRequest([id]), [db.JobHistory]);
        _loops.Add(scheduler.RunAsync(_stop.Token));

        _fixture.Time.Advance(TimeSpan.FromHours(3));                 // day 0: firing + Job
        await EventuallyAsync(async () => (await db.JobHistory.GetRecentAsync(10, Ct)).Count == 1);
        _fixture.Time.Advance(TimeSpan.FromDays(31));                 // day 31: firing + Job
        await EventuallyAsync(async () => (await db.JobHistory.GetRecentAsync(10, Ct)).Count == 2);
        _fixture.Time.Advance(TimeSpan.FromDays(60));                 // day 91

        await db.JobHistory.PruneAsync(TimeSpan.FromDays(90), Ct);

        var job = Assert.Single(await db.JobHistory.GetRecentAsync(10, Ct));
        var firing = Assert.Single(await db.JobHistory.GetRecentFiringsAsync(10, Ct));
        Assert.Equal(new DateTimeOffset(2026, 2, 7, 3, 0, 0, TimeSpan.Zero), job.FinishedAt);
        Assert.Equal(new DateTimeOffset(2026, 2, 7, 3, 0, 0, TimeSpan.Zero), firing.At);
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
