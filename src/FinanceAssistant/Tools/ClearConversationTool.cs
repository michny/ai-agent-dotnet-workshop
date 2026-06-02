using System.ComponentModel;
using FinanceAssistant.Memory;
using Microsoft.Extensions.AI;

namespace FinanceAssistant.Tools;

public class ClearConversationTool : AITool
{
    private readonly ConversationStore _conversationStore;

    public ClearConversationTool(ConversationStore conversationStore)
    {
        _conversationStore = conversationStore;
    }

    [Description("Clears the conversation history. Use this if the assistant is stuck in a loop or you want to start a new topic. The system prompt will be retained.")]
    public void ClearConversation()
    {
        _conversationStore.ClearConversation();
    }
}