using BiblioText.Cloud.Catalog.Entities;
using Microsoft.EntityFrameworkCore;

namespace BiblioText.Cloud.Catalog;

/// <summary>
/// EF Core context for the shared catalog. Postgres + pgvector: the
/// <c>vector</c> extension is declared so migrations create it, and the
/// <see cref="Book.Embedding"/> column is typed <c>vector(1536)</c>.
/// </summary>
public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Book> Books => Set<Book>();
    public DbSet<Copy> Copies => Set<Copy>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<Book>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.NormalizedKey).IsUnique();
            b.Property(x => x.NormalizedKey).HasMaxLength(512);
            b.Property(x => x.Title).HasMaxLength(1024);
            b.Property(x => x.Author).HasMaxLength(1024);
            b.Property(x => x.Embedding).HasColumnType($"vector({Book.EmbeddingDimensions})");
        });

        modelBuilder.Entity<Copy>(c =>
        {
            c.HasKey(x => x.Id);
            c.HasIndex(x => new { x.StationId, x.StationBookId }).IsUnique();
            c.Property(x => x.StationId).HasMaxLength(128);
            c.Property(x => x.StationBookId).HasMaxLength(128);
            c.Property(x => x.OwnerHousehold).HasMaxLength(256);
            c.Property(x => x.ShelfLocation).HasMaxLength(256);
            c.HasOne(x => x.Book)
                .WithMany(x => x.Copies)
                .HasForeignKey(x => x.BookId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
