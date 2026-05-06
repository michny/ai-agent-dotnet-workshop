# P5.01 - Confirmation Gate on Destructive Tools

> Pillar 5, Part 1. Individual.

## Mission

Wire a human-in-the-loop confirmation gate onto destructive tools using `ApprovalRequiredAIFunction` from `Microsoft.Extensions.AI`. By the end, the agent can decide to call a `Transfer` tool, the REPL pauses to show what's about to happen, and the action only goes through after you type "yes". Anything else is treated as a decline and the agent is told the action was not taken.

**Learning Objectives**:

- `ApprovalRequiredAIFunction` as the framework's marker for "this tool needs human approval before invocation"
- The agent loop as the right place to enforce that approval, because the loop already sits at the boundary between "model wants to act" and "the action runs"
- Returning a structured "user_declined" result so the agent can recover gracefully instead of crashing or retrying

---

## Prerequisites

- P4.02 finished. `ChatAgent` runs the loop, calls the reducer once per turn, and invokes tools through `AIFunction.InvokeAsync`.

---

## What we're solving

The agent we've built so far is enthusiastic. Give it a tool, give it a plausible-looking question, and it'll call the tool without hesitation. For read-only tools (`GetTransactions`, `SearchTransactions`, `Convert`), that's fine. For anything that changes the world, it's not.

Real systems have irreversible actions: send a payment, send an email, delete a row, cancel a subscription. The model is good at deciding when those actions are *probably* the right call. It's not good enough to be the only thing standing between a wrong inference and your bank account.

The standard fix is human-in-the-loop confirmation. Mark the destructive tools. When the agent decides to call one, pause, show the human what's about to happen, ask for explicit approval. If the human says yes, proceed. If anything else, hand the agent back a structured "user declined" result and let it apologise.

M.E.AI ships a class for exactly this marking job: `ApprovalRequiredAIFunction`. It's a `DelegatingAIFunction` that wraps another `AIFunction` and signals "I need approval before you call me". Per the framework's own docs, it does not enforce the requirement. It's the invoker's responsibility to obtain that approval before invoking. The invoker, in our codebase, is `ChatAgent`'s loop.

We're going to wire that pattern in three pieces:

1. **A destructive demo tool**: `TransferFundsTool.Transfer` is a fake money-transfer tool. It doesn't actually move money. The point is the gate, not the transfer.
2. **A wrapped registration**: in `Program.cs`, the transfer tool's `AIFunction` is wrapped with `new ApprovalRequiredAIFunction(...)` at registration time. The other tools register as plain `AIFunction` and don't need approval.
3. **A type check in `ChatAgent`**: before invoking any tool, the agent asks "is this an `ApprovalRequiredAIFunction`?". If yes, it prompts the user. If they decline, the loop short-circuits with a structured result. The hand-written loop from P3.01 makes this trivial. There's exactly one place where tools get invoked, and the check goes right above it.

> The confirmation prompt is `Console.WriteLine` and `Console.ReadLine` because that's what the REPL has. In a web app or a Slack bot, the same gate would surface as a button, a "approve / reject" message, or a one-time-link. The shape of the prompt is the integration concern. The shape of the check (ask "does this need approval?", ask the human, branch on yes) stays the same.

---

## If you're comfortable, do this

Four steps. Skip the rest if it works on the first try.

1. Create `src/FinanceAssistant/Tools/TransferFundsTool.cs` with one method, `Transfer(string fromAccount, string toAccount, decimal amount, CancellationToken ct = default)`, decorated with `[Description]`. The implementation logs a fake success.
2. Update `ChatAgent`: before invoking any tool, type-check whether it's an `ApprovalRequiredAIFunction`. If yes, prompt the user via the console. Short-circuit with a `user_declined` result on anything other than "yes".
3. In `Program.cs`, register the new tool wrapped with `new ApprovalRequiredAIFunction(...)`. Read-only tools register as plain `AIFunction` and don't trigger the gate.
4. Run. Ask the agent to "Transfer 100 EUR from Checking to Savings". Watch the prompt fire. Type `yes` once, `no` once, and verify both paths.

---

## Step 1: Create TransferFundsTool.cs

Create `src/FinanceAssistant/Tools/TransferFundsTool.cs`:

