namespace LancachePrefill.Core;

/// <summary>Told about every Schedule firing, after its Jobs were enqueued or skipped.</summary>
public interface IScheduleFiredListener
{
    Task OnScheduleFiredAsync(ScheduleFiring firing, CancellationToken ct);
}
