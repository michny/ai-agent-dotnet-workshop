# D1.02 - Split the Agent Into a Data Analyst and a Money Coach

> Lives at `docs/demos/d1-02-multi-agent-handoff.md`. Builds on `d1-01-migrate-to-agent-framework.md`. Facilitator drives, attendees follow along.

## Mission

Take the single `FinanceAssistant` agent we wired up in Demo 1 and split its responsibilities across two specialised agents that hand off to each other. `DataAnalyst` owns the data side: it can list transactions, search them by similarity, and convert currencies, but it does not give advice. `MoneyCoach` owns the advice side: it interprets findings, suggests budgets, and talks the user through trade-offs, but it has no tools that touch the database. The two agents share a conversation through MAF's handoff workflow and the user sees one coherent interaction.

**Learning Objectives**:

- `AgentWorkflowBuilder.CreateHandoffBuilderWith` and `WithHandoffs`: how to declare the handoff topology between specialised agents
- The conversation-broadcast model that keeps two agents on the same page without sharing a thread
- `InProcessExecution.RunStreamingAsync` plus `WorkflowEvent` plus `TurnToken`: the workflow-level analog of `agent.RunAsync` we used in Demo 1
- When to reach for handoff vs agent-as-tool vs group chat: control flow, task ownership, and context lifetime

---

## Prerequisites

