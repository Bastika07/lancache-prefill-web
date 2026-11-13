using LancachePrefill.Core;

namespace LancachePrefill.Core.Tests;

public sealed class JobQueueTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly CancellationTokenSource _stop = new();
    private Task _runLoop = Task.CompletedTask;

    private JobQueue CreateQueue(params IPrefillModule[] modules)
    {
        var queue = new JobQueue(modules);
        _runLoop = queue.RunAsync(_stop.Token);
        return queue;
    }

    [Fact]
    public async Task Enqueued_job_runs_its_module_and_succeeds_with_the_module_result()
    {
        var expected = new PrefillResult([new GameOutcome("440", GameOutcomeStatus.Downloaded, BytesDownloaded: 1_000)]);
        var steam = new FakeModule("steam") { AutoComplete = true, AutoResult = expected };
        var queue = CreateQueue(steam);

        var job = queue.Enqueue("steam", new PrefillRequest(["440"]));
        await job.Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Same(expected, job.Result);
        Assert.Equal(["440"], Assert.Single(steam.Runs).Request.GameIds);
    }

    [Fact]
    public async Task Jobs_run_in_enqueue_order_and_never_two_at_once()
    {
        var steam = new FakeModule("steam");
        var epic = new FakeModule("epic");
        var queue = CreateQueue(steam, epic);

        var first = queue.Enqueue("steam", new PrefillRequest(["440"]));
        var second = queue.Enqueue("epic", new PrefillRequest(["fortnite"]));

        await Eventually(() => steam.Runs.Count == 1);
        Assert.Equal(JobState.Running, first.State);
        Assert.Equal(JobState.Queued, second.State);
        Assert.Empty(epic.Runs);

        steam.Runs[0].Complete(PrefillResult.Empty);
        await Eventually(() => epic.Runs.Count == 1);
        Assert.Equal(JobState.Succeeded, first.State);
        Assert.Equal(JobState.Running, second.State);
    }

    [Fact]
    public async Task Cancelling_a_queued_job_ends_it_as_cancelled_without_running_its_module()
    {
        var steam = new FakeModule("steam");
        var epic = new FakeModule("epic");
        var queue = CreateQueue(steam, epic);
        queue.Enqueue("steam", new PrefillRequest(["440"]));
        var queued = queue.Enqueue("epic", new PrefillRequest(["fortnite"]));
        await Eventually(() => steam.Runs.Count == 1);

        Assert.True(queue.Cancel(queued.Id));
        await queued.Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        steam.Runs[0].Complete(PrefillResult.Empty);
        queue.Enqueue("epic", new PrefillRequest(["rocket-league"]));
        await Eventually(() => epic.Runs.Count > 0);

        Assert.Equal(JobState.Cancelled, queued.State);
        Assert.Equal(["rocket-league"], Assert.Single(epic.Runs).Request.GameIds);
    }

    [Fact]
    public async Task Cancelling_a_running_job_signals_its_module_and_the_queue_moves_on()
    {
        var steam = new FakeModule("steam");
        var queue = CreateQueue(steam);
        var running = queue.Enqueue("steam", new PrefillRequest(["440"]));
        queue.Enqueue("steam", new PrefillRequest(["570"]));
        await Eventually(() => steam.Runs.Count == 1);

        Assert.True(queue.Cancel(running.Id));
        await running.Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await Eventually(() => steam.Runs.Count == 2);

        Assert.True(steam.Runs[0].CancellationToken.IsCancellationRequested);
        Assert.Equal(JobState.Cancelled, running.State);
        Assert.Equal(["570"], steam.Runs[1].Request.GameIds);
    }

    [Fact]
    public async Task A_module_exception_fails_the_job_with_its_message_and_the_queue_moves_on()
    {
        var steam = new FakeModule("steam");
        var queue = CreateQueue(steam);
        var failing = queue.Enqueue("steam", new PrefillRequest(["440"]));
        queue.Enqueue("steam", new PrefillRequest(["570"]));
        await Eventually(() => steam.Runs.Count == 1);

        steam.Runs[0].Fail(new InvalidOperationException("Steam login expired"));
        await failing.Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await Eventually(() => steam.Runs.Count == 2);

        Assert.Equal(JobState.Failed, failing.State);
        Assert.Equal("Steam login expired", failing.FailureReason);
    }

    [Fact]
    public async Task Progress_reported_by_the_module_is_visible_on_the_running_job_in_order()
    {
        var steam = new FakeModule("steam");
        var queue = CreateQueue(steam);
        var job = queue.Enqueue("steam", new PrefillRequest(["440"]));
        await Eventually(() => steam.Runs.Count == 1);

        for (var i = 1; i <= 100; i++)
        {
            steam.Runs[0].Progress.Report(new PrefillProgress("Downloading", "440", BytesDone: i, BytesTotal: 100));
        }

        Assert.Equal(new PrefillProgress("Downloading", "440", BytesDone: 100, BytesTotal: 100), job.Progress);
    }

    [Fact]
    public async Task The_queue_lists_its_jobs_in_enqueue_order_so_a_reloaded_page_finds_them_again()
    {
        var steam = new FakeModule("steam");
        var epic = new FakeModule("epic");
        var queue = CreateQueue(steam, epic);

        var first = queue.Enqueue("steam", new PrefillRequest(["440"]));
        var second = queue.Enqueue("epic", new PrefillRequest(["fortnite"]));
        var third = queue.Enqueue("steam", new PrefillRequest(["570"]));
        await Eventually(() => steam.Runs.Count == 1);

        Assert.Equal([first, second, third], queue.Jobs);
        Assert.Equal([JobState.Running, JobState.Queued, JobState.Queued], queue.Jobs.Select(j => j.State));
    }

    [Fact]
    public async Task Dynamic_rules_add_their_games_after_the_explicit_games_without_duplicates()
    {
        var steam = new FakeModule("steam") { AutoComplete = true };
        steam.RuleGames[SelectionRule.AllOwned] = ["570", "440", "730"];
        steam.RuleGames[SelectionRule.TopPopular(2)] = ["730", "578080"];
        var queue = CreateQueue(steam);

        var job = queue.Enqueue("steam", new PrefillRequest(["440", "10"], Rules: [SelectionRule.AllOwned, SelectionRule.TopPopular(2)]));
        await job.Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        var request = Assert.Single(steam.Runs).Request;
        Assert.Equal(["440", "10", "570", "730", "578080"], request.GameIds);
        Assert.Equal(["440", "10", "570", "730", "578080"], job.ResolvedGameIds);
    }

    [Fact]
    public async Task Rules_are_resolved_when_the_job_starts_not_when_it_is_enqueued()
    {
        var steam = new FakeModule("steam");
        steam.RuleGames[SelectionRule.RecentlyPlayed] = ["440"];
        var queue = CreateQueue(steam);
        queue.Enqueue("steam", new PrefillRequest(["10"]));
        await Eventually(() => steam.Runs.Count == 1);

        var job = queue.Enqueue("steam", new PrefillRequest([], Rules: [SelectionRule.RecentlyPlayed]));
        Assert.Null(job.ResolvedGameIds);
        steam.RuleGames[SelectionRule.RecentlyPlayed] = ["570"];
        steam.Runs[0].Complete(PrefillResult.Empty);
        await Eventually(() => steam.Runs.Count == 2);

        Assert.Equal(["570"], steam.Runs[1].Request.GameIds);
    }

    [Fact]
    public async Task A_failing_rule_becomes_a_warning_and_the_job_prefills_the_remaining_games()
    {
        var steam = new FakeModule("steam") { AutoComplete = true };
        steam.RuleGames[SelectionRule.AllOwned] = ["570"];
        steam.FailingRules[SelectionRule.TopPopular(10)] = new HttpRequestException("SteamCharts unreachable");
        var queue = CreateQueue(steam);

        var job = queue.Enqueue("steam", new PrefillRequest(["440"], Rules: [SelectionRule.TopPopular(10), SelectionRule.AllOwned]));
        await job.Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(["440", "570"], Assert.Single(steam.Runs).Request.GameIds);
        var warning = Assert.Single(job.Warnings);
        Assert.Contains("TopPopular(10)", warning, StringComparison.Ordinal);
        Assert.Contains("SteamCharts unreachable", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_a_rule_failed_and_no_game_is_left_the_job_fails_without_running_its_module()
    {
        var steam = new FakeModule("steam") { AutoComplete = true };
        steam.FailingRules[SelectionRule.RecentlyPlayed] = new HttpRequestException("Steam Web API down");
        var queue = CreateQueue(steam);

        var job = queue.Enqueue("steam", new PrefillRequest([], Rules: [SelectionRule.RecentlyPlayed]));
        await job.Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("Steam Web API down", job.FailureReason, StringComparison.Ordinal);
        Assert.Empty(steam.Runs);
    }

    [Fact]
    public async Task Rules_that_resolve_to_no_games_without_failing_still_run_the_module()
    {
        var steam = new FakeModule("steam") { AutoComplete = true };
        steam.RuleGames[SelectionRule.RecentlyPlayed] = [];
        var queue = CreateQueue(steam);

        var job = queue.Enqueue("steam", new PrefillRequest([], Rules: [SelectionRule.RecentlyPlayed]));
        await job.Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Empty(Assert.Single(steam.Runs).Request.GameIds);
    }

    [Fact]
    public void A_rule_the_module_does_not_support_is_rejected_when_enqueued()
    {
        var battlenet = new FakeModule("battlenet");
        battlenet.RuleGames[SelectionRule.AllOwned] = [];
        var queue = CreateQueue(battlenet);

        var ex = Assert.Throws<ArgumentException>(() =>
            queue.Enqueue("battlenet", new PrefillRequest([], Rules: [SelectionRule.AllOwned, SelectionRule.RecentlyPlayed])));

        Assert.Contains("RecentlyPlayed", ex.Message, StringComparison.Ordinal);
        Assert.Empty(queue.Jobs);
    }

    [Fact]
    public async Task A_cancelled_job_keeps_the_outcomes_of_the_games_finished_before_the_cancel()
    {
        var steam = new FakeModule("steam");
        var queue = CreateQueue(steam);
        var job = queue.Enqueue("steam", new PrefillRequest(["440", "570"]));
        await Eventually(() => steam.Runs.Count == 1);

        steam.Runs[0].Progress.Report(PrefillProgress.GameFinished(new GameOutcome("440", GameOutcomeStatus.Downloaded, BytesDownloaded: 1_000)));
        queue.Cancel(job.Id);
        await job.Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(JobState.Cancelled, job.State);
        Assert.Equal([new GameOutcome("440", GameOutcomeStatus.Downloaded, BytesDownloaded: 1_000)], job.Result?.Games);
    }

    [Fact]
    public async Task A_failed_job_keeps_the_outcomes_of_the_games_finished_before_the_failure()
    {
        var steam = new FakeModule("steam");
        var queue = CreateQueue(steam);
        var job = queue.Enqueue("steam", new PrefillRequest(["440", "570"]));
        await Eventually(() => steam.Runs.Count == 1);

        steam.Runs[0].Progress.Report(PrefillProgress.GameFinished(new GameOutcome("440", GameOutcomeStatus.UpToDate)));
        steam.Runs[0].Fail(new InvalidOperationException("No Lancache found"));
        await job.Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal([new GameOutcome("440", GameOutcomeStatus.UpToDate)], job.Result?.Games);
    }

    [Fact]
    public async Task A_succeeded_job_takes_the_outcomes_the_module_returns()
    {
        var steam = new FakeModule("steam");
        var queue = CreateQueue(steam);
        var job = queue.Enqueue("steam", new PrefillRequest(["440"]));
        await Eventually(() => steam.Runs.Count == 1);

        steam.Runs[0].Progress.Report(PrefillProgress.GameFinished(new GameOutcome("440", GameOutcomeStatus.Downloaded, BytesDownloaded: 5)));
        steam.Runs[0].Complete(new PrefillResult([new GameOutcome("440", GameOutcomeStatus.Downloaded, BytesDownloaded: 5)]));
        await job.Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal([new GameOutcome("440", GameOutcomeStatus.Downloaded, BytesDownloaded: 5)], job.Result?.Games);
    }

    [Fact]
    public void Enqueueing_for_an_unknown_module_is_rejected()
    {
        var queue = CreateQueue(new FakeModule("steam"));

        var ex = Assert.Throws<ArgumentException>(() => queue.Enqueue("origin", new PrefillRequest(["x"])));

        Assert.Contains("origin", ex.Message, StringComparison.Ordinal);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Timeout);
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await _runLoop.WaitAsync(Timeout); } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
