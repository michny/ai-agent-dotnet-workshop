using FinanceAssistant.Memory;
using Microsoft.Extensions.AI;

namespace FinanceAssistant;

public class ChatAgent(IChatClient chatClient, ChatOptions options, ConversationStore conversationStore, SummarizingHistoryReducer? reducer = null, int maxIterations = 8)
{
    public async Task<string> RunTurnAsync(string userInput, CancellationToken ct = default)
    {
        conversationStore.AppendUserMessage(userInput);

        if (reducer is not null)
        {
            await reducer.TryReduceAsync(conversationStore, ct);
        }

        for (var iteration = 1; iteration <= maxIterations; iteration++)
        {
            var response = await chatClient.GetResponseAsync(conversationStore.GetMessages(), options, ct);

            // The response carries the assistant's reply (which may include tool-call requests).
            // Append it to history so the tool-result messages we add below pair correctly with
            // the model's call requests on the next GetResponseAsync.
            foreach (var responseMessage in response.Messages)
            {
                conversationStore.AppendResponseMessages([responseMessage]);
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
                var function = options.Tools?
                    .OfType<AIFunction>()
                    .FirstOrDefault(f => f.Name == call.Name);

                if (function is null)
                {
                    conversationStore.AppendToolMessage(new FunctionResultContent(
                        call.CallId,
                        $"Tool '{call.Name}' is not registered."));
                    continue;
                }

                if (function is ApprovalRequiredAIFunction && !ConfirmInteractive(call))
                {
                    conversationStore.AppendToolMessage(new FunctionResultContent(
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

                conversationStore.AppendToolMessage(resultContent);
            }

            if (conversationStore.ConsumeWasCleared())
            {
                return "Conversation cleared.";
            }
        }

        // Hit the iteration cap. The model is probably stuck in a tool-call loop.
        // Return whatever the last assistant text was, or a fallback message.
        Console.Error.WriteLine($"[agent] iteration cap of {maxIterations} hit. The agent looped on tool calls without producing a final answer.");
        var lastAssistantText = conversationStore.GetMessages()
            .Where(m => m.Role == ChatRole.Assistant)
            .Select(m => m.Text)
            .LastOrDefault(t => !string.IsNullOrWhiteSpace(t));
        return lastAssistantText ?? "(no final answer, iteration cap reached)";
    }

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
}
