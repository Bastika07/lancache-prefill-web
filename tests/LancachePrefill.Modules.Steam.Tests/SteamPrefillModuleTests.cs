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
