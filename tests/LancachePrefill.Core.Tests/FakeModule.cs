using LancachePrefill.Core;

namespace LancachePrefill.Core.Tests;

/// <summary>
/// Test double for <see cref="IPrefillModule"/>. Each PrefillAsync call is recorded and,
/// unless <see cref="AutoComplete"/> is set, blocks until the test releases it.
/// </summary>
internal sealed class FakeModule(string id) : IPrefillModule
{
    private readonly List<PendingRun> _runs = [];

    public string Id { get; } = id;
    public string DisplayName => $"Fake {Id}";

    public bool AutoComplete { get; init; }
    public PrefillResult AutoResult { get; init; } = PrefillResult.Empty;

    /// <summary>What each dynamic rule resolves to; a rule mapped to an exception throws it.</summary>
    public Dictionary<SelectionRule, IReadOnlyList<string>> RuleGames { get; } = [];

    public IReadOnlySet<SelectionRuleKind> SupportedRules => RuleGames.Keys.Select(r => r.Kind).ToHashSet();

    public List<SelectionRule> ResolvedRules { get; } = [];

    public Task<IReadOnlyList<string>> ResolveRuleAsync(SelectionRule rule, CancellationToken ct)
    {
        lock (ResolvedRules) { ResolvedRules.Add(rule); }
        return Task.FromResult(RuleGames[rule]);
    }

    public IReadOnlyList<PendingRun> Runs { get { lock (_runs) { return [.. _runs]; } } }

    public Task<PrefillResult> PrefillAsync(PrefillRequest request, IProgress<PrefillProgress> progress, CancellationToken ct)
    {
        var run = new PendingRun(request, progress, ct);
        lock (_runs) { _runs.Add(run); }
        if (AutoComplete)
        {
            run.Complete(AutoResult);
        }
        return run.Task;
    }

    internal sealed class PendingRun
    {
        private readonly TaskCompletionSource<PrefillResult> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingRun(PrefillRequest request, IProgress<PrefillProgress> progress, CancellationToken ct)
        {
            Request = request;
            Progress = progress;
            CancellationToken = ct;
            ct.Register(() => _tcs.TrySetCanceled(ct));
        }

        public PrefillRequest Request { get; }
        public IProgress<PrefillProgress> Progress { get; }
        public CancellationToken CancellationToken { get; }
        public Task<PrefillResult> Task => _tcs.Task;

        public void Complete(PrefillResult result) => _tcs.TrySetResult(result);
        public void Fail(Exception ex) => _tcs.TrySetException(ex);
    }
}
