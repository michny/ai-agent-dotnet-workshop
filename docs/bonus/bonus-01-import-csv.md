# Bonus 01 - Import a CSV Statement

> Bonus material. Optional. Pick up after Pillar 2.

## Mission

Add an `ImportStatement` tool the agent can call to load a bank statement CSV into the transactions database. The student types something like "import the statement at /Users/me/Downloads/statement.csv" and the agent reads the file, inserts the rows, and reports back what it did.

By the end, the agent can grow the dataset on demand without anyone touching the seeder or restarting the app.

**Learning Objectives**:

- Tool design for side effects (writes, not reads) and what changes in the description
- Defensive parsing: how to fail one bad row without killing the whole import
- Idempotency at the tool boundary: skip duplicates rather than throw on them
- How tool return shapes drive the agent's follow-up phrasing

---

## Prerequisites

- P2.02 finished. The agent has `GetTransactions` and `SearchTransactions` working.
- The repo already references `CsvHelper` (used by `TransactionsSeeder`). No new packages.
- A CSV file on disk you can point the tool at. A copy of `scaffolding/transactions.csv` works fine for a smoke test. Bank exports work if they match the canonical schema below.

---

## What we're solving

`TransactionsSeeder` runs once when the database is first created and reads `scaffolding/transactions.csv`. After that, the only way to add transactions is to drop the DB volume and re-seed. That is fine for the workshop. It is not fine if a student wants to play with their own statement after the workshop, or load a second month, or stress-test the search tool with more data.

A tool fixes this. We give the agent a function that takes a file path, parses the CSV, inserts the rows, and returns counts. The agent decides when to call it based on the question. The interesting design choices live at the tool boundary, not in the SQL.

> **Why this is a tool and not a CLI flag.** Both work. A CLI flag is fine for one-shot batch loads. A tool is the right choice when you want the agent itself to handle the operation as part of a conversation: "import this, then show me my top categories last month". The agent chains the two calls. That conversational shape is the through-line of the workshop, and `ImportStatement` is the smallest possible "tool with side effects" example.

Three patterns to notice:

1. **Tool descriptions for write operations.** Read tools (`GetTransactions`, `SearchTransactions`) are safe to call freely. A write tool changes the database. The description should make that explicit so the model thinks twice before guessing arguments. We will not wire it through the confirmation middleware from P5.01 here, but the description hint is the first line of defence.

2. **Partial success.** A 1000-row CSV with two malformed rows should import 998 rows and tell the agent which two failed. Throwing on the first bad row is the easy implementation and the wrong one. The agent cannot act on "FormatException at row 412". It can act on "I imported 998 of 1000 rows. Row 412 and row 700 had unparseable dates."

3. **Idempotency by content hash.** If a user imports the same file twice, the second run should be a no-op, not a duplicate. We hash `(Date, Amount, Merchant, Description)` and skip rows that already exist.

---

## Canonical CSV format

The tool accepts exactly this header, in this order:

```csv
Date,Amount,Merchant,Category,Description
```

- `Date`: ISO 8601, `YYYY-MM-DD`. No other formats. `2026-01-15`.
- `Amount`: decimal. Negative for expenses, positive for income. `-42.50` or `1500.00`. No currency symbols, no thousands separators.
- `Merchant`: free text. Max 200 chars.
- `Category`: free text. Max 100 chars. Use whatever taxonomy you like, but stay consistent so the analysis tool in Bonus 02 can map them.
- `Description`: free text. Max 2000 chars.

> **Why one format and not configurable mapping.** Real bank CSVs are messy. Different banks use different column names, different date formats, different sign conventions for debits and credits. A configurable mapper is the right answer for a production importer and the wrong answer for a workshop bonus. We document one schema and tell students to open their bank export in a spreadsheet and reshape it once before running the import. The lesson is the tool shape, not the CSV dialect zoo.

The seed file at `scaffolding/transactions.csv` already matches this schema. Use it as a reference if you need an example.

---

## If you're comfortable, do this

