using System.Net;
using System.Net.Sockets;
using LancachePrefill.Core.Lancache;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace LancachePrefill.Web.Tests;

/// <summary>
/// The real app in memory, with its data directory in a temp folder. Only the system boundaries
/// (DNS, network to the Lancache) are replaced, by <see cref="Dns"/> and <see cref="Network"/>.
/// </summary>
public sealed class PrefillAppFactory : WebApplicationFactory<Program>
{
    public const string Password = "correct horse battery staple";
    private readonly DirectoryInfo _dataDir = Directory.CreateTempSubdirectory("prefill-web-tests-");

    public string? AdminPassword { get; init; } = Password;
    public string? LancacheIp { get; init; }
    public FakeDns Dns { get; } = new();
    public FakeNetwork Network { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("DATA_DIR", _dataDir.FullName);
        builder.UseSetting("ADMIN_PASSWORD", AdminPassword ?? "");
        builder.UseSetting("LANCACHE_IP", LancacheIp ?? "");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IDnsResolver>(Dns);
            services.AddHttpClient(LancacheDetector.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Network);
        });
    }

    /// <summary>A browser-like client: keeps cookies, does not follow redirects so tests can see them.</summary>
    public HttpClient CreateBrowser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _dataDir.Delete(recursive: true);
    }
}

public sealed class FakeDns : IDnsResolver
{
    private readonly Dictionary<string, IPAddress[]> _records = new(StringComparer.OrdinalIgnoreCase);

    public FakeDns Add(string host, params string[] ips)
    {
        _records[host] = [.. ips.Select(IPAddress.Parse)];
        return this;
    }

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) =>
        _records.TryGetValue(host, out var ips)
            ? Task.FromResult(ips)
            : Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound));
}

public sealed class FakeNetwork : HttpMessageHandler
{
    private readonly HashSet<string> _lancaches = [];

    public FakeNetwork Lancache(string ip) { _lancaches.Add(ip); return this; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_lancaches.Contains(request.RequestUri!.Host))
        {
            var response = new HttpResponseMessage(HttpStatusCode.NoContent);
            response.Headers.Add("X-LanCache-Processed-By", "lancache-dns");
            return Task.FromResult(response);
        }
        return Task.FromException<HttpResponseMessage>(new HttpRequestException("Connection refused"));
    }
}
