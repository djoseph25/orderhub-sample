using Microsoft.EntityFrameworkCore;

namespace OrderHub.Api.Data;

public sealed class Product
{
    public int Id { get; set; }
    public required string Sku { get; set; }
    public required string Name { get; set; }
    public decimal Price { get; set; }

    /// <summary>
    /// Week 11: optimistic concurrency token. EF adds "WHERE Version = @original" to UPDATE/DELETE.
    /// If 0 rows are affected, it throws DbUpdateConcurrencyException, which the API maps to 409.
    /// (On SQL Server you could use a rowversion column instead. A Guid works on every provider, including SQLite in tests.)
    /// </summary>
    public Guid Version { get; set; } = Guid.NewGuid();

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Sku).HasMaxLength(32).IsRequired();
            e.HasIndex(p => p.Sku).IsUnique();
            e.Property(p => p.Name).HasMaxLength(200).IsRequired();
            e.Property(p => p.Price).HasPrecision(18, 2);
            e.Property(p => p.Version).IsConcurrencyToken();
        });
    }
}
