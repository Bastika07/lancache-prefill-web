using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace LancachePrefill.Core.Persistence;

/// <param name="Path">SQLite file inside the data volume.</param>
/// <param name="ModuleIds">Registered Modules; used to seed the default Schedule.</param>
public sealed record PrefillDatabaseOptions(
    string Path,
    IReadOnlyList<string> ModuleIds,
    IDataProtectionProvider DataProtection,
    TimeProvider Time);

/// <summary>
/// The app's single SQLite database (spec → Deployment). Entry point to all stores.
/// </summary>
public sealed class PrefillDatabase : IAsyncDisposable
{
    private readonly DbContextOptions<PrefillDbContext> _options;

    private PrefillDatabase(DbContextOptions<PrefillDbContext> options, PrefillDatabaseOptions settings)
    {
        _options = options;
        Schedules = new ScheduleStore(CreateContext);
        JobHistory = new JobHistoryStore(CreateContext, settings.Time);
        AccountTokens = new AccountTokenStore(CreateContext, settings.DataProtection);
        Settings = new SettingsStore(CreateContext);
        Selections = new SelectionStore(CreateContext);
    }

    public IScheduleStore Schedules { get; }
    public IJobHistoryStore JobHistory { get; }
    public IAccountTokenStore AccountTokens { get; }
    public ISettingsStore Settings { get; }
    public ISelectionStore Selections { get; }

    public static async Task<PrefillDatabase> OpenAsync(PrefillDatabaseOptions options, CancellationToken ct)
    {
        var dbOptions = new DbContextOptionsBuilder<PrefillDbContext>()
            .UseSqlite($"Data Source={options.Path}")
            .Options;
        var db = new PrefillDatabase(dbOptions, options);

        await using var context = db.CreateContext();
        // Migrations (not EnsureCreated) so later schema changes upgrade the existing file in the volume.
        var isNew = !(await context.Database.GetAppliedMigrationsAsync(ct)).Any();
        await context.Database.MigrateAsync(ct);
        if (isNew)
        {
            context.Schedules.Add(ScheduleEntity.From(Schedule.DefaultNightly(options.ModuleIds)));
            await context.SaveChangesAsync(ct);
        }
        return db;
    }

    private PrefillDbContext CreateContext() => new(_options);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
