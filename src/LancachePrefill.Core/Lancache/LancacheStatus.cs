using System.Net;

namespace LancachePrefill.Core.Lancache;

/// <summary>Outcome of looking for the Lancache. See GLOSSARY.md → Lancache.</summary>
public abstract record LancacheStatus
{
    private LancacheStatus() { }

    /// <param name="Source">Where the address came from: <c>LANCACHE_IP</c>, <c>settings</c>, or the host name that led to it.</param>
    public sealed record Found(IPAddress Address, string Source) : LancacheStatus;

    /// <param name="Reason">Human-readable explanation shown as a warning in the UI.</param>
    public sealed record NotFound(string Reason) : LancacheStatus;
}
