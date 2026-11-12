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

    // Occurrences in (_checkedUpTo, now] are due. Starts at construction so nothing is missed
    // between creating the Scheduler and its loop starting.
    private DateTimeOffset _checkedUpTo;

    public Scheduler(
        JobQueue queue,
        IEnumerable<Schedule> schedules,
        TimeZoneInfo timeZone,
        TimeProvider time,
        Func<string, PrefillRequest> requestFor)
    {
        _queue = queue;
        _schedules = [.. schedules.Select(s => (s, CronExpression.Parse(s.Cron)))];
        _timeZone = timeZone;
        _time = time;
        _requestFor = requestFor;
        _checkedUpTo = time.GetUtcNow();
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            var now = _time.GetUtcNow();
            FireDue(now);

            var next = NextOccurrenceAfter(now);
            var delay = next is null ? MaxWait : Min(next.Value - now, MaxWait);
            await Task.Delay(delay, _time, stoppingToken);
        }
    }

    // Task.Delay caps at ~49 days; waking up daily and recomputing is cheap and keeps it simple.
    private static readonly TimeSpan MaxWait = TimeSpan.FromDays(1);

    private void FireDue(DateTimeOffset now)
    {
        foreach (var (schedule, cron) in _schedules)
        {
            var occurrence = cron.GetNextOccurrence(_checkedUpTo, _timeZone);
            if (occurrence is not null && occurrence <= now)
            {
                Fire(schedule);
            }
        }
        _checkedUpTo = now;
    }

    private void Fire(Schedule schedule)
    {
        foreach (var moduleId in schedule.ModuleIds)
        {
            _queue.Enqueue(moduleId, _requestFor(moduleId));
        }
    }

    private DateTimeOffset? NextOccurrenceAfter(DateTimeOffset now) =>
        _schedules
            .Select(s => s.Cron.GetNextOccurrence(now, _timeZone))
            .Where(o => o is not null)
            .Min();

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
