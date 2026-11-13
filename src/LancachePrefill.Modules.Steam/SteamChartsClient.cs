// Ported from SteamPrefill/Handlers/Steam/SteamChartsService.cs (tpill90/steam-lancache-prefill, MIT). See NOTICE.
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace LancachePrefill.Modules.Steam;

/// <summary>Steam's public "most played games" chart (https://steamapi.xpaw.me/#ISteamChartsService/GetMostPlayedGames).</summary>
public sealed class SteamChartsClient(HttpClient http)
{
    public const string HttpClientName = "steam-charts";
    private static readonly Uri MostPlayed = new("https://api.steampowered.com/ISteamChartsService/GetMostPlayedGames/v1/");

    /// <summary>App ids by rank, most played first. Throws when Steam can't be reached; the Job turns that into a warning.</summary>
    public async Task<IReadOnlyList<uint>> GetMostPlayedAppIdsAsync(CancellationToken ct)
    {
        var body = await http.GetFromJsonAsync<Envelope>(MostPlayed, ct)
            ?? throw new InvalidOperationException("Steam charts returned an empty response.");
        return [.. body.Response.Ranks.OrderBy(r => r.Rank).Select(r => r.AppId)];
    }

    private sealed record Envelope([property: JsonPropertyName("response")] Ranking Response);
    private sealed record Ranking([property: JsonPropertyName("ranks")] IReadOnlyList<RankedApp> Ranks);
    private sealed record RankedApp([property: JsonPropertyName("rank")] int Rank, [property: JsonPropertyName("appid")] uint AppId);
}
