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

    private (JobQueue Queue, Scheduler Scheduler) Start(
        IPrefillModule[] modules, Schedule[] schedules, TimeZoneInfo? timeZone = null, IScheduleFiredListener[]? listeners = null,
        Func<string, Selection>? selectionFor = null)
    {
        var queue = new JobQueue(modules);
        var scheduler = new Scheduler(queue, schedules, timeZone ?? TimeZoneInfo.Utc, _time,
            new FakeSelectionStore(selectionFor ?? (moduleId => new Selection([$"{moduleId}-selection"], []))), listeners);
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

    [Fact]
    public async Task A_schedule_fires_again_at_every_later_occurrence()
    {
        var steam = new FakeModule("steam") { AutoComplete = true };
        Start([steam], [new Schedule("Nightly", "0 3 * * *", ["steam"])]);

        _time.Advance(TimeSpan.FromHours(3));
        await Eventually(() => steam.Runs.Count == 1);
        _time.Advance(TimeSpan.FromDays(1));
        await Eventually(() => steam.Runs.Count == 2);
        _time.Advance(TimeSpan.FromDays(1));
        await Eventually(() => steam.Runs.Count == 3);
    }

    [Fact]
    public async Task Cron_times_are_read_in_the_configured_time_zone()
    {
        var steam = new FakeModule("steam") { AutoComplete = true };
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        Start([steam], [new Schedule("Nightly", "0 3 * * *", ["steam"])], berlin);

        // 03:00 Berlin in January is 02:00 UTC.
        _time.Advance(TimeSpan.FromHours(2) - TimeSpan.FromSeconds(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(steam.Runs);

        _time.Advance(TimeSpan.FromSeconds(1));
        await Eventually(() => steam.Runs.Count == 1);
    }

    [Fact]
    public async Task A_module_that_already_has_a_queued_or_running_job_is_skipped_and_the_skip_is_recorded()
    {
        var steam = new FakeModule("steam");
        var epic = new FakeModule("epic");
        var (queue, scheduler) = Start([steam, epic], [new Schedule("Nightly", "0 3 * * *", ["steam", "epic"])]);
        var manual = queue.Enqueue("steam", new PrefillRequest(["440"]));
        await Eventually(() => steam.Runs.Count == 1);

        _time.Advance(TimeSpan.FromHours(3));
        await Eventually(() => scheduler.Firings.Count == 1);

        var firing = scheduler.Firings[0];
        Assert.Equal("Nightly", firing.ScheduleName);
        Assert.Equal(_time.GetUtcNow(), firing.At);
        var steamFiring = Assert.Single(firing.Modules, m => m.ModuleId == "steam");
        var epicFiring = Assert.Single(firing.Modules, m => m.ModuleId == "epic");
        Assert.Null(steamFiring.Job);
        Assert.NotNull(epicFiring.Job);

        steam.Runs[0].Complete(PrefillResult.Empty);
        await Eventually(() => epic.Runs.Count == 1);
        Assert.Single(steam.Runs);
        Assert.Equal(JobState.Succeeded, manual.State);
    }

    [Fact]
    public async Task Each_schedule_fires_only_its_own_modules_at_its_own_time()
    {
        var steam = new FakeModule("steam") { AutoComplete = true };
        var epic = new FakeModule("epic") { AutoComplete = true };
        var (_, scheduler) = Start([steam, epic],
        [
            new Schedule("Steam nightly", "0 3 * * *", ["steam"]),
            new Schedule("Epic mornings", "0 6 * * *", ["epic"]),
        ]);

        _time.Advance(TimeSpan.FromHours(3));
        await Eventually(() => scheduler.Firings.Count == 1);
        // The firing is recorded on enqueue; the Job queue starts the Job asynchronously.
        await Eventually(() => steam.Runs.Count == 1);
        Assert.Empty(epic.Runs);

        _time.Advance(TimeSpan.FromHours(3));
        await Eventually(() => scheduler.Firings.Count == 2);
        await Eventually(() => epic.Runs.Count == 1);
        Assert.Single(steam.Runs);
        Assert.Equal(["Steam nightly", "Epic mornings"], scheduler.Firings.Select(f => f.ScheduleName));
    }

    [Fact]
    public async Task The_default_schedule_prefills_every_given_module_at_three_in_the_night()
    {
        var steam = new FakeModule("steam") { AutoComplete = true };
        var epic = new FakeModule("epic") { AutoComplete = true };
        Start([steam, epic], [Schedule.DefaultNightly(["steam", "epic"])]);

        _time.Advance(TimeSpan.FromHours(3) - TimeSpan.FromSeconds(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(steam.Runs);

        _time.Advance(TimeSpan.FromSeconds(1));
        await Eventually(() => steam.Runs.Count == 1 && epic.Runs.Count == 1);
    }

    [Fact]
    public async Task A_failing_firing_listener_does_not_stop_later_firings()
    {
        var steam = new FakeModule("steam") { AutoComplete = true };
        Start([steam], [new Schedule("Nightly", "0 3 * * *", ["steam"])], listeners: [new ThrowingListener()]);

        _time.Advance(TimeSpan.FromHours(3));
        await Eventually(() => steam.Runs.Count == 1);
        _time.Advance(TimeSpan.FromDays(1));
        await Eventually(() => steam.Runs.Count == 2);
    }

    [Fact]
    public async Task A_saved_selection_the_module_rejects_skips_only_that_module_and_later_firings_still_happen()
    {
        var steam = new FakeModule("steam") { AutoComplete = true };
        var battlenet = new FakeModule("battlenet") { AutoComplete = true };
        var (_, scheduler) = Start([battlenet, steam], [new Schedule("Nightly", "0 3 * * *", ["battlenet", "steam"])],
            selectionFor: id => id == "battlenet"
                ? new Selection([], [SelectionRule.RecentlyPlayed])
                : new Selection(["440"], []));

        _time.Advance(TimeSpan.FromHours(3));
        await Eventually(() => steam.Runs.Count == 1);
        var firing = Assert.Single(scheduler.Firings);
        Assert.Null(Assert.Single(firing.Modules, m => m.ModuleId == "battlenet").Job);
        Assert.Empty(battlenet.Runs);

        _time.Advance(TimeSpan.FromDays(1));
        await Eventually(() => steam.Runs.Count == 2);
    }

    [Fact]
    public async Task Replaced_schedules_take_effect_at_once_even_when_the_new_time_is_sooner_than_the_old()
    {
        var steam = new FakeModule("steam") { AutoComplete = true };
        var (_, scheduler) = Start([steam], [new Schedule("Nightly", "0 3 * * *", ["steam"])]);
        _time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        scheduler.ReplaceSchedules([new Schedule("Early", "0 2 * * *", ["steam"])]);
        Assert.Equal(["Early"], scheduler.Schedules.Select(s => s.Name));

        _time.Advance(TimeSpan.FromHours(1));
        await Eventually(() => steam.Runs.Count == 1);
        Assert.Equal("Early", Assert.Single(scheduler.Firings).ScheduleName);

        _time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Single(steam.Runs);
    }

    private sealed class ThrowingListener : IScheduleFiredListener
    {
        public Task OnScheduleFiredAsync(ScheduleFiring firing, CancellationToken ct) =>
            throw new InvalidOperationException("database is locked");
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
