using Cronos;

namespace LancachePrefill.Core;

/// <summary>
/// Fires <see cref="Schedule"/>s at their cron times (in the configured time zone) and enqueues
/// their Jobs on the <see cref="JobQueue"/>. <see cref="RunAsync"/> is meant to be hosted by a
/// background service; all waiting goes through the injected <see cref="TimeProvider"/>.
/// </summary>
public sealed class Scheduler
{
    private readonly JobQueue _queue;
    private readonly IReadOnlyList<(Schedule Schedule, CronExpression Cron)> _schedules;
    private readonly TimeZoneInfo _timeZone;
    private readonly TimeProvider _time;
    private readonly Func<string, PrefillRequest> _requestFor;
    private readonly IReadOnlyList<IScheduleFiredListener> _listeners;

    // Occurrences in (_checkedUpTo, now] are due. Starts at construction so nothing is missed
    // between creating the Scheduler and its loop starting.
    private DateTimeOffset _checkedUpTo;

    private readonly Lock _firingsLock = new();
    private readonly List<ScheduleFiring> _firings = [];

    public Scheduler(
        JobQueue queue,
        IEnumerable<Schedule> schedules,
        TimeZoneInfo timeZone,
        TimeProvider time,
        Func<string, PrefillRequest> requestFor,
        IEnumerable<IScheduleFiredListener>? listeners = null)
    {
        _queue = queue;
        _schedules = [.. schedules.Select(s => (s, CronExpression.Parse(s.Cron)))];
        _timeZone = timeZone;
        _time = time;
        _requestFor = requestFor;
        _listeners = [.. listeners ?? []];
        _checkedUpTo = time.GetUtcNow();
    }

    /// <summary>Every firing since start, oldest first. Persisted history comes with the store (#5).</summary>
    public IReadOnlyList<ScheduleFiring> Firings
    {
        get { lock (_firingsLock) { return [.. _firings]; } }
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            var now = _time.GetUtcNow();
            await FireDueAsync(now, stoppingToken);

            var next = NextOccurrenceAfter(now);
            var delay = next is null ? MaxWait : Min(next.Value - now, MaxWait);
            await Task.Delay(delay, _time, stoppingToken);
        }
    }

    // Task.Delay caps at ~49 days; waking up daily and recomputing is cheap and keeps it simple.
    private static readonly TimeSpan MaxWait = TimeSpan.FromDays(1);

    private async Task FireDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        foreach (var (schedule, cron) in _schedules)
        {
            var occurrence = cron.GetNextOccurrence(_checkedUpTo, _timeZone);
            if (occurrence is not null && occurrence <= now)
            {
                await FireAsync(schedule, now, ct);
            }
        }
        _checkedUpTo = now;
    }

    private async Task FireAsync(Schedule schedule, DateTimeOffset at, CancellationToken ct)
    {
        var modules = schedule.ModuleIds
            .Select(moduleId => new ModuleFiring(moduleId, _queue.TryEnqueue(moduleId, _requestFor(moduleId), out var job) ? job : null))
            .ToList();
        var firing = new ScheduleFiring(schedule.Name, at, modules);
        lock (_firingsLock)
        {
            _firings.Add(firing);
        }
        foreach (var listener in _listeners)
        {
            await listener.OnScheduleFiredAsync(firing, ct);
        }
    }

    private DateTimeOffset? NextOccurrenceAfter(DateTimeOffset now) =>
        _schedules
            .Select(s => s.Cron.GetNextOccurrence(now, _timeZone))
            .Where(o => o is not null)
            .Min();

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
