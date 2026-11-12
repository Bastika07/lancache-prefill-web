namespace LancachePrefill.Core.Tests.Persistence;

public sealed class ScheduleStoreTests : IAsyncDisposable
{
    private readonly DatabaseFixture _fixture = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_fresh_database_starts_with_the_default_nightly_schedule_for_every_module()
    {
        var db = await _fixture.OpenAsync();

        var stored = Assert.Single(await db.Schedules.GetAllAsync(Ct));

        Assert.Equal("Nightly", stored.Schedule.Name);
        Assert.Equal("0 3 * * *", stored.Schedule.Cron);
        Assert.Equal(["steam", "epic"], stored.Schedule.ModuleIds);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
