using System.Collections.Concurrent;
using System.Net;

namespace LancachePrefill.Modules.Steam.Tests;

/// <summary>
/// The Lancache as the chunk downloader sees it: answers <c>/depot/{depot}/chunk/{chunk}</c>,
/// records every chunk request (probes with a Range header separately), and can hold or fail chunks.
/// </summary>
internal sealed class FakeLancache : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _holds = new();

    /// <summary>Chunk ids in request order (retries appear again); probes are not included.</summary>
    public ConcurrentQueue<string> ChunkRequests { get; } = new();

    /// <summary>Host headers seen on chunk requests.</summary>
    public ConcurrentBag<string> Hosts { get; } = [];

    /// <summary>Chunks answered with 500 on every request.</summary>
    public ConcurrentDictionary<string, byte> Failing { get; } = new();

    /// <summary>Holds requests for <paramref name="chunkId"/> until <see cref="Release"/>.</summary>
    public void Hold(string chunkId) => _holds[chunkId] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release(string chunkId) => _holds[chunkId].TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var chunkId = request.RequestUri!.AbsolutePath.Split('/')[^1];
        if (request.Headers.Range is not null)
        {
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([0]) };
        }
        ChunkRequests.Enqueue(chunkId);
        Hosts.Add(request.Headers.Host ?? "");
        if (_holds.TryGetValue(chunkId, out var hold))
        {
            await hold.Task.WaitAsync(ct);
        }
        return Failing.ContainsKey(chunkId)
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[16]) };
    }
}
