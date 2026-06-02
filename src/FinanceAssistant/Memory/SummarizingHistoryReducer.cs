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
        if (store.GetMessages().Count <= _threshold)
        {
            return false;
        }

        // The head we're about to summarise: everything except the system prompt and the kept tail.
        var headCount = store.GetMessages().Count - _keepTailCount - 1;
        var headToSummarise = store.GetMessages()
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