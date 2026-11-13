using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace LancachePrefill.Web;

/// <summary>The single admin (spec → Deployment): one password from <c>ADMIN_PASSWORD</c>, cookie session.</summary>
public static class AdminAuth
{
    public static bool IsCorrect(AppConfiguration config, string? attempt) =>
        attempt is not null && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(attempt)),
            SHA256.HashData(Encoding.UTF8.GetBytes(config.AdminPassword)));

    public static Task SignInAsync(HttpContext http) =>
        http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = true });

    public static async Task<IResult> LogoutAsync(HttpContext http)
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Redirect("/login");
    }
}