- D1.01 finished. The repo is on whatever branch you landed Demo 1 on (we'll call it `maf` here). `dotnet build` is green. `dotnet run --project src/FinanceAssistant` still runs the single-agent REPL.
- Azure OpenAI user-secrets still wired (no changes from D1.01).
- The pgvector container is healthy.

---

## What we're solving

Demo 1 left us with one agent and four tools. That's the right shape for most finance questions: ask it to convert a currency or list transactions on a date, and one agent with the right tools answers in one turn.

The shape that breaks is the question senior engineers ask after a year of running this in production. "Can I have one agent that's brilliant at digging through transactions and one agent that's brilliant at giving financial advice, and let them talk to each other?" The instinct behind the question is mostly correct: instructions, tools, and evaluation criteria for "be a great data analyst" and "be a great financial coach" pull in different directions. A single agent with the union of both jobs ends up being mediocre at both.

> The caveat we put on the Pillar 6 forward-look slide still applies. Multi-agent is only worth the cost when the responsibilities really do pull apart and a single agent measurably underperforms. The token cost is roughly 10x to 15x a single-agent baseline, and the topology adds emergent failure modes (one agent loops, the other gives up, neither tells you why). Reach for this when you've measured a single-agent ceiling, not before. The demo is here so you can see the shape.

The mental shift is this. We're going to construct two `ChatClientAgent` instances from the same `IChatClient`, give each a different set of tools and a different system prompt, and wire them with a handoff topology. MAF's handoff orchestration registers a hidden "transfer-to" tool on each agent at workflow-build time. When the model on one side decides the conversation should change hands, it calls that tool. The framework swaps control to the other agent, the other agent sees the same conversation history (modulo tool-call internals), and the user keeps typing without knowing which agent is answering.

Three pieces:

1. **Split the tool surface.** `DataAnalyst` gets `GetTransactions`, `SearchTransactions`, and `ConvertCurrency`. `MoneyCoach` gets no tools at first. We'll talk about the deliberately-omitted `TransferFundsTool` at the bottom.
2. **Author two system prompts.** Short, specialised, and explicit about when each agent should hand off.
3. **Build the workflow.** `AgentWorkflowBuilder.CreateHandoffBuilderWith(coach).WithHandoffs(coach, [analyst]).WithHandoffs(analyst, [coach]).Build()`. The REPL changes shape: instead of calling `agent.RunAsync`, we feed messages into the workflow and watch its event stream.

---

## If you're comfortable, do this

Six steps. Skip the rest if it works on the first try.

1. Update `src/FinanceAssistant/FinanceAssistant.csproj`: add `<PackageReference Include="Microsoft.Agents.AI.Workflows" Version="1.6.1" />`, glob the prompts copy-to-output entry to `<None Update="Prompts\*.md">`, and suppress the experimental-API warning by adding `<NoWarn>$(NoWarn);MAAIW001</NoWarn>` to the existing `<PropertyGroup>`. The handoff workflow APIs are flagged experimental and `TreatWarningsAsErrors=true` will block the build without the suppression.
2. Create `src/FinanceAssistant/Prompts/AnalystPrompt.md` and `src/FinanceAssistant/Prompts/CoachPrompt.md`. Short, specialised. Copy from the bodies in Step 1 below. Delete the old `SystemPrompt.md`; the glob will otherwise keep copying it to the output unused.
3. In `Program.cs`, construct two agents with `new ChatClientAgent(...)`: `analyst` (name `data_analyst`, three data tools) and `coach` (name `money_coach`, no tools).
4. Build the workflow: `AgentWorkflowBuilder.CreateHandoffBuilderWith(coach).WithHandoffs(coach, [analyst]).WithHandoffs(analyst, [coach]).Build()`.
5. Replace the REPL body. For each user turn, append a `ChatMessage` to a running `List<ChatMessage>`, call `InProcessExecution.RunStreamingAsync(workflow, messages)`, send a `TurnToken(emitEvents: true)`, watch the event stream for `AgentResponseUpdateEvent` (per-token output) and `WorkflowOutputEvent` (turn complete), and update the running message list from the workflow's final output.
6. `dotnet run --project src/FinanceAssistant`. Ask "what did I spend on coffee in January 2026" and watch the coach hand off to the analyst, the analyst search and list, then either answer directly or hand back for interpretation.

If you finish with time to spare, the stretch section below adds an approval gate around a `RecommendSavingsAllocation` tool on the coach, using MAF's `function_approval_request` workflow events.

---

## Step 1: Add the Workflows package and prompts

Three small edits to `src/FinanceAssistant/FinanceAssistant.csproj`. They all sit in the same file so it's easier to do them together up front rather than thread them through Steps 2 and 3.

**1.1: Add the Workflows package.** Demo 1 brought in `Microsoft.Agents.AI`, which ships `ChatClientAgent` and `AgentSession`. The handoff orchestration primitives (`AgentWorkflowBuilder`, `InProcessExecution`, `StreamingRun`, `TurnToken`, the `*Event` types) ship in a separate package, `Microsoft.Agents.AI.Workflows`. Everything we reference from this package lives under the `Microsoft.Agents.AI.Workflows` namespace; Step 4 adds the `using` directive when the first type appears. Add a second `PackageReference` to the same `<ItemGroup>` you used in Demo 1:

```xml
<PackageReference Include="Microsoft.Agents.AI.Workflows" Version="1.6.1" />
```

**1.2: Suppress the experimental-API warning.** The handoff workflow APIs in 1.6.1 are tagged experimental (`MAAIW001`). The repo's `FinanceAssistant.csproj` has been carrying `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in its `<PropertyGroup>` since Pillar 1 (you can confirm with a quick `grep TreatWarningsAsErrors src/FinanceAssistant/FinanceAssistant.csproj`), so any consumer of an experimental API breaks the build. Add a `<NoWarn>` line to that same `<PropertyGroup>`:

```xml
<NoWarn>$(NoWarn);MAAIW001</NoWarn>
```

That whitelists the one warning code and leaves every other warning still acting as an error. When MAAIW001 graduates to stable in a future minor, you can drop the line.

**1.3: Glob the prompts copy-to-output entry.** The next step authors two new `.md` files alongside `SystemPrompt.md`. Easiest path is to replace the existing `<None Update="Prompts\SystemPrompt.md">` block with a glob so all the prompt files come along automatically. This is a swap, not a second block.

Find this in the csproj:

```xml
<None Update="Prompts\SystemPrompt.md">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
```

Replace it with:

```xml
<None Update="Prompts\*.md">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
```

Now author the two prompts. Each is opinionated about its own job and explicit about when the other agent should take over.

Create `src/FinanceAssistant/Prompts/AnalystPrompt.md`:

```
You are the Data Analyst. You investigate the user's finances using the tools
you have: list transactions in a date range, search transactions by topic,
convert currencies. You produce factual, sourced answers with numbers.

You do not give advice. You do not recommend budgets, savings targets, or
behavioural changes. If the user asks for advice, interpretation, or "what
should I do", hand the conversation to the Money Coach.

Be concise. Numbers are exact when sourced from a tool. Cite the date range
or query you used so the Coach (or the user) can verify your work.
```

Create `src/FinanceAssistant/Prompts/CoachPrompt.md`:

```
You are the Money Coach. You help the user think about spending, saving, and
financial trade-offs. You explain your reasoning, ask clarifying questions
when the goal is ambiguous, and propose concrete next steps.

You have no tools of your own. When you need data about the user's actual
transactions (totals, lists, fuzzy searches by topic, currency conversions),
hand the conversation to the Data Analyst. Wait for the Analyst to come back
with numbers before you interpret.

When the Analyst returns with numbers, do not stay silent. Always restate the
key figure in plain language, offer one or two interpretations or trade-offs
the user might care about, and propose a concrete next step. The user should
finish every data-driven turn with both the number and your reading of it.

Lead with empathy, follow with specifics. Disagree with the user when the
data warrants it. When you don't have enough information, ask, don't guess.
```

Two things worth reading carefully:

**The prompts are explicit about handoff triggers.** "If the user asks for advice, hand to the Coach." "When you need data, hand to the Analyst." MAF's handoff orchestration injects a hidden transfer tool on each agent based on the `WithHandoffs` rules, but the model still needs to know *when* to call it. These two paragraphs are doing that work.

**The Coach has no tools on purpose.** The temptation is to give every agent every tool "just in case". That defeats the split. The Coach is the agent the user talks to most often; the Analyst is the specialist it consults. If the Coach could list transactions on its own, the topology collapses and you're back to one agent with a costlier inference pattern. Keep the tool sets disjoint.

**The "always interpret" paragraph in the Coach prompt earns its line.** Without it, the Coach in 1.6.1 frequently hands off silently (zero output before the transfer tool call) and never resumes after the Analyst answers, because the conversation can plausibly terminate on the Analyst's reply. That feels broken to a user who expects the Coach to interpret. The explicit "do not stay silent" instruction gives the model a reason to take a second turn.

Delete the old `src/FinanceAssistant/Prompts/SystemPrompt.md`. With the glob from Step 1.3, the build would otherwise keep copying it to the output directory unused.

---

## Step 2: Construct the two agents

Back in `Program.cs`, the seed-and-embed block at the top stays. The DI block stays. The four tool constructions (`convertCurrency`, `getTransactions`, `searchTransactions`, `transferFunds`) become three: drop `transferFunds` for the demo (we discuss it at the bottom).

Replace the single-agent construction from Demo 1 with two agents.

```csharp
var analystPrompt = await File.ReadAllTextAsync(
    Path.Combine(AppContext.BaseDirectory, "Prompts", "AnalystPrompt.md"));
var coachPrompt = await File.ReadAllTextAsync(
    Path.Combine(AppContext.BaseDirectory, "Prompts", "CoachPrompt.md"));

var convertCurrency = new ConvertCurrencyTool();
var getTransactions = new GetTransactionsTool();
var searchTransactions = new SearchTransactionsTool(embedder);

var analyst = new ChatClientAgent(
    chatClient,
    instructions: analystPrompt,
    name: "data_analyst",
    description: "Specialist in transaction data and currency conversion",
    tools:
    [
        AIFunctionFactory.Create(convertCurrency.Convert),
        AIFunctionFactory.Create(getTransactions.GetTransactions),
        AIFunctionFactory.Create(searchTransactions.SearchTransactions)
    ]);

var coach = new ChatClientAgent(
    chatClient,
    instructions: coachPrompt,
    name: "money_coach",
    description: "Financial coach who interprets findings and recommends next steps",
    tools: []);
```

Two things worth knowing:

**The `name` is load-bearing.** MAF uses agent names in handoff tool names ("transfer-to-data_analyst"), in workflow events (`ExecutorId`), and in trace output. Pick names that the model would plausibly call from a prompt that says "hand to the Money Coach". `data_analyst` and `money_coach` are fine; `Agent1` and `Agent2` are not.

**Both agents share the same `chatClient`.** You're not paying for two deployments. One Azure OpenAI deployment, two agents pointed at it. The cost difference between this and Demo 1 is the extra tokens (each handoff replays the relevant history into the new agent's context), not extra infrastructure.

