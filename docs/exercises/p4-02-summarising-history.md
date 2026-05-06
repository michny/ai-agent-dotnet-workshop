# P4.02 - Summarising History Reducer

> Pillar 4, Part 2. Individual.

## Mission

Compress old turns into a single summary message when the conversation grows past a threshold, so the agent stops burning tokens on history that doesn't earn its keep. By the end, a long conversation has its early turns collapsed into one system-role summary, the recent turns kept intact, and the model sees a bounded message list every turn.

**Learning Objectives**:

- Conversation history is a token bill. Unbounded growth is unbounded cost.
- Summarisation as the standard compression pattern (collapse the head, keep the tail).
- Why `ConversationStore` being the single chokepoint pays off here.

---

## Prerequisites

- P4.01 finished. `ConversationStore` owns every message write. `ChatAgent` routes every turn through the store. The optional `[memory]` line you may have dropped in shows the count climbing turn over turn.

---

## What we're solving

P4.01's Step 5 callout flagged the problem already: conversation history scales linearly with turn count. Every turn the model sees the entire conversation, every turn you pay for those tokens, every turn the model has more text to spread attention across. Eventually the context window says no. Long before that, your token bill says ouch.

There's a standard answer. When the conversation gets long, summarise the early turns into a single concise message and drop the originals. Keep the last few turns intact so the model still has fresh context. The compressed history goes back into the message list as a system-role "Conversation summary so far: ..." entry that lives where the old turns used to sit.

We're going to wrap that pattern in a `SummarizingHistoryReducer`. The reducer:

1. Checks if the store's message count exceeds a threshold (default 12).
2. If yes, takes everything except the system prompt and the last N tail messages (default 4).
3. Asks the model to summarise that middle section.
4. Tells the store to replace its contents with: original system prompt, the new summary as a system message, the kept tail.

The reducer hooks into `ConversationStore` through a single new method. The hook is the payoff of the design from P4.01: because every message write already goes through the store, the reducer has one chokepoint to insert at, and `Program.cs` doesn't need to know anything new.

> The summary is itself a model call. You're trading "N tokens of full history every turn forever" for "one occasional model call plus a smaller history every turn". Whether that maths out cheaper depends on turn length, message size, and how often the reducer fires. For long sessions with chatty tool results, summarisation is a clear win. For five-turn conversations that never hit the threshold, the reducer never fires and you pay nothing.

---

## If you're comfortable, do this

Five steps. Skip the rest if it works on the first try.

1. Add a `Compact(string summary, int keepTailCount)` method to `ConversationStore`. It rebuilds the message list as: original system prompt, a new system-role "Conversation summary so far: ..." message, the last N original messages.
2. Create `src/FinanceAssistant/Memory/SummarizingHistoryReducer.cs`. Constructor takes `IChatClient`, threshold (default 12), keep-tail (default 4). Public method `TryReduceAsync(ConversationStore store, CancellationToken ct = default)` returns `bool`.
3. Update `ChatAgent` to accept an optional `SummarizingHistoryReducer` and call its `TryReduceAsync` right after appending the user message, before the loop.
4. In `Program.cs`, instantiate the reducer and pass it into `ChatAgent`.
5. Run a long conversation (12+ turns of small talk). Watch the `[memory]` line drop after the reducer fires.

---

## Step 1: Add Compact to ConversationStore

Open `src/FinanceAssistant/Memory/ConversationStore.cs`. Add this method below the existing `Append*` methods:

```csharp
public void Compact(string summary, int keepTailCount)
{
    if (_messages.Count == 0)
    {
        return;
    }

    var systemMessage = _messages[0];
    var tail = _messages
        .Skip(Math.Max(1, _messages.Count - keepTailCount))
        .ToList();

    var summaryMessage = new ChatMessage(
        ChatRole.System,
        $"Conversation summary so far: {summary}");

    _messages.Clear();
    _messages.Add(systemMessage);
    _messages.Add(summaryMessage);
    _messages.AddRange(tail);
}
```

