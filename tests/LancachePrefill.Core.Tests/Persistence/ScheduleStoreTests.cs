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

    [Fact]
    public async Task Added_changed_and_deleted_schedules_survive_a_restart()
    {
        var db = await _fixture.OpenAsync();
        var nightly = Assert.Single(await db.Schedules.GetAllAsync(Ct));
        var weekly = await db.Schedules.AddAsync(new Schedule("Weekly", "0 4 * * 0", ["epic"]), Ct);
        await db.Schedules.UpdateAsync(weekly.Id, new Schedule("Sundays", "30 4 * * 0", ["steam", "epic"]), Ct);
        await db.Schedules.DeleteAsync(nightly.Id, Ct);

        var restarted = await _fixture.OpenAsync();

        var stored = Assert.Single(await restarted.Schedules.GetAllAsync(Ct));
        Assert.Equal(weekly.Id, stored.Id);
        Assert.Equal("Sundays", stored.Schedule.Name);
        Assert.Equal("30 4 * * 0", stored.Schedule.Cron);
        Assert.Equal(["steam", "epic"], stored.Schedule.ModuleIds);
    }

    [Fact]
    public async Task Deleting_every_schedule_does_not_bring_the_default_back_after_a_restart()
    {
        var db = await _fixture.OpenAsync();
        await db.Schedules.DeleteAsync(Assert.Single(await db.Schedules.GetAllAsync(Ct)).Id, Ct);

        var restarted = await _fixture.OpenAsync();

        Assert.Empty(await restarted.Schedules.GetAllAsync(Ct));
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
