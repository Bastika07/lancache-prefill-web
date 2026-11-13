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

        _steam.Runs[0].Progress.Report(new PrefillProgress("Downloading", "440", BytesDone: 50 * 1_048_576L, BytesTotal: 200 * 1_048_576L));

        page.WaitForAssertion(() =>
        {
            var rows = page.FindAll("[data-testid=active-job]");
            Assert.Equal(2, rows.Count);
            Assert.Contains("Running", rows[0].TextContent, StringComparison.Ordinal);
            Assert.Contains("Downloading 440", rows[0].TextContent, StringComparison.Ordinal);
            Assert.Contains("50.0 MiB / 200.0 MiB", rows[0].TextContent, StringComparison.Ordinal);
            Assert.Equal("25", rows[0].QuerySelector("progress")?.GetAttribute("value"));
            Assert.Contains("Queued", rows[1].TextContent, StringComparison.Ordinal);
            Assert.Equal($"jobs/{running.Id}", rows[0].QuerySelector("a")?.GetAttribute("href"));
            Assert.Equal($"jobs/{waiting.Id}", rows[1].QuerySelector("a")?.GetAttribute("href"));
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