Four steps. Skip the rest if it works on the first try.

1. Create `src/FinanceAssistant/Tools/ImportStatementTool.cs`. One method, one path parameter, optional `skipDuplicates` flag defaulting to `true`. Use `CsvHelper` the same way `TransactionsSeeder` does.
2. Inside the tool, parse row by row in a `try`/`catch`. Build two lists: `imported` and `skipped`. Hash `(Date, Amount, Merchant, Description)` to detect duplicates against the existing DB rows.
3. Register the tool in `Program.cs` alongside the others.
4. Run. Type `import the statement at /Users/me/Downloads/statement.csv`. Confirm the agent calls the tool and reports the counts.

---

## Step 1: Create ImportStatementTool

Create `src/FinanceAssistant/Tools/ImportStatementTool.cs`:

```csharp
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.Configuration.Attributes;
using FinanceAssistant.Data;
using FinanceAssistant.Models;
using Microsoft.EntityFrameworkCore;

namespace FinanceAssistant.Tools;

public class ImportStatementTool
{
    [Description(
        "Import a bank statement CSV into the transactions database. " +
        "This tool MODIFIES the database. Only call it when the user explicitly asks to import, load, or upload a statement file. " +
        "The CSV must have headers Date,Amount,Merchant,Category,Description with Date in YYYY-MM-DD format and Amount as a decimal (negative for expenses).")]
    public async Task<object> ImportStatement(
        [Description("Absolute path to the CSV file on disk. Example: '/Users/me/Downloads/statement.csv'.")] string filePath,
        [Description("Skip rows that already exist in the database (matched by Date+Amount+Merchant+Description). Default true.")] bool skipDuplicates = true,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
        {
            return new
            {
                error = "file_not_found",
                hint = "Pass an absolute path. Tilde (~) is not expanded.",
                path = filePath
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

        using var reader = new StreamReader(filePath);
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
            file = filePath,
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
```

> The `try`/`catch` is intentionally narrow. We catch `CsvHelperException` for malformed rows (bad date format, non-decimal amount, missing column) and `FormatException` for anything the converter throws past CsvHelper. Everything else (`IOException`, `DbUpdateException`, cancellation) bubbles up and aborts the import. That is the right behaviour. A locked file or a dead database is not a per-row problem. It is a whole-operation problem and the agent should see it as such.

> **About the embedding note.** New rows go in without an `Embedding` value. The startup block in `Program.cs` only embeds when the app boots, so freshly imported rows are invisible to `SearchTransactions` until the next restart. The `note` field in the return tells the agent to mention this. A cleaner solution would be to inject `IEmbeddingGenerator` into this tool and embed inline. We leave that as an exercise. The teaching point is that tool return shape is how the agent learns what to say next.

---

## Step 2: Register the tool

Open `src/FinanceAssistant/Program.cs`. Find the block that instantiates the existing tools and add a sibling line:

```csharp
var importStatement = new ImportStatementTool();
```

Then add it to the `ChatOptions.Tools` list:

```csharp
var chatOptions = new ChatOptions
{
    Tools =
    [
        AIFunctionFactory.Create(convertCurrency.Convert),
        AIFunctionFactory.Create(getTransactions.GetTransactions),
        AIFunctionFactory.Create(searchTransactions.SearchTransactions),
        AIFunctionFactory.Create(importStatement.ImportStatement),
        new ApprovalRequiredAIFunction(AIFunctionFactory.Create(transferFunds.Transfer))
    ]
};
```

> If you have not done Pillar 5 yet, drop the `ApprovalRequiredAIFunction` line. The `TransferFunds` registration only appears once the confirmation middleware lands.

---

## Step 3: Run

Smoke test first. Make a tiny CSV at `/tmp/test-import.csv`:

```csv
Date,Amount,Merchant,Category,Description
2026-05-01,-12.50,Test Coffee,Restaurants,Bonus import smoke test
2026-05-02,-9.99,Test Sub,Subscriptions,Bonus import smoke test
```

