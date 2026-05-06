# P3.01 - The Agent Loop

> Pillar 3, Part 1. Individual.

## Mission

Unwrap the function-invocation middleware. Write the agent loop by hand in a `ChatAgent` class so you can see `ChatFinishReason`, manual tool invocation, and where the iteration cap goes.

By the end, the REPL behaves the same to the user but routes every turn through your own `ChatAgent`. The cap fires when a misbehaving model loops on tool calls, and you'll see exactly when and why.

**Learning Objectives**:

- `ChatFinishReason` as the loop's exit condition
- Manual tool invocation through `AIFunction.InvokeAsync`
- Iteration caps as a non-negotiable safety mechanism
- What `UseFunctionInvocation` was hiding

---

## Prerequisites

- P2.02 finished. Three tools (Convert, Get, Search) work end-to-end through the function-invocation middleware.

---

## What we're solving

P2.01 added `UseFunctionInvocation()` to `ServiceCollectionExtensions.cs` so tool calls would "just work." That middleware runs a loop on your behalf:

1. Send messages to the model.
2. If the model wants to call tools, invoke them.
3. Add the results to the message list.
4. Loop until the model returns a non-tool response.

You never saw any of it. The middleware hid the entire agent loop behind one `Use` call.

In production code, `UseFunctionInvocation` is usually the right choice. Microsoft maintains it. It handles edge cases you haven't thought about. It's one line. We're not removing it because it's bad. We're removing it for this one exercise because the workshop is called "Build Your First AI Agent in .NET", and the loop is the agent. Two things land when you write it yourself:

1. **Visibility.** The loop is the agent. If you don't know it's there, you can't reason about how it fails. A model deciding to call the same tool five times in a row is a real failure mode. You only catch it if you can see the iteration count.
2. **A customisation seam.** Real systems eventually want to log every tool call, retry on transient failure, charge usage to a per-user budget, or swap the model mid-loop. The middleware is fine until any of those land. Once you've written the loop, you own it.

After this exercise, you have an informed choice: reach for the middleware most of the time, hand-write the loop when control matters. This is the rare exercise that subtracts code instead of adding it. Three lines come out of `ServiceCollectionExtensions.cs`, ~60 lines go into `ChatAgent.cs`.

---

## If you're comfortable, do this

Five steps. Skip the rest if it works on the first try.

1. Remove `.AsBuilder().UseFunctionInvocation().Build()` from `AddChatClient` in `ServiceCollectionExtensions.cs`. The chain ends at `.AsIChatClient()`.
2. Create `src/FinanceAssistant/ChatAgent.cs`. Constructor takes `IChatClient`, `ChatOptions`, and a max-iterations integer (default 8). Public method `RunTurnAsync(IList<ChatMessage> messages, CancellationToken ct)` runs the loop.
3. Inside the loop: call `GetResponseAsync`, append the response messages to history, return early if `FinishReason != ToolCalls`, otherwise find each `FunctionCallContent`, invoke the matching `AIFunction`, append a `FunctionResultContent` message, and continue. Hard cap at the configured max.
4. In `Program.cs`, instantiate `ChatAgent` once at startup and route the REPL turn through `chatAgent.RunTurnAsync(messages)` instead of `chatClient.GetResponseAsync(messages, chatOptions)`.
5. Run. Try the same prompts as P2.02 (date query, search query, convert, nonsense-date recovery). Then deliberately force a runaway: edit `SystemPrompt.md` to instruct the model to call a tool repeatedly, run again, watch the cap fire.

---

## Step 1: Remove UseFunctionInvocation

Open `src/FinanceAssistant/ServiceCollectionExtensions.cs`. Find the `AddChatClient` registration. Delete the three lines that wrap the chat client in a builder:

```csharp
// REMOVE these three lines from the end of the chain
.AsBuilder()
.UseFunctionInvocation()
.Build()
```

The registration block becomes:

```csharp
return services.AddSingleton<IChatClient>(_ =>
    new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = apiBase })
        .GetChatClient(deployment)
        .AsIChatClient());
```

