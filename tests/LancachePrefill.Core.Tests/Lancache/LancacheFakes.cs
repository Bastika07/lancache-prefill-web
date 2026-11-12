using System.Net;
using System.Net.Sockets;
using LancachePrefill.Core.Lancache;

namespace LancachePrefill.Core.Tests.Lancache;

/// <summary>DNS as the LAN sees it. Unknown hosts fail like a real lookup does.</summary>
internal sealed class FakeDns : IDnsResolver
{
    private readonly Dictionary<string, IPAddress[]> _records = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Lookups { get; } = [];

    public FakeDns Add(string host, params string[] ips)
    {
        _records[host] = [.. ips.Select(IPAddress.Parse)];
        return this;
    }

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        Lookups.Add(host);
        return _records.TryGetValue(host, out var ips)
            ? Task.FromResult(ips)
            : Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound));
    }
}

/// <summary>The network: IPs registered as Lancaches answer the heartbeat; everything else refuses.</summary>
internal sealed class FakeNetwork : HttpMessageHandler
{
    private readonly HashSet<string> _lancaches = [];
    private readonly HashSet<string> _otherServers = [];

    public FakeNetwork Lancache(string ip) { _lancaches.Add(ip); return this; }
    public FakeNetwork PlainWebServer(string ip) { _otherServers.Add(ip); return this; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var host = request.RequestUri!.Host;
        if (request.RequestUri.AbsolutePath == "/lancache-heartbeat" && _lancaches.Contains(host))
        {
            var response = new HttpResponseMessage(HttpStatusCode.NoContent);
            response.Headers.Add("X-LanCache-Processed-By", "lancache-dns");
            return Task.FromResult(response);
        }
        if (_otherServers.Contains(host))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        return Task.FromException<HttpResponseMessage>(new HttpRequestException("Connection refused"));
    }
}
