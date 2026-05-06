using System.Globalization;
using FinanceAssistant.Data;
using FinanceAssistant.Models;
using Microsoft.EntityFrameworkCore;

namespace FinanceAssistant.Services;

public class TransactionsService : ITransactionsService
{
    private readonly FinanceDbContext _db;

    public TransactionsService(FinanceDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<TransactionSummary>> GetTransactions(string dateExpression, CancellationToken cancellationToken = default)
    {
        var (start, end) = ParseDateExpression(dateExpression);

        return await _db.Transactions
            .Where(t => t.Date >= start && t.Date <= end)
            .OrderBy(t => t.Date)
            .Select(t => new TransactionSummary(t.Id, t.Date, t.Amount, t.Merchant, t.Category, t.Description))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TransactionSummary>> GetByCategory(string category, CancellationToken cancellationToken = default)
    {
        return await _db.Transactions
            .Where(t => t.Category == category)
            .OrderBy(t => t.Date)
            .Select(t => new TransactionSummary(t.Id, t.Date, t.Amount, t.Merchant, t.Category, t.Description))
            .ToListAsync(cancellationToken);
    }

    private static (DateOnly Start, DateOnly End) ParseDateExpression(string expr)
    {
        const string format = "yyyy-MM-dd";

        if (expr.Contains("..", StringComparison.Ordinal))
        {
            var parts = expr.Split("..", 2, StringSplitOptions.None);
            if (parts.Length == 2
                && DateOnly.TryParseExact(parts[0], format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                && DateOnly.TryParseExact(parts[1], format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            {
                return (start, end);
            }
        }
        else if (DateOnly.TryParseExact(expr, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var single))
        {
            return (single, single);
        }

        throw new FormatException(
            $"Cannot parse date expression: '{expr}'. Expected ISO 8601 date or range.");
    }
}