```csharp
using System.ComponentModel;

namespace FinanceAssistant.Tools;

public class TransferFundsTool
{
    [Description("Transfer money between user accounts. This action is irreversible and the user will be asked to confirm before it runs.")]
    public Task<object> Transfer(
        [Description("Source account name, e.g. 'Checking'")] string fromAccount,
        [Description("Destination account name, e.g. 'Savings'")] string toAccount,
        [Description("Amount to transfer in account currency. Must be positive.")] decimal amount,
        CancellationToken ct = default)
    {
        // No real transfer. We log the intent and return a fake success.
        // The point of this tool is the confirmation gate, not the transfer.
        return Task.FromResult<object>(new
        {
            transferred = amount,
            from = fromAccount,
            to = toAccount,
            transactionId = Guid.NewGuid()
        });
    }
}
```

Two things worth noticing:

**No `[RequiresUserConfirmation]` attribute on the method.** The "needs approval" signal lives at registration time (the wrap), not on the method itself. That's a deliberate framework choice. The same tool method can be wrapped or not depending on how the agent wants to govern it.

**The description tells the model the action is irreversible.** That nudges the model to be careful about *when* it calls the tool. The confirmation gate is the second line of defence: even if the model decides to call, the human gets the final say.

> The return type is `Task<object>` rather than a typed DTO. We want the JSON shape the model will see, and the anonymous object is the shortest way to express it. A real codebase would use a `TransferResult` record. The shape that hits the model is the same either way.

---

## Step 2: Update ChatAgent to check for ApprovalRequiredAIFunction

Two edits in `src/FinanceAssistant/ChatAgent.cs`. While we're touching the tool-invocation loop, the existing nesting (`if function is null / else { try / catch }`) gets one level deeper with the new approval check. We flatten it with early continues so the three branches (unknown tool, declined approval, invoke) read as peers instead of as nested boxes.

### 2.1: Replace the tool-call loop body

Find the existing `foreach (var call in toolCalls)` block inside `RunTurnAsync` and replace its body with this flat version:

```csharp
foreach (var call in toolCalls)
{
    var function = _options.Tools?
        .OfType<AIFunction>()
        .FirstOrDefault(f => f.Name == call.Name);

    if (function is null)
    {
        _store.AppendToolResult(new FunctionResultContent(
            call.CallId,
            $"Tool '{call.Name}' is not registered."));
        continue;
    }

    if (function is ApprovalRequiredAIFunction && !ConfirmInteractive(call))
    {
        _store.AppendToolResult(new FunctionResultContent(
            call.CallId,
            new
            {
                error = "user_declined",
                message = "The user did not confirm the action. Do not retry without explicit user permission."
            }));
        continue;
    }

    AIContent resultContent;
    try
    {
        var result = await function.InvokeAsync(new AIFunctionArguments(call.Arguments), ct);
        resultContent = new FunctionResultContent(call.CallId, result);
    }
    catch (Exception ex)
    {
        // Tool threw something the model didn't handle (or we didn't wrap at the tool boundary).
        // Hand the model a structured error so it can recover or apologise.
        resultContent = new FunctionResultContent(call.CallId, $"Tool error: {ex.Message}");
    }

    _store.AppendToolResult(resultContent);
}
```

### 2.2: Add the ConfirmInteractive helper

At the bottom of the `ChatAgent` class, add a private static method that owns the prompt UX. Keeping it out of the loop body is what lets the loop stay flat:

```csharp
private static bool ConfirmInteractive(FunctionCallContent call)
{
    var argsPretty = call.Arguments is { Count: > 0 }
        ? string.Join(", ", call.Arguments.Select(kv => $"{kv.Key}={kv.Value}"))
        : "(no arguments)";

    Console.WriteLine($"[agent] '{call.Name}' requires confirmation.");
    Console.WriteLine($"        Arguments: {argsPretty}");
    Console.Write($"        Type 'yes' to proceed: ");
    var answer = Console.ReadLine()?.Trim();
    return string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
}
```

Four things worth reading carefully:

**Three top-level branches, not nested boxes.** The loop body now reads as: "unknown tool → log and skip", "needs approval and the user declined → log and skip", "otherwise → invoke and record". Each branch ends with `continue` or falls through to the invocation block. Adding a fourth gate later (rate limit, audit log, per-user quota) is one more early-`continue` block above the `try`, not another nested `if` inside an `else`.

**The check is `function is ApprovalRequiredAIFunction`.** No new field on `ChatAgent`, no set to maintain, no reflection. The framework's wrapper type carries the signal, and a single `is` check picks it up. When you wrap a function with `new ApprovalRequiredAIFunction(inner)`, the resolved function's runtime type is `ApprovalRequiredAIFunction`, so the pattern match fires.

**The prompt shows the arguments.** Without that, "Transfer requires confirmation" tells the user nothing about what's being approved. With it, the user sees `fromAccount=Checking, toAccount=Savings, amount=100` and can make an informed decision. Showing the arguments is the difference between a real confirmation and rubber-stamping whatever the model produced. The helper method (`ConfirmInteractive`) owns this formatting so the loop body doesn't have to.

