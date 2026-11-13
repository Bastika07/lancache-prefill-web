using AngleSharp.Dom;
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

    private static IElement Row(IRenderedComponent<SelectionEditor> editor, string name) =>
        editor.FindAll("[data-testid=game-row]").Single(r => r.QuerySelector(".game-name")!.TextContent.Trim() == name);

    [Fact]
    public void The_saved_selection_is_preselected_and_picked_games_and_rules_are_saved()
    {
        _store.Saved["steam"] = new Selection(["570"], [SelectionRule.TopPopular(5)]);
        var editor = Render();
        editor.WaitForAssertion(() => Assert.True(Row(editor, "Dota 2").QuerySelector("input[type=checkbox]")!.IsChecked()), Wait);
        Assert.True(editor.Find("#rule-TopPopular").IsChecked());
        Assert.Equal("5", editor.Find("#rule-TopPopular-count").GetAttribute("value"));
        Assert.False(editor.Find("#rule-AllOwned").IsChecked());

        Row(editor, "Counter-Strike 2").QuerySelector("input[type=checkbox]")!.Change(true);
        Row(editor, "Dota 2").QuerySelector("input[type=checkbox]")!.Change(false);
        editor.Find("#rule-AllOwned").Change(true);
        editor.Find("#rule-TopPopular-count").Change("20");
        editor.Find("button.save-selection").Click();

        editor.WaitForAssertion(() =>
        {
            var saved = _store.Saved["steam"];
            Assert.Equal(["730"], saved.GameIds);
            Assert.Equal([SelectionRule.AllOwned, SelectionRule.TopPopular(20)], saved.Rules);
        }, Wait);
    }

    [Fact]
    public void Only_the_rules_the_module_supports_are_offered()
    {
        var editor = Render();

        editor.WaitForAssertion(() => Assert.Equal(["rule-AllOwned", "rule-TopPopular"],
            editor.FindAll("input[type=checkbox][id^=rule-]").Select(c => c.Id!)), Wait);
    }

    [Fact]
    public void Prefill_now_enqueues_the_saved_selection_and_waits_while_changes_are_unsaved()
    {
        _store.Saved["steam"] = new Selection(["570"], []);
        var editor = Render();
        editor.WaitForAssertion(() => Assert.False(editor.Find("button.prefill-now").IsDisabled()), Wait);

        Row(editor, "Counter-Strike 2").QuerySelector("input[type=checkbox]")!.Change(true);
        Assert.True(editor.Find("button.prefill-now").IsDisabled());
        Assert.Contains("Save", editor.Find("[data-testid=unsaved]").TextContent, StringComparison.Ordinal);

        editor.Find("button.save-selection").Click();
        editor.WaitForAssertion(() => Assert.False(editor.Find("button.prefill-now").IsDisabled()), Wait);
        Assert.Empty(editor.FindAll("[data-testid=unsaved]"));
        editor.Find("#prefill-force").Change(true);
        editor.Find("button.prefill-now").Click();

        var job = Assert.Single(_queue.Jobs);
        Assert.Equal("steam", job.ModuleId);
        Assert.Equal(["570", "730"], job.Request.GameIds);
        Assert.True(job.Request.Force);
        Assert.Equal($"jobs/{job.Id}", editor.Find("[data-testid=enqueued-job]").GetAttribute("href"));
    }

    [Fact]
    public void An_empty_selection_cannot_be_prefilled()
    {
        var editor = Render();

        editor.WaitForAssertion(() => Assert.True(editor.Find("button.prefill-now").IsDisabled()), Wait);
    }

    [Fact]
    public void Saved_games_the_module_no_longer_lists_are_shown_and_can_be_removed()
    {
        _store.Saved["steam"] = new Selection(["570", "99999"], []);
        var editor = Render();

        var unavailable = editor.WaitForElement("[data-testid=unavailable-game]", Wait);
        Assert.Contains("99999", unavailable.TextContent, StringComparison.Ordinal);
        unavailable.QuerySelector("button")!.Click();
        editor.Find("button.save-selection").Click();

        editor.WaitForAssertion(() => Assert.Equal(["570"], _store.Saved["steam"].GameIds), Wait);
        Assert.Empty(editor.FindAll("[data-testid=unavailable-game]"));
    }

    [Fact]
    public void Only_selected_shows_just_the_picked_games()
    {
        _store.Saved["steam"] = new Selection(["570", "440"], []);
        var editor = Render();
        editor.WaitForAssertion(() => Assert.Equal(4, GameNames(editor).Count), Wait);

        editor.Find("#only-selected").Change(true);

        Assert.Equal(["Dota 2", "Team Fortress 2"], GameNames(editor));
        Assert.Contains("2 of 4 selected", editor.Find("[data-testid=selected-count]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void While_the_games_load_it_says_so()
    {
        var slow = new SlowCatalog();
        var editor = _ctx.Render<SelectionEditor>(p => p.Add(c => c.Module, slow));

        Assert.Contains("Loading", editor.Find("[role=status]").TextContent, StringComparison.Ordinal);
        slow.Games.SetResult([new GameInfo("730", "Counter-Strike 2")]);
        editor.WaitForAssertion(() => Assert.Equal(["Counter-Strike 2"], GameNames(editor)), Wait);
    }

    private sealed class SlowCatalog : IPrefillModule
    {
        public TaskCompletionSource<IReadOnlyList<GameInfo>> Games { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "steam";
        public string DisplayName => "Steam";
        public Task<IReadOnlyList<GameInfo>> ListGamesAsync(CancellationToken ct) => Games.Task;
        public Task<PrefillResult> PrefillAsync(PrefillRequest request, IProgress<PrefillProgress> progress, CancellationToken ct) => Task.FromResult(PrefillResult.Empty);
    }

    public async ValueTask DisposeAsync() => await _ctx.DisposeAsync();
}