Two things worth noticing:

**The original system prompt stays at index 0.** Without it, the model loses the agent's persona and behaviour rules. The compaction never touches that message, regardless of how long the conversation has run.

**The summary goes in as a system role.** Not as an assistant message (because the assistant didn't say it), not as a user message (because the user didn't either). System is the right channel for "background context you should keep in mind". Some teams use a custom role or a tagged user message. System is the simplest option and works with every provider.

> `Compact` is the second method on the store that mutates the underlying list (the first being `AppendUserMessage` and friends). It's still the only path into the list. The reducer doesn't reach in directly. It calls `Compact`.

> The `Math.Max(1, _messages.Count - keepTailCount)` in the tail slice is a guard, not a calculation. If the conversation is shorter than `keepTailCount`, the naive `Skip(_messages.Count - keepTailCount)` would walk past index 0 and grab the system prompt as part of the tail. The `Max(1, ...)` floors the skip at 1 so the system message can never end up duplicated in the tail.

---

## Step 2: Create SummarizingHistoryReducer.cs

Create `src/FinanceAssistant/Memory/SummarizingHistoryReducer.cs`:

```csharp
using Microsoft.Extensions.AI;

namespace FinanceAssistant.Memory;

public class SummarizingHistoryReducer
{
    private readonly IChatClient _chatClient;
    private readonly int _threshold;
    private readonly int _keepTailCount;

    public SummarizingHistoryReducer(IChatClient chatClient, int threshold = 12, int keepTailCount = 4)
    {
        _chatClient = chatClient;
        _threshold = threshold;
        _keepTailCount = keepTailCount;
    }

    public async Task<bool> TryReduceAsync(ConversationStore store, CancellationToken ct = default)
    {
        if (store.Messages.Count <= _threshold)
        {
            return false;
        }

        // The head we're about to summarise: everything except the system prompt and the kept tail.
        var headCount = store.Messages.Count - _keepTailCount - 1;
        var headToSummarise = store.Messages
            .Skip(1)
            .Take(headCount)
            .ToList();

        if (headToSummarise.Count == 0)
        {
            return false;
        }

        var transcript = string.Join(
            "\n",
            headToSummarise.Select(m => $"[{m.Role}] {m.Text}"));

        var summaryRequest = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a conversation summariser. Output only the summary, no preamble. Preserve key facts, user preferences, decisions, numbers, and dates. Skip pleasantries."),
            new(ChatRole.User, $"Summarise this conversation in 2 to 4 sentences:\n\n{transcript}")
        };

        var response = await _chatClient.GetResponseAsync(summaryRequest, cancellationToken: ct);
        var summary = response.Text?.Trim() ?? "(summary unavailable)";

        Console.WriteLine($"[memory] reducing {headToSummarise.Count} messages into 1 summary");
        store.Compact(summary, _keepTailCount);
        return true;
    }
}
```

Three things worth reading carefully:

**The threshold and keep-tail are constructor parameters.** Defaults sit at 12 and 4 because that gives a single user-assistant-tool round of context in the tail (system prompt + summary + 4 recent messages = ~6 messages going to the model after a reduction). Tune both for your domain. Long technical discussions want a bigger tail. Quick lookups can survive with 2.

**The summariser is the same `gpt-4.1-mini` we use for everything else.** It doesn't have to be. Many production systems use a smaller, cheaper model for summarisation specifically, because the task is well-bounded and a 70B model is overkill.

**The summariser prompt is doing real work.** "Preserve key facts, user preferences, decisions, numbers, and dates. Skip pleasantries." is the instruction that decides what survives. Drop "numbers and dates" from that list and the agent will forget the totals from earlier tool calls, leaving the user confused on the next turn. Drop "user preferences" and a stated preference from turn three is gone by turn fifteen. Test the prompt on actual transcripts before you ship.

