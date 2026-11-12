using System.Text.Json;

namespace LancachePrefill.Core.Persistence;

/// <param name="LancacheIpOverride">null = detect the Lancache via LAN DNS (env <c>LANCACHE_IP</c> still wins at runtime).</param>
/// <param name="TimeZoneId">Time zone Schedules' cron expressions are read in.</param>
/// <param name="HistoryRetention">How long Job history and firings are kept.</param>
public sealed record GlobalSettings(string? LancacheIpOverride, string TimeZoneId, TimeSpan HistoryRetention)
{
    /// <summary>DNS detection, the container's time zone (<c>TZ</c>), 90 days of history.</summary>
    public static GlobalSettings Default => new(null, TimeZoneInfo.Local.Id, TimeSpan.FromDays(90));
}

/// <summary>Global settings plus one settings object per Module, whose shape the Module owns.</summary>
public interface ISettingsStore
{
    Task<GlobalSettings> GetGlobalAsync(CancellationToken ct);
    Task SaveGlobalAsync(GlobalSettings settings, CancellationToken ct);

    /// <summary>The Module's saved settings, or <paramref name="defaults"/> if it never saved any.</summary>
    Task<T> GetModuleAsync<T>(string moduleId, T defaults, CancellationToken ct);
    Task SaveModuleAsync<T>(string moduleId, T settings, CancellationToken ct);
}

internal sealed class SettingsStore(Func<PrefillDbContext> createContext) : ISettingsStore
{
    private const string GlobalKey = "global";
    private static string ModuleKey(string moduleId) => $"module:{moduleId}";

    public async Task<GlobalSettings> GetGlobalAsync(CancellationToken ct) =>
        await ReadAsync<GlobalSettings>(GlobalKey, ct) ?? GlobalSettings.Default;

    public Task SaveGlobalAsync(GlobalSettings settings, CancellationToken ct) => WriteAsync(GlobalKey, settings, ct);

    public async Task<T> GetModuleAsync<T>(string moduleId, T defaults, CancellationToken ct) =>
        await ReadAsync<T>(ModuleKey(moduleId), ct) ?? defaults;

    public Task SaveModuleAsync<T>(string moduleId, T settings, CancellationToken ct) => WriteAsync(ModuleKey(moduleId), settings, ct);

    private async Task<T?> ReadAsync<T>(string key, CancellationToken ct)
    {
        await using var context = createContext();
        var entity = await context.Settings.FindAsync([key], ct);
        return entity is null ? default : JsonSerializer.Deserialize<T>(entity.Json);
    }

    private async Task WriteAsync<T>(string key, T value, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(value);
        await using var context = createContext();
        var entity = await context.Settings.FindAsync([key], ct);
        if (entity is null)
        {
            context.Settings.Add(new SettingEntity { Key = key, Json = json });
        }
        else
        {
            entity.Json = json;
        }
        await context.SaveChangesAsync(ct);
    }
}

internal sealed class SettingEntity
{
    public required string Key { get; set; }
    public required string Json { get; set; }
}
