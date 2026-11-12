using Microsoft.EntityFrameworkCore;

namespace LancachePrefill.Core.Persistence;

/// <summary>A Schedule as stored, with the id used to edit or delete it.</summary>
public sealed record StoredSchedule(Guid Id, Schedule Schedule);

public interface IScheduleStore
{
    Task<IReadOnlyList<StoredSchedule>> GetAllAsync(CancellationToken ct);
    Task<StoredSchedule> AddAsync(Schedule schedule, CancellationToken ct);

    /// <exception cref="KeyNotFoundException">No Schedule with this id.</exception>
    Task UpdateAsync(Guid id, Schedule schedule, CancellationToken ct);

    /// <summary>Deletes the Schedule; false if there was none with this id.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}

internal sealed class ScheduleStore(Func<PrefillDbContext> createContext) : IScheduleStore
{
    public async Task<IReadOnlyList<StoredSchedule>> GetAllAsync(CancellationToken ct)
    {
        await using var context = createContext();
        var entities = await context.Schedules.AsNoTracking().ToListAsync(ct);
        return [.. entities.Select(e => e.ToStored())];
    }

    public async Task<StoredSchedule> AddAsync(Schedule schedule, CancellationToken ct)
    {
        await using var context = createContext();
        var entity = ScheduleEntity.From(schedule);
        context.Schedules.Add(entity);
        await context.SaveChangesAsync(ct);
        return entity.ToStored();
    }

    public async Task UpdateAsync(Guid id, Schedule schedule, CancellationToken ct)
    {
        await using var context = createContext();
        var entity = await context.Schedules.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"No Schedule with id {id}.");
        entity.Name = schedule.Name;
        entity.Cron = schedule.Cron;
        entity.ModuleIds = [.. schedule.ModuleIds];
        await context.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var context = createContext();
        return await context.Schedules.Where(e => e.Id == id).ExecuteDeleteAsync(ct) > 0;
    }
}
