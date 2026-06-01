using System.ClientModel;
using FinanceAssistant.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;

namespace FinanceAssistant;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChatClient(this IServiceCollection services, IConfiguration config)
    {
        var endpoint = config["AzureOpenAI:Endpoint"]
            ?? throw new InvalidOperationException("Missing AzureOpenAI:Endpoint.");
        var apiKey = config["AzureOpenAI:ApiKey"]
            ?? throw new InvalidOperationException("Missing AzureOpenAI:ApiKey.");
        var deployment = config["AzureOpenAI:Deployment"]
            ?? throw new InvalidOperationException("Missing AzureOpenAI:Deployment.");

        // /openai/v1/ is the OpenAI SDK's Azure v1-compatible surface.
        var apiBase = new UriBuilder(endpoint) { Path = "openai/v1/" }.Uri;

        return services.AddSingleton<IChatClient>(_ =>
            new OpenAIClient(
                    new ApiKeyCredential(apiKey),
                    new OpenAIClientOptions { Endpoint = apiBase })
                .GetChatClient(deployment)
                .AsIChatClient());
    }

    public static IServiceCollection AddEmbeddingGenerator(this IServiceCollection services, IConfiguration config)
    {
        var endpoint = config["AzureOpenAI:Endpoint"]
            ?? throw new InvalidOperationException("Missing AzureOpenAI:Endpoint.");
        var apiKey = config["AzureOpenAI:ApiKey"]
            ?? throw new InvalidOperationException("Missing AzureOpenAI:ApiKey.");
        var embeddingDeployment = config["AzureOpenAI:EmbeddingDeployment"]
            ?? throw new InvalidOperationException("Missing AzureOpenAI:EmbeddingDeployment.");

        var apiBase = new UriBuilder(endpoint) { Path = "openai/v1/" }.Uri;

        return services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_ =>
            new OpenAIClient(
                    new ApiKeyCredential(apiKey),
                    new OpenAIClientOptions { Endpoint = apiBase })
                .GetEmbeddingClient(embeddingDeployment)
                .AsIEmbeddingGenerator());
    }
}
