// Ported from SteamPrefill/Handlers/DownloadHandler.cs (tpill90/steam-lancache-prefill, MIT). See NOTICE.
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;

namespace LancachePrefill.Modules.Steam;

/// <summary>Result of downloading a set of chunks through the Lancache.</summary>
public sealed record ChunkDownloadResult(IReadOnlyList<(SteamChunkRequest Chunk, Exception Error)> Failures)
{
    public bool Succeeded => Failures.Count == 0;
}

/// <summary>
/// Requests chunks from the Lancache (with the CDN host as <c>Host</c> header) and discards the bytes,
/// so the Lancache stores them. Per depot it probes for a CDN that serves it and fails over on 403;
/// failed chunks are retried twice with <c>?nocache=1</c>.
/// </summary>
public sealed class SteamChunkDownloader(HttpClient http)
{
    public const string HttpClientName = "steam-chunks";
    public const string UserAgent = "Valve/Steam HTTP Client 1.0";

    /// <summary>
    /// Starts <paramref name="maxConcurrentRequests"/> workers that download every chunk handed to the
    /// session, first come first served, so one Game's tail overlaps the next Game's start.
    /// </summary>
    public ChunkDownloadSession Start(IPAddress lancache, IReadOnlyList<CdnServer> cdnServers, int maxConcurrentRequests, CancellationToken ct) =>
        new(http, lancache, cdnServers, maxConcurrentRequests, ct);
}

/// <summary>One shared chunk download pipeline for a Job; see <see cref="SteamChunkDownloader.Start"/>.</summary>
public sealed class ChunkDownloadSession : IAsyncDisposable
{
    private const int Retries = 2;

