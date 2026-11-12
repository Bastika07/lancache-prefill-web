using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LancachePrefill.Core.Persistence;

/// <summary>Lets <c>dotnet ef migrations add</c> build the model. Not used at runtime.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PrefillDbContext>
{
    public PrefillDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PrefillDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
