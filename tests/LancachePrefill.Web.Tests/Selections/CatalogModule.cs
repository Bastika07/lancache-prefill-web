using LancachePrefill.Core;

namespace LancachePrefill.Web.Tests.Selections;

/// <summary>A Module with a fixed Game list and rule set; its Jobs never run in these tests.</summary>
public sealed class CatalogModule(string id, IReadOnlyList<GameInfo> games, params SelectionRuleKind[] rules) : IPrefillModule
{
    public string Id => id;
    public string DisplayName => $"Fake {id}";
    public TaskCompletionSource<IReadOnlyList<GameInfo>> Games { get; } = Ready(games);

    public IReadOnlySet<SelectionRuleKind> SupportedRules { get; } = rules.ToHashSet();

    public Task<IReadOnlyList<GameInfo>> ListGamesAsync(CancellationToken ct) => Games.Task;

    public Task<IReadOnlyList<string>> ResolveRuleAsync(SelectionRule rule, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);

    public Task<PrefillResult> PrefillAsync(PrefillRequest request, IProgress<PrefillProgress> progress, CancellationToken ct) =>
        Task.FromResult(PrefillResult.Empty);

    private static TaskCompletionSource<IReadOnlyList<GameInfo>> Ready(IReadOnlyList<GameInfo> games)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<GameInfo>>();
        tcs.SetResult(games);
        return tcs;
    }
}
