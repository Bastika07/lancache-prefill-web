using LancachePrefill.Core;
using LancachePrefill.Core.Persistence;

namespace LancachePrefill.Web.Tests.Selections;

public sealed class InMemorySelectionStore : ISelectionStore
{
    public Dictionary<string, Selection> Saved { get; } = [];

    public Task<Selection> GetAsync(string moduleId, CancellationToken ct) => Task.FromResult(Saved.GetValueOrDefault(moduleId, Selection.Empty));

    public Task SaveAsync(string moduleId, Selection selection, CancellationToken ct)
    {
        Saved[moduleId] = selection;
        return Task.CompletedTask;
    }
}
