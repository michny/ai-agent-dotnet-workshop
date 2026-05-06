# Bonus 02 - Analyze Expenses with the 50/30/20 Rule

> Bonus material. Optional. Pick up after Pillar 2.

## Mission

Add an `AnalyzeExpenses` tool the agent can call to summarise spending over a date range against the 50/30/20 budgeting rule. The student says something like "how is my budget looking in April" and the agent returns a breakdown across Needs, Wants, and Savings, with actual percentages, target percentages, and the variance between them.

By the end, the agent can answer "am I overspending" questions with numbers, not vibes.

**Learning Objectives**:

- Aggregate-shape tools: returning summaries the model can narrate, not raw rows it has to count
- Mapping a domain taxonomy (categories) onto a framework (50/30/20)
- Why default parameters and sensible fallbacks matter for an LLM caller
- The split of responsibilities between the tool (facts) and the model (story)

---

## Prerequisites

- P2.02 finished. The agent has `GetTransactions` and `SearchTransactions` working and the database has transactions in it.
- A date range you actually have data for. The seed file covers June 2024 through roughly mid-2026. Pick a month in that window for your first test.

> If you also did Bonus 01, you can import a real statement first and then run the analysis against it. The flow is the point.

---

## What we're solving

`GetTransactions` returns a list of rows. `SearchTransactions` returns a list of rows. If the user asks "where is my money going", the model gets a list of rows and has to count, group, and compute percentages itself. That works sometimes. It also drifts. Decimal arithmetic in token space is unreliable. With 30 transactions the model might be fine. With 300 it will quietly round, miscount, or hallucinate a category that does not exist in the data.

The fix is a tool that returns aggregates, not rows. The 50/30/20 framework gives us a concrete shape:

- **Needs** (target 50%): rent, groceries, utilities, healthcare, transport, insurance. The things you cannot easily skip.
- **Wants** (target 30%): restaurants, entertainment, subscriptions, shopping, travel. Discretionary spending.
- **Savings** (target 20%): savings transfers, investments, debt paydown above minimums.

We map every category to one of those three buckets, sum the absolute value of expenses in each bucket, divide by total expenses, and return both the actuals and the targets. The model then narrates: "You're at 62% on Needs, 12 points over the 50% target, mostly driven by Rent and Groceries."

> **Why 50/30/20 and not something more flexible.** The rule is well known and concrete. Students can argue with the percentages but they cannot misunderstand the shape. A more sophisticated tool would accept a budget configuration (per-category targets, custom buckets, rolling envelopes) and that is the right design for a personal finance product. For a workshop bonus, opinionated is better. The interesting wiring is in how the tool maps categories to buckets, not in how it loads a budget config.

Three patterns to notice:

1. **Aggregates, not rows.** The return is small and structured. The agent does not need to count anything. It can quote the numbers as-is.

2. **A defaultable taxonomy.** The category-to-bucket map lives in the tool. Students using their own data will have categories that do not match the seed file ("Streaming" instead of "Subscriptions"). The tool returns an `unmapped` section listing those categories with their totals, so the agent can ask the user how to classify them or the student can extend the map.

3. **Income vs expenses.** Positive amounts in the database are income, not spending. The tool filters them out so the percentages mean what the model thinks they mean. Income is reported separately for completeness.

---

## If you're comfortable, do this

Four steps. Skip the rest if it works on the first try.

1. Create `src/FinanceAssistant/Tools/AnalyzeExpensesTool.cs`. One method, takes a date expression (same `..` range syntax as `GetTransactions`), optional `needsTarget`, `wantsTarget`, `savingsTarget` parameters defaulting to 50/30/20.
2. Inside the tool, query the DB for the range. Sum income (`Amount > 0`) and expenses (`Amount < 0`) separately. Map each expense category to a bucket using a static dictionary. Track `unmapped` for anything that does not match.
3. Return per-bucket actuals, targets, variance, and top categories per bucket. Include `unmapped` and `income` as separate fields.
4. Register the tool in `Program.cs`. Run. Ask "How is my budget looking in 2025-09?"

