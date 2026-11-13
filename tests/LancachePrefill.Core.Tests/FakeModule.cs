using LancachePrefill.Core;
using Microsoft.Extensions.Logging;

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

    /// <summary>Logged line by line (split on \n) when a run starts, from inside the Job.</summary>
    public string? LogOnRun { get; init; }
    public ILogger? Logger { get; init; }
    public PrefillResult AutoResult { get; init; } = PrefillResult.Empty;

    /// <summary>What each dynamic rule resolves to.</summary>
    public Dictionary<SelectionRule, IReadOnlyList<string>> RuleGames { get; } = [];

    public Dictionary<SelectionRule, Exception> FailingRules { get; } = [];

    public IReadOnlySet<SelectionRuleKind> SupportedRules => RuleGames.Keys.Concat(FailingRules.Keys).Select(r => r.Kind).ToHashSet();

    public List<SelectionRule> ResolvedRules { get; } = [];

    public Task<IReadOnlyList<string>> ResolveRuleAsync(SelectionRule rule, CancellationToken ct)
    {
        lock (ResolvedRules) { ResolvedRules.Add(rule); }
        return FailingRules.TryGetValue(rule, out var ex) ? Task.FromException<IReadOnlyList<string>>(ex) : Task.FromResult(RuleGames[rule]);
    }

    public IReadOnlyList<PendingRun> Runs { get { lock (_runs) { return [.. _runs]; } } }

    public Task<PrefillResult> PrefillAsync(PrefillRequest request, IProgress<PrefillProgress> progress, CancellationToken ct)
    {
        if (LogOnRun is not null && Logger is not null)
        {
            foreach (var line in LogOnRun.Split('\n'))
            {
#pragma warning disable CA1848, CA1873 // test double: plain logging is fine
                Logger.LogInformation("{Line}", line);
#pragma warning restore CA1848, CA1873
            }
        }
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
