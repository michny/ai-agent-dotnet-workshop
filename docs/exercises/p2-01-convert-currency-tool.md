# P2.01 - Add a ConvertCurrency Tool

> Pillar 2, Part 1. Individual.

## Mission

Add a `ConvertCurrency` tool to the agent. The tool is a hardcoded-rate currency converter the model can call when the user asks something like "Convert 100 EUR to USD". You'll wire it through M.E.AI's function-invocation pipeline so the agent calls it automatically.

By the end, the REPL answers conversion questions with real numbers from your tool, not training-data guesses.

**Learning Objectives**:

- Tool definition with `[Description]` attributes
- `AIFunctionFactory.Create` for wrapping a method as an AI tool
- The function-invocation pipeline (`UseFunctionInvocation`)
- Why tool descriptions are the API the model reasons over

---

## Prerequisites

- P1.02 finished. The REPL replies to `hello` with a sentence from your `gpt-4.1-mini` deployment.

---

## What we're solving

Right now, ask the agent "Convert 100 EUR to USD" and you'll get a guess. The model has training data on exchange rates, but those rates are stale and the model can't tell you when they were collected. There's no source of truth.

A tool fixes this. We give the agent a function it can call (`Convert(amount, fromCurrency, toCurrency)`), backed by a hardcoded rate table. The agent decides when to call the tool and what arguments to pass. M.E.AI's function-invocation pipeline handles the round-trip. The result lands back in the conversation, and the agent uses the real number to answer.

> **The rate table is static on purpose.** In a real system this tool would take an `IRatesService` in its constructor and call out to a live rates API, with caching and error handling. The tool plumbing is identical either way: same class shape, same `[Description]` attributes, same registration. The difference is only what's behind the method body. P2.02 demonstrates the service-injected variant when we add transaction tools backed by `ITransactionsService`. For now, hardcoded keeps the exercise focused on the wiring and the descriptions, not on the rates.

The most important thing in this exercise is not the C# code. It's the `[Description]` attributes.

The model never sees your method body. It sees:

1. The method name.
2. The method's `[Description]` text.
3. Each parameter's name, type, and `[Description]` text.

That metadata is the entire API the agent reasons over. Vague descriptions mean wrong tool calls. Specific descriptions mean the agent picks the right tool with the right arguments. Anthropic calls this the agent-computer interface (ACI), and the lesson lands the moment you watch the agent confidently produce nonsense because a parameter description was unclear.

---

## If you're comfortable, do this

Five steps. Skip the rest if it works on the first try.

1. Create `src/FinanceAssistant/Tools/ConvertCurrencyTool.cs`. One class, one method, hardcoded rate table, `[Description]` on the method and on every parameter.
2. Update `ServiceCollectionExtensions.cs` to add `UseFunctionInvocation()` to the chat-client pipeline so M.E.AI auto-invokes tool calls.
3. In `Program.cs`, instantiate the tool, wrap its method with `AIFunctionFactory.Create`, and put it into a `ChatOptions.Tools` list.
4. Pass the `ChatOptions` to `GetResponseAsync` inside the loop.
5. Run. Type "Convert 100 EUR to USD". Confirm a real number from the rate table appears.

---

## Step 1: Create ConvertCurrencyTool.cs

Create `src/FinanceAssistant/Tools/ConvertCurrencyTool.cs`:

```csharp
using System.ComponentModel;

namespace FinanceAssistant.Tools;

public class ConvertCurrencyTool
{
    // Rates expressed in USD per unit of the source currency.
    // Hardcoded for the workshop. In a real system this would come from a live rates API,
    // typically injected as an IRatesService in the constructor.
    private static readonly Dictionary<string, decimal> RatesToUsd = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = 1.00m,
        ["EUR"] = 1.10m,
        ["GBP"] = 1.27m,
        ["JPY"] = 0.0067m,
        ["CHF"] = 1.13m,
        ["CAD"] = 0.74m,
        ["AUD"] = 0.66m,
    };

    [Description("Convert an amount from one currency to another using fixed reference rates. Returns a string like '100 EUR = 110.00 USD'. Supports USD, EUR, GBP, JPY, CHF, CAD, AUD.")]
    public string Convert(
        [Description("The amount to convert, in the source currency. A positive number.")] decimal amount,
        [Description("The 3-letter ISO currency code of the source amount, e.g. EUR, USD, GBP.")] string fromCurrency,
        [Description("The 3-letter ISO currency code of the target currency, e.g. USD, EUR, JPY.")] string toCurrency)
    {
        if (!RatesToUsd.TryGetValue(fromCurrency, out var fromRate))
            return $"Unknown source currency '{fromCurrency}'. Supported: {string.Join(", ", RatesToUsd.Keys)}.";

        if (!RatesToUsd.TryGetValue(toCurrency, out var toRate))
            return $"Unknown target currency '{toCurrency}'. Supported: {string.Join(", ", RatesToUsd.Keys)}.";

        var amountInUsd = amount * fromRate;
        var converted = amountInUsd / toRate;
        return $"{amount} {fromCurrency} = {converted:F2} {toCurrency}";
    }
}
```

> Two things worth noticing about the `[Description]` text.
>
> First, the method-level description tells the agent what the tool returns and in what shape. The agent uses that to decide whether the tool's output is what it needs.
>
> Second, the parameter descriptions name the format ("3-letter ISO currency code") and give examples ("EUR, USD, GBP"). Without those examples the agent might pass "euros" and your dictionary lookup falls through to the unknown-currency branch. With them the agent maps "euros" to "EUR" before it calls.

> **A note on the method name.** `Convert` shadows `System.Convert` inside this class. That's fine here because we never call `System.Convert` from inside the tool. If you later add something like `Convert.ToDecimal(...)` in the method body, you'll need to fully qualify it as `System.Convert.ToDecimal(...)`.

