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

    // Guards the hand-over from "queued" to "running" so Cancel never misses a Job in between.
    private readonly Lock _gate = new();
    private Job? _runningJob;
    private CancellationTokenSource? _runningCancellation;

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

    /// <summary>Cancels a queued or running Job. Returns false if the Job is unknown or already finished.</summary>
    public bool Cancel(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return false;
        }
        lock (_gate)
        {
            if (job.TryCancelQueued())
            {
                return true;
            }
            if (_runningJob == job)
            {
                _runningCancellation!.Cancel();
                return true;
            }
            return false;
        }
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _pending.Reader.ReadAllAsync(stoppingToken))
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (_gate)
            {
                if (!job.TryStart())
                {
                    continue;
                }
                _runningJob = job;
                _runningCancellation = cancellation;
            }
            try
            {
                var result = await _modules[job.ModuleId].PrefillAsync(job.Request, new Progress<PrefillProgress>(), cancellation.Token);
                job.MarkSucceeded(result);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                job.MarkCancelled();
                stoppingToken.ThrowIfCancellationRequested();
            }
            catch (Exception ex)
            {
                // A broken Module must not stall the Job queue for every other Module.
                job.MarkFailed(ex.Message);
            }
            finally
            {
                lock (_gate)
                {
                    _runningJob = null;
                    _runningCancellation = null;
                }
            }
        }
    }
}