**The declined result is structured, not an exception.** The agent appends a `FunctionResultContent` carrying `{ error = "user_declined", ... }` and continues the loop. The model sees the result on the next iteration and can apologise, ask the user what they'd prefer, or move on. Throwing an exception here would surface as a generic tool error and the model would probably retry. The `"Do not retry without explicit user permission"` hint in the message tells it not to bother.

> Wrapping with `ApprovalRequiredAIFunction` does NOT change how the function is invoked. The wrapper is a `DelegatingAIFunction`. When you call `wrapper.InvokeAsync(...)`, it forwards to the inner function. The check we just added decides *whether* to invoke. The framework deliberately separates "mark it" from "enforce it" so different invokers (a console REPL, a web app, a background worker) can implement the approval UX their way.

> The prompt uses the same `Console.ReadLine()` the REPL itself uses. If you script this exercise by piping input in, the same stdin stream feeds both the REPL prompt and the gate prompt. A line you intended as input for one can end up consumed by the other. Worth knowing if you automate testing.

---

## Step 3: Wrap the destructive tool in Program.cs

Find the existing `chatOptions` block:

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

Add `TransferFundsTool` to the instances, and wrap its registration with `ApprovalRequiredAIFunction`:

```csharp
var convertCurrency = new ConvertCurrencyTool();
var getTransactions = new GetTransactionsTool();
var searchTransactions = new SearchTransactionsTool(embedder);
var transferFunds = new TransferFundsTool();

var chatOptions = new ChatOptions
{
    Tools =
    [
        AIFunctionFactory.Create(convertCurrency.Convert),
        AIFunctionFactory.Create(getTransactions.GetTransactions),
        AIFunctionFactory.Create(searchTransactions.SearchTransactions),
        new ApprovalRequiredAIFunction(AIFunctionFactory.Create(transferFunds.Transfer))
    ]
};
```

That last line is the entire opt-in. Wrap the function to mark it. Don't wrap to leave it un-gated. The other three tools remain plain `AIFunction` instances and the type check in `ChatAgent` sees them as `not ApprovalRequiredAIFunction` and lets them through.

The diff is just two lines: a trailing comma on the previous last entry (`SearchTransactions)` → `SearchTransactions),`) and the new wrapped registration on the next line. Auto-format won't add the comma for you.

`ChatAgent`'s constructor doesn't change. The check we added in Step 2 reads from `_options.Tools`, which the agent already has. The `chatAgent = new ChatAgent(...)` line from P4.02 stays exactly as it was.

> Compared to a per-tool attribute or a hand-rolled `IReadOnlySet<string>` of names, this is the cleanest version of the pattern. The marker is at the registration site, where the decision actually lives. The agent doesn't need to know which tools were marked. It only needs to recognise the marker type at invocation time.

---

## Step 4: Run, approve, decline

From the repo root:

```bash
dotnet run --project src/FinanceAssistant
```

> Depending on the model, you may need to reply "yes, do it" to a clarifying question before the gate ever triggers. Some models will refuse to issue the tool call on a first ambiguous user message and ask for confirmation in plain text instead ("Are you sure you want to transfer 100 EUR from Checking to Savings?"). The gate triggers on the iteration where the model actually issues the tool call, which may be your second user message rather than your first. The transcript below assumes the model goes straight to the tool call. If your model hedges, follow up with "yes, please do it" and watch the gate fire on the iteration after.

Try the approval path first:

```
> Transfer 100 EUR from Checking to Savings.
[agent] iteration 1: calling Transfer
[agent] 'Transfer' requires confirmation.
        Arguments: fromAccount=Checking, toAccount=Savings, amount=100
        Type 'yes' to proceed: yes
[agent] iteration 2: final answer
Transferred 100 EUR from Checking to Savings. Reference: 8f3...
```

Your exact text will vary. The model paraphrases the structured return value (`transactionId`, `transferred`, `from`, `to`) into prose, and "Reference: 8f3..." is just one likely phrasing. The agent might say "Transfer ID:" or "Confirmation:" or nothing at all. What matters is the prompt fired, the answer was "yes", and the tool actually ran.

Then try the decline path. Restart the REPL (or just try again) and decline:

```
> Transfer 50 USD from Savings to Checking.
[agent] iteration 1: calling Transfer
[agent] 'Transfer' requires confirmation.
        Arguments: fromAccount=Savings, toAccount=Checking, amount=50
        Type 'yes' to proceed: no
[agent] iteration 2: final answer
I haven't transferred anything. You declined the confirmation. Let me know if you'd like to try a different amount or accounts.
```

