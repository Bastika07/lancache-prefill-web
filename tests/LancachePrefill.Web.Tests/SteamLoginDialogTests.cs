using Bunit;
using LancachePrefill.Modules.Steam;
using LancachePrefill.Web.Components.Steam;
using Microsoft.Extensions.DependencyInjection;

namespace LancachePrefill.Web.Tests;

public sealed class SteamLoginDialogTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly FakeSteamLogin _login = new();

    public SteamLoginDialogTests() => _ctx.Services.AddSingleton<ISteamLogin>(_login);

    private IRenderedComponent<SteamLoginDialog> Render() => _ctx.Render<SteamLoginDialog>();

    [Fact]
    public void Signed_out_it_asks_for_username_and_password_and_starts_the_login_with_them()
    {
        var dialog = Render();

        dialog.Find("#steam-username").Change("gaben");
        dialog.Find("#steam-password").Change("hunter2");
        dialog.Find("form#steam-credentials").Submit();

        Assert.Equal(["credentials:gaben:hunter2"], _login.Calls);
    }

    public void Dispose() => _ctx.Dispose();
}
