using LancachePrefill.Core.Persistence;

namespace LancachePrefill.Core.Tests;

/// <summary>In-memory <see cref="ISelectionStore"/> whose Selection per Module is computed by a function.</summary>
internal sealed class FakeSelectionStore(Func<string, Selection> selectionFor) : ISelectionStore
{
    public Task<Selection> GetAsync(string moduleId, CancellationToken ct) => Task.FromResult(selectionFor(moduleId));

    public Task SaveAsync(string moduleId, Selection selection, CancellationToken ct) => throw new NotSupportedException();
}
