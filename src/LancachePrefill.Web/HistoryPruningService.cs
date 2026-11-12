using LancachePrefill.Core.Persistence;

namespace LancachePrefill.Web;

/// <summary>Prunes Job history once at startup and then daily, using the retention from the settings.</summary>
public sealed class HistoryPruningService(PrefillDatabase db, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = await db.Settings.GetGlobalAsync(stoppingToken);
            await db.JobHistory.PruneAsync(settings.HistoryRetention, stoppingToken);
            await Task.Delay(TimeSpan.FromDays(1), time, stoppingToken);
        }
    }
}
