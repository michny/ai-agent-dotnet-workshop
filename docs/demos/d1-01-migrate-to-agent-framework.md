# D1.01 - Migrate the Finance Assistant to Microsoft Agent Framework

> Demo 1. Post-workshop. Facilitator drives, attendees follow along.

## Mission

Take the agent we shipped at the end of Pillar 6 and rewrite the loop, the tool dispatch, and the message-list bookkeeping using Microsoft Agent Framework. By the end, `Program.cs` is roughly half its current size, `ChatAgent.cs` and `ConversationStore` are deleted, and the REPL behaves exactly the same way it did on `end-of-pillar-6`. The four tools, the system prompt, and the Azure OpenAI wiring all stay put.

**Learning Objectives**:

- `Microsoft.Agents.AI.ChatClientAgent` as the production-grade replacement for the hand-written `ChatAgent` loop we built in Pillar 3
- `AgentSession` as MAF's owned-by-the-framework alternative to `ConversationStore`: same idea (a multi-turn message buffer), no longer your code
- The boundary between Microsoft.Extensions.AI primitives (`IChatClient`, `AIFunction`, `AIFunctionFactory`) and MAF abstractions (`AIAgent`, `ChatClientAgent`, `AgentSession`): MAF sits on top of M.E.AI, not next to it

---

## Prerequisites

- The repo is on the `end-of-pillar-6` tag. `git status` clean.
- `dotnet build FinanceAssistant.sln` succeeds with zero warnings.
- Azure OpenAI user-secrets for `finance-assistant-workshop` are still wired (`AzureOpenAI:Endpoint`, `AzureOpenAI:ApiKey`, `AzureOpenAI:Deployment`, `AzureOpenAI:EmbeddingDeployment`).
- The pgvector Postgres container is healthy (`docker compose ps`).

---

## What we're solving

The hand-written `ChatAgent` we shipped in Pillar 3 is honest about what an agent is. There's a `for` loop, a `GetResponseAsync` call inside it, a check for `FinishReason.ToolCalls`, a loop over `FunctionCallContent` items, an `InvokeAsync` per tool, and a `FunctionResultContent` appended to the message list before the next turn. We wrote it on purpose so the loop, the tool-call boundary, and the message bookkeeping all stay in sight.

That works for teaching. It also works for one agent doing one job in one process. The shape that breaks is the day you want a second agent, or a conversation that survives a process restart, or an approval flow that pauses for hours, or a workflow that hands a conversation between two specialists. Every one of those needs the loop wrapped behind an abstraction so the orchestration layer above it has something to compose.

Microsoft Agent Framework is that abstraction. The loop becomes `ChatClientAgent`. The message list becomes `AgentSession`. The tool dispatch is the same (still `AIFunction`, still `AIFunctionFactory.Create`, still `[Description]`-driven schemas), because MAF leans on M.E.AI for that. What MAF adds is: an agent type you can pass to a workflow, a session type the framework can serialise, and the orchestration primitives we'll use in Demo 2 (handoff, group chat, graph).

We're going to do this in three pieces:

1. **Add the MAF package.** One `PackageReference`. The existing M.E.AI packages stay; MAF builds on them.
2. **Replace the loop.** Construct a `ChatClientAgent` from the same `IChatClient` we already register, hand it the same four `AIFunction` tools, point it at the same system prompt. Delete `ChatAgent.cs` and `ConversationStore.cs`.
3. **Use it from `Program.cs`.** `await agent.CreateSessionAsync()` once at startup, `agent.RunAsync(input, session)` inside the REPL loop. That's the whole REPL.

> We are intentionally dropping the `ApprovalRequiredAIFunction` confirmation gate from `TransferFundsTool` for this demo. MAF has its own approval primitive (`function_approval_request` events surfaced through workflows), but wiring it into a single-agent REPL is more code than the demo earns. Demo 2's multi-agent shape is where the approval primitive starts to pay for itself; we'll cover it there.

---

## If you're comfortable, do this

Five steps. Skip the rest if it works on the first try.

