using FinanceAssistant.Models;

namespace FinanceAssistant.Services;

public interface ITransactionsService
{
    Task<IReadOnlyList<TransactionSummary>> GetTransactions(string dateExpression, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TransactionSummary>> GetByCategory(string category, CancellationToken cancellationToken = default);
}
