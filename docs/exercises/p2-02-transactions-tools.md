# P2.02 - GetTransactions and SearchTransactions

> Pillar 2, Part 2. Individual.

## Mission

Add two real tools backed by your transactions database. `GetTransactions` answers date-range queries and demonstrates the deliberate-failure recovery pattern. `SearchTransactions` answers fuzzy questions through embeddings stored in pgvector, ordered by cosine distance at the SQL level.

By the end, the agent has three tools (Convert, Get, Search) and picks the right one based on how the question is phrased.

**Learning Objectives**:

- The tool-boundary failure pattern: wrap a throwing service so the agent receives a structured error
- M.E.AI's `IEmbeddingGenerator<string, Embedding<float>>` for text-to-vector conversion
- pgvector for similarity search done in the database
- Multi-tool disambiguation: the agent picks the right tool based on `[Description]` quality

---

## Prerequisites

- P2.01 finished. ConvertCurrency tool runs against `gpt-4.1-mini`.
- The starter is on Postgres with the `pgvector` extension enabled. Image: `pgvector/pgvector:pg16` or equivalent. (If you're migrating from the SQLite starter, the swap requires a fresh database. `docker compose down -v` deletes all data; `docker compose up -d` brings up the new image.)
- `FinanceAssistant.csproj` references `Pgvector.EntityFrameworkCore` (`0.2.2` or later).
- `Transaction` entity has a nullable `Pgvector.Vector` property called `Embedding`.
- `FinanceDbContext.OnModelCreating` calls `modelBuilder.HasPostgresExtension("vector")`, sets the column type to `vector(1536)` (the dimension `text-embedding-3-small` returns), and adds an HNSW index on the column with `vector_cosine_ops`.
- `FinanceDbContext` is registered in DI with the Npgsql provider, and the options call `o.UseVector()` so EF knows about the vector type.
- Postgres connection string is in `dotnet user-secrets` or `appsettings.json`.

> **Why HNSW.** Without an index, pgvector does a sequential scan and computes cosine distance against every row. Fine for 200 transactions. Painful for 200,000. HNSW (Hierarchical Navigable Small World) is an approximate-nearest-neighbour graph that returns close-enough matches in logarithmic time. The query syntax doesn't change. The query plan does.

---

## What we're solving

The model can chat. It can convert currencies. It cannot answer "How much did I spend on restaurants last month?" because it has no access to your transactions. Both tools fix that.

`GetTransactions` answers questions like "What did I spend on 2026-03-15?" or "Show me transactions between 2026-01-01 and 2026-01-31". It takes a date or a range, queries the DB, returns the rows.

`SearchTransactions` answers questions like "Find anything about coffee" or "Subscriptions I might want to cancel". It embeds the query, asks pgvector for the closest matches by cosine distance, and returns them. The vectors stay in the database. Only the query embedding crosses the wire each time.

Two patterns are the real lesson here, not the queries themselves:

1. **The tool-boundary failure pattern.** `TransactionsService.GetTransactions` was written to throw `FormatException` on natural-language dates ("yesterday", "last month", "January 2099"). Without a fix at the tool boundary, that throw bubbles up, the agent sees an opaque error, and it flails. Wrap the call, catch the throw, return a structured error the model can reason about, and the agent recovers gracefully. Often by retrying the call with an ISO 8601 date instead.

2. **Multi-tool disambiguation.** With three tools registered (Convert, Get, Search), the agent has to pick the right one for each question. That decision is driven entirely by your `[Description]` text. Same lesson as P2.01, applied across multiple tools.

> The deliberate-failure parser is a teaching artifact. In production you'd want the parser itself to handle natural language (relative dates, named months, locale-aware formats). The point of the exercise is that you can't always trust your own services. Tools sit at the boundary between the model and your code, and the boundary is the right place to convert exceptions into something the model can read.

---

## If you're comfortable, do this

Six steps. Skip the rest if it works on the first try.

1. Deploy `text-embedding-3-small` in Azure AI Foundry. Add a fourth user secret `AzureOpenAI:EmbeddingDeployment`.
2. Add an `AddEmbeddingGenerator` extension method in `ServiceCollectionExtensions.cs` that registers `IEmbeddingGenerator<string, Embedding<float>>`.
3. In `Program.cs`, embed any transaction whose `Embedding` column is `null` and persist the result.
4. Create `Tools/GetTransactionsTool.cs` (with the `FormatException` catch at the tool boundary).
5. Create `Tools/SearchTransactionsTool.cs` that embeds the query and orders by pgvector's `CosineDistance`.
6. Register both new tools in `ChatOptions.Tools` alongside `ConvertCurrency`. Run. Try a date question, a search question, and a nonsense-date question. Confirm each lands on the right tool.

---

## Step 1: Deploy the embeddings model and add the secret

### 1.1: Deploy `text-embedding-3-small`

Open Azure AI Foundry from your resource. Same place you deployed `gpt-4.1-mini` in P1.01.

Click "Deploy model", then "Deploy base model". Pick `text-embedding-3-small`.

- **Deployment name**: `text-embedding-3-small`. Use exactly this name.
- **Deployment type**: Standard.
- Leave the rest at defaults.

Click "Deploy" and wait for the green check.

### 1.2: Add the user secret

```bash
cd src/FinanceAssistant
dotnet user-secrets set "AzureOpenAI:EmbeddingDeployment" "text-embedding-3-small"
```

Verify with `dotnet user-secrets list`. You should now see four keys.

---

## Step 2: Register the embedding generator

Open `src/FinanceAssistant/ServiceCollectionExtensions.cs`. Add a second extension method below `AddChatClient`:

```csharp
public static IServiceCollection AddEmbeddingGenerator(this IServiceCollection services, IConfiguration config)
{
    var endpoint = config["AzureOpenAI:Endpoint"]
        ?? throw new InvalidOperationException("Missing AzureOpenAI:Endpoint.");
    var apiKey = config["AzureOpenAI:ApiKey"]
        ?? throw new InvalidOperationException("Missing AzureOpenAI:ApiKey.");
    var embeddingDeployment = config["AzureOpenAI:EmbeddingDeployment"]
        ?? throw new InvalidOperationException("Missing AzureOpenAI:EmbeddingDeployment.");

    var apiBase = new UriBuilder(endpoint) { Path = "openai/v1/" }.Uri;

    return services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_ =>
        new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = apiBase })
            .GetEmbeddingClient(embeddingDeployment)
            .AsIEmbeddingGenerator());
}
```

> The embedding client uses the same Azure v1-compatible endpoint as the chat client. Same auth, same URL pattern, different deployment name. Two clients, one resource, one key.

Notice there's no `IEmbeddingsIndex` registration. We don't need an in-memory index. pgvector is the index.

---

## Step 3: Embed transactions on first run

Three discrete edits to `Program.cs`.

### 3.1: Add usings

At the top of `Program.cs`, alongside the existing `using` lines, add:

```csharp
using Microsoft.EntityFrameworkCore;
using Pgvector;
```

### 3.2: Register the embedding generator

Find the `services.AddChatClient(config);` line. Add a sibling line right after:

```csharp
services.AddEmbeddingGenerator(config);
```

### 3.3: Embed any transactions missing an embedding

Right after the `var chatClient = provider.GetRequiredService<IChatClient>();` line, before the `var systemPrompt = await File.ReadAllTextAsync(...);` line, add this block:

```csharp
var embedder = provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

await using (var db = new FinanceDbContext())
{
    var unembedded = await db.Transactions
        .Where(t => t.Embedding == null)
        .ToListAsync();

    if (unembedded.Count > 0)
    {
        Console.WriteLine($"Embedding {unembedded.Count} transactions...");
        var texts = unembedded.Select(t => $"{t.Merchant} {t.Description}").ToList();
        var embeddings = await embedder.GenerateAsync(texts);
        for (int i = 0; i < unembedded.Count; i++)
        {
            unembedded[i].Embedding = new Vector(embeddings[i].Vector.ToArray());
        }
        await db.SaveChangesAsync();
        Console.WriteLine($"Embedded {unembedded.Count} transactions.");
    }
}
```

The `Where(t => t.Embedding == null)` filter means this block only does work the first time. Once every transaction has an embedding, subsequent runs are a no-op single SQL `COUNT`.

> **Embedding generation in production.** Doing this on REPL startup is a workshop convenience. In a real system, embedding generation is its own infrastructure. Pick whichever shape fits your stack: a recurring background job that processes new and updated rows from a queue, a domain-event handler that fires whenever a transaction is inserted or its searchable text changes, or a sync hook in your write path. The principle is "embed once per row when its searchable text changes, persist the vector, never compute again." The startup hook in this exercise is the smallest version of that pipeline.

---

## Step 4: Create GetTransactionsTool

Create `src/FinanceAssistant/Tools/GetTransactionsTool.cs`:

```csharp
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
```

> The `try`/`catch` is the entire lesson. Without it, `FormatException` bubbles up to the function-invocation pipeline, which surfaces it as a generic tool failure. The model gets back something like "Tool error: System.FormatException: Cannot parse date expression" and either gives up or hallucinates a fallback. With the structured error, the model gets a `hint` field telling it exactly what format to use, and the next call usually succeeds.

---

## Step 5: Create SearchTransactionsTool

Create `src/FinanceAssistant/Tools/SearchTransactionsTool.cs`:

```csharp
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
```

> `CosineDistance` translates to pgvector's `<=>` operator. Postgres sorts by distance, applies `LIMIT`, and returns the top K rows. No vectors get pulled into the .NET process for ranking. That's the pgvector pitch in one sentence: "let the database do the work."

---

## Step 6: Register both tools and run

Two edits to `Program.cs`.

### 6.1: Instantiate the tools and add them to ChatOptions

Find the `var chatOptions = new ChatOptions { ... };` block from P2.01. Update it to register all three tools:

```csharp
var convertCurrency = new ConvertCurrencyTool();
var getTransactions = new GetTransactionsTool();
var searchTransactions = new SearchTransactionsTool(embedder);

var chatOptions = new ChatOptions
{
    Tools =
    [
        AIFunctionFactory.Create(convertCurrency.Convert),
        AIFunctionFactory.Create(getTransactions.GetTransactions),
        AIFunctionFactory.Create(searchTransactions.SearchTransactions)
    ]
};
```

### 6.2: Run

From the repo root:

```bash
dotnet run --project src/FinanceAssistant
```

On first run you'll see the embedding pass: `Embedding 200 transactions...` followed by `Embedded 200 transactions.`. On subsequent runs that block is silent (everything has an embedding already).

Try four prompts in turn. Each one tests something different:

- `Show me transactions on 2026-01-15` (Get tool, ISO date, happy path)
- `Find anything about coffee` (Search tool, fuzzy query)
- `Convert 100 EUR to USD` (Convert tool from P2.01, still works)
- `What did I spend yesterday?` (Get tool, deliberate-failure recovery)

For the last one, watch what happens. The agent calls `GetTransactions` with `"yesterday"`. The tool catches the `FormatException` and returns the structured error. The model reads the `hint` field, computes today's date in ISO 8601, calls again with the right format, and gives you a real answer.

If the agent picks the wrong tool for any of these, your `[Description]` text isn't doing enough work. Sharpen it.

---

## Troubleshooting

### `Missing AzureOpenAI:EmbeddingDeployment` thrown at startup

You haven't set the fourth user secret. From `src/FinanceAssistant/`:

```bash
dotnet user-secrets set "AzureOpenAI:EmbeddingDeployment" "text-embedding-3-small"
```

### `extension "vector" does not exist` from Postgres

The pgvector extension isn't enabled in your database. Connect to the Postgres instance and run:

```sql
CREATE EXTENSION IF NOT EXISTS vector;
```

If you're using the `pgvector/pgvector` Docker image, this should be available out of the box. If you swapped to a stock `postgres` image, install pgvector or switch images.

### `CosineDistance` is not found

You're missing the `Pgvector.EntityFrameworkCore` package or the `using Pgvector.EntityFrameworkCore;` line at the top of `SearchTransactionsTool.cs`. Both are needed for the EF translation to work.

### `AsIEmbeddingGenerator()` is not found

Same package as `AsIChatClient` (`Microsoft.Extensions.AI.OpenAI`). The using is `using Microsoft.Extensions.AI;`. Already there from P1.02.

### Search returns matches but the agent ignores them

The model decided not to surface the search results. Two checks:

1. The system prompt in `Prompts/SystemPrompt.md`. If it says "answer concisely from your knowledge", the model may prefer its own training data over the tool output. Add a line like "Prefer information from tool results over your own knowledge."
2. The tool description. If it doesn't make clear that the tool returns the actual transactions, the model may discount them.

### Agent calls `GetTransactions` for fuzzy questions like "coffee shops"

The model is reading the descriptions and the date-range tool is winning ambiguous calls. Sharpen `SearchTransactions` to say "use this when the user asks about purchases by topic, theme, or fuzzy description". Sharpen `GetTransactions` to say "only use this when an ISO 8601 date or range is specified".

### Agent calls `SearchTransactions` for date questions like "What did I spend on 2026-01-15?"

The reverse problem. Same fix in the opposite direction. Strong descriptions matter both ways.

---

## You can now

Ask three different kinds of questions and watch the agent route to the right tool:

- "Show me transactions on 2026-01-15" hits `GetTransactions`.
- "Find anything about coffee" hits `SearchTransactions`. Postgres ranks the rows by cosine distance and returns the top K.
- "Convert 100 EUR to USD" hits `ConvertCurrency` from P2.01.

You also got the deliberate-failure pattern in your hands: ask "What did I spend yesterday?" and watch the agent retry with an ISO date after seeing the structured error.

---

## Summary

You've added:

- **`AddEmbeddingGenerator`**: a second extension on `IServiceCollection` that registers `IEmbeddingGenerator`.
- **A startup embedder**: every transaction's `Merchant + Description` is embedded once when its row first lands in the DB, persisted as a `vector(1536)` column.
- **`Tools/GetTransactionsTool.cs`**: date-range query with the tool-boundary fix for the deliberate-failure parser.
- **`Tools/SearchTransactionsTool.cs`**: free-text similarity search ordered by pgvector's cosine distance.
- **A three-tool agent**: Convert, Get, Search, all picked by the model based on the question.

---

## What's next

P3.01 is where the agent loop becomes visible. With three tools, the model sometimes wants to call multiple in sequence (e.g. "find coffee shops, then total what I spent there last quarter"). You'll write a `ChatLoop` that handles multi-turn tool calls, plus an iteration cap so a misbehaving model can't burn through your token budget.

---

## Additional Resources

- [Microsoft.Extensions.AI tool calling](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- [pgvector on GitHub](https://github.com/pgvector/pgvector)
- [Pgvector.EntityFrameworkCore on NuGet](https://www.nuget.org/packages/Pgvector.EntityFrameworkCore)