1. Add `Microsoft.Agents.AI` to `src/FinanceAssistant/FinanceAssistant.csproj` (version `1.6.1` or whatever is current on NuGet).
2. Delete all three files in one go: `src/FinanceAssistant/ChatAgent.cs`, `src/FinanceAssistant/Memory/ConversationStore.cs`, and `src/FinanceAssistant/Memory/SummarizingHistoryReducer.cs`. The reducer references the store directly, so they have to leave together.
3. In `Program.cs`, replace the `ChatAgent` construction with `new ChatClientAgent(chatClient, systemPrompt, name: "FinanceAssistant", description: "Personal finance assistant", tools: [...])` using the same four tools wrapped via `AIFunctionFactory.Create`.
4. Replace the REPL body's `chatAgent.RunTurnAsync(input)` with `agent.RunAsync(input, session)` where `session` is created once via `await agent.CreateSessionAsync()`.
5. `dotnet run --project src/FinanceAssistant`. The REPL should behave identically to `p6-01-end` for non-transfer prompts.

If you finish with time to spare, the stretch section at the bottom walks through running the agent with `RunStreamingAsync` and surfacing tokens to the console as they arrive.

---

## Step 1: Add the MAF package

Open `src/FinanceAssistant/FinanceAssistant.csproj`. Find the `<ItemGroup>` that holds the M.E.AI package references. Add one line:

```xml
<PackageReference Include="Microsoft.Agents.AI" Version="1.6.1" />
```

That's the only new package. `Microsoft.Agents.AI` ships `ChatClientAgent`, `AIAgent`, and `AgentSession`. It depends on `Microsoft.Extensions.AI` (which you already have), so the rest of the M.E.AI surface keeps working.

Two things worth knowing:

**MAF reached 1.0 in April 2026.** The 1.x line is the LTS contract; the public surface this demo uses (`ChatClientAgent` constructor, `AIAgent.RunAsync`, `AIAgent.CreateSessionAsync`) is stable across it. We pin `1.6.1` here. If a newer 1.x minor is on NuGet by the time you read this, bump it.

**MAF is built on M.E.AI, not parallel to it.** The same `IChatClient` you registered in `ServiceCollectionExtensions.AddChatClient` is what `ChatClientAgent` wraps. The same `AIFunction` shape your tools produce via `AIFunctionFactory.Create` is what MAF dispatches. You are not rewriting the tools, the chat client, the embedding generator, or the database wiring. You are replacing the loop and the message buffer.

> The constructor `new ChatClientAgent(chatClient, instructions, name, description, tools)` is the path we'll use throughout. There is no `CreateAIAgent` extension method on `IChatClient` in 1.6.1; an older preview surface had one, but the working 1.x API is the explicit constructor. Demo 2 uses the same constructor with two agents.

---

## Step 2: Delete the hand-written loop

Delete three files outright:

```bash
rm src/FinanceAssistant/ChatAgent.cs
rm src/FinanceAssistant/Memory/ConversationStore.cs
rm src/FinanceAssistant/Memory/SummarizingHistoryReducer.cs
```

`ChatAgent.cs` held the `for` loop, the `GetResponseAsync` call, the `FunctionCallContent` enumeration, the `function.InvokeAsync` per tool call, the `ApprovalRequiredAIFunction` confirmation gate, and the iteration-cap fallback. All of that is now `ChatClientAgent`'s job.

`ConversationStore.cs` held `_messages`, `AppendSystemMessage`, `AppendUserMessage`, `AppendResponseMessages`, and `AppendToolResult`. All of that is now `AgentSession`'s job.

`SummarizingHistoryReducer.cs` has to go with the store. Its public surface is `TryReduceAsync(ConversationStore store, ...)`, so it stops compiling the moment `ConversationStore.cs` is deleted. There is no honest "leave it on disk for later" path; it's deletion or it's a port. The Pillar 4 summarising-history work targeted `ConversationStore`'s message list, and MAF's `AgentSession` owns its own message list through a different abstraction (`ChatHistoryProvider`). The right shape, when you want this back, is a custom `ChatHistoryProvider` that truncates and summarises as messages are appended. That's roughly thirty lines we won't write today; the algorithm is still in `git show end-of-pillar-6:src/FinanceAssistant/Memory/SummarizingHistoryReducer.cs` when you need it back.

---

## Step 3: Rewrite Program.cs

Open `src/FinanceAssistant/Program.cs`. The seed-and-embed block at the top stays. The `ConfigurationBuilder` block stays. The DI registration stays. The embedding-backfill loop stays. The four tool constructions (`convertCurrency`, `getTransactions`, `searchTransactions`, `transferFunds`) stay.

