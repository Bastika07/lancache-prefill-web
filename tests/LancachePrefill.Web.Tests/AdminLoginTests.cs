using System.Net;
using System.Text.RegularExpressions;

namespace LancachePrefill.Web.Tests;

public sealed class AdminLoginTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_app_refuses_to_start_without_an_admin_password()
    {
        await using var app = new PrefillAppFactory { AdminPassword = null };

        var ex = Assert.ThrowsAny<Exception>(() => app.CreateBrowser());

        Assert.Contains("ADMIN_PASSWORD", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_anonymous_visitor_is_sent_to_the_login_page()
    {
        await using var app = new PrefillAppFactory();
        using var browser = app.CreateBrowser();

        using var response = await browser.GetAsync(new Uri("/", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", PathOf(response.Headers.Location));
    }

    [Fact]
    public async Task A_wrong_password_shows_an_error_and_does_not_sign_in()
    {
        await using var app = new PrefillAppFactory();
        using var browser = app.CreateBrowser();

        using var response = await SignInAsync(browser, "hunter2");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Wrong password", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        using var home = await browser.GetAsync(new Uri("/", UriKind.Relative), Ct);
        Assert.Equal("/login", PathOf(home.Headers.Location));
    }

    [Fact]
    public async Task The_right_password_signs_in_and_returns_to_the_requested_page()
    {
        await using var app = new PrefillAppFactory { LancacheIp = "192.168.1.20" };
        using var browser = app.CreateBrowser();

        using var response = await SignInAsync(browser, PrefillAppFactory.Password, returnUrl: "/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", PathOf(response.Headers.Location));
        using var home = await browser.GetAsync(new Uri("/", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Contains("Dashboard", await home.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_login_form_never_redirects_to_another_site()
    {
        await using var app = new PrefillAppFactory();
        using var browser = app.CreateBrowser();

        using var response = await SignInAsync(browser, PrefillAppFactory.Password, returnUrl: "//evil.example/phish");

        Assert.Equal(new Uri("http://localhost/"), new Uri(new Uri("http://localhost"), response.Headers.Location!));
    }

    /// <summary>Does what a browser does: load the form, keep its hidden fields, submit it.</summary>
    internal static async Task<HttpResponseMessage> SignInAsync(HttpClient browser, string password, string? returnUrl = null)
    {
        var loginUrl = returnUrl is null ? "/login" : $"/login?ReturnUrl={Uri.EscapeDataString(returnUrl)}";
        var form = await browser.GetStringAsync(new Uri(loginUrl, UriKind.Relative), Ct);
        var fields = Regex.Matches(form, """<input type="hidden" name="(?<name>[^"]+)" value="(?<value>[^"]*)""")
            .ToDictionary(m => m.Groups["name"].Value, m => WebUtility.HtmlDecode(m.Groups["value"].Value));
        fields["Password"] = password;
        using var content = new FormUrlEncodedContent(fields);
        return await browser.PostAsync(new Uri(loginUrl, UriKind.Relative), content, Ct);
    }

    private static string PathOf(Uri? location) => new Uri(new Uri("http://localhost"), location!).AbsolutePath;
}