---

## Step 3: Wire the reducer into ChatAgent

Two small edits in `src/FinanceAssistant/ChatAgent.cs`.

### 3.1: Add an optional reducer to the constructor

Add a `_reducer` field and accept it as an optional constructor parameter:

```csharp
private readonly SummarizingHistoryReducer? _reducer;

public ChatAgent(
    IChatClient chatClient,
    ChatOptions options,
    ConversationStore store,
    string systemPrompt,
    SummarizingHistoryReducer? reducer = null,
    int maxIterations = 8)
{
    _chatClient = chatClient;
    _options = options;
    _store = store;
    _maxIterations = maxIterations;
    _reducer = reducer;
    _store.AppendSystemMessage(systemPrompt); // existing line from P4.01, stays
}
```

The reducer is nullable on purpose. An agent without a reducer is still a valid agent. The store just grows unbounded, which is fine for short sessions or test runs.

### 3.2: Call the reducer once per turn

At the top of `RunTurnAsync`, after appending the user message, before the iteration loop:

```csharp
public async Task<string> RunTurnAsync(string input, CancellationToken ct = default)
{
    _store.AppendUserMessage(input);

    if (_reducer is not null)
    {
        await _reducer.TryReduceAsync(_store, ct);
    }

    for (var iteration = 1; iteration <= _maxIterations; iteration++)
    {
        // ...existing loop body unchanged
    }
}
```

> The reducer runs **after** appending the user message and **before** the model call. That ordering matters. The new user message is part of "the tail we want to keep", not part of "the head we want to summarise". Reduce before appending the user message and you risk summarising a question the model hasn't answered yet.

> `TryReduceAsync` returns a `bool` that we discard here. That's fine. The return value is there in case you want to log "reduction happened on this turn" or branch on it (e.g., only persist the conversation to disk after a reduction). The agent's behaviour is the same whether it fires or not, so the call site doesn't need to read the result.

---

## Step 4: Pass the reducer in from Program.cs

In `Program.cs`, find the line that constructs the `ChatAgent`:

```csharp
var chatAgent = new ChatAgent(chatClient, chatOptions, store, systemPrompt);
```

Replace it with the two-line version that builds the reducer first:

```csharp
var reducer = new SummarizingHistoryReducer(chatClient);
var chatAgent = new ChatAgent(chatClient, chatOptions, store, systemPrompt, reducer);
```

That's the entire `Program.cs` change. No other lines move.

---

## Step 5: Force a reduction and watch it fire

If you didn't keep the optional `[memory] {store.Messages.Count} messages in history` line from P4.01 Step 5, add it back into `Program.cs` right before the `RunTurnAsync` call. It's the easiest way to see the reducer working.

Run the project:

```bash
dotnet run --project src/FinanceAssistant
```

Hold a long conversation. The fastest way to push past the threshold is small back-and-forth turns:

```
> hi
> what tools do you have?
> what is gpt-4.1-mini good for?
> can you list a few things you can help me with?
> what are some common categories in personal finance?
> ...
```

After every turn, the `[memory]` line shows the count climbing. Around turn six (sooner if the agent calls tools, since tool-call requests and tool results both land as messages) you'll cross the threshold of 12 and see:

```
[memory] 13 messages in history
[memory] reducing 8 messages into 1 summary
```

The next turn's `[memory]` line drops to six. You collapsed eight messages into one summary, and the model on the following turn sees the system prompt, the summary, the last four messages, and the new user question.

Ask a question that depends on something said in one of the summarised turns ("What did I ask first?" or "What were the categories you mentioned earlier?"). The model can still answer if the summary captured it. If it can't, the summariser prompt needs sharpening.

---

## What's actually happening inside one reducing turn

The `[memory]` line only prints between turns, so the trace hides what the reducer is doing mid-turn. Here's a full walk-through of one turn that crosses the threshold, using realistic numbers from a tool-using conversation.

