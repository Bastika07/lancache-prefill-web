using LancachePrefill.Core;
using Microsoft.Extensions.Logging;

namespace LancachePrefill.Web.Tests.JobView;

/// <summary>A Module whose runs block until the test completes them; it can log from inside its Job.</summary>
public sealed class BlockingModule(string id, ILogger? logger = null) : IPrefillModule
{
    private readonly List<Run> _runs = [];

    public string Id => id;
    public string DisplayName => $"Fake {id}";
    public string? LogOnRun { get; set; }

    public IReadOnlyList<Run> Runs { get { lock (_runs) { return [.. _runs]; } } }

    public Task<PrefillResult> PrefillAsync(PrefillRequest request, IProgress<PrefillProgress> progress, CancellationToken ct)
    {
        if (LogOnRun is not null && logger is not null)
        {
#pragma warning disable CA1848, CA1873 // test double
            logger.LogInformation("{Line}", LogOnRun);
#pragma warning restore CA1848, CA1873
        }
        var run = new Run(request, progress, ct);
        lock (_runs) { _runs.Add(run); }
        return run.Result.Task;
    }

    public sealed class Run
    {
        public Run(PrefillRequest request, IProgress<PrefillProgress> progress, CancellationToken ct)
        {
            Request = request;
            Progress = progress;
            ct.Register(() => Result.TrySetCanceled(ct));
        }

        public PrefillRequest Request { get; }
        public IProgress<PrefillProgress> Progress { get; }
        public TaskCompletionSource<PrefillResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
