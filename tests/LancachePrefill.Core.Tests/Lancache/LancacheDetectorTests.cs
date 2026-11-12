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

    [Fact]
    public async Task When_the_trigger_domain_does_not_resolve_localhost_the_docker_host_and_the_machine_name_are_tried_in_that_order()
    {
        _dns.Add("localhost", "127.0.0.1").Add("172.17.0.1", "172.17.0.1").Add("prefill-box", "192.168.1.30");
        _network.Lancache("172.17.0.1").Lancache("192.168.1.30");

        var status = await new LancacheDetector(_dns, _http, lancacheIpEnv: null, machineName: "prefill-box").DetectAsync(null, Ct);

        Assert.Equal(new LancacheStatus.Found(IPAddress.Parse("172.17.0.1"), "172.17.0.1"), status);
        Assert.Equal(["lancache.steamcontent.com", "localhost", "172.17.0.1"], _dns.Lookups);
    }

    public void Dispose() => _http.Dispose();
}
