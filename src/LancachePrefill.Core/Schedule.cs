namespace LancachePrefill.Core;

/// <summary>
/// A cron expression plus the Modules it prefills. When it fires, each Module gets one
/// update-only Job. See GLOSSARY.md → Schedule.
/// </summary>
public sealed record Schedule(string Name, string Cron, IReadOnlyList<string> ModuleIds);
