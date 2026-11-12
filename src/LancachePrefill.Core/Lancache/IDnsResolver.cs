using System.Net;

namespace LancachePrefill.Core.Lancache;

/// <summary>DNS lookups, behind an interface so detection can be tested without a real LAN.</summary>
public interface IDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

public sealed class SystemDnsResolver : IDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Dns.GetHostAddressesAsync(host, ct);
}