Run the app:

```bash
dotnet run --project src/FinanceAssistant
```

In the REPL, type:

```
Import the statement at /tmp/test-import.csv
```

Expected behaviour:

1. The agent calls `ImportStatement`.
2. The tool returns `importedCount: 2, skippedCount: 0, errorCount: 0`.
3. The agent replies with a sentence like "Imported 2 transactions from /tmp/test-import.csv. Note that new rows will only be searchable after a restart."

Now try the same prompt again. The agent should call the tool again and this time get `importedCount: 0, skippedCount: 2`, because the SHA-256 hashes match existing rows.

Finally, break a row on purpose. Replace the date in row 2 with `not-a-date` and re-run the import. You should see `importedCount: 0` (the good row is a duplicate now) and `errorCount: 1` with the bad row's number.

---

## Troubleshooting

### `file_not_found` even though the file exists

The path you passed is not absolute, or the agent expanded a tilde and your shell expanded it again. Pass a fully resolved path like `/Users/me/Downloads/statement.csv`. Do not use `~/Downloads/...`.

### Every row lands in `errors` with "Field with name 'Date' does not exist"

Your CSV headers do not match the canonical schema. Open the file. The first line must be exactly `Date,Amount,Merchant,Category,Description` in that order. Excel and Numbers sometimes export with quoted headers (`"Date","Amount",...`) which CsvHelper handles fine, but a different column order or different names will not auto-map.

### `FormatException` on amounts like `1,234.56`

CsvHelper is parsing with `CultureInfo.InvariantCulture`, which expects `.` as the decimal separator and no thousands grouping. Reshape the CSV: remove thousands separators before importing.

### Agent calls `ImportStatement` for questions like "show me last month's spending"

Your tool description is too permissive. The model is reading "import a bank statement" as roughly equivalent to "look at transactions". Sharpen the first sentence: "This tool MODIFIES the database. Only call it when the user explicitly asks to import, load, or upload a statement file." Restart the app so the description re-registers.

### Imported rows do not show up in `SearchTransactions`

Expected. New rows have `Embedding = null`. Restart the app. The embedding pass in `Program.cs` picks them up on the next boot. If you want inline embedding, inject `IEmbeddingGenerator<string, Embedding<float>>` through the constructor and embed each row before adding it to the context.

---

## You can now

Grow the dataset from inside the agent loop:

- "Import the statement at /tmp/april.csv" hits `ImportStatement` and inserts the rows.
- "Now show me what I spent on restaurants in April" hits `GetTransactions` against the rows you just imported.

You also have a clean example of a tool that mutates state, with three properties worth copying into future tools: a description that admits the side effect, partial-success semantics that let one bad row pass through without poisoning the rest, and a content-hash duplicate check that makes the operation safe to retry.

---

## Summary

You've added:

- **`Tools/ImportStatementTool.cs`**: a write tool that ingests CSV statements row by row.
- **Defensive parsing**: per-row `try`/`catch`, capped error reporting, narrow exception catch.
- **Content-hash idempotency**: `SHA-256` of `(Date, Amount, Merchant, Description)` to detect re-imports.
- **A return shape that drives follow-up phrasing**: counts, samples of failures, and a `note` field that tells the agent about the embedding lag.

---

## What's next

Bonus 02 is a natural follow-on. With an `ImportStatement` tool in hand you can drop fresh data into the database mid-conversation, then ask the agent to analyse it. The analysis tool lives at `bonus-02-analyze-expenses.md`.

If you want to extend `ImportStatement` itself, two good directions:

1. Inject `IEmbeddingGenerator` and embed rows inline so `SearchTransactions` sees them immediately.
2. Wrap the tool with the `ApprovalRequiredAIFunction` middleware from P5.01 so imports require confirmation. Writes are exactly the kind of operation that middleware was built for.

---

## Additional Resources

- [CsvHelper documentation](https://joshclose.github.io/CsvHelper/)
- [Microsoft.Extensions.AI tool calling](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)