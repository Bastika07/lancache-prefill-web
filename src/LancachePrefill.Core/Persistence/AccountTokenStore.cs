using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace LancachePrefill.Core.Persistence;

/// <summary>One Account token (e.g. a refresh token) per Module, encrypted at rest.</summary>
public interface IAccountTokenStore
{
    Task<string?> GetAsync(string moduleId, CancellationToken ct);
    Task SetAsync(string moduleId, string token, CancellationToken ct);
    Task RemoveAsync(string moduleId, CancellationToken ct);
}

internal sealed class AccountTokenStore(Func<PrefillDbContext> createContext, IDataProtectionProvider dataProtection) : IAccountTokenStore
{
    // Changing this string makes every stored token unreadable.
    private readonly IDataProtector _protector = dataProtection.CreateProtector("LancachePrefill.AccountTokens.v1");

    public async Task<string?> GetAsync(string moduleId, CancellationToken ct)
    {
        await using var context = createContext();
        var entity = await context.AccountTokens.FindAsync([moduleId], ct);
        return entity is null ? null : _protector.Unprotect(entity.ProtectedToken);
    }

    public async Task SetAsync(string moduleId, string token, CancellationToken ct)
    {
        await using var context = createContext();
        var entity = await context.AccountTokens.FindAsync([moduleId], ct);
        if (entity is null)
        {
            context.AccountTokens.Add(new AccountTokenEntity { ModuleId = moduleId, ProtectedToken = _protector.Protect(token) });
        }
        else
        {
            entity.ProtectedToken = _protector.Protect(token);
        }
        await context.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(string moduleId, CancellationToken ct)
    {
        await using var context = createContext();
        await context.AccountTokens.Where(e => e.ModuleId == moduleId).ExecuteDeleteAsync(ct);
    }
}

internal sealed class AccountTokenEntity
{
    public required string ModuleId { get; set; }
    public required string ProtectedToken { get; set; }
}
