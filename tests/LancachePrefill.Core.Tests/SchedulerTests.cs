using LancachePrefill.Core;
using Microsoft.Extensions.Time.Testing;

namespace LancachePrefill.Core.Tests;

public sealed class SchedulerTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // Wednesday 2026-01-07 00:00 UTC — winter, so Europe/Berlin is UTC+1.
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 7, 0, 0, 0, TimeSpan.Zero));
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _loops = [];

    private (JobQueue Queue, Scheduler Scheduler) Start(IPrefillModule[] modules, Schedule[] schedules, TimeZoneInfo? timeZone = null)
    {
        var queue = new JobQueue(modules);
        var scheduler = new Scheduler(queue, schedules, timeZone ?? TimeZoneInfo.Utc, _time, moduleId => new PrefillRequest([$"{moduleId}-selection"]));
        _loops.Add(queue.RunAsync(_stop.Token));
        _loops.Add(scheduler.RunAsync(_stop.Token));
        return (queue, scheduler);
    }

    [Fact]
    public async Task A_schedule_enqueues_one_job_per_module_when_its_cron_time_arrives_and_not_before()
    {
        var steam = new FakeModule("steam");
        var epic = new FakeModule("epic");
        Start([steam, epic], [new Schedule("Nightly", "0 3 * * *", ["steam", "epic"])]);

        _time.Advance(TimeSpan.FromHours(3) - TimeSpan.FromSeconds(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(steam.Runs);

        _time.Advance(TimeSpan.FromSeconds(1));
        await Eventually(() => steam.Runs.Count == 1);

        Assert.Equal(["steam-selection"], steam.Runs[0].Request.GameIds);
        steam.Runs[0].Complete(PrefillResult.Empty);
        await Eventually(() => epic.Runs.Count == 1);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Timeout);
        while (!condition())
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
    }
}
