using FinanceAssistant.Models;
using Microsoft.EntityFrameworkCore;

namespace FinanceAssistant.Data;

public class FinanceDbContext : DbContext
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=financeassistant;Username=postgres;Password=postgres";

    public FinanceDbContext()
    {
    }

    public FinanceDbContext(DbContextOptions<FinanceDbContext> options) : base(options)
    {
    }

    public DbSet<Transaction> Transactions => Set<Transaction>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseNpgsql(DefaultConnectionString, o => o.UseVector());
        }

        optionsBuilder.UseAsyncSeeding(TransactionsSeeder.SeedAsync);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // pgvector extension. EnsureCreatedAsync installs it when the schema is built.
        modelBuilder.HasPostgresExtension("vector");

        var transaction = modelBuilder.Entity<Transaction>();
        transaction.ToTable("Transactions");
        transaction.HasKey(t => t.Id);
        transaction.Property(t => t.Amount).HasPrecision(18, 2);
        transaction.Property(t => t.Merchant).HasMaxLength(200).IsRequired();
        transaction.Property(t => t.Category).HasMaxLength(100).IsRequired();
        transaction.Property(t => t.Description).HasMaxLength(2000).IsRequired();

        // 1536 matches the dimension of text-embedding-3-small.
        transaction.Property(t => t.Embedding).HasColumnType("vector(1536)");

        transaction.HasIndex(t => t.Date);
        transaction.HasIndex(t => t.Category);

        // HNSW index for fast cosine-distance similarity search.
        // Without it, similarity search is a sequential scan over every row.
        transaction.HasIndex(t => t.Embedding)
            .HasMethod("hnsw")
            .HasOperators("vector_cosine_ops");
    }
}
