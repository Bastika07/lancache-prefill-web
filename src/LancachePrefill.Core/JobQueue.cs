using System.Collections.Concurrent;
using System.Threading.Channels;

namespace LancachePrefill.Core;

/// <summary>
/// The single global FIFO of Jobs across all Modules; at most one Job runs at a time (ADR 0004).
/// <see cref="RunAsync"/> is the processing loop and is meant to be hosted by a background service.
/// </summary>
public sealed class JobQueue
{
    private readonly Dictionary<string, IPrefillModule> _modules;
    private readonly Channel<Job> _pending = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, Job> _jobs = new();

    public JobQueue(IEnumerable<IPrefillModule> modules)
    {
        _modules = modules.ToDictionary(m => m.Id);
    }

    public Job Enqueue(string moduleId, PrefillRequest request)
    {
        var job = new Job(moduleId, request);
        _jobs[job.Id] = job;
        _pending.Writer.TryWrite(job);
        return job;
    }

    /// <summary>Cancels a queued Job. Returns false if the Job is unknown or already past the queue.</summary>
    public bool Cancel(Guid jobId) => _jobs.TryGetValue(jobId, out var job) && job.TryCancelQueued();

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _pending.Reader.ReadAllAsync(stoppingToken))
        {
            if (!job.TryStart())
            {
                continue;
            }
            var result = await _modules[job.ModuleId].PrefillAsync(job.Request, new Progress<PrefillProgress>(), stoppingToken);
            job.MarkSucceeded(result);
        }
    }
}