Everything from the `var chatOptions = new ChatOptions { ... }` block down to the end of the REPL gets replaced. Here is the new tail.

```csharp
var systemPrompt = await File.ReadAllTextAsync(
    Path.Combine(AppContext.BaseDirectory, "Prompts", "SystemPrompt.md"));

var agent = new ChatClientAgent(
    chatClient,
    instructions: systemPrompt,
    name: "FinanceAssistant",
    description: "Personal finance assistant",
    tools:
    [
        AIFunctionFactory.Create(convertCurrency.Convert),
        AIFunctionFactory.Create(getTransactions.GetTransactions),
        AIFunctionFactory.Create(searchTransactions.SearchTransactions),
        AIFunctionFactory.Create(transferFunds.Transfer)
    ]);

var session = await agent.CreateSessionAsync();

Console.WriteLine("Finance assistant. Type a message, or 'exit' to quit.");

while (true)
{
    Console.Write("> ");
    var input = Console.ReadLine();
    if (input is null || string.Equals(input.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    var result = await agent.RunAsync(input, session);
    Console.WriteLine(result.Text);
}

return 0;
```

Add the two new `using` directives at the top of the file, alongside the existing ones:

```csharp
using Microsoft.Agents.AI;
```

`Microsoft.Extensions.AI` is already there (the tools use it for `[Description]` and `IEmbeddingGenerator`). `using FinanceAssistant;` stays (it's how `ServiceCollectionExtensions.AddChatClient` and `AddEmbeddingGenerator` resolve). You can drop `using FinanceAssistant.Memory;` now that `ConversationStore` and the reducer are gone.

Four things worth reading carefully:

**`ChatClientAgent` is the `AIAgent` for any `IChatClient`-backed model.** That's the bridge from M.E.AI to MAF. The same chat client your old `ChatAgent` called via `GetResponseAsync` is now wrapped inside the agent. The constructor owns the system prompt (passed as `instructions`), the name (used in handoff scenarios and trace output), a description (free-text label MAF surfaces in workflows), and the tool list.

**Tools are the same `AIFunction` instances.** `AIFunctionFactory.Create(method)` produces an `AIFunction` from any `[Description]`-decorated method. MAF accepts the same type M.E.AI accepts. If you wrote good `[Description]` attributes on the tools in Pillars 2 and 5, they keep working without edits.

**`AgentSession` replaces `ConversationStore`.** `await agent.CreateSessionAsync()` returns a session tied to this specific agent instance. The session accumulates messages across `RunAsync` calls. We construct it once at startup and reuse it for the lifetime of the REPL; that gives us a single multi-turn conversation. If you wanted N parallel users, you'd construct N sessions from the same agent.

**The loop disappeared.** There is no `for (var iteration = 1; iteration <= _maxIterations; iteration++)`. There is no `if (response.FinishReason != ChatFinishReason.ToolCalls) return`. There is no manual `function.InvokeAsync`. `ChatClientAgent` owns all of it: the agent loop, the tool-call detection, the tool invocation, the result appending, the next-turn dispatch. The iteration cap is configurable via `ChatClientAgentRunOptions` (check IntelliSense for the exact property name; the option lives on the per-run options object you can pass to `RunAsync`). The default is sensible for a workshop.

> The `RunAsync` return type is `AgentResponse`. It has `.Text` (the model's final natural-language answer), `.Messages` (everything that landed in this turn, including any tool-call/tool-result pairs), and a few other fields. For the REPL we only need `.Text`. If you'd rather see the tool calls that fired during a turn, iterate `result.Messages` and filter on `m.Contents.OfType<FunctionCallContent>()`.

---

## Step 4: Run it

From the repo root:

```bash
dotnet run --project src/FinanceAssistant
```

The REPL should look identical to the one from `end-of-pillar-6`:

```
Finance assistant. Type a message, or 'exit' to quit.
>
```

Try the four prompts you tried at the end of Pillar 6, in order. The behaviour should match.

1. `Convert 100 EUR to USD.` → ConvertCurrency tool fires, returns a string, agent paraphrases it.
2. `List my transactions on 2026-01-09.` → GetTransactions tool fires with `dateExpression="2026-01-09"`, returns the rows the seed CSV has on that date, agent summarises them. Pick another single-day date if you've reseeded with different data; not every day in the seed has transactions, and a date with zero rows produces an empty (but valid) tool result.
3. `Find coffee shop purchases.` → SearchTransactions tool fires with `query="coffee shops"`, returns the top 5, agent narrates them.
4. `What's the biggest transaction in January 2026?` → GetTransactions over the range, then the model picks the biggest from the list. Two turns of reasoning, both inside one `RunAsync`.

The first time you ask a follow-up, notice that the agent remembers context across the call. That's the session doing its job. The hand-written `ConversationStore` did the same thing; the difference is you didn't have to write it.

---

## Step 5: Diff what just happened

Run `git diff --stat end-of-pillar-6 HEAD`. You should see roughly (line counts will drift as the scaffold evolves):

```
 src/FinanceAssistant/ChatAgent.cs                        | 130 ---------------
 src/FinanceAssistant/Memory/ConversationStore.cs         |  55 -------
 src/FinanceAssistant/Memory/SummarizingHistoryReducer.cs |  54 -------
 src/FinanceAssistant/FinanceAssistant.csproj             |   1 +
 src/FinanceAssistant/Program.cs                          |  ~35 ++---
```

Roughly 240 lines deleted, 1 line added to the csproj, the REPL inside `Program.cs` halved. The behaviour is the same. That's the trade MAF asks you to make: hand over the loop, the session, and the tool dispatch in exchange for less code, an agent type that composes into workflows, and a session type the framework can serialise.

What you give up is also worth naming. The `ApprovalRequiredAIFunction` gate is gone (we'll get it back in Demo 2 via MAF's approval primitive). The summarising-history reducer is gone (it comes back as a custom `ChatHistoryProvider` if you need it). The `[agent] iteration N: calling foo, bar` log line is gone (MAF emits trace events via `ILogger` if you add logging; the format is different).

The pattern this enables matters more than the lines saved. With a `ChatClientAgent` instance, you can pass it to `AgentWorkflowBuilder.CreateHandoffBuilderWith(agent)`, to group-chat orchestration, to graph workflows, and to checkpointing. Every one of those takes an `AIAgent`, not a hand-written class. Demo 2 picks up from here with the handoff workflow.

---

## Stretch goal: stream the response

`RunAsync` returns one big `AgentResponse` when the model is done. For a REPL that's fine. For anything user-facing you usually want to surface tokens as they arrive. MAF supports this via `RunStreamingAsync`.

Replace the line:

```csharp
var result = await agent.RunAsync(input, session);
Console.WriteLine(result.Text);
```

with:

```csharp
await foreach (var update in agent.RunStreamingAsync(input, session))
{
    Console.Write(update.Text);
}
Console.WriteLine();
```

`RunStreamingAsync` returns an `IAsyncEnumerable<AgentResponseUpdate>`. Each update carries a delta (`update.Text` is the partial token text the model just produced, or empty when the update is a tool call rather than text). You can also filter on `update.Contents` to render tool calls inline ("calling get_transactions...") which is closer to what a chat UI would do.

The session still accumulates correctly under streaming. After the loop completes, the full assistant turn is in the session, same as if you'd called `RunAsync`.

> Streaming changes the latency story but not the cost story. The tool calls still happen in the middle of the stream; the model still pays for every token it produces, streamed or not. The win is that the user sees the first token in ~300ms instead of waiting for the full turn.

---

## Troubleshooting

### Build fails with "The type or namespace name 'AIAgent' could not be found"

`Microsoft.Agents.AI` either isn't in the csproj or `dotnet restore` hasn't picked it up. Confirm the `PackageReference` is inside an `<ItemGroup>` (not orphaned), then `dotnet restore`. If that still fails, your local NuGet feed may not have the package: check `nuget.org` is in `~/.nuget/NuGet/NuGet.Config` sources.

### `agent.RunAsync` returns the same answer every time, or context is lost between turns

You're probably constructing a new session on every loop iteration. The session is what carries history. Construct it once via `await agent.CreateSessionAsync()` before the `while (true)` loop, not inside it. The pattern is one agent, one session for the conversation.

### Tools fire but the answer is "the agent could not complete the task"

MAF caps tool iterations like the hand-written loop did. If a tool is throwing or returning a shape the model can't reason about, the agent hits the cap and returns a generic fallback. The cap is configurable on `ChatClientAgentRunOptions` (the per-run options object you can pass as the third argument to `RunAsync`); IntelliSense will surface the exact property name in your installed version. Add `using Microsoft.Extensions.Logging;` and pass an `ILoggerFactory` into the `ChatClientAgent` constructor (there's an overload that takes it) to see what's happening per turn. The first non-obvious culprit is usually `GetTransactionsTool` returning an `error` object the model treats as terminal; the safe-fallback shape we built in Pillar 2 is what keeps it from looping.

### "InvalidOperationException: ChatRole.Tool messages require a CallId"

Almost always means a tool returned `null` or threw without being caught. `AIFunctionFactory.Create` wraps thrown exceptions into structured tool results, but a `null` return value sometimes confuses the serialiser. Make sure each tool returns a non-null object (the existing four tools all do; the trap is when you copy this pattern to a new tool and forget the `return new { ... }` at the bottom).

### The system prompt isn't being honoured

Confirm the `instructions:` parameter on the `ChatClientAgent` constructor is set to the full text of `SystemPrompt.md`, not the path. Easy to copy the wrong line. If the prompt loads correctly but the model still ignores it, MAF appends instructions as a system message at session initialisation; constructing a fresh session (`session = await agent.CreateSessionAsync()`) and trying again rules out a contaminated turn.

### Transfer prompts no longer ask for confirmation

Correct, and expected. The `ApprovalRequiredAIFunction` wrapper is M.E.AI-specific and the hand-written loop in `ChatAgent.cs` was what surfaced the confirmation prompt. MAF's approval primitive lives at the workflow level (we'll see it in Demo 2). For now, `TransferFundsTool` fires without a gate. If this is dangerous in your environment, comment the tool out of the `tools:` list until Demo 2 lands.

