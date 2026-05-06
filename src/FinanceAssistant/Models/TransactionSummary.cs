namespace FinanceAssistant.Models;

// Tool-facing projection of Transaction. Excludes the Embedding vector,
// which is internal infrastructure and would blow the model's context window.
public record TransactionSummary(
    Guid Id,
    DateOnly Date,
    decimal Amount,
    string Merchant,
    string Category,
    string Description);
