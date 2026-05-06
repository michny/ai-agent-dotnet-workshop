using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.Configuration.Attributes;
using FinanceAssistant.Models;
using Microsoft.EntityFrameworkCore;

namespace FinanceAssistant.Data;

public static class TransactionsSeeder
{
    // Invoked by EF Core's UseAsyncSeeding hook after EnsureCreatedAsync
    // creates the database. The bool flag (performAsyncSeeding) is unused
    // here. We only seed via EnsureCreatedAsync, never migrations.
    public static async Task SeedAsync(
        DbContext context,
        bool _,
        CancellationToken cancellationToken = default)
    {
        if (context is not FinanceDbContext db)
        {
            return;
        }

        if (await db.Transactions.AnyAsync(cancellationToken))
        {
            return;
        }

        var csvPath = Path.Combine(AppContext.BaseDirectory, "scaffolding", "transactions.csv");

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            TrimOptions = TrimOptions.Trim
        };

        using var reader = new StreamReader(csvPath);
        using var csv = new CsvReader(reader, config);

        var rows = csv.GetRecords<TransactionCsvRow>().ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var transactions = rows.Select(r => new Transaction
        {
            Id = Guid.NewGuid(),
            Date = r.Date,
            Amount = r.Amount,
            Merchant = r.Merchant,
            Category = r.Category,
            Description = r.Description
        }).ToList();

        await db.Transactions.AddRangeAsync(transactions, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private sealed class TransactionCsvRow
    {
        [Format("yyyy-MM-dd")]
        public DateOnly Date { get; set; }

        public decimal Amount { get; set; }

        public string Merchant { get; set; } = string.Empty;

        public string Category { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;
    }
}