---

## Step 3: Build the handoff workflow

Add the workflow builder just below the agent constructions.

```csharp
var workflow = AgentWorkflowBuilder
    .CreateHandoffBuilderWith(coach)
    .WithHandoffs(coach, [analyst])
    .WithHandoffs(analyst, [coach])
    .Build();
```

You'll also need a new `using` directive at the top:

```csharp
using Microsoft.Agents.AI.Workflows;
```

Three things worth reading carefully:

**`CreateHandoffBuilderWith(coach)` makes the Coach the entry point.** The first user message goes to the Coach. From there, the Coach decides whether to answer directly or hand off. If you want the Analyst to be the front door (more useful when the typical question is a data question), swap them.

**`WithHandoffs(coach, [analyst])` declares a one-way edge.** Coach can transfer to Analyst. We add `WithHandoffs(analyst, [coach])` so the Analyst can return the conversation. Without the return edge, the Analyst would be a one-way trap: once you transfer in, you can't transfer back.

**The framework injects the transfer tools.** You did not write a "transfer-to-analyst" tool, and you won't see one in your code. MAF inspects the topology you declared and registers hidden tools on each agent at build time. The agent decides to hand off by calling one of those hidden tools; the framework intercepts the call and swaps executors. From the model's perspective, handing off looks like calling any other tool.

