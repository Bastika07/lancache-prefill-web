using Bunit;
using LancachePrefill.Core;
using LancachePrefill.Core.Persistence;
using LancachePrefill.Web.Components.Selections;
using Microsoft.Extensions.DependencyInjection;

namespace LancachePrefill.Web.Tests.Selections;

public sealed class SelectionEditorTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly BunitContext _ctx = new();
    private readonly InMemorySelectionStore _store = new();
    private readonly CatalogModule _steam = new("steam",
    [
        new GameInfo("730", "Counter-Strike 2"),
        new GameInfo("570", "Dota 2"),
        new GameInfo("440", "Team Fortress 2"),
        new GameInfo("4000", "Garry's Mod"),
    ], SelectionRuleKind.AllOwned, SelectionRuleKind.TopPopular);
    private readonly JobQueue _queue;

    public SelectionEditorTests()
    {
        _queue = new JobQueue([_steam]);
        _ctx.Services.AddSingleton(_queue);
        _ctx.Services.AddSingleton<ISelectionStore>(_store);
    }

    private IRenderedComponent<SelectionEditor> Render() => _ctx.Render<SelectionEditor>(p => p.Add(c => c.Module, _steam));

    private static IReadOnlyList<string> GameNames(IRenderedComponent<SelectionEditor> editor) =>
        [.. editor.FindAll("[data-testid=game-row]").Select(r => r.QuerySelector(".game-name")!.TextContent.Trim())];

    [Fact]
    public void It_lists_the_modules_games_by_name_and_the_search_matches_name_or_id()
    {
        var editor = Render();
        editor.WaitForAssertion(() => Assert.Equal(["Counter-Strike 2", "Dota 2", "Garry's Mod", "Team Fortress 2"], GameNames(editor)), Wait);

        editor.Find("#game-search").Input("team");
        Assert.Equal(["Team Fortress 2"], GameNames(editor));

        editor.Find("#game-search").Input("57");
        Assert.Equal(["Dota 2"], GameNames(editor));
    }

    public async ValueTask DisposeAsync() => await _ctx.DisposeAsync();
}
