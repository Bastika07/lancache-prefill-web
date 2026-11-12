using System.Net;
using LancachePrefill.Core.Lancache;

namespace LancachePrefill.Core.Tests.Lancache;

public sealed class LancacheDetectorTests : IDisposable
{
    private readonly FakeDns _dns = new();
    private readonly FakeNetwork _network = new();
    private readonly HttpClient _http;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public LancacheDetectorTests() => _http = new HttpClient(_network);

    private LancacheDetector Detector(string? lancacheIpEnv = null) => new(_dns, _http, lancacheIpEnv);

    [Fact]
    public async Task The_LANCACHE_IP_environment_variable_wins_without_any_dns_lookup()
    {
        _dns.Add("lancache.steamcontent.com", "10.0.0.5");
        _network.Lancache("10.0.0.5");

        var status = await Detector(lancacheIpEnv: "192.168.1.20").DetectAsync(settingsOverride: "10.0.0.9", Ct);

        var found = Assert.IsType<LancacheStatus.Found>(status);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), found.Address);
        Assert.Equal("LANCACHE_IP", found.Source);
        Assert.Empty(_dns.Lookups);
    }

    [Fact]
    public async Task Without_LANCACHE_IP_the_ip_from_the_settings_is_used_without_any_dns_lookup()
    {
        var status = await Detector().DetectAsync(settingsOverride: "10.0.0.9", Ct);

        Assert.Equal(new LancacheStatus.Found(IPAddress.Parse("10.0.0.9"), "settings"), status);
        Assert.Empty(_dns.Lookups);
    }

    [Fact]
    public async Task The_trigger_domain_resolving_to_a_private_ip_that_answers_the_heartbeat_is_the_lancache()
    {
        _dns.Add("lancache.steamcontent.com", "10.0.0.4", "10.0.0.5");
        _network.Lancache("10.0.0.5");

        var status = await Detector().DetectAsync(settingsOverride: null, Ct);

        Assert.Equal(new LancacheStatus.Found(IPAddress.Parse("10.0.0.5"), "lancache.steamcontent.com"), status);
    }

    public void Dispose() => _http.Dispose();
}
