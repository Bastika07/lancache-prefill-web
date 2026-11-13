using Cronos;
using LancachePrefill.Core.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancachePrefill.Core;

/// <summary>
/// Fires <see cref="Schedule"/>s at their cron times (in the configured time zone) and enqueues
/// their Jobs on the <see cref="JobQueue"/>. <see cref="RunAsync"/> is meant to be hosted by a
/// background service; all waiting goes through the injected <see cref="TimeProvider"/>.
/// </summary>
public sealed partial class Scheduler
{
    private readonly JobQueue _queue;
    // Replaced as a whole by ReplaceSchedules; the loop reads it under the lock.
    private readonly Lock _schedulesLock = new();
    private IReadOnlyList<(Schedule Schedule, CronExpression Cron)> _schedules;
    private TaskCompletionSource _wake = NewWake();
    private readonly TimeZoneInfo _timeZone;
    private readonly TimeProvider _time;
    private readonly ISelectionStore _selections;
    private readonly IReadOnlyList<IScheduleFiredListener> _listeners;
    private readonly ILogger _logger;

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
        ISelectionStore selections,
        IEnumerable<IScheduleFiredListener>? listeners = null,
        ILogger<Scheduler>? logger = null)
    {
        _queue = queue;
        _schedules = Parse(schedules);
        _timeZone = timeZone;
        _time = time;
        _selections = selections;
        _listeners = [.. listeners ?? []];
        _logger = logger ?? NullLogger<Scheduler>.Instance;
        _checkedUpTo = time.GetUtcNow();
    }

    /// <summary>Every firing since start, oldest first. Persisted history comes with the store (#5).</summary>
    public IReadOnlyList<ScheduleFiring> Firings
    {
        get { lock (_firingsLock) { return [.. _firings]; } }
    }

    /// <summary>The Schedules currently in effect.</summary>
    public IReadOnlyList<Schedule> Schedules
    {
        get { lock (_schedulesLock) { return [.. _schedules.Select(s => s.Schedule)]; } }
    }

    /// <summary>
    /// Puts <paramref name="schedules"/> in effect at once, e.g. after they were edited in the UI.
    /// A running loop wakes up and waits for the new next occurrence.
    /// </summary>
    /// <exception cref="CronFormatException">A cron expression is invalid; nothing is replaced.</exception>
    public void ReplaceSchedules(IEnumerable<Schedule> schedules)
    {
        var parsed = Parse(schedules);
        TaskCompletionSource wake;
        lock (_schedulesLock)
        {
            _schedules = parsed;
            wake = _wake;
            _wake = NewWake();
        }
        wake.TrySetResult();
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            Task wake;
            lock (_schedulesLock)
            {
                wake = _wake.Task;
            }
            var now = _time.GetUtcNow();
            await FireDueAsync(now, stoppingToken);

            var next = NextOccurrenceAfter(now);
            var delay = next is null ? MaxWait : Min(next.Value - now, MaxWait);
            using var delayStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            // ForceYielding: never continue inline on the thread that completed the timer (e.g. inside
            // FakeTimeProvider.Advance), so the loop observes the clock only after it settled.
            await Task.WhenAny(Task.Delay(delay, _time, delayStop.Token), wake).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            await delayStop.CancelAsync();
            stoppingToken.ThrowIfCancellationRequested();
        }
    }

    private static List<(Schedule, CronExpression)> Parse(IEnumerable<Schedule> schedules) =>
        [.. schedules.Select(s => (s, CronExpression.Parse(s.Cron)))];

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IReadOnlyList<(Schedule Schedule, CronExpression Cron)> CurrentSchedules()
    {
        lock (_schedulesLock)
        {
            return _schedules;
        }
    }

    // Task.Delay caps at ~49 days; waking up daily and recomputing is cheap and keeps it simple.
    private static readonly TimeSpan MaxWait = TimeSpan.FromDays(1);

    private async Task FireDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        foreach (var (schedule, cron) in CurrentSchedules())
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
        var modules = new List<ModuleFiring>();
        foreach (var moduleId in schedule.ModuleIds)
        {
            // Update-only: never Force. Rules are resolved by the Job queue when the Job starts.
            var request = (await _selections.GetAsync(moduleId, ct)).ToRequest(force: false);
            Job? job;
            try
            {
                _queue.TryEnqueue(moduleId, request, out job);
            }
            catch (ArgumentException ex)
            {
                // E.g. a saved rule the Module no longer supports: skip this Module, keep the others and the loop.
                LogEnqueueRejected(_logger, ex, schedule.Name, moduleId);
                job = null;
            }
            modules.Add(new ModuleFiring(moduleId, job));
        }
        var firing = new ScheduleFiring(schedule.Name, at, modules);
        lock (_firingsLock)
        {
            _firings.Add(firing);
        }
        foreach (var listener in _listeners)
        {
            try
            {
                await listener.OnScheduleFiredAsync(firing, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failing listener (e.g. history store) must not stop future firings.
                LogListenerFailed(_logger, ex, listener.GetType().Name, schedule.Name);
            }
        }
    }

    private DateTimeOffset? NextOccurrenceAfter(DateTimeOffset now) =>
        CurrentSchedules()
            .Select(s => s.Cron.GetNextOccurrence(now, _timeZone))
            .Where(o => o is not null)
            .Min();

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Schedule {Schedule} skipped Module {ModuleId}: its Selection was rejected")]
    private static partial void LogEnqueueRejected(ILogger logger, Exception exception, string schedule, string moduleId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Schedule-fired listener {Listener} failed for Schedule {ScheduleName}")]
    private static partial void LogListenerFailed(ILogger logger, Exception exception, string listener, string scheduleName);
}
