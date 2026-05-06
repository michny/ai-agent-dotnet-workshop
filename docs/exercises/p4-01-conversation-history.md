# P4.01 - Conversation History

> Pillar 4, Part 1. Individual.

## Mission

Pull the message list out of the per-turn scope so the agent remembers what was said. By the end, "What was the total?" works as a follow-up to "Show me transactions on 2026-05-24."

**Learning Objectives**:

- Episodic memory: a single conversation persists turn to turn
- Why the agent currently forgets (the list is rebuilt every turn)
- `ConversationStore` as the single seam for every message operation, and the foundation P4.02 will build summarisation onto

---

## Prerequisites

- P3.01 finished. The REPL routes turns through `chatAgent.RunTurnAsync`. The iteration cap works.

---

## What we're solving

Right now every REPL turn starts with a fresh message list. Look at the inside of the `while` loop in `Program.cs`:

```csharp
var messages = new List<ChatMessage>
{
    new(ChatRole.System, systemPrompt),
    new(ChatRole.User, input)
};

var reply = await chatAgent.RunTurnAsync(messages);
```

The `messages` variable is declared *inside* the loop. Every turn rebuilds it from scratch. The system prompt and the new user message land in the list. `ChatAgent.RunTurnAsync` appends assistant responses and tool results during its work, but those additions die when the loop iterates. The next turn doesn't see them.

The result: the agent has no memory.

Run the REPL today and try this sequence:

```
> What did I spend on 2026-05-24?
[agent answers with a total]
> What was the total?
[agent has no idea what total you mean]
```

The previous turn's answer was thrown away when the loop iterated. The model on the second turn sees only the system prompt and the words "What was the total?". It can't reach back to the first turn because the first turn is gone.

The fix in concept: move the list out of the loop. Initialise it once with the system prompt. Append messages to it across turns. Let the model see the full history every time.

But there's a follow-on design question: where should that list live, and *who's allowed to grow it*? The answer we're going with is a `ConversationStore` that owns every message operation, and a `ChatAgent` that orchestrates it. The agent (not `Program.cs`) drives the conversation: appends the user input, asks the model, appends the response, runs tools, appends results, loops. `Program.cs` becomes a thin REPL shell that hands input to the agent and prints the reply.

That split matters for P4.02. A `SummarizingHistoryReducer` will hang off the store, and because the store is the single funnel for every append, the reducer sees every growth event and can compress old turns when the conversation gets long. Putting that funnel in place now is most of the work.

> This is episodic memory. The agent remembers the current conversation. It does not remember anything from yesterday, last session, or another user's conversation. That's semantic memory, and we're not building it here. P4.02 adds a small taste through a "recall highlights" tool, but the conversation history itself lives only as long as the REPL process.

---

## If you're comfortable, do this

Four steps. Skip the rest if it works on the first try.

1. Create `src/FinanceAssistant/Memory/ConversationStore.cs`. Parameterless constructor. Four append methods (system message, user message, response messages from the model, tool result). Expose `Messages` as `IReadOnlyList<ChatMessage>` for snapshotting into `GetResponseAsync`.
2. Update `ChatAgent`: take a `ConversationStore` and the system prompt at construction, seed the store with the system prompt in the constructor body, change `RunTurnAsync` to accept a single `string input`, and replace every direct `messages.Add(...)` inside the loop with a store call.
3. In `Program.cs`, create the store before the agent and pass it in along with the system prompt. Replace the per-turn message-list block with a single `await chatAgent.RunTurnAsync(input)`.
4. Run. Try the two-question sequence. Confirm the follow-up works.

---

## Step 1: Create ConversationStore.cs

Create `src/FinanceAssistant/Memory/` (new folder) and inside it create `ConversationStore.cs`:

```csharp
using Microsoft.Extensions.AI;

namespace FinanceAssistant.Memory;

public class ConversationStore
{
    private readonly List<ChatMessage> _messages = new();

    public IReadOnlyList<ChatMessage> Messages => _messages;

    public void AppendSystemMessage(string text)
    {
        _messages.Add(new ChatMessage(ChatRole.System, text));
    }

    public void AppendUserMessage(string text)
    {
        _messages.Add(new ChatMessage(ChatRole.User, text));
    }

    public void AppendResponseMessages(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            _messages.Add(message);
        }
    }

    public void AppendToolResult(AIContent content)
    {
        _messages.Add(new ChatMessage(ChatRole.Tool, [content]));
    }
}
```

