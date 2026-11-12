namespace LancachePrefill.Core;

/// <summary>
/// A cron expression plus the Modules it prefills. When it fires, each Module gets one
/// update-only Job. See GLOSSARY.md → Schedule.
/// </summary>
public sealed record Schedule(string Name, string Cron, IReadOnlyList<string> ModuleIds);

/// <summary>One time a Schedule fired, and what happened per Module.</summary>
public sealed record ScheduleFiring(string ScheduleName, DateTimeOffset At, IReadOnlyList<ModuleFiring> Modules);

/// <summary>The Job a firing enqueued for a Module, or null when it was skipped because one was already queued or running.</summary>
public sealed record ModuleFiring(string ModuleId, Job? Job)
{
    public bool Skipped => Job is null;
}
