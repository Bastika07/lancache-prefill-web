using LancachePrefill.Core.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Time.Testing;

namespace LancachePrefill.Core.Tests.Persistence;

/// <summary>
/// A real SQLite file plus a real Data Protection key ring in a temp directory.
/// <see cref="OpenAsync"/> again on the same fixture simulates an app restart.
/// </summary>
public sealed class DatabaseFixture : IAsyncDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("prefill-db-tests-");
    private readonly List<PrefillDatabase> _opened = [];

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 7, 0, 0, 0, TimeSpan.Zero));
    public IReadOnlyList<string> ModuleIds { get; init; } = ["steam", "epic"];
    public string DatabasePath => Path.Combine(_dir.FullName, "prefill.db");

    public async Task<PrefillDatabase> OpenAsync()
    {
        var keys = new DirectoryInfo(Path.Combine(_dir.FullName, "keys"));
        var db = await PrefillDatabase.OpenAsync(
            new PrefillDatabaseOptions(DatabasePath, ModuleIds, DataProtectionProvider.Create(keys), Time),
            TestContext.Current.CancellationToken);
        _opened.Add(db);
        return db;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var db in _opened)
        {
            await db.DisposeAsync();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _dir.Delete(recursive: true);
    }
}
