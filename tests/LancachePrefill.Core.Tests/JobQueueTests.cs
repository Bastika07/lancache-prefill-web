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
