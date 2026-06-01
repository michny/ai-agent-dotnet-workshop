using System.ComponentModel;
using FinanceAssistant.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace FinanceAssistant.Tools;

public class SearchTransactionsTool
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;

    public SearchTransactionsTool(IEmbeddingGenerator<string, Embedding<float>> embedder)
    {
        _embedder = embedder;
    }

    [Description("Search transactions by free-text similarity over merchant and description fields. Returns the top K matching transactions ordered by relevance. Use this when the user asks about purchases by topic, theme, or fuzzy description, like 'coffee shops', 'subscriptions I might cancel', or 'flights last quarter'.")]
    public async Task<object> SearchTransactions(
        [Description("Free-text query. Examples: 'coffee shops in december', 'subscription cancellations', 'restaurants in Lisbon'.")] string query,
        [Description("How many top matches to return. Default 5. Maximum 20.")] int topK = 5,
        CancellationToken ct = default)
    {
        var queryEmbedding = await _embedder.GenerateAsync(query, cancellationToken: ct);
        var queryVector = new Vector(queryEmbedding.Vector.ToArray());
        var limit = Math.Clamp(topK, 1, 20);

        await using var db = new FinanceDbContext();
        var matches = await db.Transactions
            .Where(t => t.Embedding != null)
            .OrderBy(t => t.Embedding!.CosineDistance(queryVector))
            .Take(limit)
            .Select(t => new
            {
                t.Id,
                t.Date,
                t.Amount,
                t.Merchant,
                t.Category,
                t.Description,
                Distance = t.Embedding!.CosineDistance(queryVector)
            })
            .ToListAsync(ct);

        return new { matches };
    }
}
