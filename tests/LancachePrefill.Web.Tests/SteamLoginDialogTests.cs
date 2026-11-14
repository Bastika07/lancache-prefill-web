using Bunit;
using LancachePrefill.Modules.Steam;
using LancachePrefill.Web.Components.Steam;
using Microsoft.Extensions.DependencyInjection;

namespace LancachePrefill.Web.Tests;

public sealed class SteamLoginDialogTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly FakeSteamLogin _login = new();

    public SteamLoginDialogTests() => _ctx.Services.AddSingleton<ISteamLogin>(_login);

    private IRenderedComponent<SteamLoginDialog> Render() => _ctx.Render<SteamLoginDialog>();

    [Fact]
    public void When_steam_is_unreachable_it_says_so_retries_by_itself_and_offers_to_retry_now()
    {
        var dialog = Render();
        _login.SetState(new SteamLoginState.Unavailable("Could not connect to Steam."));

        dialog.WaitForAssertion(() =>
        {
            Assert.Contains("Could not connect to Steam.", dialog.Find("[role=alert]").TextContent, StringComparison.Ordinal);
            Assert.Contains("automatically", dialog.Markup, StringComparison.Ordinal);
            Assert.Empty(dialog.FindAll("#steam-credentials")); // the stored login is kept: no new sign-in needed
        }, Wait);

        dialog.Find("#steam-retry").Click();

        Assert.Equal(["reconnect"], _login.Calls);
    }

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

    [Fact]
    public void While_waiting_for_approval_in_the_app_it_says_so_and_can_be_cancelled()
    {
        _login.SetState(new SteamLoginState.AwaitingDeviceConfirmation());
        var dialog = Render();

        Assert.Contains("Steam Mobile app", dialog.Find("[role=status]").TextContent, StringComparison.Ordinal);
        dialog.Find("button#steam-cancel").Click();

        Assert.Equal(["cancel"], _login.Calls);
    }

    [Fact]
    public void While_connecting_it_shows_progress_and_no_form()
    {
        _login.SetState(new SteamLoginState.Connecting());

        var dialog = Render();

        Assert.Contains("Connecting", dialog.Find("[role=status]").TextContent, StringComparison.Ordinal);
        Assert.Empty(dialog.FindAll("form"));
    }

    [Fact]
    public void The_qr_code_encodes_the_current_challenge_and_follows_when_steam_rotates_it()
    {
        _login.SetState(new SteamLoginState.ShowingQrCode("https://s.team/q/1/1111"));
        var dialog = Render();
        var first = dialog.Find("#steam-qr").GetAttribute("data-challenge");

        _login.SetState(new SteamLoginState.ShowingQrCode("https://s.team/q/1/2222"));

        Assert.Equal("https://s.team/q/1/1111", first);
        dialog.WaitForAssertion(() => Assert.Equal("https://s.team/q/1/2222", dialog.Find("#steam-qr").GetAttribute("data-challenge")));
        Assert.NotNull(dialog.Find("#steam-qr svg"));
    }

    [Fact]
    public void Signed_in_it_shows_the_account_and_can_sign_out()
    {
        _login.SetState(new SteamLoginState.SignedIn("gaben"));
        var dialog = Render();

        Assert.Contains("gaben", dialog.Find("[data-testid=steam-account]").TextContent, StringComparison.Ordinal);
        dialog.Find("button#steam-sign-out").Click();

        Assert.Equal(["signout"], _login.Calls);
    }

    public void Dispose() => _ctx.Dispose();
}
