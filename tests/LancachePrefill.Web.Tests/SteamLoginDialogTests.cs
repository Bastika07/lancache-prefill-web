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

    [Fact]
    public void When_steam_wants_an_authenticator_code_it_asks_for_it_and_submits_it()
    {
        var dialog = Render();
        _login.SetState(new SteamLoginState.AwaitingCode(SteamGuardCodeKind.Authenticator));

        dialog.WaitForAssertion(() => Assert.Contains("Steam Mobile app", dialog.Find("label[for=steam-code]").TextContent, StringComparison.Ordinal));
        dialog.Find("#steam-code").Change("R2D2X");
        dialog.Find("form#steam-guard").Submit();

        Assert.Equal(["code:R2D2X"], _login.Calls);
        Assert.Empty(dialog.FindAll("[role=alert]"));
    }

    [Fact]
    public void For_an_email_code_it_names_the_address_and_says_when_the_last_code_was_wrong()
    {
        _login.SetState(new SteamLoginState.AwaitingCode(SteamGuardCodeKind.Email, EmailHint: "ga***@valvesoftware.com", PreviousCodeWasWrong: true));

        var dialog = Render();

        Assert.Contains("ga***@valvesoftware.com", dialog.Find("label[for=steam-code]").TextContent, StringComparison.Ordinal);
        Assert.Contains("wrong", dialog.Find("[role=alert]").TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_login_waiting_for_a_code_can_be_cancelled()
    {
        _login.SetState(new SteamLoginState.AwaitingCode(SteamGuardCodeKind.Authenticator));
        var dialog = Render();

        dialog.Find("button#steam-cancel").Click();

        Assert.Equal(["cancel"], _login.Calls);
    }

    public void Dispose() => _ctx.Dispose();
}