---

## Step 1: Create AnalyzeExpensesTool

Create `src/FinanceAssistant/Tools/AnalyzeExpensesTool.cs`:

```csharp
using System.ComponentModel;
using System.Globalization;
using FinanceAssistant.Data;
using Microsoft.EntityFrameworkCore;

namespace FinanceAssistant.Tools;

public class AnalyzeExpensesTool
{
    // Map from transaction Category to 50/30/20 bucket.
    // Keep keys lowercase. The lookup normalises both sides.
    private static readonly Dictionary<string, string> CategoryToBucket =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Needs
            ["Rent"] = "Needs",
            ["Mortgage"] = "Needs",
            ["Utilities"] = "Needs",
            ["Groceries"] = "Needs",
            ["Healthcare"] = "Needs",
            ["Transport"] = "Needs",
            ["Insurance"] = "Needs",
            ["Childcare"] = "Needs",

            // Wants
            ["Restaurants"] = "Wants",
            ["Entertainment"] = "Wants",
            ["Subscriptions"] = "Wants",
            ["Shopping"] = "Wants",
            ["Travel"] = "Wants",
            ["Hobbies"] = "Wants",
            ["Gifts"] = "Wants",

            // Savings
            ["Savings"] = "Savings",
            ["Investments"] = "Savings",
            ["Retirement"] = "Savings",
            ["DebtPaydown"] = "Savings"
        };

    [Description(
        "Analyse spending against the 50/30/20 budgeting rule (Needs 50%, Wants 30%, Savings 20%). " +
        "Returns total expenses, total income, the share each bucket consumed of total expenses, " +
        "variance from the target percentages, and the top categories inside each bucket. " +
        "Use this when the user asks about budget, spending breakdown, where their money is going, or how they are doing against a target.")]
    public async Task<object> AnalyzeExpenses(
        [Description("ISO 8601 date or range, e.g. '2026-03' is NOT supported. Pass '2026-03-01..2026-03-31' for a full month or '2026-01-01..2026-12-31' for a year.")] string dateExpression,
        [Description("Target percentage for Needs. Default 50.")] double needsTarget = 50,
        [Description("Target percentage for Wants. Default 30.")] double wantsTarget = 30,
        [Description("Target percentage for Savings. Default 20.")] double savingsTarget = 20,
        CancellationToken ct = default)
    {
        DateOnly start, end;
        try
        {
            (start, end) = ParseRange(dateExpression);
        }
        catch (FormatException ex)
        {
            return new
            {
                error = "invalid_date",
                hint = "Use ISO 8601: YYYY-MM-DD for a single day, or YYYY-MM-DD..YYYY-MM-DD for a range.",
                message = ex.Message
            };
        }

        if (Math.Abs(needsTarget + wantsTarget + savingsTarget - 100) > 0.01)
        {
            return new
            {
                error = "invalid_targets",
                hint = "needsTarget + wantsTarget + savingsTarget must sum to 100.",
                provided = new { needsTarget, wantsTarget, savingsTarget }
            };
        }

        await using var db = new FinanceDbContext();

        var rows = await db.Transactions
            .Where(t => t.Date >= start && t.Date <= end)
            .Select(t => new { t.Date, t.Amount, t.Category })
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return new
            {
                range = new { start, end },
                message = "No transactions in this range.",
                totalExpenses = 0m,
                totalIncome = 0m
            };
        }

        var income = rows.Where(r => r.Amount > 0).Sum(r => r.Amount);
        var expenses = rows.Where(r => r.Amount < 0).ToList();
        var totalExpenses = expenses.Sum(r => Math.Abs(r.Amount));

        if (totalExpenses == 0)
        {
            return new
            {
                range = new { start, end },
                message = "No expenses in this range. Income only.",
                totalExpenses = 0m,
                totalIncome = income
            };
        }

        // Group expenses by category, then bucket each group.
        var byCategory = expenses
            .GroupBy(r => r.Category)
            .Select(g => new
            {
                Category = g.Key,
                Total = g.Sum(r => Math.Abs(r.Amount)),
                Bucket = CategoryToBucket.TryGetValue(g.Key, out var b) ? b : null
            })
            .ToList();

        var unmapped = byCategory
            .Where(c => c.Bucket is null)
            .OrderByDescending(c => c.Total)
            .Select(c => new { c.Category, c.Total })
            .ToList();

        var mappedTotal = byCategory.Where(c => c.Bucket is not null).Sum(c => c.Total);

        decimal BucketTotal(string bucket) =>
            byCategory.Where(c => c.Bucket == bucket).Sum(c => c.Total);

        IEnumerable<object> TopCategoriesIn(string bucket) =>
            byCategory
                .Where(c => c.Bucket == bucket)
                .OrderByDescending(c => c.Total)
                .Take(5)
                .Select(c => new { c.Category, total = c.Total, share = SafePercent(c.Total, BucketTotal(bucket)) });

        object Summary(string bucket, double target)
        {
            var total = BucketTotal(bucket);
            var actual = SafePercent(total, mappedTotal);
            return new
            {
                total,
                actualPercent = Math.Round(actual, 1),
                targetPercent = target,
                variancePoints = Math.Round(actual - target, 1),
                topCategories = TopCategoriesIn(bucket).ToList()
            };
        }

        return new
        {
            range = new { start, end },
            totalIncome = income,
            totalExpenses,
            mappedExpenses = mappedTotal,
            unmappedExpenses = unmapped.Sum(u => u.Total),
            needs = Summary("Needs", needsTarget),
            wants = Summary("Wants", wantsTarget),
            savings = Summary("Savings", savingsTarget),
            unmapped,
            note = unmapped.Count > 0
                ? "Some categories are not mapped to a 50/30/20 bucket. They are excluded from the percentage math. Map them in CategoryToBucket if you want them counted."
                : null
        };
    }

    private static double SafePercent(decimal numerator, decimal denominator) =>
        denominator == 0 ? 0 : (double)(numerator / denominator) * 100.0;

    private static (DateOnly Start, DateOnly End) ParseRange(string expr)
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

        throw new FormatException($"Cannot parse date expression: '{expr}'. Expected ISO 8601 date or range.");
    }
}
```

