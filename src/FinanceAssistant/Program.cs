using FinanceAssistant.Data;
using Microsoft.Extensions.Configuration;
using FinanceAssistant;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using FinanceAssistant.Tools;
using FinanceAssistant.Services;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using FinanceAssistant.Memory;

await using (var db = new FinanceDbContext())
{
    await db.Database.EnsureCreatedAsync();
}

var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();
services.AddChatClient(config);
services.AddEmbeddingGenerator(config);
var provider = services.BuildServiceProvider();

var chatClient = provider.GetRequiredService<IChatClient>();
var embedder = provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
var conversationStore = new ConversationStore();
var summarizingHistoryReducer = new SummarizingHistoryReducer(chatClient);

// Bootstraps the tool. In a real system, tools would typically be registered in DI and injected where needed.
var convertCurrency = new ConvertCurrencyTool();
var getTransactions = new GetTransactionsTool();
var searchTransactions = new SearchTransactionsTool(embedder);
var importStatement = new ImportStatementTool();
var clearConversation = new ClearConversationTool(conversationStore);
var chatOptions = new ChatOptions
{
    Tools = [
        AIFunctionFactory.Create(convertCurrency.Convert),
        AIFunctionFactory.Create(convertCurrency.GetSupportedCurrencies),
        AIFunctionFactory.Create(getTransactions.GetTransactions),
        AIFunctionFactory.Create(searchTransactions.SearchTransactions),
        AIFunctionFactory.Create(importStatement.ImportTransactionsFromCsv),
        AIFunctionFactory.Create(clearConversation.ClearConversation)
    ],
};

var systemPrompt = await File.ReadAllTextAsync(
    Path.Combine(AppContext.BaseDirectory, "Prompts", "SystemPrompt.md"));
conversationStore.AppendSystemMessage(systemPrompt);
var chatAgent = new ChatAgent(chatClient, chatOptions, conversationStore, summarizingHistoryReducer);

await using (var db = new FinanceDbContext())
{
    var unembedded = await db.Transactions
        .Where(t => t.Embedding == null)
        .ToListAsync();

    if (unembedded.Count > 0)
    {
        Console.WriteLine($"Embedding {unembedded.Count} transactions...");
        var texts = unembedded.Select(t => $"{t.Merchant} {t.Description}").ToList();
        var embeddings = await embedder.GenerateAsync(texts);
        for (int i = 0; i < unembedded.Count; i++)
        {
            unembedded[i].Embedding = new Vector(embeddings[i].Vector.ToArray());
        }
        await db.SaveChangesAsync();
        Console.WriteLine($"Embedded {unembedded.Count} transactions.");
    }
}

Console.WriteLine("Finance assistant. Type a message, or 'exit' to quit.");

while (true)
{
    Console.Write("> ");
    var input = Console.ReadLine();
    if (input is null || string.Equals(input.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    Console.WriteLine($"[memory] {conversationStore.GetMessages().Count} messages in history");
    var reply = await chatAgent.RunTurnAsync(input);
    Console.WriteLine(reply);
}

return 0;
