using LancachePrefill.Core;
using LancachePrefill.Core.Lancache;
using LancachePrefill.Core.Persistence;
using LancachePrefill.Modules.Steam;
using LancachePrefill.Web;
using LancachePrefill.Web.Components;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// Every registered Module's id; used to seed the default Schedule without resolving the Modules
// (they depend on the database themselves).
string[] moduleIds = [SteamPrefillModule.ModuleId];
var config = AppConfiguration.From(builder.Configuration);
builder.Services.AddSingleton(config);

// Persistence: SQLite file + Data Protection key ring, both in the data volume.
builder.Services.AddDataProtection()
    .SetApplicationName("lancache-prefill-web")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(config.DataDir, "keys")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => PrefillDatabase.OpenAsync(
        new PrefillDatabaseOptions(
            Path.Combine(config.DataDir, "prefill.db"),
            moduleIds,
            sp.GetRequiredService<IDataProtectionProvider>(),
            sp.GetRequiredService<TimeProvider>()),
        CancellationToken.None)
    .GetAwaiter().GetResult()); // once, at startup

builder.Services.AddSingleton(sp => sp.GetRequiredService<PrefillDatabase>().Settings);
builder.Services.AddSingleton(sp => sp.GetRequiredService<PrefillDatabase>().AccountTokens);

// Steam Module.
builder.Services.AddSingleton<SteamKitSession>();
builder.Services.AddSingleton<ISteamSession>(sp => sp.GetRequiredService<SteamKitSession>());
builder.Services.AddSingleton<ISteamLogin>(sp => sp.GetRequiredService<SteamKitSession>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<SteamKitSession>());
builder.Services.AddSingleton<IDownloadedDepotStore, SettingsDownloadedDepotStore>();
builder.Services.AddHttpClient(SteamChunkDownloader.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(2));
builder.Services.AddSingleton(sp => new SteamChunkDownloader(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(SteamChunkDownloader.HttpClientName)));
builder.Services.AddSingleton<IPrefillModule, SteamPrefillModule>();

// Jobs and Schedules.
builder.Services.AddSingleton(sp => new JobQueue(
    sp.GetServices<IPrefillModule>(),
    sp.GetRequiredService<TimeProvider>(),
    [sp.GetRequiredService<PrefillDatabase>().JobHistory],
    sp.GetRequiredService<ILogger<JobQueue>>()));
builder.Services.AddSingleton(sp =>
{
    var db = sp.GetRequiredService<PrefillDatabase>();
    var schedules = db.Schedules.GetAllAsync(CancellationToken.None).GetAwaiter().GetResult();
    var settings = db.Settings.GetGlobalAsync(CancellationToken.None).GetAwaiter().GetResult();
    return new Scheduler(
        sp.GetRequiredService<JobQueue>(),
        schedules.Select(s => s.Schedule),
        TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId),
        sp.GetRequiredService<TimeProvider>(),
        moduleId => new PrefillRequest([]), // TODO(#3): resolve the Module's Selection
        [db.JobHistory],
        sp.GetRequiredService<ILogger<Scheduler>>());
});
builder.Services.AddHostedService<JobQueueService>();
builder.Services.AddHostedService<SchedulerService>();
builder.Services.AddHostedService<HistoryPruningService>();

// Lancache detection.
builder.Services.AddSingleton<IDnsResolver, SystemDnsResolver>();
builder.Services.AddHttpClient(LancacheDetector.HttpClientName, c => c.Timeout = TimeSpan.FromMilliseconds(500));
builder.Services.AddSingleton(sp => new LancacheDetector(
    sp.GetRequiredService<IDnsResolver>(),
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(LancacheDetector.HttpClientName),
    config.LancacheIp));

// Admin login.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.Cookie.Name = "prefill-admin";
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Open the database now, so a broken data volume fails at startup rather than on first request.
app.Services.GetRequiredService<PrefillDatabase>();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
// Served over plain HTTP on the LAN: no HTTPS redirection / HSTS.
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapPost("/logout", (Delegate)AdminAuth.LogoutAsync);
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();

/// <summary>Entry point; public for WebApplicationFactory in tests.</summary>
public partial class Program;