---

## You can now

Take any M.E.AI-based agent and migrate it to MAF in under an hour: add one package, delete your hand-written loop, construct a `ChatClientAgent` from the same `IChatClient`, pass the same `AIFunction` instances as tools. The agent and session types you end up with compose into the rest of MAF (workflows, handoff, group chat, checkpointing); the M.E.AI primitives underneath stay where they are.

The mental model is layers: tools and chat clients live in M.E.AI; agents and sessions and orchestrations live in MAF. M.E.AI is the "what" (a tool, a model call); MAF is the "who" (an agent, a workflow). Demo 2 picks up the second half.

---

## Summary

You've changed:

- **`FinanceAssistant.csproj`**: one new `PackageReference` for `Microsoft.Agents.AI` (`1.6.1`).
- **`ChatAgent.cs`**: deleted. The loop, the tool-call detection, the tool invocation, the iteration cap, all live in `ChatClientAgent` now.
- **`Memory/ConversationStore.cs`**: deleted. The session owns the message buffer.
- **`Memory/SummarizingHistoryReducer.cs`**: deleted. It referenced `ConversationStore` directly, so it had to go with it. The algorithm is preserved in git at the `end-of-pillar-6` tag, ready to come back as a custom `ChatHistoryProvider`.
- **`Program.cs`**: rewritten REPL. `new ChatClientAgent(chatClient, instructions, name, description, tools)` constructs the agent; `await agent.CreateSessionAsync()` is called once; `agent.RunAsync(input, session)` replaces the per-turn loop.

What survived intact:

- All four tools in `Tools/`, unchanged.
- `SystemPrompt.md`, unchanged.
- `ServiceCollectionExtensions.AddChatClient` and `AddEmbeddingGenerator`, unchanged.
- The pgvector container, the seed CSV, the embedding backfill.

---

## Additional Resources

- [Microsoft Agent Framework Overview](https://learn.microsoft.com/en-us/agent-framework/overview/): conceptual map of agents, sessions, workflows.
- [`ChatClientAgent` class reference](https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.chatclientagent?view=agent-framework-dotnet-latest): constructor overloads, options, lifecycle.
- [Multi-turn conversations](https://learn.microsoft.com/en-us/agent-framework/user-guide/agents/multi-turn-conversation): how sessions carry history across `RunAsync` calls.
- [Upgrading to MAF in your .NET AI chat app](https://devblogs.microsoft.com/dotnet/upgrading-to-microsoft-agent-framework-in-your-dotnet-ai-chat-app/): the migration path Microsoft recommends.

Next: D1.02 splits this single agent into two specialists (DataAnalyst, MoneyCoach) and wires them up with MAF's handoff orchestration. The work you did here is the foundation; the workflow piece sits on top.