The four append methods map one-to-one to the message roles the agent ever produces:

| Role | Store method | When the agent appends it |
| --- | --- | --- |
| `System` | `AppendSystemMessage(string)` | Once, from `ChatAgent`'s constructor, to seed the conversation |
| `User` | `AppendUserMessage(string)` | Top of `RunTurnAsync`, when the REPL hands in fresh input |
| `Assistant` | `AppendResponseMessages(IEnumerable<ChatMessage>)` | After `GetResponseAsync`, for the model's reply (may carry tool-call requests) |
| `Tool` | `AppendToolResult(AIContent)` | After invoking a tool, to feed the result back on the next iteration |

Four things worth noticing:

**The store is a neutral message container.** The constructor takes no arguments. It doesn't know about system prompts, agents, or any other concept. It just knows how to hold an ordered list of `ChatMessage` and how to grow it through a controlled API. The system prompt arrives via `AppendSystemMessage` like any other message; the agent owns the responsibility of seeding it on construction.

**`Messages` is `IReadOnlyList`, not `IList`.** Callers can iterate it (which is what `IChatClient.GetResponseAsync` needs) but can't mutate it. Every write goes through one of the four `Append*` methods. That matters: the store can guarantee its invariants (append-only, no surprise role swaps, no out-of-band mutations) because nothing outside the class can touch the underlying list.

**One append method per role we ever add.** `AppendSystemMessage` for the initial system prompt, `AppendUserMessage` for user input, `AppendResponseMessages` for assistant turns (which may carry multiple messages and embedded tool-call requests, hence `IEnumerable`), `AppendToolResult` for the result of invoking a tool. Together they cover every message the agent loop produces.

**There's no `Reset` method.** A real REPL would have one (`:reset` or similar). The workshop ships without it on purpose. If a session goes sideways, restart the REPL. P4.02 is where the "what if the list grows too long" question actually gets answered.

---

## Step 2: Update ChatAgent to use the store

`ChatAgent` currently takes the message list as a method argument and mutates it in place. We're flipping that: the agent holds a `ConversationStore` for its lifetime, and every append goes through the store's API instead of `messages.Add(...)`. The agent also takes the system prompt at construction and seeds the store with it before any turn runs.

### 2.1: Constructor takes the store and the system prompt

Open `src/FinanceAssistant/ChatAgent.cs`. Add the `FinanceAssistant.Memory` namespace alongside the existing `Microsoft.Extensions.AI` using. The top of the file should read:

```csharp
using FinanceAssistant.Memory;
using Microsoft.Extensions.AI;
```

Add a `_store` field, accept the store and the system prompt in the constructor, and seed the store on construction:

```csharp
private readonly IChatClient _chatClient;
private readonly ChatOptions _options;
private readonly ConversationStore _store;
private readonly int _maxIterations;

public ChatAgent(IChatClient chatClient, ChatOptions options, ConversationStore store, string systemPrompt, int maxIterations = 8)
{
    _chatClient = chatClient;
    _options = options;
    _store = store;
    _maxIterations = maxIterations;

    _store.AppendSystemMessage(systemPrompt);
}
```

The store doesn't know about system prompts; the agent does. Seeding in the constructor means the store has a system message at index 0 before any caller can hand the agent a user input.

### 2.2: RunTurnAsync takes a string

Change the method signature so the agent no longer receives a list from the outside:

```csharp
// Before:
public async Task<string> RunTurnAsync(IList<ChatMessage> messages, CancellationToken ct = default)

// After:
public async Task<string> RunTurnAsync(string input, CancellationToken ct = default)
```

The first thing the method does is append the user input through the store:

```csharp
public async Task<string> RunTurnAsync(string input, CancellationToken ct = default)
{
    _store.AppendUserMessage(input);

    for (var iteration = 1; iteration <= _maxIterations; iteration++)
    {
        // ...
    }
}
```

### 2.3: Replace every direct `messages` reference with a store call

The `messages` parameter is gone, so every local reference to it has to be rewritten. There are four sites: three inside the loop (one read, two writes) and one in the iteration-cap fallback after the loop. The snapshot handed to the model:

```csharp
// Before:
var response = await _chatClient.GetResponseAsync(messages, _options, ct);

// After:
var response = await _chatClient.GetResponseAsync(_store.Messages, _options, ct);
```

