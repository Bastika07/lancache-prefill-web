using Bunit;
using LancachePrefill.Core;
using LancachePrefill.Core.Persistence;
using LancachePrefill.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace LancachePrefill.Web.Tests.JobView;

public sealed class JobsPageTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly BunitContext _ctx = new();
    private readonly BlockingModule _steam = new("steam");
    private readonly FakeJobHistory _history = new();
    private readonly JobQueue _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public JobsPageTests()
    {
        _queue = new JobQueue([_steam]);
        _loop = _queue.RunAsync(_stop.Token);
        _ctx.Services.AddSingleton(_queue);
        _ctx.Services.AddSingleton<IJobHistoryStore>(_history);
    }

    [Fact]
    public void The_running_job_shows_its_live_progress_and_waiting_jobs_are_listed_after_it()
    {
        var running = _queue.Enqueue("steam", new PrefillRequest(["440"]));
        var waiting = _queue.Enqueue("steam", new PrefillRequest(["570"]));
        var page = _ctx.Render<Jobs>();
        page.WaitForAssertion(() => Assert.Single(_steam.Runs), Wait);

        _steam.Runs[0].Progress.Report(new PrefillProgress("Downloading", "440", BytesDone: 50 * 1_048_576L, BytesTotal: 200 * 1_048_576L, CurrentGameName: "Team Fortress 2"));

        page.WaitForAssertion(() =>
        {
            var rows = page.FindAll("[data-testid=active-job]");
            Assert.Equal(2, rows.Count);
            Assert.Contains("Running", rows[0].TextContent, StringComparison.Ordinal);
            Assert.Contains("Downloading Team Fortress 2", rows[0].TextContent, StringComparison.Ordinal);
            Assert.Contains("50.0 MiB / 200.0 MiB", rows[0].TextContent, StringComparison.Ordinal);
            Assert.Equal("25", rows[0].QuerySelector("progress")?.GetAttribute("value"));
            Assert.Contains("Queued", rows[1].TextContent, StringComparison.Ordinal);
            Assert.Equal($"jobs/{running.Id}", rows[0].QuerySelector("a")?.GetAttribute("href"));
            Assert.Equal($"jobs/{waiting.Id}", rows[1].QuerySelector("a")?.GetAttribute("href"));
        }, Wait);
    }

    [Fact]
    public void Cancel_stops_a_running_job_and_removes_a_waiting_one_from_the_queue()
    {
        var running = _queue.Enqueue("steam", new PrefillRequest(["440"]));
        var waiting = _queue.Enqueue("steam", new PrefillRequest(["570"]));
        var page = _ctx.Render<Jobs>();
        page.WaitForAssertion(() => Assert.Single(_steam.Runs), Wait);

        page.WaitForElements("[data-testid=active-job]", 2, Wait)[1].QuerySelector("button.cancel")!.Click();
        page.WaitForAssertion(() => Assert.Equal(JobState.Cancelled, waiting.State), Wait);
        page.Find("[data-testid=active-job] button.cancel").Click();

        page.WaitForAssertion(() => Assert.Equal(JobState.Cancelled, running.State), Wait);
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("[data-testid=active-job]")), Wait);
    }

    [Fact]
    public void The_history_lists_finished_jobs_newest_first_with_duration_bytes_speed_and_game_count()
    {
        var start = new DateTimeOffset(2026, 11, 12, 3, 0, 0, TimeSpan.Zero);
        var older = Record(JobState.Succeeded, start.AddDays(-1), TimeSpan.FromMinutes(1),
            [new GameOutcome("440", GameOutcomeStatus.UpToDate)]);
        var newer = Record(JobState.Cancelled, start, TimeSpan.FromSeconds(100),
            [new GameOutcome("440", GameOutcomeStatus.Downloaded, BytesDownloaded: 1_000_000_000), new GameOutcome("570", GameOutcomeStatus.Failed, FailureReason: "timeout")]);
        _history.Records.AddRange([older, newer]);

        var page = _ctx.Render<Jobs>();

        page.WaitForAssertion(() =>
        {
            var rows = page.FindAll("[data-testid=history-job]");
            Assert.Equal(2, rows.Count);
            var text = rows[0].TextContent;
            Assert.Contains("Cancelled", text, StringComparison.Ordinal);
            Assert.Contains("1m 40s", text, StringComparison.Ordinal);
            Assert.Contains("953.7 MiB", text, StringComparison.Ordinal);
            Assert.Contains("10.0 MB/s", text, StringComparison.Ordinal);
            Assert.Contains("2 Games", text, StringComparison.Ordinal);
            Assert.Contains("1 failed", text, StringComparison.Ordinal);
            Assert.Equal($"jobs/{newer.JobId}", rows[0].QuerySelector("a")?.GetAttribute("href"));
            Assert.Contains("Succeeded", rows[1].TextContent, StringComparison.Ordinal);
        }, Wait);
    }

    [Fact]
    public void A_job_that_just_finished_moves_from_the_queue_into_the_history()
    {
        var job = _queue.Enqueue("steam", new PrefillRequest(["440"]));
        var page = _ctx.Render<Jobs>();
        page.WaitForAssertion(() => Assert.Single(_steam.Runs), Wait);

        _steam.Runs[0].Result.SetResult(new PrefillResult([new GameOutcome("440", GameOutcomeStatus.UpToDate)]));
        page.WaitForAssertion(() => Assert.Equal(JobState.Succeeded, job.State), Wait);
        _history.Records.Add(Record(JobState.Succeeded, job.StartedAt!.Value, TimeSpan.Zero, job.Result!.Games, job.Id));

        page.WaitForAssertion(() =>
        {
            Assert.Empty(page.FindAll("[data-testid=active-job]"));
            Assert.Single(page.FindAll("[data-testid=history-job]"));
        }, Wait);
    }

    internal static JobRecord Record(JobState state, DateTimeOffset started, TimeSpan duration, IReadOnlyList<GameOutcome> games, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), "steam", state, started, started, started + duration, null, games);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await _loop.WaitAsync(Wait); } catch (OperationCanceledException) { }
        _stop.Dispose();
        await _ctx.DisposeAsync();
    }
}
