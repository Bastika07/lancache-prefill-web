using AngleSharp.Dom;
using Bunit;
using LancachePrefill.Core.Persistence;
using LancachePrefill.Modules.Steam;
using LancachePrefill.Web.Components.Steam;
using Microsoft.Extensions.DependencyInjection;

namespace LancachePrefill.Web.Tests;

public sealed class SteamSettingsFormTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly BunitContext _ctx = new();
    private readonly InMemorySettings _settings = new();

    public SteamSettingsFormTests() => _ctx.Services.AddSingleton<ISettingsStore>(_settings);

    private IRenderedComponent<SteamSettingsForm> Render() => _ctx.Render<SteamSettingsForm>();

    [Fact]
    public void Without_saved_settings_it_shows_the_defaults()
    {
        var form = Render();

        form.WaitForAssertion(() =>
        {
            Assert.True(form.Find("#os-windows").IsChecked());
            Assert.False(form.Find("#os-linux").IsChecked());
            Assert.False(form.Find("#os-macos").IsChecked());
            Assert.Equal("30", form.Find("#max-concurrent-requests").GetAttribute("value"));
            Assert.Equal("english", form.Find("#steam-language").GetAttribute("value"));
        }, Wait);
    }

    [Fact]
    public void Changed_settings_are_saved_and_the_architecture_is_kept()
    {
        _settings.Saved = SteamSettings.Default with { Architecture = "32" };
        var form = Render();
        form.WaitForElement("#os-linux", Wait).Change(true);
        form.Find("#max-concurrent-requests").Change("50");
        form.Find("#steam-language").Change("german");

        form.Find("form#steam-settings").Submit();

        form.WaitForAssertion(() =>
        {
            var saved = Assert.IsType<SteamSettings>(_settings.Saved);
            Assert.Equal(["windows", "linux"], saved.OperatingSystems);
            Assert.Equal(50, saved.MaxConcurrentRequests);
            Assert.Equal("german", saved.Language);
            Assert.Equal("32", saved.Architecture);
        }, Wait);
        Assert.Contains("Saved", form.Find("[role=status]").TextContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    public void Concurrent_requests_outside_1_to_100_are_rejected(string value)
    {
        var form = Render();
        form.WaitForElement("#max-concurrent-requests", Wait).Change(value);

        form.Find("form#steam-settings").Submit();

        Assert.Contains("1 and 100", form.Find("[role=alert]").TextContent, StringComparison.Ordinal);
        Assert.Null(_settings.Saved);
    }

    [Fact]
    public void At_least_one_operating_system_is_required()
    {
        var form = Render();
        form.WaitForElement("#os-windows", Wait).Change(false);

        form.Find("form#steam-settings").Submit();

        Assert.Contains("operating system", form.Find("[role=alert]").TextContent, StringComparison.Ordinal);
        Assert.Null(_settings.Saved);
    }

    public async ValueTask DisposeAsync() => await _ctx.DisposeAsync();

    private sealed class InMemorySettings : ISettingsStore
    {
        public object? Saved { get; set; }

        public Task<GlobalSettings> GetGlobalAsync(CancellationToken ct) => Task.FromResult(GlobalSettings.Default);
        public Task SaveGlobalAsync(GlobalSettings settings, CancellationToken ct) => Task.CompletedTask;
        public Task<T> GetModuleAsync<T>(string moduleId, T defaults, CancellationToken ct) => Task.FromResult(Saved is T t ? t : defaults);

        public Task SaveModuleAsync<T>(string moduleId, T settings, CancellationToken ct)
        {
            Assert.Equal(SteamPrefillModule.ModuleId, moduleId);
            Saved = settings;
            return Task.CompletedTask;
        }
    }
}
