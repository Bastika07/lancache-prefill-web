using Bunit;
using LancachePrefill.Core;
using LancachePrefill.Core.Persistence;
using LancachePrefill.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LancachePrefill.Web.Tests.JobView;

public sealed class JobDetailPageTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly BunitContext _ctx = new();
    private readonly JobLogProvider _jobLogs = new();
    private readonly BlockingModule _steam;
    private readonly FakeJobHistory _history = new();
    private readonly JobQueue _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public JobDetailPageTests()
    {
        _steam = new BlockingModule("steam", _jobLogs.CreateLogger("Steam"));
        _queue = new JobQueue([_steam]);
        _loop = _queue.RunAsync(_stop.Token);
        _ctx.Services.AddSingleton(_queue);
        _ctx.Services.AddSingleton<IJobHistoryStore>(_history);
        _ctx.Services.AddSingleton(TimeProvider.System);
    }

    private IRenderedComponent<JobDetail> Render(Guid id) => _ctx.Render<JobDetail>(p => p.Add(c => c.Id, id));

    [Fact]
    public void A_running_job_shows_its_games_so_far_its_progress_and_its_log_live()
    {
        _steam.LogOnRun = "Planning 440";
        var job = _queue.Enqueue("steam", new PrefillRequest(["440", "570"]));
        var page = Render(job.Id);
        page.WaitForAssertion(() => Assert.Single(_steam.Runs), Wait);

        _steam.Runs[0].Progress.Report(PrefillProgress.GameFinished(new GameOutcome("440", GameOutcomeStatus.Downloaded, BytesDownloaded: 3 * 1_048_576L)));
        _steam.Runs[0].Progress.Report(new PrefillProgress("Downloading", "570", 1, 4));

        page.WaitForAssertion(() =>
        {
            Assert.Contains("Running", page.Find("[data-testid=job-state]").TextContent, StringComparison.Ordinal);
            Assert.Contains("Downloading 570", page.Find("[data-testid=job-progress]").TextContent, StringComparison.Ordinal);
            var game = Assert.Single(page.FindAll("[data-testid=game]"));
            Assert.Contains("440", game.TextContent, StringComparison.Ordinal);
            Assert.Contains("Downloaded", game.TextContent, StringComparison.Ordinal);
            Assert.Contains("3.0 MiB", game.TextContent, StringComparison.Ordinal);
            Assert.Contains(page.FindAll("[data-testid=log-line]"), l => l.TextContent.Contains("Planning 440", StringComparison.Ordinal));
        }, Wait);
    }

    [Fact]
    public void A_job_from_the_history_shows_its_outcome_figures_warnings_failure_and_log()
    {
        var started = new DateTimeOffset(2026, 11, 12, 3, 0, 0, TimeSpan.Zero);
        var record = new JobRecord(Guid.NewGuid(), "steam", JobState.Failed, started, started, started.AddSeconds(100),
            "No Lancache found; see the dashboard for details.",
            [new GameOutcome("440", GameOutcomeStatus.Downloaded, BytesDownloaded: 1_000_000_000),
             new GameOutcome("570", GameOutcomeStatus.Failed, FailureReason: "12 of 40 chunks failed")],
            ["Rule TopPopular(10) could not be resolved: SteamCharts unreachable"],
            [new JobLogLine(started, LogLevel.Warning, "Steam", "Chunk retry 3/3")]);
        _history.Records.Add(record);

        var page = Render(record.JobId);

        page.WaitForAssertion(() =>
        {
            Assert.Contains("Failed", page.Find("[data-testid=job-state]").TextContent, StringComparison.Ordinal);
            Assert.Contains("953.7 MiB", page.Find("[data-testid=job-bytes]").TextContent, StringComparison.Ordinal);
            Assert.Contains("No Lancache found", page.Find("[data-testid=job-failure]").TextContent, StringComparison.Ordinal);
            Assert.Contains("SteamCharts unreachable", page.Find("[data-testid=job-warning]").TextContent, StringComparison.Ordinal);
            var games = page.FindAll("[data-testid=game]");
            Assert.Equal(2, games.Count);
            Assert.Contains("12 of 40 chunks failed", games[1].TextContent, StringComparison.Ordinal);
            var log = Assert.Single(page.FindAll("[data-testid=log-line]"));
            Assert.Contains("WARN", log.TextContent, StringComparison.Ordinal);
            Assert.Contains("Chunk retry 3/3", log.TextContent, StringComparison.Ordinal);
            Assert.Empty(page.FindAll("button.cancel"));
        }, Wait);
    }

    [Fact]
    public void An_unknown_job_says_so()
    {
        var page = Render(Guid.NewGuid());

        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=job-not-found]")), Wait);
    }

    [Fact]
    public void A_running_job_can_be_cancelled_from_its_page()
    {
        var job = _queue.Enqueue("steam", new PrefillRequest(["440"]));
        var page = Render(job.Id);
        page.WaitForAssertion(() => Assert.Single(_steam.Runs), Wait);

        page.WaitForElement("button.cancel", Wait).Click();

        page.WaitForAssertion(() => Assert.Contains("Cancelled", page.Find("[data-testid=job-state]").TextContent, StringComparison.Ordinal), Wait);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await _loop.WaitAsync(Wait); } catch (OperationCanceledException) { }
        _stop.Dispose();
        await _ctx.DisposeAsync();
    }
}
