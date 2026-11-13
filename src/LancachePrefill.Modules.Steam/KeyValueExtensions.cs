// Ported from SteamPrefill/Extensions/KeyValueExtensions.cs (tpill90/steam-lancache-prefill, MIT). See NOTICE.
using System.Globalization;
using SteamKit2;

namespace LancachePrefill.Modules.Steam;

internal static class KeyValueExtensions
{
    public static ulong? AsUnsignedLongNullable(this KeyValue kv) =>
        ulong.TryParse(kv.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static uint? AsUnsignedIntNullable(this KeyValue kv) =>
        uint.TryParse(kv.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static string? ToLowerCaseString(this KeyValue kv) =>
        kv == KeyValue.Invalid || string.IsNullOrEmpty(kv.Value) ? null : kv.Value.ToLowerInvariant();

    public static IReadOnlyList<string> SplitCommaDelimited(this KeyValue kv) =>
        kv == KeyValue.Invalid || string.IsNullOrEmpty(kv.Value) ? [] : kv.Value.Split(',');
}
