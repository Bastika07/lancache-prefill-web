using LancachePrefill.Core.Persistence;

namespace LancachePrefill.Core.Tests.Persistence;

public sealed class SelectionStoreTests : IAsyncDisposable
{
    private readonly DatabaseFixture _fixture = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_module_without_a_saved_selection_selects_nothing()
    {
        var db = await _fixture.OpenAsync();

        var selection = await db.Selections.GetAsync("steam", Ct);

        Assert.Empty(selection.GameIds);
        Assert.Empty(selection.Rules);
    }

    [Fact]
    public async Task A_saved_selection_survives_a_restart_and_belongs_to_its_module_only()
    {
        var db = await _fixture.OpenAsync();
        await db.Selections.SaveAsync("steam",
            new Selection(["440", "570"], [SelectionRule.RecentlyPlayed, SelectionRule.TopPopular(20)]), Ct);

        var restarted = await _fixture.OpenAsync();

        var steam = await restarted.Selections.GetAsync("steam", Ct);
        Assert.Equal(["440", "570"], steam.GameIds);
        Assert.Equal([SelectionRule.RecentlyPlayed, SelectionRule.TopPopular(20)], steam.Rules);
        Assert.Empty((await restarted.Selections.GetAsync("epic", Ct)).GameIds);
    }

    [Fact]
    public async Task A_schedule_firing_prefills_each_modules_saved_selection_update_only()
    {
        var db = await _fixture.OpenAsync();
        await db.Selections.SaveAsync("steam", new Selection(["440"], [SelectionRule.AllOwned]), Ct);
        var steam = new FakeModule("steam") { AutoComplete = true };
        steam.RuleGames[SelectionRule.AllOwned] = ["570"];
        var epic = new FakeModule("epic") { AutoComplete = true };
        var queue = new JobQueue([steam, epic], _fixture.Time);
        var scheduler = new Scheduler(queue, [new Schedule("Nightly", "0 3 * * *", ["steam", "epic"])],
            TimeZoneInfo.Utc, _fixture.Time, db.Selections);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var loops = new[] { queue.RunAsync(stop.Token), scheduler.RunAsync(stop.Token) };

        _fixture.Time.Advance(TimeSpan.FromHours(3));
        await Eventually(() => steam.Runs.Count == 1 && epic.Runs.Count == 1);
        await stop.CancelAsync();
        await Task.WhenAll(loops).ContinueWith(_ => { }, TaskScheduler.Default);

        var steamRequest = steam.Runs[0].Request;
        Assert.Equal(["440", "570"], steamRequest.GameIds);
        Assert.False(steamRequest.Force);
        Assert.Empty(epic.Runs[0].Request.GameIds);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
