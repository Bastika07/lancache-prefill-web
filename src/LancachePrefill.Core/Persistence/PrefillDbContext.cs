using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LancachePrefill.Core.Persistence;

internal sealed class PrefillDbContext(DbContextOptions<PrefillDbContext> options) : DbContext(options)
{
    public DbSet<ScheduleEntity> Schedules => Set<ScheduleEntity>();
    public DbSet<JobRecordEntity> JobRecords => Set<JobRecordEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite can't order or compare DateTimeOffset; store it as a sortable 64-bit value.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        configurationBuilder.Properties<JobState>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JobRecordEntity>().HasKey(e => e.JobId);
        modelBuilder.Entity<JobRecordEntity>().HasIndex(e => e.FinishedAt);
    }
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
