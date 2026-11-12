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

    internal Job(string moduleId, PrefillRequest request)
    {
        ModuleId = moduleId;
        Request = request;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string ModuleId { get; }
    public PrefillRequest Request { get; }
    public JobState State { get; private set; } = JobState.Queued;
    public PrefillResult? Result { get; private set; }

    /// <summary>Completes when the Job reaches a final state; never faults.</summary>
    public Task Completion => _completion.Task;

    /// <summary>Moves a queued Job to Running; false if it was cancelled meanwhile.</summary>
    internal bool TryStart()
    {
        lock (_lock)
        {
            if (State != JobState.Queued)
            {
                return false;
            }
            State = JobState.Running;
            return true;
        }
    }

    /// <summary>Cancels a Job that has not started yet.</summary>
    internal bool TryCancelQueued()
    {
        lock (_lock)
        {
            if (State != JobState.Queued)
            {
                return false;
            }
            Finish(JobState.Cancelled);
            return true;
        }
    }

    internal void MarkSucceeded(PrefillResult result)
    {
        lock (_lock)
        {
            Result = result;
            Finish(JobState.Succeeded);
        }
    }

    private void Finish(JobState state)
    {
        State = state;
        _completion.TrySetResult();
    }
}