> Adding a third agent later is a matter of declaring it and adding the right `WithHandoffs` edges. The topology can be a star (one triage agent hands to N specialists), a mesh (every agent can transfer to every other), or a chain (sequential). For two agents the question of topology barely registers; for five it's the architecture diagram.

---

## Step 4: Drive the workflow from the REPL

This is the biggest change. Workflows are not invoked like agents. Instead of `agent.RunAsync(input, session)`, you feed messages into the workflow and watch its event stream. The pattern in MAF's docs is the one we'll use here.

Replace the REPL body with:

```csharp
Console.WriteLine("Finance assistant (multi-agent). Type a message, or 'exit' to quit.");

List<ChatMessage> messages = new();

while (true)
{
    Console.Write("> ");
    var input = Console.ReadLine();
    if (input is null || string.Equals(input.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    messages.Add(new ChatMessage(ChatRole.User, input));

    await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, messages);
    await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

    string? lastExecutorId = null;
    List<ChatMessage> newMessages = new();

    await foreach (WorkflowEvent evt in run.WatchStreamAsync())
    {
        if (evt is AgentResponseUpdateEvent e)
        {
            if (e.ExecutorId != lastExecutorId)
            {
                lastExecutorId = e.ExecutorId;
                // ExecutorId looks like "money_coach_d08e4110e9c848eaa0823762ca570c17":
                // the agent name plus an underscore plus a 32-char instance id.
                // Strip the trailing _hex32 for display so the labels stay readable.
                var suffix = e.ExecutorId.LastIndexOf('_');
                var label = suffix > 0 ? e.ExecutorId[..suffix] : e.ExecutorId;
                Console.WriteLine();
                Console.WriteLine($"[{label}]");
            }

            Console.Write(e.Update.Text);
        }
        else if (evt is WorkflowOutputEvent outputEvt)
        {
            newMessages = outputEvt.As<List<ChatMessage>>()!;
            break;
        }
    }

    Console.WriteLine();

    // newMessages is the FULL workflow conversation after this turn, not the delta.
    // Skip the messages we already know about and append only what the workflow added.
    messages.AddRange(newMessages.Skip(messages.Count));
}

return 0;
```

Four things worth reading carefully:

**The conversation lives in a `List<ChatMessage>`, not a session.** Workflows don't take an `AgentSession`. They take the full conversation history every turn. After each turn, the workflow returns the updated message list (its own messages plus whatever the agents produced), and we add the delta back to our running list. The pattern is "send the whole story, receive the whole story plus this turn's chapter".

**`TurnToken(emitEvents: true)` is what kicks the workflow.** Sending the user message is not enough; the workflow needs an explicit signal that this user turn is ready to execute. `emitEvents: true` says "stream me the agent response updates as they happen" (the alternative is "give me the final output and nothing else").

