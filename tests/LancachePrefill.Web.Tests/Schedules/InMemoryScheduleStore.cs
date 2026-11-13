using LancachePrefill.Core;
using LancachePrefill.Core.Persistence;

namespace LancachePrefill.Web.Tests.Schedules;

public sealed class InMemoryScheduleStore : IScheduleStore
{
    private readonly List<StoredSchedule> _schedules = [];

    public IReadOnlyList<Schedule> Schedules => [.. _schedules.Select(s => s.Schedule)];

    public StoredSchedule Add(Schedule schedule)
    {
        var stored = new StoredSchedule(Guid.NewGuid(), schedule);
        _schedules.Add(stored);
        return stored;
    }

    public Task<IReadOnlyList<StoredSchedule>> GetAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<StoredSchedule>>([.. _schedules]);

    public Task<StoredSchedule> AddAsync(Schedule schedule, CancellationToken ct) => Task.FromResult(Add(schedule));

    public Task UpdateAsync(Guid id, Schedule schedule, CancellationToken ct)
    {
        var index = _schedules.FindIndex(s => s.Id == id);
        if (index < 0)
        {
            throw new KeyNotFoundException($"No Schedule with id {id}.");
        }
        _schedules[index] = new StoredSchedule(id, schedule);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => Task.FromResult(_schedules.RemoveAll(s => s.Id == id) > 0);
}