Two things to notice about the decline path:

1. The agent didn't retry the transfer with different arguments. The `"Do not retry without explicit user permission"` hint in the structured error did its job.
2. The agent apologised in plain English. The model handled the recovery without needing additional logic in our code.

Ask the agent something unrelated like "What did I spend on coffee?" and confirm the gate doesn't trip. Read-only tools should fire without any prompt because their `AIFunction` instances are not wrapped in `ApprovalRequiredAIFunction`.

---

## Troubleshooting

### Prompt fires for every tool call

You wrapped more than the destructive tool. Look at your `chatOptions.Tools` list. Only `transferFunds.Transfer` should be wrapped with `new ApprovalRequiredAIFunction(...)`. The other three (Convert, GetTransactions, SearchTransactions) should be plain `AIFunctionFactory.Create(...)`.

### Prompt never fires for Transfer

Three things to check in order:

1. The `chatOptions.Tools` list actually wraps `AIFunctionFactory.Create(transferFunds.Transfer)` inside `new ApprovalRequiredAIFunction(...)`. If you just added the unwrapped function, the gate doesn't trigger.
2. The type check inside `ChatAgent` reads `function is ApprovalRequiredAIFunction`. If you used a different type name or missed the using for `Microsoft.Extensions.AI`, the check won't compile or won't match.
3. The model is actually picking the tool. Check the `[agent] iteration 1: calling Transfer` log line. If the model is calling a different tool (or no tool), the gate has nothing to fire on.

### Agent retries after a decline

The structured error message wasn't strong enough. Two escalations, in order:

1. Strengthen the hint on the declined result: `"The user has declined this action. Do not call this tool again in this conversation without the user explicitly asking for it."`. This is enough for most models.
2. If the model still hammers the tool, you have two settings on `ChatOptions` that act as harder mitigations. `Temperature = 0` makes the model more deterministic and less prone to creative retries. `ToolMode = ChatToolMode.None` forbids any tool call on the turn entirely, which is the hard escape hatch when a particular model decides to be persistent. Either can be applied for just the next turn, then reset.

### Prompt shows `Arguments: (no arguments)` for a call that clearly has arguments

`call.Arguments` is null or empty even though the call clearly carries some. On `Microsoft.Extensions.AI 10.5.2` (what the csproj pins), `FunctionCallContent.Arguments` is `IDictionary<string, object?>?` and the projection in the guide assumes that exact shape. If you see this in practice, inspect the model's raw response. The most likely cause is that the model produced a tool call with no arguments at all (unusual but possible if the system prompt or tool description nudged it that way), and the formatter is correctly falling through to the `(no arguments)` placeholder.

### `ApprovalRequiredAIFunction` is not found

It's in `Microsoft.Extensions.AI` (specifically the `Microsoft.Extensions.AI.Abstractions` assembly that ships transitively with the main package). Add `using Microsoft.Extensions.AI;` at the top of `Program.cs` and `ChatAgent.cs` if it isn't already there.

---

## You can now

Mark any tool as destructive by wrapping its `AIFunction` with `new ApprovalRequiredAIFunction(...)` at registration time. The agent will pause before calling it, show the user what's about to happen, and only proceed on explicit approval. Decline is treated as a structured signal the agent can recover from, not an exception.

The gate lives inside the agent loop, in one place, applied uniformly. Adding a tenth destructive tool later is one wrap in `Program.cs`. No new code paths in `ChatAgent`.

---

## Summary

You've added:

- **`Tools/TransferFundsTool.cs`**: a fake destructive tool with `[Description]` only. The framework's wrapper, not a per-method attribute, marks it as needing approval.
- **`ChatAgent` updated**: before each tool invocation, type-checks `function is ApprovalRequiredAIFunction`. On match, prompts the user via the console and short-circuits with a `user_declined` structured result on anything other than "yes".
- **`Program.cs` updated**: the destructive tool's `AIFunction` is wrapped with `new ApprovalRequiredAIFunction(...)` at registration. Read-only tools register as plain `AIFunction`. `ChatAgent`'s constructor signature is unchanged.
- **A working confirmation gate**: tried both approve and decline paths, watched the agent recover gracefully from the decline.

---

## What's next

There's no P5.02. The confirmation gate was Pillar 5's only hands-on exercise. P6.01 is the workshop's final one. We expose the agent's tools through an MCP (Model Context Protocol) server so other agents can use them as clients. Same `FinanceAssistant` tools, new transport, new audience.
