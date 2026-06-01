using System.ComponentModel;
using FinanceAssistant.Data;
using FinanceAssistant.Services;

namespace FinanceAssistant.Tools;

public class GetTransactionsTool
{
    [Description("List transactions in a given date range. The dateExpression must be ISO 8601: a single date '2026-05-06' or a range '2026-01-01..2026-01-31'. Natural language like 'yesterday' or 'last month' is not supported. Convert any relative date to ISO 8601 first.")]
    public async Task<object> GetTransactions(
        [Description("ISO 8601 date or range, e.g. '2026-01-15' for one day or '2026-01-01..2026-01-31' for a range.")] string dateExpression,
        CancellationToken ct = default)
    {
        try
        {
            await using var db = new FinanceDbContext();
            var service = new TransactionsService(db);
            var transactions = await service.GetTransactions(dateExpression, ct);
            return new { transactions };
        }
        catch (FormatException ex)
        {
            // The service throws on natural-language dates by design.
            // We turn the throw into a structured error the model can reason about.
            return new
            {
                error = "invalid_date",
                hint = "Use ISO 8601: YYYY-MM-DD for a single date, or YYYY-MM-DD..YYYY-MM-DD for a range.",
                message = ex.Message
            };
        }
    }
}