**`AgentResponseUpdateEvent.ExecutorId` is the agent's name plus an instance suffix.** In 1.6.1 the raw value looks like `money_coach_d08e4110e9c848eaa0823762ca570c17`: the agent name from Step 2, an underscore, and a 32-character hex instance id MAF generates per workflow run. The REPL code above strips the suffix so the labels stay readable. The first time the (trimmed) executor ID changes mid-turn, you've seen a handoff happen live. Watch for `[money_coach]` followed by `[data_analyst]` in a single user turn; the Coach often follows up after the Analyst returns (the prompt is wired to make it do so), but it isn't guaranteed every turn.

**`WorkflowOutputEvent` is the "turn done" signal.** When you see it, the workflow has nothing more to say and is waiting for the next user input. Its payload is the full message list the workflow generated this turn. We pull it out via `outputEvt.As<List<ChatMessage>>()!` and use the delta to update our running buffer.

> The `await using StreamingRun run` is a per-turn handle. Each user turn opens one, watches its event stream until `WorkflowOutputEvent`, then disposes. You do not reuse a `StreamingRun` across user turns; the message list is what carries history.

---

## Step 5: Run it

From the repo root:

```bash
dotnet run --project src/FinanceAssistant
```

You should see:

```
Finance assistant (multi-agent). Type a message, or 'exit' to quit.
>
```

Try four prompts in order. Each one shows a different shape of interaction. The four prompts below assume the default scaffolding CSV, which covers transactions from mid-2024 through mid-2026 with reasonable density across Q1 2026. If you reseeded with different data, adjust the date ranges so the Analyst has something to find; otherwise the "lied to me" failure mode is the fixture, not the model.

**1. "How much did I spend on coffee in January 2026?"** The Coach receives the question, recognises it needs data, and hands off to the Analyst. The Analyst calls `search_transactions` with `query="coffee"`, gets the matches, and produces the answer. Expect at least two `[executor]` labels: `[money_coach]` (often a silent or near-silent hand-off; the Coach commonly streams nothing before the transfer tool call) and `[data_analyst]` (the data answer). A third label (`[money_coach]` again, interpreting) is the prompt-driven happy path that the "always interpret" paragraph in `CoachPrompt.md` is trying to produce, but on `gpt-4o`-class deployments in 1.6.1 the workflow most often terminates on the Analyst's factual reply and you will see only the two labels.

> If you want the third turn reliably, tighten the "always interpret" paragraph in `CoachPrompt.md` further (be more explicit about restating the figure, offering a trade-off, suggesting a next step) and rerun. Models in 1.6.1 default to terminating once the question is factually answered, and the prompt is the lever that pulls them back into the conversation. With a stronger model (or a lower temperature) the third turn appears more often; with `gpt-4o-mini` you may not see it without further prompt work.

**2. "Should I cut back on Uber Eats?"** Pure advice question. The Coach handles it without handing off, because it does not need data. You will see only `[money_coach]` in the output. This is the case where keeping the Coach tool-free pays off: the cheap, fast answer stays on the cheap, fast agent.

**3. "Compare my January and February spend."** Coach hands off; Analyst calls `get_transactions` for the range (or twice, once per month), summarises totals; Coach narrates the comparison and offers interpretation. You'll see at least two labels; a third (return to Coach) is the typical happy path but not guaranteed.

**4. "What was the biggest charge in 2026 so far?"** A pure data question. The Coach should hand off immediately; the Analyst answers directly without bouncing back. Watch for `[money_coach]` then `[data_analyst]`. The Coach often does not produce a second turn here because the Analyst's reply is fully factual and the workflow terminates; that's expected behaviour for this prompt.

If a prompt should clearly involve the Analyst and the Coach answers without handing off, the prompt's handoff trigger is too weak. Edit `CoachPrompt.md` to be more explicit about what "needs data" means. The two prompts in Step 2 are tuned for these four prompts; your prompts will need iteration.

---

## Step 6: Diff what just happened

`git diff --stat` between Demo 1's commit and now should show roughly:

```
 src/FinanceAssistant/FinanceAssistant.csproj            |   5 +-
 src/FinanceAssistant/Program.cs                         |  ~75 ++++++++++-----
 src/FinanceAssistant/Prompts/AnalystPrompt.md           |  12 +++
 src/FinanceAssistant/Prompts/CoachPrompt.md             |  18 +++
 src/FinanceAssistant/Prompts/SystemPrompt.md            |   4 -
```

