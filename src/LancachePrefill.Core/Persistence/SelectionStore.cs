using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancachePrefill.Core.Persistence;

/// <summary>One Selection per Module.</summary>
public interface ISelectionStore
{
    /// <summary>The Module's saved Selection, or <see cref="Selection.Empty"/>.</summary>
    Task<Selection> GetAsync(string moduleId, CancellationToken ct);
    Task SaveAsync(string moduleId, Selection selection, CancellationToken ct);
}

/// <summary>Stored as JSON in the Settings table under <c>selection:&lt;module&gt;</c>; no schema of its own.</summary>
internal sealed class SelectionStore(Func<PrefillDbContext> createContext) : ISelectionStore
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private static string Key(string moduleId) => $"selection:{moduleId}";

    public async Task<Selection> GetAsync(string moduleId, CancellationToken ct)
    {
        await using var context = createContext();
        var entity = await context.Settings.FindAsync([Key(moduleId)], ct);
        return entity is null ? Selection.Empty : JsonSerializer.Deserialize<Selection>(entity.Json, Json) ?? Selection.Empty;
    }

    public async Task SaveAsync(string moduleId, Selection selection, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(selection, Json);
        await using var context = createContext();
        var entity = await context.Settings.FindAsync([Key(moduleId)], ct);
        if (entity is null)
        {
            context.Settings.Add(new SettingEntity { Key = Key(moduleId), Json = json });
        }
        else
        {
            entity.Json = json;
        }
        await context.SaveChangesAsync(ct);
    }
}
