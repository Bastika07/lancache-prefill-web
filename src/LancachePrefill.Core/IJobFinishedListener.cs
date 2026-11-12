namespace LancachePrefill.Core;

/// <summary>
/// Told about every Job once it reaches a final state, in queue order. The Job queue awaits each
/// listener before the next Job starts; a throwing listener is logged and ignored.
/// </summary>
public interface IJobFinishedListener
{
    Task OnJobFinishedAsync(Job job, CancellationToken ct);
}
