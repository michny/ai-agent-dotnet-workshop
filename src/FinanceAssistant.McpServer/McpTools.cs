using System.ComponentModel;
using FinanceAssistant.Tools;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace FinanceAssistant.McpServer;

[McpServerToolType]
public class McpTools
{
    [McpServerTool]
    [Description("List transactions in a given date range. The dateExpression must be ISO 8601: a single date '2026-05-06' or a range '2026-01-01..2026-01-31'. Natural language like 'yesterday' or 'last month' is not supported. Convert any relative date to ISO 8601 first.")]
    public static Task<object> GetTransactions(
        [Description("ISO 8601 date or range, e.g. '2026-01-15' for one day or '2026-01-01..2026-01-31' for a range.")] string dateExpression,
        CancellationToken ct = default)
    {
        return new GetTransactionsTool().GetTransactions(dateExpression, ct);
    }

    [McpServerTool]
    [Description("Search transactions by free-text similarity over merchant and description fields. Returns the top K matching transactions ordered by relevance. Use this when the user asks about purchases by topic, theme, or fuzzy description, like 'coffee shops', 'subscriptions I might cancel', or 'flights last quarter'.")]
    public static Task<object> SearchTransactions(
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        [Description("Free-text query. Examples: 'coffee shops in december', 'subscription cancellations', 'restaurants in Lisbon'.")] string query,
        [Description("How many top matches to return. Default 5. Maximum 20.")] int topK = 5,
        CancellationToken ct = default)
    {
        return new SearchTransactionsTool(embedder).SearchTransactions(query, topK, ct);
    }
}