> The `Summary` local function recomputes `BucketTotal` and `mappedTotal` once per bucket. For three buckets that is fine and the code reads well. If you ever extend this to a 20-bucket envelope budget, compute the totals once up front and reuse them.

> **Why "percent of mapped" and not "percent of total".** The denominator for the 50/30/20 split is mapped expenses only, not all expenses. Unmapped categories sit on the side, visible to the agent, but they do not pollute the bucket percentages. If a student has 30% of their spend in an unmapped category, the Needs/Wants/Savings shares would otherwise all look artificially low. The `note` field tells the agent this is happening.

---

## Step 2: Register the tool

Open `src/FinanceAssistant/Program.cs`. Add the instantiation alongside the other tools:

```csharp
var analyzeExpenses = new AnalyzeExpensesTool();
```

Then add it to `ChatOptions.Tools`:

```csharp
var chatOptions = new ChatOptions
{
    Tools =
    [
        AIFunctionFactory.Create(convertCurrency.Convert),
        AIFunctionFactory.Create(getTransactions.GetTransactions),
        AIFunctionFactory.Create(searchTransactions.SearchTransactions),
        AIFunctionFactory.Create(analyzeExpenses.AnalyzeExpenses),
        new ApprovalRequiredAIFunction(AIFunctionFactory.Create(transferFunds.Transfer))
    ]
};
```

> Drop the `ApprovalRequiredAIFunction` line if you have not done Pillar 5 yet. Add the `importStatement` line if you did Bonus 01.

---

## Step 3: Run

From the repo root:

```bash
dotnet run --project src/FinanceAssistant
```

Try four prompts in turn:

