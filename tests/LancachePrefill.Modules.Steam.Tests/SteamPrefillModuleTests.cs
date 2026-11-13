using System.Net;
using LancachePrefill.Core;
using LancachePrefill.Core.Lancache;
using LancachePrefill.Core.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancachePrefill.Modules.Steam.Tests;

/// <summary>
/// The Steam prefill flow end to end: <see cref="SteamPrefillModule.PrefillAsync"/> against the fixture-backed
/// Steam session and a fake Lancache. Real Steam/CDN behaviour is covered by the manual smoke test.
/// </summary>
public sealed class SteamPrefillModuleTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly FakeSteamSession _steam = new();
    private readonly InMemoryDownloadedDepots _downloaded = new();
    private readonly FakeLancache _lancache = new();
    private readonly HttpClient _http;
    private readonly SteamPrefillModule _module;
    private readonly RecordingProgress _progress = new();

    public SteamPrefillModuleTests()
    {
        _http = new HttpClient(_lancache);
        _module = new SteamPrefillModule(
            _steam,
            new DefaultSettings(),
            _downloaded,
            new LancacheDetector(new NoDns(), _http, lancacheIpEnv: "10.13.37.2"),
            new SteamChunkDownloader(_http),
            new NoLibrary(),
            new SteamChartsClient(_http),
            TimeProvider.System,
            NullLogger<SteamPrefillModule>.Instance);
    }

    private Task<PrefillResult> PrefillAsync(params string[] gameIds) =>
        _module.PrefillAsync(new PrefillRequest(gameIds), _progress, Ct);

    [Fact]
    public async Task Each_game_is_downloaded_through_the_lancache_reported_by_name_and_up_to_date_on_the_next_run()
    {
        _steam.AddSimpleGame(5000, "Alpha");
        _steam.AddSimpleGame(5100, "Bravo");

        var result = await PrefillAsync("5100", "5000", "abc");

        Assert.Equal(
            [
                new GameOutcome("abc", GameOutcomeStatus.Skipped, FailureReason: "Not a Steam app id."),
                new GameOutcome("5000", GameOutcomeStatus.Downloaded, 1_048_576, GameName: "Alpha"),
                new GameOutcome("5100", GameOutcomeStatus.Downloaded, 1_048_576, GameName: "Bravo"),
            ],
            result.Games);
        Assert.Equal([FakeSteamSession.SimpleGameChunk(5000), FakeSteamSession.SimpleGameChunk(5100)], _lancache.ChunkRequests.Order());
        Assert.All(_lancache.Hosts, host => Assert.Equal("cache1-fra1.steamcontent.com", host));
        Assert.Equal(result.Games, _progress.Finished);

        var again = await PrefillAsync("5000", "5100");

        Assert.Equal(
            [
                new GameOutcome("5000", GameOutcomeStatus.UpToDate, GameName: "Alpha"),
                new GameOutcome("5100", GameOutcomeStatus.UpToDate, GameName: "Bravo"),
            ],
            again.Games);
    }

    [Fact]
    public async Task A_chunk_that_keeps_failing_fails_only_its_own_game_which_is_tried_again_next_time()
    {
        _steam.AddSimpleGame(5000, "Alpha");
        _steam.AddSimpleGame(5100, "Bravo");
        var alphaChunk = FakeSteamSession.SimpleGameChunk(5000);
        _lancache.Failing[alphaChunk] = 0;

        var result = await PrefillAsync("5000", "5100");

        var alpha = result.Games[0];
        Assert.Equal(GameOutcomeStatus.Failed, alpha.Status);
        Assert.Contains($"chunk {alphaChunk}", alpha.FailureReason, StringComparison.Ordinal);
        Assert.Equal(new GameOutcome("5100", GameOutcomeStatus.Downloaded, 1_048_576, GameName: "Bravo"), result.Games[1]);
        Assert.Equal(3, _lancache.ChunkRequests.Count(c => c == alphaChunk)); // first try + two retries

        _lancache.Failing.Clear();
        var again = await PrefillAsync("5000", "5100");
        Assert.Equal([GameOutcomeStatus.Downloaded, GameOutcomeStatus.UpToDate], again.Games.Select(g => g.Status));
    }

    [Fact]
    public async Task A_steam_timeout_that_persists_fails_only_its_game_and_does_not_end_the_job()
    {
        _steam.AddSimpleGame(5000, "Alpha");
        _steam.AddSimpleGame(5100, "Bravo");
        _steam.TimingOutDepots.Add(5001);

        var result = await PrefillAsync("5000", "5100");

        Assert.Equal([GameOutcomeStatus.Failed, GameOutcomeStatus.Downloaded], result.Games.Select(g => g.Status));
    }

    [Fact]
    public async Task The_next_game_downloads_while_the_current_one_is_still_finishing_and_results_keep_game_order()
    {
        _steam.AddSimpleGame(5000, "Alpha");
        _steam.AddSimpleGame(5100, "Bravo");
        var alphaChunk = FakeSteamSession.SimpleGameChunk(5000);
        _lancache.Hold(alphaChunk);

        var prefill = PrefillAsync("5000", "5100");

        await Eventually(() => _lancache.ChunkRequests.Contains(FakeSteamSession.SimpleGameChunk(5100)));
        await Task.Delay(50, Ct);
        Assert.Empty(_progress.Finished); // Bravo is done, but Alpha comes first
        var current = _progress.Reports.Last(r => r.FinishedGame is null);
        Assert.Equal("Alpha", current.CurrentGameName);

        _lancache.Release(alphaChunk);
        var result = await prefill;

        Assert.Equal(["Alpha", "Bravo"], result.Games.Select(g => g.GameName));
        Assert.All(result.Games, g => Assert.Equal(GameOutcomeStatus.Downloaded, g.Status));
        Assert.Equal(result.Games, _progress.Finished);
    }

    [Fact]
    public async Task Planning_runs_ahead_only_a_bounded_number_of_games_past_the_oldest_unfinished_one()
    {
        var ids = Enumerable.Range(0, SteamPrefillModule.GamesInFlight + 4).Select(i => (uint)(5000 + (i * 100))).ToList();
        foreach (var (id, i) in ids.Select((id, i) => (id, i)))
        {
            _steam.AddSimpleGame(id, $"Game {i:00}");
        }
        _lancache.Hold(FakeSteamSession.SimpleGameChunk(ids[0]));

        var prefill = PrefillAsync([.. ids.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture))]);

        await Eventually(() => _lancache.ChunkRequests.Count == SteamPrefillModule.GamesInFlight);
        await Task.Delay(100, Ct);
        Assert.Equal(SteamPrefillModule.GamesInFlight, _lancache.ChunkRequests.Count);
        lock (_steam.ManifestDownloads)
        {
            Assert.Equal(SteamPrefillModule.GamesInFlight, _steam.ManifestDownloads.Count);
        }

        _lancache.Release(FakeSteamSession.SimpleGameChunk(ids[0]));
        var result = await prefill;
        Assert.Equal(ids.Count, result.Games.Count(g => g.Status == GameOutcomeStatus.Downloaded));
    }

    [Fact]
    public async Task Cancelling_stops_the_job_promptly_and_keeps_the_games_finished_so_far()
    {
        _steam.AddSimpleGame(5000, "Alpha");
        _steam.AddSimpleGame(5100, "Bravo");
        _steam.AddSimpleGame(5200, "Charlie");
        _lancache.Hold(FakeSteamSession.SimpleGameChunk(5100));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var prefill = _module.PrefillAsync(new PrefillRequest(["5000", "5100", "5200"]), _progress, cancel.Token);
        await Eventually(() => _progress.Finished.Count == 1 && _lancache.ChunkRequests.Contains(FakeSteamSession.SimpleGameChunk(5200)));
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prefill.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal(["Alpha"], _progress.Finished.Select(g => g.GameName));
        Assert.True(await _downloaded.IsDownloadedAsync(5001, 14000, Ct));
        Assert.False(await _downloaded.IsDownloadedAsync(5201, 14200, Ct)); // downloaded, but not reported before the cancel
    }

    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    public void Dispose()
    {
        _module.Dispose();
        _http.Dispose();
        _lancache.Dispose();
    }

    private sealed class RecordingProgress : IProgress<PrefillProgress>
    {
        private readonly Lock _lock = new();
        private readonly List<PrefillProgress> _reports = [];

        public IReadOnlyList<GameOutcome> Finished
        {
            get { lock (_lock) { return [.. _reports.Select(r => r.FinishedGame).OfType<GameOutcome>()]; } }
        }

        public IReadOnlyList<PrefillProgress> Reports
        {
            get { lock (_lock) { return [.. _reports]; } }
        }

        public void Report(PrefillProgress value)
        {
            lock (_lock)
            {
                _reports.Add(value);
            }
        }
    }

    private sealed class DefaultSettings : ISettingsStore
    {
        public Task<GlobalSettings> GetGlobalAsync(CancellationToken ct) => Task.FromResult(GlobalSettings.Default);
        public Task SaveGlobalAsync(GlobalSettings settings, CancellationToken ct) => Task.CompletedTask;
        public Task<T> GetModuleAsync<T>(string moduleId, T defaults, CancellationToken ct) => Task.FromResult(defaults);
        public Task SaveModuleAsync<T>(string moduleId, T settings, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoDns : IDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Task.FromResult<IPAddress[]>([]);
    }

    private sealed class NoLibrary : ISteamLibrary
    {
        public Task<IReadOnlyDictionary<uint, DateTimeOffset>> GetOwnedAppsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<uint>> GetRecentlyPlayedAppIdsAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}
