using Microsoft.Extensions.AI;

namespace FinanceAssistant.Memory;

public class ConversationStore
{
    private readonly  List<ChatMessage> _messages = [];

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
    
    public void AppendToolMessage(AIContent content) 
    {
        _messages.Add(new ChatMessage(ChatRole.Tool, [content]));
    }

    private bool _wasCleared;

    public void ClearConversation()
    {
        _messages.RemoveAll(m => m.Role != ChatRole.System);
        _wasCleared = true;
    }

    public bool ConsumeWasCleared()
    {
        if (!_wasCleared) return false;
        _wasCleared = false;
        // Remove any orphaned tool messages added after the clear
        _messages.RemoveAll(m => m.Role != ChatRole.System);
        return true;
    }

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

    public IReadOnlyList<ChatMessage> GetMessages() => _messages;
}
