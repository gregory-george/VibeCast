using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using VibeCast.AppHost;

namespace VibeCast.Data;

/// <summary>
/// Design-time factory used by the `dotnet ef` CLI (no DI container is running
/// when migrations are authored). The running app uses IDbContextFactory&lt;AppDbContext&gt;
/// from DI instead (see Program.cs) — never a shared DbContext.
/// </summary>
internal sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseSqlite($"Data Source={AppPaths.DatabaseFile};Default Timeout=30");
        return new AppDbContext(optionsBuilder.Options);
    }
}
