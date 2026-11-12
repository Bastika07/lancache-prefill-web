using Microsoft.Extensions.Hosting;

namespace LancachePrefill.Core;

/// <summary>Runs the <see cref="JobQueue"/> loop for the lifetime of the app.</summary>
public sealed class JobQueueService(JobQueue queue) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => queue.RunAsync(stoppingToken);
}

/// <summary>Runs the <see cref="Scheduler"/> loop for the lifetime of the app.</summary>
public sealed class SchedulerService(Scheduler scheduler) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => scheduler.RunAsync(stoppingToken);
}