Roughly +100 lines, much of it the workflow event-loop body. We added one package (`Microsoft.Agents.AI.Workflows`, in the same `1.6.1` line as the base MAF package) and one `<NoWarn>` line to whitelist `MAAIW001`. We did not rewrite the tools, did not touch the chat client wiring. The structural change is small. The behavioural change is significant: two agents now share a conversation, with the topology declared in four lines.

The pattern this enables is the one the Pillar 6 forward-look slide was pointing at. Add a third agent (a `BudgetPlanner` that owns multi-month projections, say), give it tools the other two don't have, and extend the `WithHandoffs` block:

```csharp
AgentWorkflowBuilder
    .CreateHandoffBuilderWith(coach)
    .WithHandoffs(coach, [analyst, budgetPlanner])
    .WithHandoffs(analyst, [coach])
    .WithHandoffs(budgetPlanner, [coach])
    .Build();
```

The REPL doesn't change. The event-loop body doesn't change. Only the topology widens.

---

## Stretch goal: an approval gate on a Coach tool

Demo 1 dropped the `ApprovalRequiredAIFunction` confirmation gate. This stretch puts it back, using MAF's workflow-level approval primitive.

> **Before you start.** The approval surface in MAF is the part of the workflows API that has moved the most across preview versions. The recipe below is the working shape in `Microsoft.Agents.AI.Workflows 1.6.1` and uses `Microsoft.Extensions.AI.ApprovalRequiredAIFunction` for the tool-side flag. The event type that signals "approval needed" is named `FunctionApprovalRequestEvent` here, but variants of that name have shipped across MAF previews (`ToolApprovalRequest`, `FunctionApprovalRequest`, etc.). Treat this stretch as "if the surface is still as described" rather than "this will compile clean against any future MAF." If `FunctionApprovalRequestEvent` does not resolve, fall back to the same diagnostic move as the streaming events: dump `evt.GetType().Name` and pattern-match on whatever the actual approval event is called.

Add a small mock tool to the Coach. Create `src/FinanceAssistant/Tools/RecommendSavingsAllocationTool.cs`:

```csharp
using System.ComponentModel;

namespace FinanceAssistant.Tools;

public class RecommendSavingsAllocationTool
{
    [Description("Set the user's automatic monthly savings allocation. This changes a real setting in the user's account. The user must approve before this runs.")]
    public Task<object> Recommend(
        [Description("Monthly amount to allocate, in account currency. Must be positive.")] decimal monthlyAmount,
        CancellationToken ct = default)
    {
        return Task.FromResult<object>(new
        {
            allocationSet = monthlyAmount,
            effective = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1))
        });
    }
}
```

Wire it into the Coach in `Program.cs`. We reuse the same `ApprovalRequiredAIFunction` wrapper from `Microsoft.Extensions.AI` that Pillar 5 used on `TransferFundsTool`; the wrapper marks the function as gated, and MAF's workflow surfaces the gate as a per-call event:

```csharp
using Microsoft.Extensions.AI;

var recommendSavings = new RecommendSavingsAllocationTool();

var coach = new ChatClientAgent(
    chatClient,
    instructions: coachPrompt,
    name: "money_coach",
    description: "Financial coach who interprets findings and recommends next steps",
    tools:
    [
        new ApprovalRequiredAIFunction(AIFunctionFactory.Create(recommendSavings.Recommend))
    ]);
```

> `ApprovalRequiredAIFunction` is the M.E.AI 10.x wrapper that Pillar 5 introduced. It is still the working path in MAF 1.6.1: the wrapper signals "needs approval" on the function metadata, and the handoff workflow's executor reads that flag and emits the approval event below. If the wrapper renames in a future M.E.AI minor, the principle is identical: an `AIFunction` carries an "approval required" flag, the workflow respects it, and your event loop handles the resulting request.

In the REPL event loop, handle the new event type alongside `AgentResponseUpdateEvent`. The workflow emits a `request_info` event with a `function_approval_request` payload when the tool is about to run:

```csharp
await foreach (WorkflowEvent evt in run.WatchStreamAsync())
{
    if (evt is AgentResponseUpdateEvent e) { /* unchanged */ }
    else if (evt is FunctionApprovalRequestEvent approval)
    {
        Console.WriteLine();
        Console.WriteLine($"[approval] {approval.FunctionCall.Name}({approval.FunctionCall.ParseArguments()?.ToString()})");
        Console.Write("Approve? (y/n): ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        var approved = answer is "y" or "yes";
        await run.TrySendMessageAsync(approval.CreateResponse(approved));
    }
    else if (evt is WorkflowOutputEvent outputEvt) { /* unchanged */ }
}
```

