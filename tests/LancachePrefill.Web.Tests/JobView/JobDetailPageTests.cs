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

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await _loop.WaitAsync(Wait); } catch (OperationCanceledException) { }
        _stop.Dispose();
        await _ctx.DisposeAsync();
    }
}
