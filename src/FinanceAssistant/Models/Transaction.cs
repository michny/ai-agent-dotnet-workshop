using Pgvector;

namespace FinanceAssistant.Models;

public class Transaction
{
    public Guid Id { get; set; }

    public DateOnly Date { get; set; }

    public decimal Amount { get; set; }

    public string Merchant { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    // Populated in P2.02 by the embedding pass at startup.
    // 1536 dimensions matches text-embedding-3-small.
    public Vector? Embedding { get; set; }
}
