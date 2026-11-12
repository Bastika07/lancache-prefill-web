// Ported from SteamPrefill/Handlers/DownloadHandler.cs (tpill90/steam-lancache-prefill, MIT). See NOTICE.
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

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
    private const int Retries = 2;

    public async Task<ChunkDownloadResult> DownloadAsync(
        IReadOnlyList<SteamChunkRequest> chunks,
        IPAddress lancache,
        IReadOnlyList<CdnServer> cdnServers,
        int maxConcurrentRequests,
        IProgress<long> bytesDone,
        CancellationToken ct)
    {
        var servers = cdnServers.OrderBy(s => s.Load).ToList();
        var blacklist = new ConcurrentDictionary<uint, ConcurrentDictionary<string, byte>>();
        var depotServers = await ProbeDepotServersAsync(chunks, lancache, servers, blacklist, ct);

        var failures = await AttemptAsync(chunks, forceRecache: false);
        for (var retry = 1; retry <= Retries && !failures.IsEmpty; retry++)
        {
            failures = await AttemptAsync([.. failures.Select(f => f.Chunk)], forceRecache: true);
        }
        return new ChunkDownloadResult([.. failures]);

        async Task<ConcurrentBag<(SteamChunkRequest Chunk, Exception Error)>> AttemptAsync(IReadOnlyList<SteamChunkRequest> batch, bool forceRecache)
        {
            var failed = new ConcurrentBag<(SteamChunkRequest, Exception)>();
            var assignLock = new Lock();
            var options = new ParallelOptions { MaxDegreeOfParallelism = maxConcurrentRequests, CancellationToken = ct };
            await Parallel.ForEachAsync(batch, options, async (chunk, token) =>
            {
                try
                {
                    while (true)
                    {
                        var server = ServerFor(chunk.DepotId)
                            ?? throw new InvalidOperationException($"Every CDN server returned 403 for depot {chunk.DepotId}; it may not be on any available CDN.");
                        try
                        {
                            await DownloadChunkAsync(chunk, lancache, server, forceRecache, token);
                            break;
                        }
                        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
                        {
                            Blacklist(chunk.DepotId, server.Host); // fail over to the next server
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    failed.Add((chunk, ex));
                }
                bytesDone.Report(chunk.CompressedLength);
            });
            return failed;

            CdnServer? ServerFor(uint depotId)
            {
                if (depotServers.TryGetValue(depotId, out var assigned) && !IsBlacklisted(depotId, assigned.Host))
                {
                    return assigned;
                }
                lock (assignLock)
                {
                    if (depotServers.TryGetValue(depotId, out var current) && !IsBlacklisted(depotId, current.Host))
                    {
                        return current;
                    }
                    var next = servers.FirstOrDefault(s => !IsBlacklisted(depotId, s.Host));
                    if (next is not null)
                    {
                        depotServers[depotId] = next;
                    }
                    return next;
                }
            }
        }

        bool IsBlacklisted(uint depotId, string host) => blacklist.TryGetValue(depotId, out var hosts) && hosts.ContainsKey(host);
        void Blacklist(uint depotId, string host) => blacklist.GetOrAdd(depotId, _ => new()).TryAdd(host, 0);
    }

    private async Task DownloadChunkAsync(SteamChunkRequest chunk, IPAddress lancache, CdnServer server, bool forceRecache, CancellationToken ct)
    {
        var url = $"http://{lancache}/depot/{chunk.DepotId}/chunk/{chunk.ChunkId}{(forceRecache ? "?nocache=1" : "")}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Host = server.Host;
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        // Read and discard: the point is that the Lancache caches it on the way through.
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await body.CopyToAsync(Stream.Null, ct);
    }

    /// <summary>Per depot, finds the first CDN (by load) that serves its first chunk; 403s are blacklisted.</summary>
    private async Task<ConcurrentDictionary<uint, CdnServer>> ProbeDepotServersAsync(
        IReadOnlyList<SteamChunkRequest> chunks, IPAddress lancache, List<CdnServer> servers,
        ConcurrentDictionary<uint, ConcurrentDictionary<string, byte>> blacklist, CancellationToken ct)
    {
        var depotServers = new ConcurrentDictionary<uint, CdnServer>();
        var probes = chunks.GroupBy(c => c.DepotId).Select(g => g.First()).ToList();
        await Parallel.ForEachAsync(probes, ct, async (probe, token) =>
        {
            foreach (var server in servers)
            {
                try
                {
                    var url = $"http://{lancache}/depot/{probe.DepotId}/chunk/{probe.ChunkId}?nocache=1";
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Host = server.Host;
                    request.Headers.UserAgent.ParseAdd(UserAgent);
                    request.Headers.Range = new RangeHeaderValue(0, 0);
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    if (response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        blacklist.GetOrAdd(probe.DepotId, _ => new()).TryAdd(server.Host, 0);
                        continue;
                    }
                    response.EnsureSuccessStatusCode();
                    depotServers[probe.DepotId] = server;
                    return;
                }
                catch (HttpRequestException)
                {
                    // Try the next server; the download phase reports real failures.
                }
            }
        });
        return depotServers;
    }
}