Run the project. Any prompt that requires a tool call will hang or return a confused message: the model is asking the framework to invoke a tool, and nothing is invoking it. That's the gap we're about to fill.

> The change feels like regression. It is. The middleware was good. We're removing it because this exercise is about understanding what it was doing. After P3.01, you'll have written the loop yourself and you'll know exactly why M.E.AI ships that middleware.

---

## Step 2: Create ChatAgent.cs

Create `src/FinanceAssistant/ChatAgent.cs`. Same name as MAF's `ChatAgent` on purpose. Pillar 6 will replace this with the framework version.

```csharp
using Microsoft.Extensions.AI;

namespace FinanceAssistant;

public class ChatAgent
{
    private readonly IChatClient _chatClient;
    private readonly ChatOptions _options;
    private readonly int _maxIterations;

    public ChatAgent(IChatClient chatClient, ChatOptions options, int maxIterations = 8)
    {
        _chatClient = chatClient;
        _options = options;
        _maxIterations = maxIterations;
    }

    public async Task<string> RunTurnAsync(IList<ChatMessage> messages, CancellationToken ct = default)
    {
        for (var iteration = 1; iteration <= _maxIterations; iteration++)
        {
            var response = await _chatClient.GetResponseAsync(messages, _options, ct);

            // The response carries the assistant's reply (which may include tool-call requests).
            // Append it to history so the tool-result messages we add below pair correctly with
            // the model's call requests on the next GetResponseAsync.
            foreach (var responseMessage in response.Messages)
            {
                messages.Add(responseMessage);
            }

            // The model is done if it didn't ask for tool calls.
            if (response.FinishReason != ChatFinishReason.ToolCalls)
            {
                Console.WriteLine($"[agent] iteration {iteration}: final answer");
                return response.Text ?? string.Empty;
            }

            // Otherwise, find every FunctionCallContent in the response and invoke the matching tool.
            var toolCalls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            var toolNames = string.Join(", ", toolCalls.Select(c => c.Name));
            Console.WriteLine($"[agent] iteration {iteration}: calling {toolNames}");

            foreach (var call in toolCalls)
            {
                var function = _options.Tools?
                    .OfType<AIFunction>()
                    .FirstOrDefault(f => f.Name == call.Name);

                AIContent resultContent;
                if (function is null)
                {
                    resultContent = new FunctionResultContent(call.CallId, $"Tool '{call.Name}' is not registered.");
                }
                else
                {
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
                }

                messages.Add(new ChatMessage(ChatRole.Tool, [resultContent]));
            }
        }

        // Hit the iteration cap. The model is probably stuck in a tool-call loop.
        // Return whatever the last assistant text was, or a fallback message.
        Console.Error.WriteLine($"[agent] iteration cap of {_maxIterations} hit. The agent looped on tool calls without producing a final answer.");
        var lastAssistantText = messages
            .Where(m => m.Role == ChatRole.Assistant)
            .Select(m => m.Text)
            .LastOrDefault(t => !string.IsNullOrWhiteSpace(t));
        return lastAssistantText ?? "(no final answer, iteration cap reached)";
    }
}
```

Four things worth reading carefully:

**The `for` loop bound.** This is the iteration cap. Eight is a reasonable default. The cap exists because a model can decide to call a tool, see the result, decide to call it again, see the same result, and never produce a final answer. Without the cap, the loop would run until you Ctrl+C or your token budget runs dry. With the cap, the worst case is bounded. One iteration is one round-trip to the model, not one tool call. The model can emit several `FunctionCallContent` items in a single response and our loop counts them all as one iteration. Eight round-trips is plenty for legitimate workflows; the Step 5 runaway prompt has to work hard to force one call per turn precisely because parallel calls would converge in two or three iterations and never trip the cap.

