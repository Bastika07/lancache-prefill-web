namespace LancachePrefill.Core;

public enum JobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>One run of one Module's prefill. See GLOSSARY.md → Job.</summary>
public sealed class Job
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _lock = new();

    internal Job(string moduleId, PrefillRequest request, DateTimeOffset enqueuedAt)
    {
        ModuleId = moduleId;
        Request = request;
        EnqueuedAt = enqueuedAt;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string ModuleId { get; }
    public PrefillRequest Request { get; }
    public JobState State { get; private set; } = JobState.Queued;
    public DateTimeOffset EnqueuedAt { get; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public PrefillResult? Result { get; private set; }
    public string? FailureReason { get; private set; }

    /// <summary>The Job log keeps this many newest lines.</summary>
    public const int MaxLogLines = 1000;

    /// <summary>The newest <see cref="MaxLogLines"/> lines logged while the Job ran.</summary>
    public IReadOnlyList<JobLogLine> Log
    {
        get { lock (_lock) { return [.. _log]; } }
    }
    private readonly Queue<JobLogLine> _log = new();

    internal void AppendLog(JobLogLine line)
    {
        lock (_lock)
        {
            _log.Enqueue(line);
            if (_log.Count > MaxLogLines)
            {
                _log.Dequeue();
            }
        }
    }

    /// <summary>The Games the Module was asked for after resolving the Selection rules; null until the Job starts.</summary>
    public IReadOnlyList<string>? ResolvedGameIds { get; internal set; }

    /// <summary>Problems that did not stop the Job, e.g. a dynamic rule that could not be resolved.</summary>
    public IReadOnlyList<string> Warnings { get; internal set; } = [];

    /// <summary>Latest progress reported by the Module; null until the first report.</summary>
    public PrefillProgress? Progress => Volatile.Read(ref _progress);
    private PrefillProgress? _progress;

    private readonly List<GameOutcome> _finishedGames = [];

    /// <summary>Games the Module reported as finished so far, in order.</summary>
    public IReadOnlyList<GameOutcome> FinishedGames
    {
        get { lock (_lock) { return [.. _finishedGames]; } }
    }

    internal void ReportProgress(PrefillProgress progress)
    {
        if (progress.FinishedGame is { } outcome)
        {
            lock (_lock)
            {
                _finishedGames.Add(outcome);
            }
            return;
        }
        Volatile.Write(ref _progress, progress);
    }

    /// <summary>Completes when the Job reaches a final state; never faults.</summary>
    public Task Completion => _completion.Task;

    /// <summary>Moves a queued Job to Running; false if it was cancelled meanwhile.</summary>
    internal bool TryStart(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (State != JobState.Queued)
            {
                return false;
            }
            State = JobState.Running;
            StartedAt = now;
            return true;
        }
    }

    /// <summary>Cancels a Job that has not started yet.</summary>
    internal bool TryCancelQueued(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (State != JobState.Queued)
            {
                return false;
            }
            Finish(JobState.Cancelled, now);
            return true;
        }
    }

    internal void MarkCancelled(DateTimeOffset now)
    {
        lock (_lock)
        {
            Result = new PrefillResult([.. _finishedGames]);
            Finish(JobState.Cancelled, now);
        }
    }

    internal void MarkFailed(string reason, DateTimeOffset now)
    {
        lock (_lock)
        {
            FailureReason = reason;
            Result = new PrefillResult([.. _finishedGames]);
            Finish(JobState.Failed, now);
        }
    }

    internal void MarkSucceeded(PrefillResult result, DateTimeOffset now)
    {
        lock (_lock)
        {
            Result = result;
            Finish(JobState.Succeeded, now);
        }
    }

    private void Finish(JobState state, DateTimeOffset now)
    {
        State = state;
        FinishedAt = now;
        _completion.TrySetResult();
    }
}
