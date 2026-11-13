using Bunit;
using LancachePrefill.Core;
using LancachePrefill.Core.Persistence;
using LancachePrefill.Web.Components.Schedules;
using LancachePrefill.Web.Tests.Selections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace LancachePrefill.Web.Tests.Schedules;

public sealed class ScheduleEditorTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly BunitContext _ctx = new();
    private readonly InMemoryScheduleStore _store = new();
    private readonly InMemorySelectionStore _selections = new();
    private readonly CatalogModule _steam = new("steam", []);
    private readonly CatalogModule _epic = new("epic", []);

    // Wednesday 2026-01-07 00:00 UTC = 01:00 in Berlin (winter, UTC+1).
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 7, 0, 0, 0, TimeSpan.Zero));
    private readonly JobQueue _queue;
    private readonly Scheduler _scheduler;

    public ScheduleEditorTests()
    {
        _queue = new JobQueue([_steam, _epic]);
        _scheduler = new Scheduler(_queue, [], TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"), _time, _selections);
        _ctx.Services.AddSingleton<IScheduleStore>(_store);
        _ctx.Services.AddSingleton(_scheduler);
        _ctx.Services.AddSingleton<TimeProvider>(_time);
        _ctx.Services.AddSingleton<IEnumerable<IPrefillModule>>([_steam, _epic]);
    }

    private IRenderedComponent<ScheduleEditor> Render() => _ctx.Render<ScheduleEditor>();

    [Fact]
    public void It_lists_each_schedule_with_its_modules_and_next_run_in_the_configured_time_zone()
    {
        _store.Add(new Schedule("Nightly", "0 3 * * *", ["steam", "epic"]));

        var editor = Render();

        editor.WaitForAssertion(() =>
        {
            var row = Assert.Single(editor.FindAll("[data-testid=schedule-row]"));
            Assert.Contains("Nightly", row.TextContent, StringComparison.Ordinal);
            Assert.Contains("0 3 * * *", row.TextContent, StringComparison.Ordinal);
            Assert.Contains("Fake steam, Fake epic", row.TextContent, StringComparison.Ordinal);
            Assert.Contains("Wed 2026-01-07 03:00", row.TextContent, StringComparison.Ordinal);
        }, Wait);
        Assert.Contains("Europe/Berlin", editor.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_schedule_previews_its_next_runs_and_is_in_effect_once_saved()
    {
        var editor = Render();
        editor.WaitForElement("#schedule-name", Wait).Change("Mornings");
        editor.Find("#schedule-cron").Input("30 6 * * 1-5");
        editor.Find("#schedule-module-epic").Change(true);

        var preview = editor.Find("[data-testid=cron-preview]").TextContent;
        Assert.Contains("Wed 2026-01-07 06:30", preview, StringComparison.Ordinal);
        Assert.Contains("Thu 2026-01-08 06:30", preview, StringComparison.Ordinal);
        Assert.Contains("Fri 2026-01-09 06:30", preview, StringComparison.Ordinal);

        editor.Find("form#schedule-form").Submit();

        editor.WaitForAssertion(() =>
        {
            var saved = Assert.Single(_store.Schedules);
            Assert.Equal("Mornings", saved.Name);
            Assert.Equal("30 6 * * 1-5", saved.Cron);
            Assert.Equal(["epic"], saved.ModuleIds);
            Assert.Equal("Mornings", Assert.Single(_scheduler.Schedules).Name);
            Assert.Single(editor.FindAll("[data-testid=schedule-row]"));
        }, Wait);
    }

    [Theory]
    [InlineData("", "0 3 * * *", true, "name")]
    [InlineData("nightly", "0 3 * * *", true, "already exists")]
    [InlineData("Mornings", "every day at 6", true, "invalid")]
    [InlineData("Mornings", "0 6 * * *", false, "Module")]
    public void An_incomplete_or_invalid_schedule_is_rejected_and_nothing_changes(string name, string cron, bool tickModule, string error)
    {
        _store.Add(new Schedule("Nightly", "0 3 * * *", ["steam"]));
        var editor = Render();
        editor.WaitForElement("#schedule-name", Wait).Change(name);
        editor.Find("#schedule-cron").Input(cron);
        if (tickModule)
        {
            editor.Find("#schedule-module-steam").Change(true);
        }

        editor.Find("form#schedule-form").Submit();

        Assert.Contains(error, editor.Find("[role=alert]").TextContent, StringComparison.Ordinal);
        Assert.Equal(["Nightly"], _store.Schedules.Select(s => s.Name));
        Assert.Empty(_scheduler.Schedules);
    }

    [Fact]
    public void An_invalid_cron_says_so_in_the_preview_while_typing()
    {
        var editor = Render();

        editor.WaitForElement("#schedule-cron", Wait).Input("0 25 * * *");

        Assert.Contains("Not a valid cron", editor.Find("[data-testid=cron-preview]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Editing_a_schedule_changes_it_in_place_and_keeping_its_own_name_is_allowed()
    {
        _store.Add(new Schedule("Nightly", "0 3 * * *", ["steam"]));
        var weekly = _store.Add(new Schedule("Weekly", "0 4 * * 0", ["epic"]));
        var editor = Render();
        editor.WaitForElement("[data-testid=schedule-row]", Wait);

        editor.FindAll("[data-testid=schedule-row]")[0].QuerySelector("button.edit")!.Click();
        Assert.Equal("Nightly", editor.Find("#schedule-name").GetAttribute("value"));
        Assert.Equal("0 3 * * *", editor.Find("#schedule-cron").GetAttribute("value"));
        editor.Find("#schedule-cron").Input("0 2 * * *");
        editor.Find("#schedule-module-epic").Change(true);
        editor.Find("form#schedule-form").Submit();

        editor.WaitForAssertion(() =>
        {
            Assert.Equal(
                [new Schedule("Nightly", "0 2 * * *", ["steam", "epic"]), weekly.Schedule],
                _store.Schedules,
                ScheduleComparer.Instance);
            Assert.Equal(["Nightly", "Weekly"], _scheduler.Schedules.Select(s => s.Name));
            Assert.Equal("0 2 * * *", _scheduler.Schedules[0].Cron);
        }, Wait);
    }

    [Fact]
    public void Deleting_a_schedule_removes_it_from_the_store_and_the_scheduler()
    {
        _store.Add(new Schedule("Nightly", "0 3 * * *", ["steam"]));
        _store.Add(new Schedule("Weekly", "0 4 * * 0", ["epic"]));
        var editor = Render();
        editor.WaitForElement("[data-testid=schedule-row]", Wait);

        editor.FindAll("[data-testid=schedule-row]")[0].QuerySelector("button.delete")!.Click();

        editor.WaitForAssertion(() =>
        {
            Assert.Equal(["Weekly"], _store.Schedules.Select(s => s.Name));
            Assert.Equal(["Weekly"], _scheduler.Schedules.Select(s => s.Name));
            Assert.Single(editor.FindAll("[data-testid=schedule-row]"));
        }, Wait);
    }

    [Fact]
    public void Run_now_queues_the_schedules_jobs_and_says_which_modules_were_skipped()
    {
        _store.Add(new Schedule("Nightly", "0 3 * * *", ["steam", "epic"]));
        _selections.Saved["steam"] = new Selection(["730"], []);
        _selections.Saved["epic"] = new Selection(["fortnite"], []);
        _queue.Enqueue("epic", new PrefillRequest(["manual"]));
        var editor = Render();
        editor.WaitForElement("[data-testid=schedule-row]", Wait);

        editor.Find("[data-testid=schedule-row] button.run-now").Click();

        editor.WaitForAssertion(() =>
        {
            var steamJob = Assert.Single(_queue.Jobs, j => j.ModuleId == "steam");
            Assert.Equal(["730"], steamJob.Request.GameIds);
            Assert.False(steamJob.Request.Force);
            Assert.Single(_queue.Jobs, j => j.ModuleId == "epic");
            var message = editor.Find("[data-testid=run-now-result]").TextContent;
            Assert.Contains("queued Fake steam", message, StringComparison.Ordinal);
            Assert.Contains("skipped Fake epic", message, StringComparison.Ordinal);
        }, Wait);
    }

    private sealed class ScheduleComparer : IEqualityComparer<Schedule>
    {
        public static readonly ScheduleComparer Instance = new();

        public bool Equals(Schedule? x, Schedule? y) =>
            x is not null && y is not null && x.Name == y.Name && x.Cron == y.Cron && x.ModuleIds.SequenceEqual(y.ModuleIds);

        public int GetHashCode(Schedule obj) => HashCode.Combine(obj.Name, obj.Cron);
    }

    public async ValueTask DisposeAsync() => await _ctx.DisposeAsync();
}