- `How is my budget looking in September 2025?`
  Agent should call `AnalyzeExpenses` with `2025-09-01..2025-09-30`. Watch what it does with the variance numbers in its reply.

- `Am I overspending on wants this year?`
  Agent should call with a full-year range and quote the Wants bucket. Bonus points if it explains the variance.

- `Compare August and September 2025`
  Multi-tool reasoning. The agent should call `AnalyzeExpenses` twice (once per month) and narrate the diff. This is where the per-bucket totals earn their keep over per-row tools.

---

## Troubleshooting

### `invalid_targets` even though I asked for the defaults

The model is passing all three parameters and one of them came back malformed. Check the tool log. If the agent is sending `needsTarget: 50, wantsTarget: 30, savingsTarget: 20` and you still get this error, you have a floating-point drift somewhere. The `1e-2` tolerance in the check should cover normal cases. If it does not, log the actual sum and look at where it diverged.

### Everything lands in `unmapped`

You are running against imported data with categories that do not appear in `CategoryToBucket`. Two options:

1. Edit your CSV to use the canonical categories before importing.
2. Extend the dictionary. The keys are case-insensitive, so add the categories you actually have.

There is no "right" answer here. The map is a policy choice that lives in the tool.

### Agent quotes wrong percentages

Read the raw return shape (turn on verbose logging if you have it from Pillar 3). If the tool returned `actualPercent: 62.3` and the model said `roughly 70%`, the description is not clear enough that these are pre-computed. Add a line: "Return values are already computed percentages. Quote them directly, do not recompute." That sentence has saved me more than once.

### Tool runs but no data comes back for ranges you know have transactions

`Date >= start && Date <= end` works for `DateOnly`. If you are seeing zero rows for a range that should be populated, double-check the seed file's date range. The seed covers June 2024 to mid-2026. February 2024 is empty by design.

### The Wants bucket includes my essential commute

Categorisation is subjective. Move `Transport` from Needs to Wants, or split it into `Commute` (Needs) and `Travel` (Wants) and assign accordingly. The dictionary is the policy.

---

## You can now

Ask budget-shape questions and watch the agent answer with real numbers:

- "How is my budget looking in 2025-09?" pulls a one-month aggregate.
- "Am I overspending on wants this year?" pulls a one-year aggregate and quotes the Wants variance.
- "Compare August and September 2025" triggers two calls and a narrative diff.

You also have a working example of the tool/model split: the tool returns the table, the model writes the story. Once that shape is in your toolbox you will start to see it everywhere. Most "analytics" tools should be aggregates with light structure, not raw queries.

---

## Summary

You've added:

- **`Tools/AnalyzeExpensesTool.cs`**: a tool that returns 50/30/20 aggregates over a date range.
- **A category-to-bucket map** that lives in the tool and is the single place to evolve the taxonomy.
- **Defaultable targets** so the agent can call the tool without thinking about parameters in the common case, and can override them when the user has a custom budget.
- **An `unmapped` escape hatch** so categories outside the map remain visible without polluting the percentages.

---

## What's next

This tool is the smallest interesting analytics tool. Three natural extensions:

1. **Month-over-month deltas.** Add a `compareToPrevious: true` parameter that returns last-period figures alongside this one. The agent can then say "Wants are up 8 points versus August" without two tool calls.

2. **Per-merchant drill-down.** Add a sibling tool `MerchantBreakdown(category, dateExpression)` that returns top merchants inside a category. Useful when the user asks "what's driving my Restaurants spend?".

3. **Forecast.** Take the last N months of data, compute the average per bucket, and project the remainder of the current month or year. Be careful with the description so the model does not present projections as facts.

Each of these is one or two hours of work and reinforces the same lesson: small, opinionated, structured returns beat raw rows for narrative tasks.

---

## Additional Resources

- [Investopedia on the 50/30/20 rule](https://www.investopedia.com/ask/answers/022916/what-502030-budget-rule.asp)
- [Microsoft.Extensions.AI tool calling](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
