// Prefill flow ported from SteamPrefill/SteamManager.cs (tpill90/steam-lancache-prefill, MIT). See NOTICE.
using System.Globalization;
using LancachePrefill.Core;
using LancachePrefill.Core.Lancache;
using LancachePrefill.Core.Persistence;

namespace LancachePrefill.Modules.Steam;

/// <summary>
/// The Steam Module: Games are Steam app ids. Plans each app, downloads its chunks through the
/// Lancache, and reports one outcome per Game. Games run one after another, chunks in parallel
/// (spec → Delivery order 1).
/// </summary>
public sealed class SteamPrefillModule(
    ISteamSession steam,
    ISettingsStore settings,
    IDownloadedDepotStore downloaded,
    LancacheDetector lancache,
    SteamChunkDownloader downloader,
    ISteamLibrary library,
    SteamChartsClient charts,
    TimeProvider time) : IPrefillModule
{
    public const string ModuleId = "steam";
    private static readonly TimeSpan RecentPurchaseWindow = TimeSpan.FromDays(30);

    public string Id => ModuleId;
    public string DisplayName => "Steam";

    public IReadOnlySet<SelectionRuleKind> SupportedRules { get; } = new HashSet<SelectionRuleKind>
    {
        SelectionRuleKind.AllOwned,
        SelectionRuleKind.RecentlyPlayed,
        SelectionRuleKind.RecentlyPurchased,
        SelectionRuleKind.TopPopular,
    };

    /// <summary>
    /// Port of SteamManager.BuildAppIdDownloadListAsync. Results are narrowed to owned, prefillable
    /// games, so e.g. "all owned" doesn't list every DLC and tool as a skipped Game.
    /// </summary>
    public async Task<IReadOnlyList<string>> ResolveRuleAsync(SelectionRule rule, CancellationToken ct)
    {
        IReadOnlyList<uint> candidates = rule.Kind switch
        {
            SelectionRuleKind.AllOwned => [.. (await library.GetOwnedAppsAsync(ct)).Keys],
            SelectionRuleKind.RecentlyPlayed => await library.GetRecentlyPlayedAppIdsAsync(ct),
            SelectionRuleKind.RecentlyPurchased => [.. (await library.GetOwnedAppsAsync(ct))
                .Where(a => a.Value >= time.GetUtcNow() - RecentPurchaseWindow).Select(a => a.Key)],
            SelectionRuleKind.TopPopular => [.. (await charts.GetMostPlayedAppIdsAsync(ct)).Take(rule.Count)],
            _ => throw new NotSupportedException($"Steam does not support the rule {rule}."),
        };
        var games = await new SteamDownloadPlanner(steam, downloaded).GetAvailableGamesAsync(candidates, ct);
        return [.. games.Select(g => g.AppId.ToString(CultureInfo.InvariantCulture))];
    }

    public async Task<PrefillResult> PrefillAsync(PrefillRequest request, IProgress<PrefillProgress> progress, CancellationToken ct)
    {
        var global = await settings.GetGlobalAsync(ct);
        if (await lancache.DetectAsync(global.LancacheIpOverride, ct) is not LancacheStatus.Found found)
        {
            // Downloading without a Lancache would only waste bandwidth.
            throw new InvalidOperationException("No Lancache found; see the dashboard for details.");
        }
        var steamSettings = await settings.GetModuleAsync(ModuleId, SteamSettings.Default, ct);

        var outcomes = new List<GameOutcome>();
        // Reported one by one, so a cancelled or failed Job still records the Games already done.
        void Finished(GameOutcome outcome)
        {
            outcomes.Add(outcome);
            progress.Report(PrefillProgress.GameFinished(outcome));
        }

        var appIds = new List<uint>();
        foreach (var gameId in request.GameIds)
        {
            if (uint.TryParse(gameId, NumberStyles.None, CultureInfo.InvariantCulture, out var appId))
            {
                appIds.Add(appId);
            }
            else
            {
                Finished(new GameOutcome(gameId, GameOutcomeStatus.Skipped, FailureReason: "Not a Steam app id."));
            }
        }

        progress.Report(new PrefillProgress("Loading app metadata"));
        var planner = new SteamDownloadPlanner(steam, downloaded); // fresh per Job: current metadata
        var games = await planner.GetAvailableGamesAsync(appIds, ct);
        var available = games.Select(g => g.AppId).ToHashSet();
        foreach (var unavailable in appIds.Distinct().Where(id => !available.Contains(id)).Select(id => new GameOutcome(
            id.ToString(CultureInfo.InvariantCulture), GameOutcomeStatus.Skipped,
            FailureReason: "Not a prefillable game: not owned, not a game, a mod, or delisted.")))
        {
            Finished(unavailable);
        }

        IReadOnlyList<CdnServer>? cdnServers = null;
        foreach (var game in games)
        {
            ct.ThrowIfCancellationRequested();
            var gameId = game.AppId.ToString(CultureInfo.InvariantCulture);
            try
            {
                progress.Report(new PrefillProgress("Planning", gameId));
                var plan = await planner.PlanAsync(game, steamSettings, request.Force, ct);
                switch (plan.Status)
                {
                    case SteamPlanStatus.NothingToDownload:
                        Finished(new GameOutcome(gameId, GameOutcomeStatus.Skipped, FailureReason: "The settings filtered out every depot."));
                        continue;
                    case SteamPlanStatus.UpToDate:
                        Finished(new GameOutcome(gameId, GameOutcomeStatus.UpToDate));
                        continue;
                }

                cdnServers ??= await steam.GetCdnServersAsync(ct);
                var total = plan.TotalBytes;
                long done = 0;
                // Retries report their chunks again; cap so progress never passes 100 %.
                var bytes = new SyncProgress<long>(n =>
                    progress.Report(new PrefillProgress("Downloading", gameId, Math.Min(Interlocked.Add(ref done, n), total), total)));
                var result = await downloader.DownloadAsync(plan.Chunks, found.Address, cdnServers, steamSettings.MaxConcurrentRequests, bytes, ct);
                if (result.Succeeded)
                {
                    await planner.MarkDownloadedAsync(plan, ct);
                    Finished(new GameOutcome(gameId, GameOutcomeStatus.Downloaded, BytesDownloaded: total));
                }
                else
                {
                    var first = result.Failures[0];
                    Finished(new GameOutcome(gameId, GameOutcomeStatus.Failed,
                        FailureReason: $"{result.Failures.Count} of {plan.Chunks.Count} chunks failed, e.g. depot {first.Chunk.DepotId} chunk {first.Chunk.ChunkId}: {first.Error.Message}"));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One broken app must not stop the others (upstream: "Skipping app...").
                Finished(new GameOutcome(gameId, GameOutcomeStatus.Failed, FailureReason: ex.Message));
            }
        }
        return new PrefillResult(outcomes);
    }

    /// <summary>Synchronous IProgress: Progress&lt;T&gt; would post to the thread pool and reorder.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
