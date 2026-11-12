using Microsoft.EntityFrameworkCore;

namespace LancachePrefill.Core.Persistence;

internal sealed class PrefillDbContext(DbContextOptions<PrefillDbContext> options) : DbContext(options)
{
    public DbSet<ScheduleEntity> Schedules => Set<ScheduleEntity>();
}

internal sealed class ScheduleEntity
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Cron { get; set; }
    public required List<string> ModuleIds { get; set; }

    public static ScheduleEntity From(Schedule schedule) => new()
    {
        Id = Guid.NewGuid(),
        Name = schedule.Name,
        Cron = schedule.Cron,
        ModuleIds = [.. schedule.ModuleIds],
    };

    public StoredSchedule ToStored() => new(Id, new Schedule(Name, Cron, ModuleIds));
}