**Starting state.** Previous turn ended with 15 messages in the store, so the next prompt shows `[memory] 15`. The list looks like this:

```
index │ role       │ content (abbreviated)
──────┼────────────┼────────────────────────────────────────────
  0   │ system     │ "You are a finance assistant..."
  1   │ user       │ "hello"
  2   │ assistant  │ "Hello! How can I assist..."
  3   │ user       │ "how are you?"
  4   │ assistant  │ "I'm just a program..."
  5   │ user       │ "What's the most recent transaction?"
  6   │ assistant  │ <FunctionCall GetTransactions>
  7   │ tool       │ <FunctionResult: REWE 2024-06-28 -23.33>
  8   │ assistant  │ "The most recent transaction was..."
  9   │ user       │ "Do you know the currency?"
 10   │ assistant  │ "The currency hasn't been specified..."
 11   │ user       │ "assume it's USD. Show me in eur."
 12   │ assistant  │ <FunctionCall Convert>
 13   │ tool       │ <FunctionResult: 21.21 EUR>
 14   │ assistant  │ "Assuming USD, ~21.21 EUR."
```

**The user types `"Now in cad"`. Inside `RunTurnAsync`:**

**Step 1: append user message.** Count goes 15 → 16. (You never see 16 in the trace because `[memory]` prints before this happens.)

```
 15   │ user       │ "Now in cad"          ← new
```

**Step 2: reducer runs (`16 > threshold 12`).** It slices the list into three regions:

```
                   ┌─────────────────────────────────────┐
   keep at top ──► │  0  system  (the original prompt)   │
                   ├─────────────────────────────────────┤
                   │  1  user       "hello"              │
                   │  2  assistant  "Hello!..."          │
                   │  3  user       "how are you?"       │
   summarise  ──►  │  4  assistant  "I'm just..."        │  11 messages
   into one        │  5  user       "most recent tx?"    │  →  1 summary
   summary         │  6  assistant  <call GetTx>         │
                   │  7  tool       <REWE 2024-06-28>    │
                   │  8  assistant  "most recent was..." │
                   │  9  user       "know the currency?" │
                   │ 10  assistant  "not specified..."   │
                   │ 11  user       "USD. Show me in eur"│
                   ├─────────────────────────────────────┤
                   │ 12  assistant  <call Convert>       │
   keep tail  ──►  │ 13  tool       <21.21 EUR>          │  keepTail = 4
   (last 4)        │ 14  assistant  "Assuming USD..."    │
                   │ 15  user       "Now in cad"         │
                   └─────────────────────────────────────┘
```

The summariser model is called on the middle slice and returns a 2–4 sentence summary.

**Step 3: `Compact` rebuilds the list. Count: 16 → 6.**

```
index │ role       │ content
──────┼────────────┼────────────────────────────────────────────
  0   │ system     │ "You are a finance assistant..."    ← preserved
  1   │ system     │ "Conversation summary so far: user  ← NEW
      │            │  greeted, asked for most recent     │ (summary)
      │            │  transaction (REWE -23.33 on 2024-  │
      │            │  06-28). User said assume USD..."   │
  2   │ assistant  │ <FunctionCall Convert>              ← tail
  3   │ tool       │ <FunctionResult: 21.21 EUR>         ← tail
  4   │ assistant  │ "Assuming USD, ~21.21 EUR."         ← tail
  5   │ user       │ "Now in cad"                        ← tail
```

**Step 4: the agent loop runs against this 6-message context.** The model answers, calling `Convert` once:

```
  6   │ assistant  │ <FunctionCall Convert USD→CAD>     +1
  7   │ tool       │ <FunctionResult: 31.53 CAD>        +1
  8   │ assistant  │ "~31.53 CAD."                       +1
```

End of turn: **9 messages**. The next turn opens with `[memory] 9`.

### Two things worth noticing

