// Prefill flow ported from SteamPrefill/SteamManager.cs (tpill90/steam-lancache-prefill, MIT). See NOTICE.
using System.Globalization;
using LancachePrefill.Core;
using LancachePrefill.Core.Lancache;
using LancachePrefill.Core.Persistence;
using Microsoft.Extensions.Logging;

namespace LancachePrefill.Modules.Steam;

/// <summary>
/// The Steam Module: Games are Steam app ids. Plans each app, downloads its chunks through the
/// Lancache, and reports one outcome per Game. Upcoming Games are planned while earlier ones download,
/// and one shared chunk downloader runs across Game boundaries (spec → Delivery order 2).
/// </summary>
public sealed partial class SteamPrefillModule(
    ISteamSession steam,
    ISettingsStore settings,
    IDownloadedDepotStore downloaded,
    LancacheDetector lancache,
    SteamChunkDownloader downloader,
    ISteamLibrary library,
    SteamChartsClient charts,
    TimeProvider time,
    ILogger<SteamPrefillModule> logger) : IPrefillModule, IDisposable
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

    private static readonly TimeSpan GameListLifetime = TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim _gameListLock = new(1, 1);
    private (string Key, DateTimeOffset At, IReadOnlyList<GameInfo> Games)? _gameList;

    public void Dispose() => _gameListLock.Dispose();

    /// <summary>
    /// The Account's owned, prefillable Games for the chosen operating systems, by name. Loading it asks
    /// Steam for every owned app, so it is kept for a while per operating-system choice.
    /// </summary>
    public async Task<IReadOnlyList<GameInfo>> ListGamesAsync(CancellationToken ct)
    {
        var steamSettings = await settings.GetModuleAsync(ModuleId, SteamSettings.Default, ct);
        var key = string.Join(",", steamSettings.OperatingSystems.Order(StringComparer.Ordinal));
        await _gameListLock.WaitAsync(ct);
        try
        {
            if (_gameList is { } cached && cached.Key == key && time.GetUtcNow() - cached.At < GameListLifetime)
            {
                return cached.Games;
            }
            var owned = await library.GetOwnedAppsAsync(ct);
            var games = await new SteamDownloadPlanner(steam, downloaded).GetAvailableGamesAsync(owned.Keys, steamSettings, ct);
            IReadOnlyList<GameInfo> list = [.. games.Select(g => new GameInfo(g.AppId.ToString(CultureInfo.InvariantCulture), g.Name))];
            _gameList = (key, time.GetUtcNow(), list);
            return list;
        }
        finally
        {
            _gameListLock.Release();
        }
    }

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
        var steamSettings = await settings.GetModuleAsync(ModuleId, SteamSettings.Default, ct);
        var games = await new SteamDownloadPlanner(steam, downloaded).GetAvailableGamesAsync(candidates, steamSettings, ct);
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
            LogGameFinished(logger, outcome.GameId, outcome.Status, outcome.FailureReason ?? "");
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
        var games = await planner.GetAvailableGamesAsync(appIds, steamSettings, ct);
        var available = games.Select(g => g.AppId).ToHashSet();
        var operatingSystems = string.Join(", ", steamSettings.OperatingSystems);
        LogAvailable(logger, games.Count, appIds.Count, operatingSystems);
        foreach (var unavailable in appIds.Distinct().Where(id => !available.Contains(id)).Select(id => new GameOutcome(
            id.ToString(CultureInfo.InvariantCulture), GameOutcomeStatus.Skipped,
            FailureReason: "Not a prefillable game: not owned, not a game, a mod, or delisted.")))
        {
            Finished(unavailable);
        }

        await using var pipeline = new Pipeline(steam, downloader, logger, planner, games, steamSettings, request.Force, found.Address, progress, ct);
        await pipeline.RunAsync(Finished);
        return new PrefillResult(outcomes);
    }

    /// <summary>How many Games, counted from the oldest unfinished one, may be planned or downloading at once.</summary>
    public const int GamesInFlight = 8;

    /// <summary>How many of those Games may be planned (appinfo, manifests) at the same time.</summary>
    public const int ParallelPlans = 4;

    /// <summary>
    /// One Job's Games as a pipeline: Games are planned up to <see cref="GamesInFlight"/> ahead of the
    /// oldest unfinished one, their chunks go to one shared <see cref="ChunkDownloadSession"/>, and outcomes
    /// are reported (and depots marked downloaded) strictly in Game order.
    /// </summary>
    private sealed class Pipeline : IAsyncDisposable
    {
        private readonly ISteamSession _steam;
        private readonly SteamChunkDownloader _downloader;
        private readonly ILogger _logger;
        private readonly SteamDownloadPlanner _planner;
        private readonly IReadOnlyList<SteamAppInfo> _games;
        private readonly SteamSettings _settings;
        private readonly bool _force;
        private readonly System.Net.IPAddress _lancache;
        private readonly IProgress<PrefillProgress> _progress;
        private readonly CancellationToken _jobToken;
        private readonly CancellationTokenSource _stop;
        private readonly SemaphoreSlim _inFlight = new(GamesInFlight);
        private readonly SemaphoreSlim _planSlots = new(ParallelPlans);
        private readonly Lock _sessionLock = new();
        private readonly GameState[] _states;
        private readonly List<Task> _started = [];
        private Task<ChunkDownloadSession>? _session;
        private int _current;

        public Pipeline(ISteamSession steam, SteamChunkDownloader downloader, ILogger logger, SteamDownloadPlanner planner, IReadOnlyList<SteamAppInfo> games, SteamSettings settings,
            bool force, System.Net.IPAddress lancache, IProgress<PrefillProgress> progress, CancellationToken ct)
        {
            _steam = steam;
            _downloader = downloader;
            _logger = logger;
            _planner = planner;
            _games = games;
            _settings = settings;
            _force = force;
            _lancache = lancache;
            _progress = progress;
            _jobToken = ct;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _states = [.. games.Select(g => new GameState(g))];
        }

        public async Task RunAsync(Action<GameOutcome> finished)
        {
            var launcher = LaunchAsync();
            for (var i = 0; i < _states.Length; i++)
            {
                Volatile.Write(ref _current, i);
                ReportCurrent(_states[i]);
                var (outcome, toMark) = await _states[i].Result.Task.WaitAsync(_jobToken);
                if (toMark is not null)
                {
                    await _planner.MarkDownloadedAsync(toMark, _jobToken);
                }
                finished(outcome);
                _inFlight.Release();
            }
            await launcher;
        }

        /// <summary>Starts each Game in order once fewer than <see cref="GamesInFlight"/> are unfinished.</summary>
        private async Task LaunchAsync()
        {
            foreach (var state in _states)
            {
                await _inFlight.WaitAsync(_stop.Token);
                lock (_started)
                {
                    _started.Add(RunGameAsync(state));
                }
            }
        }

        private async Task RunGameAsync(GameState state)
        {
            var game = state.Game;
            var gameId = game.AppId.ToString(CultureInfo.InvariantCulture);
            try
            {
                SteamAppPlan plan;
                await _planSlots.WaitAsync(_stop.Token);
                try
                {
                    plan = await _planner.PlanAsync(game, _settings, _force, _stop.Token);
                }
                finally
                {
                    _planSlots.Release();
                }
                LogPlanned(_logger, gameId, game.Name, plan.Status, plan.Depots.Count, plan.Chunks.Count, plan.TotalBytes / 1_048_576.0);
                switch (plan.Status)
                {
                    case SteamPlanStatus.NothingToDownload:
                        state.Result.TrySetResult((new GameOutcome(gameId, GameOutcomeStatus.Skipped,
                            FailureReason: "The settings filtered out every depot.", GameName: game.Name), null));
                        return;
                    case SteamPlanStatus.UpToDate:
                        state.Result.TrySetResult((new GameOutcome(gameId, GameOutcomeStatus.UpToDate, GameName: game.Name), null));
                        return;
                }

                state.Start(plan.TotalBytes);
                if (IsCurrent(state))
                {
                    ReportCurrent(state);
                }
                var session = await SessionAsync();
                var bytes = new SyncProgress<long>(n =>
                {
                    state.Add(n);
                    if (IsCurrent(state))
                    {
                        ReportCurrent(state);
                    }
                });
                var result = await session.DownloadAsync(plan.Chunks, bytes, _stop.Token);
                if (result.Succeeded)
                {
                    state.Result.TrySetResult((new GameOutcome(gameId, GameOutcomeStatus.Downloaded, BytesDownloaded: plan.TotalBytes, GameName: game.Name), plan));
                }
                else
                {
                    var first = result.Failures[0];
                    state.Result.TrySetResult((new GameOutcome(gameId, GameOutcomeStatus.Failed,
                        FailureReason: $"{result.Failures.Count} of {plan.Chunks.Count} chunks failed, e.g. depot {first.Chunk.DepotId} chunk {first.Chunk.ChunkId}: {first.Error.Message}",
                        GameName: game.Name), null));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                state.Result.TrySetCanceled(_stop.Token);
            }
            catch (Exception ex)
            {
                // One broken app must not stop the others (upstream: "Skipping app...").
                state.Result.TrySetResult((new GameOutcome(gameId, GameOutcomeStatus.Failed, FailureReason: ex.Message, GameName: game.Name), null));
            }
        }

        private Task<ChunkDownloadSession> SessionAsync()
        {
            lock (_sessionLock)
            {
                return _session ??= StartSessionAsync();
            }

            async Task<ChunkDownloadSession> StartSessionAsync()
            {
                var servers = await _steam.GetCdnServersAsync(_stop.Token);
                return _downloader.Start(_lancache, servers, _settings.MaxConcurrentRequests, _stop.Token);
            }
        }

        private bool IsCurrent(GameState state) => ReferenceEquals(_states[Volatile.Read(ref _current)], state);

        private void ReportCurrent(GameState state)
        {
            var gameId = state.Game.AppId.ToString(CultureInfo.InvariantCulture);
            var (done, total) = state.Bytes;
            _progress.Report(total is null
                ? new PrefillProgress("Planning", gameId, CurrentGameName: state.Game.Name)
                // Retries report their chunks again; cap so progress never passes 100 %.
                : new PrefillProgress("Downloading", gameId, Math.Min(done, total.Value), total.Value, state.Game.Name));
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            Task[] started;
            lock (_started)
            {
                started = [.. _started];
            }
            await Task.WhenAll(started); // each Game catches its own exceptions
            if (_session is { } session)
            {
                try
                {
                    await (await session).DisposeAsync();
                }
                catch (OperationCanceledException)
                {
                    // Never started: cancelled while fetching the CDN servers.
                }
            }
            _stop.Dispose();
            _inFlight.Dispose();
            _planSlots.Dispose();
        }

        /// <summary>One Game in the pipeline: its outcome (plus the plan to mark downloaded) and its bytes.</summary>
        private sealed class GameState(SteamAppInfo game)
        {
            private long _done;
            private long _total = -1;

            public SteamAppInfo Game => game;

            public TaskCompletionSource<(GameOutcome Outcome, SteamAppPlan? ToMark)> Result { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public (long Done, long? Total) Bytes
            {
                get
                {
                    var total = Interlocked.Read(ref _total);
                    return (Interlocked.Read(ref _done), total < 0 ? null : total);
                }
            }

            public void Start(long total) => Interlocked.Exchange(ref _total, total);

            public void Add(long bytes) => Interlocked.Add(ref _done, bytes);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Available} of {Requested} Games are prefillable ({OperatingSystems})")]
    private static partial void LogAvailable(ILogger logger, int available, int requested, string operatingSystems);

    [LoggerMessage(Level = LogLevel.Information, Message = "{GameId} {Name}: {Status}, {Depots} depots, {Chunks} chunks, {MiB:F1} MiB")]
    private static partial void LogPlanned(ILogger logger, string gameId, string name, SteamPlanStatus status, int depots, int chunks, double miB);

    [LoggerMessage(Level = LogLevel.Information, Message = "{GameId}: {Status} {Reason}")]
    private static partial void LogGameFinished(ILogger logger, string gameId, GameOutcomeStatus status, string reason);

    /// <summary>Synchronous IProgress: Progress&lt;T&gt; would post to the thread pool and reorder.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
