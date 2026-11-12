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

    [Fact]
    public void Signed_out_it_offers_the_qr_code_login()
    {
        var dialog = Render();

        dialog.Find("button#steam-qr-login").Click();

        Assert.Equal(["qr"], _login.Calls);
    }

    [Fact]
    public void After_a_failed_attempt_it_shows_why()
    {
        _login.SetState(new SteamLoginState.SignedOut("Invalid username or password."));

        var dialog = Render();

        Assert.Equal("Invalid username or password.", dialog.Find("[role=alert]").TextContent.Trim());
    }

    public void Dispose() => _ctx.Dispose();
}
