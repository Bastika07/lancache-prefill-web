using Microsoft.EntityFrameworkCore;

namespace LancachePrefill.Core.Persistence;

/// <summary>A Schedule as stored, with the id used to edit or delete it.</summary>
public sealed record StoredSchedule(Guid Id, Schedule Schedule);

public interface IScheduleStore
{
    Task<IReadOnlyList<StoredSchedule>> GetAllAsync(CancellationToken ct);
}

internal sealed class ScheduleStore(Func<PrefillDbContext> createContext) : IScheduleStore
{
    public async Task<IReadOnlyList<StoredSchedule>> GetAllAsync(CancellationToken ct)
    {
        await using var context = createContext();
        var entities = await context.Schedules.AsNoTracking().ToListAsync(ct);
        return [.. entities.Select(e => e.ToStored())];
    }
}
