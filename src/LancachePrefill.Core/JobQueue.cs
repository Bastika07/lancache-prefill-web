using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancachePrefill.Core;

/// <summary>
/// The single global FIFO of Jobs across all Modules; at most one Job runs at a time (ADR 0004).
/// <see cref="RunAsync"/> is the processing loop and is meant to be hosted by a background service.
/// </summary>
public sealed partial class JobQueue
{
    private readonly Dictionary<string, IPrefillModule> _modules;
    private readonly Channel<Job> _pending = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, Job> _jobs = new();
    private readonly List<Job> _jobsInOrder = [];
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<IJobFinishedListener> _listeners;
    private readonly ILogger _logger;

    // Guards the hand-over from "queued" to "running" so Cancel never misses a Job in between.
    private readonly Lock _gate = new();
    private Job? _runningJob;
    private CancellationTokenSource? _runningCancellation;

    public JobQueue(
        IEnumerable<IPrefillModule> modules,
        TimeProvider? time = null,
        IEnumerable<IJobFinishedListener>? listeners = null,
        ILogger<JobQueue>? logger = null)
    {
        _modules = modules.ToDictionary(m => m.Id);
        _time = time ?? TimeProvider.System;
        _listeners = [.. listeners ?? []];
        _logger = logger ?? NullLogger<JobQueue>.Instance;
    }

    public Job Enqueue(string moduleId, PrefillRequest request)
    {
        if (!_modules.ContainsKey(moduleId))
        {
            throw new ArgumentException($"No Module with id '{moduleId}' is registered.", nameof(moduleId));
        }
        var job = new Job(moduleId, request, _time.GetUtcNow());
        lock (_gate)
        {
            _jobs[job.Id] = job;
            _jobsInOrder.Add(job);
        }
        _pending.Writer.TryWrite(job);
        return job;
    }

    /// <summary>Every Job since app start, in enqueue order, with its live state.</summary>
    public IReadOnlyList<Job> Jobs
    {
        get
        {
            lock (_gate)
            {
                return [.. _jobsInOrder];
            }
        }
    }

    /// <summary>
    /// Enqueues a Job unless the Module already has a queued or running one (the Schedule skip rule).
    /// Check and enqueue happen atomically.
    /// </summary>
    public bool TryEnqueue(string moduleId, PrefillRequest request, [NotNullWhen(true)] out Job? job)
    {
        lock (_gate)
        {
            if (_jobs.Values.Any(j => j.ModuleId == moduleId && j.State is JobState.Queued or JobState.Running))
            {
                job = null;
                return false;
            }
            job = Enqueue(moduleId, request);
            return true;
        }
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
            if (job.TryCancelQueued(_time.GetUtcNow()))
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
            bool startedHere;
            lock (_gate)
            {
                startedHere = job.TryStart(_time.GetUtcNow());
                if (startedHere)
                {
                    _runningJob = job;
                    _runningCancellation = cancellation;
                }
            }
            if (!startedHere)
            {
                // Cancelled while queued: it already finished in Cancel(); report it now, in queue order.
                await NotifyFinishedAsync(job, stoppingToken);
                continue;
            }
            try
            {
                var module = _modules[job.ModuleId];
                var (request, warnings) = await ResolveSelectionAsync(module, job.Request, cancellation.Token);
                job.ResolvedGameIds = request.GameIds;
                job.Warnings = warnings;
                if (request.GameIds.Count == 0 && warnings.Count > 0)
                {
                    throw new InvalidOperationException(string.Join(" ", warnings));
                }
                var result = await module.PrefillAsync(request, new JobProgress(job), cancellation.Token);
                job.MarkSucceeded(result, _time.GetUtcNow());
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                job.MarkCancelled(_time.GetUtcNow());
                if (stoppingToken.IsCancellationRequested)
                {
                    // Shutting down: still record the Job; CancellationToken.None so the write itself isn't cancelled.
                    await NotifyFinishedAsync(job, CancellationToken.None);
                    throw;
                }
            }
            catch (Exception ex)
            {
                // A broken Module must not stall the Job queue for every other Module.
                job.MarkFailed(ex.Message, _time.GetUtcNow());
            }
            finally
            {
                lock (_gate)
                {
                    _runningJob = null;
                    _runningCancellation = null;
                }
            }
            await NotifyFinishedAsync(job, stoppingToken);
        }
    }

    /// <summary>
    /// Explicit Games first, then each rule's Games in rule order; duplicates dropped.
    /// A rule that fails becomes a warning so one unreachable service doesn't sink the whole Job.
    /// </summary>
    private static async Task<(PrefillRequest Request, IReadOnlyList<string> Warnings)> ResolveSelectionAsync(
        IPrefillModule module, PrefillRequest request, CancellationToken ct)
    {
        var gameIds = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        void Add(IEnumerable<string> ids) => gameIds.AddRange(ids.Where(seen.Add));

        Add(request.GameIds);
        foreach (var rule in request.Rules)
        {
            try
            {
                Add(await module.ResolveRuleAsync(rule, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                warnings.Add($"Rule {rule} could not be resolved: {ex.Message}");
            }
        }
        return (request with { GameIds = gameIds, Rules = [] }, warnings);
    }

    private async Task NotifyFinishedAsync(Job job, CancellationToken ct)
    {
        foreach (var listener in _listeners)
        {
            try
            {
                await listener.OnJobFinishedAsync(job, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failing listener (e.g. history store) must not stop the Job queue.
                LogListenerFailed(_logger, ex, listener.GetType().Name, job.Id);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Job-finished listener {Listener} failed for Job {JobId}")]
    private static partial void LogListenerFailed(ILogger logger, Exception exception, string listener, Guid jobId);

    /// <summary>
    /// Writes progress straight onto the Job. Unlike <see cref="Progress{T}"/> it reports
    /// synchronously, so reports cannot arrive out of order.
    /// </summary>
    private sealed class JobProgress(Job job) : IProgress<PrefillProgress>
    {
        public void Report(PrefillProgress value) => job.ReportProgress(value);
    }
}
