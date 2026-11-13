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

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