(`GetResponseAsync` accepts `IEnumerable<ChatMessage>`, and `IReadOnlyList<ChatMessage>` satisfies that. No adapter needed.)

The model response:

```csharp
// Before:
foreach (var responseMessage in response.Messages)
{
    messages.Add(responseMessage);
}

// After:
_store.AppendResponseMessages(response.Messages);
```

The tool result:

```csharp
// Before:
messages.Add(new ChatMessage(ChatRole.Tool, [resultContent]));

// After:
_store.AppendToolResult(resultContent);
```

And the "last assistant text" fallback at the bottom, *after* the loop, also reads from the store. Don't miss this one. It's outside the `for` block, so a search-and-replace pass that only scans the loop body will leave it pointing at the now-deleted local:

```csharp
// Before:
var lastAssistantText = messages
    .Where(m => m.Role == ChatRole.Assistant)
    .Select(m => m.Text)
    .LastOrDefault(t => !string.IsNullOrWhiteSpace(t));

// After:
var lastAssistantText = _store.Messages
    .Where(m => m.Role == ChatRole.Assistant)
    .Select(m => m.Text)
    .LastOrDefault(t => !string.IsNullOrWhiteSpace(t));
```

After all the edits, the agent never touches a raw list. Every write is a method call on the store. Every read is `_store.Messages`. The `messages` parameter is gone from the public API.

---

## Step 3: Wire ConversationStore into Program.cs

Three discrete edits.

### 3.1: Add a using

At the top of `Program.cs`, alongside the existing `using FinanceAssistant;` line, add:

```csharp
using FinanceAssistant.Memory;
```

### 3.2: Create the store before the agent, and pass it in

Find this block:

```csharp
var chatAgent = new ChatAgent(chatClient, chatOptions);

var systemPrompt = await File.ReadAllTextAsync(
    Path.Combine(AppContext.BaseDirectory, "Prompts", "SystemPrompt.md"));
```

The construction order has to flip. The agent now depends on the store *and* on the system prompt (it seeds the store on construction), so the chain is: read prompt → build store → build agent. Reorder accordingly:

```csharp
var systemPrompt = await File.ReadAllTextAsync(
    Path.Combine(AppContext.BaseDirectory, "Prompts", "SystemPrompt.md"));

var store = new ConversationStore();
var chatAgent = new ChatAgent(chatClient, chatOptions, store, systemPrompt);
```

The store is built empty. The agent's constructor calls `_store.AppendSystemMessage(systemPrompt)` immediately, so by the time `RunTurnAsync` is called for the first time, `store.Messages[0]` is already the system message.

### 3.3: Replace the per-turn message-list logic

Find this block inside the `while` loop:

```csharp
var messages = new List<ChatMessage>
{
    new(ChatRole.System, systemPrompt),
    new(ChatRole.User, input)
};

var reply = await chatAgent.RunTurnAsync(messages);
Console.WriteLine(reply);
```

Replace it with:

```csharp
var reply = await chatAgent.RunTurnAsync(input);
Console.WriteLine(reply);
```

`Program.cs` is now out of the message business entirely. It reads input, hands it to the agent, prints the reply. The store and the agent take care of the rest.

---

## Step 4: Verify the follow-up works

From the repo root:

```bash
dotnet run --project src/FinanceAssistant
```

Try the two-question sequence:

```
> What did I spend on 2026-05-24?
[agent calls GetTransactions, returns an answer with a total]
> What was the total?
[agent answers with the total from the previous turn]
```

The second answer is the success signal. The agent reached back into the conversation history and found the relevant number from the previous response.

Try a longer thread:

```
> List my transactions on May 2026.
> Just the restaurants.
> What was the most expensive one?
```

Each follow-up should make sense in the context of the previous turns. The agent isn't doing anything clever. It's just seeing the full conversation every time, which is what humans do without thinking about it.

---

## Step 5: Optional. Watch the list grow

If you want to see episodic memory in action, drop a one-line print before each `RunTurnAsync` call in `Program.cs`:

```csharp
Console.WriteLine($"[memory] {store.Messages.Count} messages in history");
```

Run again, ask a few questions in sequence. You'll see the count climb on every turn. After ten turns with a couple of tool calls each, you'll be at thirty-plus messages. That number is also what's being sent to the model every turn. Tokens add up.

This is what motivates P4.02. Conversation history scales linearly with turn count. At some point the context window or your token budget says no. The fix is summarisation: at a configurable threshold, compress the early turns into a single system message and keep only the last few intact. Because every append in the agent goes through the store, the reducer has a single chokepoint to hook into. That's the payoff of the design we built in Step 1 and Step 2.