**The peak (16) and trough (6) never appear in the trace.** You see 15 → 9 across the turn, which understates the work. The hidden 16 → 6 reduction is where the win actually happens. If you want to see those numbers directly, move the log into `RunTurnAsync` and print it twice: once after `AppendUserMessage`, once after the reducer call.

**There are now two `system` messages.** Index 0 is the persona prompt; index 1 is the summary. That's why `Compact` is careful to preserve `_messages[0]` before rebuilding: if it overwrote index 0 with the summary, the agent would lose every behaviour rule the system prompt set, and the model on the next turn would have no idea it's a finance assistant.

### Without the reducer

The same "Now in cad" turn would have ended at **19 messages**, and every subsequent turn would carry all 19 forward into the prompt: token cost growing linearly forever. Across a 30-turn session, that's the difference between sending roughly 30 × 19 ≈ 570 message-positions to the model versus a bounded ~30 × 9 ≈ 270, plus one extra summariser call per reduction. The longer the session, the bigger the gap.

---

## Troubleshooting

### `[memory] reducing` never fires

The conversation isn't long enough. Threshold defaults to 12 messages, but every user turn adds at least two (user + assistant) and tool-calling turns add more. Keep going for a few more turns. If you want it to fire faster, lower the threshold in the constructor: `new SummarizingHistoryReducer(chatClient, threshold: 6, keepTailCount: 2)`.

### Reducer fires but the agent forgets recent context

You set `keepTailCount` too low. With `keepTailCount: 0`, the tail is gone, and only the summary survives. Bump it back to 4 or higher.

### Summary is garbage

Either the summariser prompt isn't strong enough, or the model can't compress what's there. Tighten the instruction with concrete examples of what to preserve ("preserve transaction totals, category names, and date ranges; skip greetings and acknowledgements"). If the conversation is dominated by tool-call JSON, the summariser will see ugly text. That's a sign your `[tool]` results are too verbose for human reading and might benefit from a shorter shape.

Also expect blank-looking lines in the transcript. Assistant messages that only carry tool-call requests have no `.Text` (the content is structured `FunctionCallContent`, not prose), so they serialise as `[Assistant]` with nothing after the bracket. That's normal. The summariser sees the surrounding messages and the tool results, which usually carry the information that matters.

### Agent loses persona after reduction

The system prompt at index 0 isn't being preserved. Check that `ConversationStore.Compact` keeps `_messages[0]` before clearing and rebuilding. If you accidentally replaced it with the summary, the model loses every behaviour rule the system prompt set.

### Compile error on `_reducer` access

`ChatAgent.RunTurnAsync` is calling `_reducer.TryReduceAsync` without the null check. Wrap it: `if (_reducer is not null) { await _reducer.TryReduceAsync(_store, ct); }`. The reducer is optional on purpose.

---

## You can now

Hold a conversation as long as you want. The reducer keeps the message list bounded. The agent still has the system prompt, a summary of what came before, and the recent context to reason over.

---

## Summary

You've added:

- **`ConversationStore.Compact(string summary, int keepTailCount)`**: rebuilds the message list as [system prompt, summary as system message, kept tail].
- **`Memory/SummarizingHistoryReducer.cs`**: checks the threshold, summarises the head via a model call, calls `store.Compact`. Threshold and tail size are configurable. Defaults are 12 and 4.
- **`ChatAgent` updated**: takes an optional `SummarizingHistoryReducer`. Calls `TryReduceAsync` right after appending the user message, before the loop.
- **`Program.cs` updated**: constructs the reducer and passes it into the agent.
- **A bounded conversation**: long sessions stay token-cheap and stay inside the context window.

---

## What's next

P5.01 wires a `RequiresUserConfirmation` middleware onto a destructive tool so the agent has to ask the human before doing something irreversible.

---

## Additional Resources

- [Microsoft.Extensions.AI: chat messages and roles](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- [Anthropic: long-context patterns and conversation management](https://www.anthropic.com/engineering/building-effective-agents)