---

## Step 2: Add UseFunctionInvocation to the chat client

Open `src/FinanceAssistant/ServiceCollectionExtensions.cs`. The `AddChatClient` method currently registers an `IChatClient` directly. We're going to wrap that registration with a builder that adds the function-invocation middleware.

Find the `return services.AddSingleton<IChatClient>(_ => ...)` block. Add `.AsBuilder().UseFunctionInvocation().Build()` to the end of the chain. The block becomes:

```csharp
return services.AddSingleton<IChatClient>(_ =>
    new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = apiBase })
        .GetChatClient(deployment)
        .AsIChatClient()
        .AsBuilder()
        .UseFunctionInvocation()
        .Build());
```

Two new lines. They wrap the chat client with middleware that:

1. Receives the model's response.
2. If the response includes tool-call requests, M.E.AI invokes the matching tool method with the model's arguments.
3. The tool result is added to the message list, and the chat is re-issued automatically.
4. The loop continues until the model returns a non-tool response.

That's the agent loop, hidden behind one `Use` call. Pillar 3 unpacks what's inside. For now, this is enough to make tools work.

---

## Step 3: Register the tool in Program.cs

Three small changes.

### 3.1: Add a using

At the top of `Program.cs`, alongside the existing `using` lines, add:

```csharp
using FinanceAssistant.Tools;
```

### 3.2: Build the tool list once at startup

Right after the `var chatClient = provider.GetRequiredService<IChatClient>();` line, add:

```csharp
var convertCurrency = new ConvertCurrencyTool();
var chatOptions = new ChatOptions
{
    Tools = [AIFunctionFactory.Create(convertCurrency.Convert)]
};
```

`AIFunctionFactory.Create(method)` reflects over the method, picks up the `[Description]` attributes, generates the JSON schema for the parameters, and returns an `AIFunction` that M.E.AI's function-invocation middleware can invoke.

### 3.3: Pass the options to GetResponseAsync

Find this line in the loop:

```csharp
var response = await chatClient.GetResponseAsync(messages);
```

Replace it with:

```csharp
var response = await chatClient.GetResponseAsync(messages, chatOptions);
```

---

## Step 4: Run

From the repo root:

```bash
dotnet run --project src/FinanceAssistant
```

Try a few prompts in turn:

- `Convert 100 EUR to USD`
- `How much is 50 GBP in JPY?`
- `What's 10 dollars in euros?`

Each should print a sentence containing a number from your rate table.

The first prompt is the easy one. The second tests whether the agent normalises the currency names you wrote into ISO codes. The third tests whether it maps "dollars" to "USD" and "euros" to "EUR" using the parameter descriptions you wrote.

If the third one comes back with the right currencies, your descriptions are doing real work.

> **Confirming the tool actually ran.** The failure mode where the model bluffs an answer without calling the tool looks identical to success. Both print a number. To confirm the tool is being invoked, drop a `Console.WriteLine($"[tool] Convert({amount}, {fromCurrency}, {toCurrency})");` at the top of `Convert`, or set a breakpoint there. You should see the tool fire on every conversion prompt.

---

## Troubleshooting

### Agent says "I don't have access to a currency conversion tool"

The tool isn't reaching the model. Two checks:

1. Did you pass `chatOptions` to `GetResponseAsync(messages, chatOptions)` in Step 3.3? Without it the model never sees the tool list.
2. Is `UseFunctionInvocation()` in the registration? Without it, the model can request a tool call but nothing actually invokes it, so it gives up and apologises.

### Agent calls the tool with the wrong currency code

Look at the `[Description]` on `fromCurrency` and `toCurrency`. If the description doesn't say "3-letter ISO currency code" and give examples, the agent may pass "euros" or "USD dollars" and your dictionary lookup falls through to the unknown-currency branch.

This is the experiential lesson. Strengthen the descriptions, the agent gets it right.

### `AIFunctionFactory` is not found

It lives in `Microsoft.Extensions.AI`. Confirm `using Microsoft.Extensions.AI;` is at the top of `Program.cs`. (It already is, from P1.02.)

### `UseFunctionInvocation` is not found

Same package as `AIFunctionFactory`. Two things to check:

1. `using Microsoft.Extensions.AI;` is present in `ServiceCollectionExtensions.cs`. Both `AsBuilder()` and `UseFunctionInvocation()` are extension methods that won't resolve without it. (It's already there from P1.02. If you refactored usings, this is where it goes missing.)
2. Your `Microsoft.Extensions.AI` reference is 10.5.2 or later. That's the version that ships the function-invocation middleware.

---

## You can now

Type natural-language conversion questions and get real numbers out of your hardcoded rate table. The agent normalises "dollars" or "euros" into ISO codes before calling the tool, because the parameter descriptions told it how. That's the lesson the exercise exists to deliver: the `[Description]` text *is* the API the agent reasons over.

---

## Summary

You've added:

- **`Tools/ConvertCurrencyTool.cs`**: a hardcoded-rate currency converter with `[Description]` on the method and on every parameter.
- **`UseFunctionInvocation()`**: the M.E.AI middleware that auto-invokes tool calls inside the chat client.
- **`ChatOptions.Tools`**: the list of tools the agent can choose from on each turn.
- **A working tool call**: ask the agent to convert currencies, watch it call your method.

---

## What's next

P2.02 adds two real tools backed by your transactions database: `GetTransactions` (date-range query) and `SearchTransactions` (semantic search). The bigger lesson there lands at the failure boundary, when a parser throws on natural-language input and the agent has to recover.

---

## Additional Resources

- [Microsoft.Extensions.AI tool calling](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- [Anthropic on agent-computer interfaces](https://www.anthropic.com/engineering/building-effective-agents)
