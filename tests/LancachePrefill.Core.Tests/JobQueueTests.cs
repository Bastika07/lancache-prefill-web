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

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await _runLoop.WaitAsync(Timeout); } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