    private readonly HttpClient _http;
    private readonly IPAddress _lancache;
    private readonly List<CdnServer> _servers;
    private readonly CancellationTokenSource _stop;
    private readonly Channel<WorkItem> _work = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleWriter = false });
    private readonly Task[] _workers;

    private readonly ConcurrentDictionary<uint, Lazy<Task>> _probes = new();
    private readonly ConcurrentDictionary<uint, CdnServer> _depotServers = new();
    private readonly ConcurrentDictionary<uint, ConcurrentDictionary<string, byte>> _blacklist = new();
    private readonly Lock _assignLock = new();

    internal ChunkDownloadSession(HttpClient http, IPAddress lancache, IReadOnlyList<CdnServer> cdnServers, int maxConcurrentRequests, CancellationToken ct)
    {
        _http = http;
        _lancache = lancache;
        _servers = [.. cdnServers.OrderBy(s => s.Load)];
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _workers = [.. Enumerable.Range(0, maxConcurrentRequests).Select(_ => Task.Run(WorkAsync))];
    }

    /// <summary>
    /// Downloads <paramref name="chunks"/> (queued behind chunks handed over earlier) and completes when
    /// all of them are done, after up to two retries of the failed ones.
    /// </summary>
    public async Task<ChunkDownloadResult> DownloadAsync(IReadOnlyList<SteamChunkRequest> chunks, IProgress<long> bytesDone, CancellationToken ct)
    {
        var failures = await AttemptAsync(chunks, forceRecache: false, bytesDone, ct);
        for (var retry = 1; retry <= Retries && failures.Count > 0; retry++)
        {
            failures = await AttemptAsync([.. failures.Select(f => f.Chunk)], forceRecache: true, bytesDone, ct);
        }
        return new ChunkDownloadResult(failures);
    }

    private Task<List<(SteamChunkRequest Chunk, Exception Error)>> AttemptAsync(
        IReadOnlyList<SteamChunkRequest> chunks, bool forceRecache, IProgress<long> bytesDone, CancellationToken ct)
    {
        if (chunks.Count == 0)
        {
            return Task.FromResult<List<(SteamChunkRequest Chunk, Exception Error)>>([]);
        }
        var batch = new Batch(chunks.Count, bytesDone);
        foreach (var chunk in chunks)
        {
            _work.Writer.TryWrite(new WorkItem(chunk, forceRecache, batch));
        }
        return batch.Completion.WaitAsync(ct);
    }

    private async Task WorkAsync()
    {
        var ct = _stop.Token;
        try
        {
            await foreach (var item in _work.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await DownloadWithFailoverAsync(item.Chunk, item.ForceRecache, ct);
                    item.Batch.Done(item.Chunk, null);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    item.Batch.Done(item.Chunk, ex);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Job cancelled or session disposed.
        }
    }

    private async Task DownloadWithFailoverAsync(SteamChunkRequest chunk, bool forceRecache, CancellationToken ct)
    {
        try
        {
            await _probes.GetOrAdd(chunk.DepotId, _ => new Lazy<Task>(() => ProbeAsync(chunk, ct))).Value;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A failed probe only means no server was chosen up front; ServerFor picks one.
        }
        while (true)
        {
            var server = ServerFor(chunk.DepotId)
                ?? throw new InvalidOperationException($"Every CDN server returned 403 for depot {chunk.DepotId}; it may not be on any available CDN.");
            try
            {
                await DownloadChunkAsync(chunk, server, forceRecache, ct);
                return;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
            {
                Blacklist(chunk.DepotId, server.Host); // fail over to the next server
            }
        }
    }

    private CdnServer? ServerFor(uint depotId)
    {
        if (_depotServers.TryGetValue(depotId, out var assigned) && !IsBlacklisted(depotId, assigned.Host))
        {
            return assigned;
        }
        lock (_assignLock)
        {
            if (_depotServers.TryGetValue(depotId, out var current) && !IsBlacklisted(depotId, current.Host))
            {
                return current;
            }
            var next = _servers.FirstOrDefault(s => !IsBlacklisted(depotId, s.Host));
            if (next is not null)
            {
                _depotServers[depotId] = next;
            }
            return next;
        }
    }

    private bool IsBlacklisted(uint depotId, string host) => _blacklist.TryGetValue(depotId, out var hosts) && hosts.ContainsKey(host);
    private void Blacklist(uint depotId, string host) => _blacklist.GetOrAdd(depotId, _ => new()).TryAdd(host, 0);

    private async Task DownloadChunkAsync(SteamChunkRequest chunk, CdnServer server, bool forceRecache, CancellationToken ct)
    {
        var url = $"http://{_lancache}/depot/{chunk.DepotId}/chunk/{chunk.ChunkId}{(forceRecache ? "?nocache=1" : "")}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Host = server.Host;
        request.Headers.UserAgent.ParseAdd(SteamChunkDownloader.UserAgent);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        // Read and discard: the point is that the Lancache caches it on the way through.
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await body.CopyToAsync(Stream.Null, ct);
    }

    /// <summary>Finds the first CDN (by load) that serves the depot's first requested chunk; 403s are blacklisted.</summary>
    private async Task ProbeAsync(SteamChunkRequest probe, CancellationToken ct)
    {
        foreach (var server in _servers)
        {
            try
            {
                var url = $"http://{_lancache}/depot/{probe.DepotId}/chunk/{probe.ChunkId}?nocache=1";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Host = server.Host;
                request.Headers.UserAgent.ParseAdd(SteamChunkDownloader.UserAgent);
                request.Headers.Range = new RangeHeaderValue(0, 0);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    Blacklist(probe.DepotId, server.Host);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                _depotServers[probe.DepotId] = server;
                return;
            }
            catch (HttpRequestException)
            {
                // Try the next server; the download phase reports real failures.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _work.Writer.TryComplete();
        await _stop.CancelAsync();
        await Task.WhenAll(_workers);
        _stop.Dispose();
    }

    private sealed record WorkItem(SteamChunkRequest Chunk, bool ForceRecache, Batch Batch);

    /// <summary>The chunks of one <see cref="AttemptAsync"/> call; completes when the last one is done.</summary>
    private sealed class Batch(int count, IProgress<long> bytesDone)
    {
        private readonly ConcurrentBag<(SteamChunkRequest Chunk, Exception Error)> _failures = [];
        private readonly TaskCompletionSource<List<(SteamChunkRequest Chunk, Exception Error)>> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining = count;

        public Task<List<(SteamChunkRequest Chunk, Exception Error)>> Completion => _done.Task;

        public void Done(SteamChunkRequest chunk, Exception? error)
        {
            if (error is not null)
            {
                _failures.Add((chunk, error));
            }
            bytesDone.Report(chunk.CompressedLength);
            if (Interlocked.Decrement(ref _remaining) == 0)
            {
                _done.TrySetResult([.. _failures]);
            }
        }
    }
}
