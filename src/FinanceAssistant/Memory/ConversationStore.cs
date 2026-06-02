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

    public IReadOnlyList<ChatMessage> GetMessages() => _messages;
}
