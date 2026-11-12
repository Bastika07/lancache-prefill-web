using LancachePrefill.Core.Persistence;

namespace LancachePrefill.Core.Tests.Persistence;

public sealed class SettingsStoreTests : IAsyncDisposable
{
    private readonly DatabaseFixture _fixture = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record FakeModuleSettings(IReadOnlyList<string> OperatingSystems, int MaxConcurrentRequests);
    private static readonly FakeModuleSettings Defaults = new(["windows"], 30);

    [Fact]
    public async Task Global_settings_default_to_dns_detection_the_container_time_zone_and_90_days_retention()
    {
        var db = await _fixture.OpenAsync();

        var settings = await db.Settings.GetGlobalAsync(Ct);

        Assert.Null(settings.LancacheIpOverride);
        Assert.Equal(TimeZoneInfo.Local.Id, settings.TimeZoneId);
        Assert.Equal(TimeSpan.FromDays(90), settings.HistoryRetention);
    }

    [Fact]
    public async Task Saved_global_settings_survive_a_restart()
    {
        var db = await _fixture.OpenAsync();
        await db.Settings.SaveGlobalAsync(new GlobalSettings("192.168.1.20", "Europe/Berlin", TimeSpan.FromDays(30)), Ct);

        var restarted = await _fixture.OpenAsync();

        Assert.Equal(new GlobalSettings("192.168.1.20", "Europe/Berlin", TimeSpan.FromDays(30)), await restarted.Settings.GetGlobalAsync(Ct));
    }

    [Fact]
    public async Task Module_settings_fall_back_to_the_module_defaults_until_saved_and_then_survive_a_restart()
    {
        var db = await _fixture.OpenAsync();
        Assert.Same(Defaults, await db.Settings.GetModuleAsync("steam", Defaults, Ct));

        await db.Settings.SaveModuleAsync("steam", new FakeModuleSettings(["windows", "linux"], 50), Ct);
        var restarted = await _fixture.OpenAsync();

        var saved = await restarted.Settings.GetModuleAsync("steam", Defaults, Ct);
        Assert.Equal(["windows", "linux"], saved.OperatingSystems);
        Assert.Equal(50, saved.MaxConcurrentRequests);
        Assert.Same(Defaults, await restarted.Settings.GetModuleAsync("epic", Defaults, Ct));
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
