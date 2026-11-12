using System.Net;
using System.Net.Sockets;

namespace LancachePrefill.Core.Lancache;

/// <summary>
/// Finds the Lancache (spec → Deployment). Ported from lancache-prefill-common's LancacheIpResolver; see NOTICE.
/// Order: <c>LANCACHE_IP</c> → IP from the settings → DNS detection (trigger domain, then local fallbacks),
/// where a candidate counts only if it is a private IPv4 address that answers the Lancache heartbeat.
/// </summary>
/// <param name="machineName">This machine's host name, last fallback; defaults to <see cref="Dns.GetHostName"/>.</param>
public sealed class LancacheDetector(IDnsResolver dns, HttpClient http, string? lancacheIpEnv, string? machineName = null)
{
    /// <summary>Lancache DNS poisons this Steam domain; resolving it is the primary way to find the cache.</summary>
    public const string TriggerDomain = "lancache.steamcontent.com";

    /// <param name="settingsOverride">The Lancache IP saved in the global settings, if any.</param>
    public async Task<LancacheStatus> DetectAsync(string? settingsOverride, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(lancacheIpEnv))
        {
            return new LancacheStatus.Found(IPAddress.Parse(lancacheIpEnv), "LANCACHE_IP");
        }
        if (!string.IsNullOrWhiteSpace(settingsOverride))
        {
            return new LancacheStatus.Found(IPAddress.Parse(settingsOverride), "settings");
        }

        // Poisoned DNS first; then a Lancache on this machine, on the Docker host, or reachable by our own name.
        string[] candidates = [TriggerDomain, "localhost", "172.17.0.1", machineName ?? Dns.GetHostName()];
        foreach (var host in candidates)
        {
            foreach (var ip in (await TryResolveAsync(host, ct)).Where(IsPrivateIPv4))
            {
                if (await AnswersHeartbeatAsync(ip, ct))
                {
                    return new LancacheStatus.Found(ip, host);
                }
            }
        }
        return new LancacheStatus.NotFound($"No Lancache found via {TriggerDomain}.");
    }

    private async Task<IPAddress[]> TryResolveAsync(string host, CancellationToken ct)
    {
        try
        {
            return await dns.ResolveAsync(host, ct);
        }
        catch (SocketException)
        {
            return [];
        }
    }

    private async Task<bool> AnswersHeartbeatAsync(IPAddress ip, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(new Uri($"http://{ip}/lancache-heartbeat"), ct);
            return response.Headers.Contains("X-LanCache-Processed-By");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Refused or timed out: nothing there.
            return false;
        }
    }

    private static bool IsPrivateIPv4(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false; // Lancache does not support IPv6.
        }
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || b[0] == 127;
    }
}
