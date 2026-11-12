using System.Text;

namespace LancachePrefill.Core.Tests.Persistence;

public sealed class AccountTokenStoreTests : IAsyncDisposable
{
    private const string RefreshToken = "eyJhbGciOiJFZERTQSJ9.steam-refresh-token-7f3a9c";
    private readonly DatabaseFixture _fixture = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_stored_token_survives_a_restart()
    {
        var db = await _fixture.OpenAsync();
        await db.AccountTokens.SetAsync("steam", RefreshToken, Ct);

        var restarted = await _fixture.OpenAsync();

        Assert.Equal(RefreshToken, await restarted.AccountTokens.GetAsync("steam", Ct));
        Assert.Null(await restarted.AccountTokens.GetAsync("epic", Ct));
    }

    [Fact]
    public async Task A_stored_token_never_appears_in_plain_text_in_the_database_files()
    {
        var db = await _fixture.OpenAsync();
        await db.AccountTokens.SetAsync("steam", RefreshToken, Ct);
        await db.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Deliberate side channel: "encrypted at rest" can only be checked by looking at the bytes.
        var dir = Path.GetDirectoryName(_fixture.DatabasePath)!;
        var files = Directory.GetFiles(dir, Path.GetFileName(_fixture.DatabasePath) + "*");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var bytes = await File.ReadAllBytesAsync(file, Ct);
            Assert.DoesNotContain("steam-refresh-token-7f3a9c", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
            Assert.DoesNotContain("steam-refresh-token-7f3a9c", Encoding.Unicode.GetString(bytes), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_removed_token_is_gone_after_a_restart()
    {
        var db = await _fixture.OpenAsync();
        await db.AccountTokens.SetAsync("steam", RefreshToken, Ct);
        await db.AccountTokens.RemoveAsync("steam", Ct);

        var restarted = await _fixture.OpenAsync();

        Assert.Null(await restarted.AccountTokens.GetAsync("steam", Ct));
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