---

## Troubleshooting

### The agent still forgets after the change

Something is still creating a fresh message list per turn. Two places to check: (a) the only place a `List<ChatMessage>` is ever instantiated should be inside `ConversationStore`'s constructor, and (b) `ChatAgent.RunTurnAsync` takes a `string`, not an `IList<ChatMessage>`. If the old method signature is still there, your new code probably isn't being called.

### `store.Messages` shows duplicate system prompts

The agent's constructor calls `AppendSystemMessage` once. If you see two, you're either constructing the agent twice (one store, two agents) or calling `AppendSystemMessage` somewhere outside the constructor. The store should hold the system prompt exactly once, appended on agent construction.

### The first message in `_store.Messages` isn't the system prompt

Some providers reject conversations that don't start with a system role, and some downstream code (including a reducer we'll add in P4.02) assumes index 0 is the system prompt. The agent's constructor appends one before any turn runs, so this should hold. If it doesn't, check that nothing in `Program.cs` appends to the store *between* `new ConversationStore()` and `new ChatAgent(..., store, systemPrompt)`. The agent constructor is what plants the system message, and anything appended before it will end up at index 0 instead. Unrelated tool-call errors are not a symptom of this. Chase those separately.

### Compile error on `store.Messages.Add(...)` or similar

`Messages` is `IReadOnlyList<ChatMessage>`. It has no `Add`, `Clear`, or `Insert`. That's deliberate. If you find yourself reaching for one of those, you want an `Append*` method on the store instead. If the operation you need isn't covered by the existing four, add a new method on `ConversationStore` rather than widening the read-only view.

### `ChatAgent` constructor call in `Program.cs` doesn't compile

The constructor signature changed in Step 2.1 to require a `ConversationStore` *and* a `string systemPrompt`. Make sure Step 3.2 reads the prompt, instantiates the store, then constructs the agent with both. If you reordered correctly but it still doesn't compile, double-check the `using FinanceAssistant.Memory;` line in `Program.cs`.

### Conversation history grows unbounded

That's the design. Conversations grow turn over turn until the REPL exits. P4.02 fixes the unbounded part with a summarising reducer. Until then, restart the REPL when things get slow or expensive.

---

## You can now

Hold a multi-turn conversation. Ask a question. Refine it. Ask a follow-up that depends on the previous answer. The agent remembers because the message list outlives the loop iteration. And every message that lands in it does so through a single, controlled API.

You also have a place to put the next memory feature. `ConversationStore` is the seam, and unlike a leaky-list version, it's a real one: every append is a method call the store can observe or intercept. P4.02 will add summarisation behind it, and the call site in `Program.cs` won't change.

---

## Summary

You've added:

- **`Memory/ConversationStore.cs`**: a neutral message container that owns every operation on the conversation's message list. Parameterless constructor. Four append methods (system, user, response, tool result) are the only ways to grow the list. `Messages` is exposed read-only for snapshotting into `GetResponseAsync`.
- **`ChatAgent.cs` updated**: takes a `ConversationStore` and the system prompt at construction, and seeds the store with the system message in its constructor body. `RunTurnAsync(string input)` appends the user message, then drives the model/tool loop entirely through the store's API. No raw `messages.Add(...)` anywhere.
- **`Program.cs` simplified**: store is created empty, then the agent is constructed with the store and the prompt. The REPL loop is two lines: one to call the agent, one to print the reply.
- **An agent with episodic memory**: follow-up questions resolve against previous turns instead of being treated as standalone questions.

---

## What's next

P4.02 adds two things on top of `ConversationStore`. First, a `SummarizingHistoryReducer` that compresses old turns into a single system message once the list grows past a configurable threshold (default 12 turns, keep the last 4 intact). Because every append goes through the store, the reducer has one place to observe growth and decide when to compress. Second, a `RecallSessionHighlightsTool` that reaches into pre-seeded "highlights from previous sessions" through the same embeddings index P2.02 set up. The reducer is the answer to unbounded growth. The recall tool is the first taste of semantic memory: the agent reaches across sessions, not just inside one.

---

## Additional Resources

- [Microsoft.Extensions.AI: chat messages and roles](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- [Anthropic: long-context patterns and conversation management](https://www.anthropic.com/engineering/building-effective-agents)