> The exact event type name (`FunctionApprovalRequestEvent`, `ToolApprovalRequest`, etc.) has shifted across MAF previews. The shape is the same: an event that carries the function call about to be made, a way to inspect arguments, and a way to send back an approve/deny response. Whatever the current name is, the four-step flow (intercept, render, prompt, respond) is the pattern.

Run the demo and ask the Coach "set my monthly savings to 300 dollars". The Coach proposes the tool call; the workflow pauses and emits an approval request; you see the function name and arguments; you type `y` or `n`; the workflow proceeds. Deny it and watch how the Coach explains the rejection.

This is the pattern that earns the multi-agent architecture for finance use cases. Anything irreversible (transfers, autosave changes, account closures) lives behind an approval gate; the gate lives at the workflow level so a future browser-based or API-based client can render the same approval prompt without your code changing.

---

## Troubleshooting

### Both agents answer every turn

The `WithHandoffs` topology is too permissive or the prompts are too vague. Confirm `CreateHandoffBuilderWith` names exactly one start agent (Coach in this demo). Re-read both prompts; the handoff triggers should be clear paragraphs, not throwaway lines. If the model still picks the wrong agent, lower the temperature on the chat client or move to a stronger model: handoff decisions are a structured-output problem and weaker models punt.

### The Analyst hands off, calls a tool, then loops back without answering

The Analyst returned to the Coach before producing an answer. Add a sentence to `AnalystPrompt.md`: "Return your data findings to the Coach in a clearly-labelled `Findings:` block before handing off." Models lean on structural cues from the prompt to know when their work is done.

### Workflow never emits `WorkflowOutputEvent`

The `TurnToken(emitEvents: true)` is missing or you're disposing the `StreamingRun` before the loop ends. The pattern is: send the token, watch the event stream until you see `WorkflowOutputEvent`, then break and dispose. If you forget to send the token, the workflow accepts the message and waits forever.

### `outputEvt.As<List<ChatMessage>>()` returns null

The output type changed. Some MAF versions return a `ChatMessageList` wrapper or a custom type. Inspect `outputEvt.Data` in the debugger and adjust the cast accordingly. The data is always there; it's just the wrapper type that drifts across versions.

### `[data_analyst]` and `[money_coach]` labels never appear

The `AgentResponseUpdateEvent.ExecutorId` is empty or you're filtering it out. Confirm the agent constructions in Step 2 pass `name:` (not just `description:`). The executor ID is composed from the agent name plus an instance suffix MAF generates; agents constructed without a `name` produce only the bare suffix, which is what your label-trim code sees and treats as empty.

> **Forward-looking caveat.** In 1.6.1 the streaming event is `AgentResponseUpdateEvent` and the non-streaming sibling is `AgentResponseEvent`, both in `Microsoft.Agents.AI.Workflows`. Microsoft has used `AgentRun*` naming in samples elsewhere, so a future minor may rename one or both. If a future version of MAF stops resolving `AgentResponseUpdateEvent`, dump every `evt.GetType().Name` in the foreach for one turn and pattern-match on whatever the actual streaming type is called. Do not assume a specific replacement name in 1.6.1 - it does not exist yet.

### Labels look like `[money_coach_d08e4110...]` with a hex tail

That's the raw `ExecutorId` reaching the console. The REPL code in Step 4 trims the trailing `_<hex32>` for display. If your labels still show the suffix, your event-loop body is using `e.ExecutorId` directly instead of the trimmed `label` variable. Compare against the reference block.

### Coach has no tools but still tries to answer data questions itself

The agent's prompt is winning over the topology. Re-read `CoachPrompt.md` and confirm the "hand to the Data Analyst" paragraph is explicit. If the model still makes up numbers, the underlying chat model is too aggressive about confabulation; check that `Temperature` is set to a low value (the M.E.AI default is 1.0 which is too high for handoff scenarios). Lower it to 0.2 via `ChatOptions` on the `AddChatClient` registration.

### The two agents disagree on the user's name