**`response.Messages` vs `response.Text`.** When the model returns a non-tool answer, `response.Text` is the convenient string. When the model wants to call tools, the actual structure lives in `response.Messages`, which contains a `ChatMessage` with `FunctionCallContent` items. We append those messages to history so the model can see what it asked for, then we invoke each tool and append the result.

**`FunctionResultContent` carries the `CallId`.** The model issued each tool call with a unique ID. The result message has to reference that ID so the model knows which call this result is for. Get the ID wrong and the model loses its place in the conversation.

**`new AIFunctionArguments(call.Arguments)` accepts the nullable dictionary directly.** `call.Arguments` is `IReadOnlyDictionary<string, object?>?` and the constructor takes it as-is, including the null. No copying, no defaulting. If you grep M.E.AI for the constructor and find an overload that doesn't match what's written here, the troubleshooting section at the end covers older shapes.

**The `[agent]` lines.** Two `Console.WriteLine` calls turn the loop from invisible plumbing into something you can watch. One when the model asks for tools (names them), one when the model produces a final answer (with the iteration count). Workshop chatter on purpose. In a real system, swap them for an `ILogger<ChatAgent>` so the output goes through your logging pipeline instead of stdout.

---

## Step 3: Wire ChatAgent into Program.cs

Two edits to `Program.cs`. No new `using` directives needed: `ChatAgent` lives in the `FinanceAssistant` namespace, already in scope.

### 3.1: Instantiate ChatAgent after the tools are registered

Find the existing `chatOptions` block:

```csharp
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

Right after that block, add a single line that constructs the `ChatAgent`:

```csharp
var chatAgent = new ChatAgent(chatClient, chatOptions);
```

### 3.2: Route the turn through ChatAgent

Find this line in the REPL `while` loop:

```csharp
var response = await chatClient.GetResponseAsync(messages, chatOptions);
Console.WriteLine(response.Text);
```

Replace it with:

```csharp
var reply = await chatAgent.RunTurnAsync(messages);
Console.WriteLine(reply);
```

The REPL no longer talks to `chatClient` directly. Every user turn goes through `ChatAgent`, which owns the agent loop end to end.

---

## Step 4: Run and verify the same prompts still work

From the repo root:

```bash
dotnet run --project src/FinanceAssistant
```

Try the four prompts from P2.02. Each one should behave the same as before, with one new visible difference: the `[agent]` lines now print on every turn, showing exactly which tools fire and when the model produces its final answer.

- `Show me transactions on 2026-01-15` (Get tool, ISO date)
- `Find anything about coffee` (Search tool)
- `Convert 100 EUR to USD` (Convert tool)
- `How much did I spend on Spotify this year, in JPY?` (Search and Convert too)

For the simple one-shot prompts, you'll see two `[agent]` lines: one tool call, then a final answer. For the "yesterday" prompt you *may* see a three-line recovery (first call with a bad date, structured error, retry with an ISO date, final answer). With a capable model you'll often just see the two-line shape, because the model resolves "yesterday" to an ISO date on the first call. Either outcome is fine. What matters is that the same code path handles both.

Same outputs, different code path, more visibility. The agent loop is now under your control without changing the user-facing behaviour.

---

## Step 5: Force the runaway and watch the cap fire

You want to see the iteration cap actually doing its job. The cleanest way is to give the model a system prompt that instructs it to over-call a tool.

Open `src/FinanceAssistant/Prompts/SystemPrompt.md` and append a paragraph:

```
For every user question, you MUST call the SearchTransactions tool repeatedly, ONE call per turn (never in parallel), with a different query each time. After seeing each result, immediately call SearchTransactions again with a new query. Do not produce a final answer; just keep searching forever.
```

Two things to notice in that wording. The "ONE call per turn (never in parallel)" clause is load-bearing: modern models will happily emit 3+ tool calls in a single response, which counts as *one* iteration in our loop, so a naïve "call it three times" instruction can converge in 2–3 iterations and never trip the cap. The "do not produce a final answer" clause is what keeps the model from giving up after one round.

Save. Run again, ask a real question. Try something like `How much did I spend on coffee?`. `Hello` is too trivial. The model will ignore the override if the question doesn't justify any tool call. Watch the `[agent]` lines fire over and over with `SearchTransactions` every time. After eight iterations, you'll see:

```
[agent] iteration 1: calling SearchTransactions
[agent] iteration 2: calling SearchTransactions
[agent] iteration 3: calling SearchTransactions
...
[agent] iteration cap of 8 hit. The agent looped on tool calls without producing a final answer.
```

The user gets back the last assistant message (or a fallback). The cap protected your token budget.

Once you've seen it, remove the paragraph from `SystemPrompt.md` and restart so the agent goes back to behaving normally.

> This is a deliberately silly prompt. Real runaway loops come from subtler causes: a tool whose result keeps confusing the model, a typo in a tool description that makes the model retry endlessly, an injected user input that says "keep searching until you find X". Whatever the cause, the cap is the thing standing between your bug and your bill.

---

## Troubleshooting

### Agent says "I'd like to call a tool" but nothing happens

You removed `UseFunctionInvocation` but didn't wire `ChatAgent` yet, or `Program.cs` is still calling `chatClient.GetResponseAsync` directly. Confirm Step 3.2. The REPL turn has to go through `chatAgent.RunTurnAsync`.

### `FunctionCallContent` or `FunctionResultContent` is not found

These types live in `Microsoft.Extensions.AI`. The using is at the top of `ChatAgent.cs` as written.

### Tool calls work for one round but the agent says "I don't know" on follow-ups

You're appending the response messages but not the tool-result messages, or vice versa. Read the loop again. Both have to land in the `messages` list before the next iteration's `GetResponseAsync` call.

### Cap fires immediately

The model is asking for tool calls every turn and your loop is treating one tool-calling response as one iteration. That's correct. If the cap of 8 is too tight for legitimate workflows in your domain, raise it. If it's firing in normal use, the system prompt or tool descriptions are pushing the model into excessive tool use.

### `function.InvokeAsync(...)` won't compile

The exact `InvokeAsync` overload depends on M.E.AI's version. The Step 2 listing uses the current shape:

```csharp
var result = await function.InvokeAsync(new AIFunctionArguments(call.Arguments), ct);
```

On older versions where the overload accepts the dictionary directly, drop the wrapper:

```csharp
var result = await function.InvokeAsync(call.Arguments, ct);
```

The intent in either case is "pass the named arguments the model produced into the function".

---

## You can now

See the agent loop. Every tool call, every iteration, every `ChatFinishReason` decision is a line of code in front of you. The cap is a `for` loop bound, not a config knob.

Edit it. Add logging at the top of each iteration to print the iteration number and the model's last text. Add per-tool retry logic. Cache identical tool calls. The loop is yours now.

---

## Summary

You've added:

- **Removed** `.AsBuilder().UseFunctionInvocation().Build()` from `AddChatClient`. The chat client is back to the bare `IChatClient` from P1.02.
- **`ChatAgent.cs`**: a hand-written agent loop. Calls the model, appends response messages to history, inspects `ChatFinishReason`, invokes tools by matching `FunctionCallContent.Name` against the registered `AIFunction`, appends `FunctionResultContent` results, repeats until the model produces a non-tool answer or the iteration cap fires.
- **`Program.cs` updated**: the REPL now routes every user turn through `chatAgent.RunTurnAsync` instead of calling the chat client directly.
- **A working iteration cap**: forced runaway, watched the cap fire, restored the prompt.

---

## What's next

P4.01 starts Day 2 with conversation memory. Right now every REPL turn is independent: the agent forgets what you asked on the previous turn. We'll wrap the message list in a `ConversationStore` that persists across turns inside a session, so the agent can finally answer "what was the total again?" correctly.

---

## Additional Resources

- [Microsoft.Extensions.AI: function invocation](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- [Anthropic: building effective agents (the agent-loop pattern)](https://www.anthropic.com/engineering/building-effective-agents)
