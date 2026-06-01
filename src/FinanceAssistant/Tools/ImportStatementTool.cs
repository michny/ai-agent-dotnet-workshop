using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.Configuration.Attributes;
using FinanceAssistant.Data;
using FinanceAssistant.Models;
using Microsoft.Extensions.AI;
using Microsoft.EntityFrameworkCore;

namespace FinanceAssistant.Tools;

public class ImportStatementTool : AITool
{
    [Description("Imports transactions from a CSV file. The transactions from the file are inserted to the database without any further guardrails. The CSV should have the following columns: Date, Amount, Merchant, Category, Description. Return a summary of the imported transactions.")]
    public async Task<object> ImportTransactionsFromCsv(
        [Description("The path to the CSV file containing the transactions.")] string path, 
        [Description("Skip rows that already exist in the database (matched by Date+Amount+Merchant+Description). Default true.")] bool skipDuplicates = true,
        CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            return new
            {
                error = "file_not_found",
                hint = "Pass an absolute path. Tilde (~) is not expanded.",
                path = path
            };
        }

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            TrimOptions = TrimOptions.Trim
        };

        var imported = new List<object>();
        var skipped = new List<object>();
        var errors = new List<object>();

        await using var db = new FinanceDbContext();

        // Build a hash set of existing rows once, so we can detect duplicates
        // in memory without N+1 queries.
        var existing = skipDuplicates
            ? (await db.Transactions
                .Select(t => new { t.Date, t.Amount, t.Merchant, t.Description })
                .ToListAsync(ct))
                .Select(t => HashKey(t.Date, t.Amount, t.Merchant, t.Description))
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, config);

        await csv.ReadAsync();
        csv.ReadHeader();

        int rowNumber = 1; // header is row 1
        while (await csv.ReadAsync())
        {
            rowNumber++;
            try
            {
                var row = csv.GetRecord<StatementCsvRow>();
                var key = HashKey(row.Date, row.Amount, row.Merchant, row.Description);

                if (existing.Contains(key))
                {
                    skipped.Add(new { row = rowNumber, reason = "duplicate", row.Date, row.Amount, row.Merchant });
                    continue;
                }

                db.Transactions.Add(new Transaction
                {
                    Id = Guid.NewGuid(),
                    Date = row.Date,
                    Amount = row.Amount,
                    Merchant = row.Merchant,
                    Category = row.Category,
                    Description = row.Description
                });

                existing.Add(key);
                imported.Add(new { row = rowNumber, row.Date, row.Amount, row.Merchant });
            }
            catch (Exception ex) when (ex is CsvHelperException or FormatException)
            {
                errors.Add(new { row = rowNumber, message = ex.Message });
            }
        }

        await db.SaveChangesAsync(ct);

        return new
        {
            file = path,
            importedCount = imported.Count,
            skippedCount = skipped.Count,
            errorCount = errors.Count,
            skipped = skipped.Take(5).ToList(),
            errors = errors.Take(5).ToList(),
            note = imported.Count > 0
                ? "Imported rows do not have embeddings yet. Restart the app so the embedding pass in Program.cs picks them up, or search will not find them."
                : null
        };
    }

    private static string HashKey(DateOnly date, decimal amount, string merchant, string description)
    {
        var raw = $"{date:yyyy-MM-dd}|{amount.ToString(CultureInfo.InvariantCulture)}|{merchant}|{description}";
        var bytes = Encoding.UTF8.GetBytes(raw);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private sealed class StatementCsvRow
    {
        [Format("yyyy-MM-dd")]
        public DateOnly Date { get; set; }

        public decimal Amount { get; set; }

        public string Merchant { get; set; } = string.Empty;

        public string Category { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;
    }
}