Context broadcast in handoff is best-effort. User and agent messages broadcast across agents; tool-call internals do not. If the agent name comes from a tool call earlier in the turn, the other agent won't see it. The fix is to have the agent that learned the name include it in its prose before handing off, so it lands in a `ChatMessage` the other agent can see.

### `TransferFundsTool` no longer fires

Correct, and expected for this demo. We removed it from the tool surface in Step 2. If you want it back, add it to the Analyst's tool list. The right shape for it (production) is on a third agent, behind the approval gate from the stretch section, so the transfer never fires without a human in the loop.

---

## You can now

Take a single agent and split it into N specialists with declarative handoff rules. The tools, the chat client, the embedding generator all stay; you author one extra system prompt per specialist and one extra `WithHandoffs` edge per route. The REPL changes once (workflow event loop instead of `RunAsync`) and never again as you add more agents.

The pattern beyond two agents is the same. A triage agent in front of specialists, a back-of-house "escalator" specialist that can refuse and return to triage, sensitive operations behind approval gates that pause the workflow. All of it is the same six API surfaces: `ChatClientAgent` (constructor), `CreateHandoffBuilderWith`, `WithHandoffs`, `RunStreamingAsync`, `TurnToken`, and the workflow events.

The harder questions (when is multi-agent the right call, what evaluation looks like, how the token budget grows, how to keep specialists from collapsing into generalists over time) are real questions and out of scope for the demo. The pattern is what you take home.

---

## Summary

You've changed:

- **`FinanceAssistant.csproj`**: added `Microsoft.Agents.AI.Workflows` (`1.6.1`), suppressed `MAAIW001` via `<NoWarn>` (the handoff APIs are tagged experimental in 1.x and the repo treats warnings as errors), and globbed the `Prompts\*.md` copy-to-output entry so the new prompt files reach the build directory.
- **`Program.cs`**: two agent constructions instead of one, a workflow builder, a workflow-driven REPL using `InProcessExecution.RunStreamingAsync` and `TurnToken`. The label-trim for `ExecutorId` keeps the per-agent labels readable.
- **`Prompts/AnalystPrompt.md`** and **`Prompts/CoachPrompt.md`**: two short, specialised system prompts with explicit handoff triggers and an "always interpret after the Analyst returns" paragraph on the Coach.
- **`Prompts/SystemPrompt.md`**: deleted (the old single-agent prompt is no longer used and the glob would otherwise keep copying it).
- **Tools split**: Analyst owns `GetTransactions`, `SearchTransactions`, `ConvertCurrency`. Coach owns nothing (in the stretch, owns a single approval-gated `RecommendSavingsAllocation`). `TransferFundsTool` is parked.

What survived intact:

- All tool implementations in `Tools/` (besides the parked one).
- `ServiceCollectionExtensions.AddChatClient` and `AddEmbeddingGenerator`.
- The pgvector container, the seed CSV, the embedding backfill.
- The `Microsoft.Agents.AI` package from Demo 1; the workflow primitives ship in a sibling package (`Microsoft.Agents.AI.Workflows`) on the same `1.6.1` release line.

---

## Additional Resources

- [Microsoft Agent Framework Workflows - Handoff orchestration](https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/handoff): the canonical handoff documentation, with the math-tutor and history-tutor example this demo's shape is based on.
- [A Tour of Handoff Orchestration Pattern](https://devblogs.microsoft.com/agent-framework/a-tour-of-handoff-orchestration-pattern/): blog walkthrough of the pattern with worked examples.
- [Group Chat Orchestration](https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/group-chat): the alternative orchestration when you'd rather let multiple agents speak in a turn-taking loop than hand off explicitly.
- [Microsoft Agent Framework Workflows overview](https://learn.microsoft.com/en-us/agent-framework/workflows/): the wider menu (sequential, concurrent, handoff, group chat, Magentic-One).

The workshop ends here. Demo 1 migrated the single agent to MAF; Demo 2 split it into a multi-agent architecture. The next steps you'll take on your own (checkpointing, durable workflows, evaluation, observability across agents) are on the same MAF surface, split between `Microsoft.Agents.AI` (agents, sessions) and `Microsoft.Agents.AI.Workflows` (orchestration, checkpointing, durable execution). The shape you build first is the shape you keep.
