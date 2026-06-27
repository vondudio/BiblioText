using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BiblioText.Cloud.Catalog;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations</c> can construct the context
/// without running <c>Program.cs</c>. The connection string here is only used to
/// build the model/migration; it doesn't need to point at a live database.
/// </summary>
public sealed class CatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=bibliotext;Username=postgres;Password=postgres",
                npgsql => npgsql.UseVector())
            .Options;
        return new CatalogDbContext(options);
    }
}
