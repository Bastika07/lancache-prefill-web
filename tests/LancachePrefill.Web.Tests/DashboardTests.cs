using System.Net;

namespace LancachePrefill.Web.Tests;

public sealed class DashboardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> SignedInDashboardAsync(PrefillAppFactory app)
    {
        using var browser = app.CreateBrowser();
        (await AdminLoginTests.SignInAsync(browser, PrefillAppFactory.Password)).Dispose();
        using var response = await browser.GetAsync(new Uri("/", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task The_dashboard_shows_the_lancache_found_via_dns()
    {
        await using var app = new PrefillAppFactory();
        app.Dns.Add("lancache.steamcontent.com", "10.0.0.5");
        app.Network.Lancache("10.0.0.5");

        var html = await SignedInDashboardAsync(app);

        Assert.Contains("Using Lancache at <strong>10.0.0.5</strong> (via lancache.steamcontent.com)", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_dashboard_warns_with_the_reason_when_no_lancache_is_found()
    {
        await using var app = new PrefillAppFactory();
        app.Dns.Add("lancache.steamcontent.com", "23.45.67.89");

        var html = await SignedInDashboardAsync(app);

        Assert.Contains("No Lancache found.", html, StringComparison.Ordinal);
        Assert.Contains("resolves to the public IP 23.45.67.89", html, StringComparison.Ordinal);
    }
}
