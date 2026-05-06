# P1.02 - Wire IChatClient

> Pillar 1, Part 2. Individual.

## Mission

Add the AI packages, register `IChatClient` in a DI container, and replace the echo line in `Program.cs` with a real call to your `gpt-4.1-mini` deployment.

By the end, typing "hello" in the REPL prints a sentence from your Azure deployment.

**Learning Objectives**:

- `IChatClient` as the abstraction over chat providers
- DI registration of an LLM client through a `ServiceCollection`
- Calling Azure OpenAI through the OpenAI SDK's v1-compatible endpoint

---

## Prerequisites

- P1.01 finished (Azure resource, three secrets in `dotnet user-secrets`, Foundry playground replied to "hello")
- The Postgres + pgvector container is running. From the repo root: `docker compose up -d`. Verify with `docker compose ps`.
- The starter's REPL still echoes (you haven't done P1.02 yet)

---

## What we're solving

The starter has a REPL that does nothing useful. It reads input, echoes it back. There's no model behind it.

Three things have to land before "hello" gets a real answer:

1. **The packages** that expose `IChatClient` and the OpenAI client implementation.
2. **A registration** that tells DI how to construct an `IChatClient` from your Azure secrets.
3. **A real call inside the loop**: build a list of messages, send it to the chat client, print the response.

---

## If you're comfortable, do this

Four steps. Skip the rest if it works on the first try.

1. Add three package references to `FinanceAssistant.csproj`: `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI`, `Microsoft.Extensions.DependencyInjection`.
2. Create `src/FinanceAssistant/ServiceCollectionExtensions.cs` with an `AddChatClient` extension method that registers `IChatClient` against your Azure deployment via the OpenAI SDK's Azure v1 endpoint.
3. Update `Program.cs`: add three `using` lines, build a `ServiceCollection`, resolve `IChatClient`, replace the echo with a real call (system prompt plus user input, into `GetResponseAsync`, print the text).
4. Run. Type "hello". Confirm a sentence from your model.

---

## Step 1: Add the AI packages

Open `src/FinanceAssistant/FinanceAssistant.csproj`. Inside the existing `<ItemGroup>` that already lists `CsvHelper`, the EF Core packages, and the configuration packages, add three more lines:

```xml
<PackageReference Include="Microsoft.Extensions.AI" Version="10.5.2" />
<PackageReference Include="Microsoft.Extensions.AI.OpenAI" Version="10.5.2" />
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.7" />
```

Verify:

```bash
dotnet build
```

Should still build with zero errors. The OpenAI SDK comes in transitively through `Microsoft.Extensions.AI.OpenAI`. You don't need to reference it directly.

---

## Step 2: Create ServiceCollectionExtensions.cs

`ServiceCollectionExtensions.cs` holds a static class with an extension method on `IServiceCollection`. The method registers `IChatClient` against your Azure deployment.

Create `src/FinanceAssistant/ServiceCollectionExtensions.cs`:

```csharp
using System.ClientModel;
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
}
```

> **Why `AddSingleton` and not `AddScoped`?** The chat client is a thin wrapper over an `HttpClient` that is thread-safe and stateless. Every request carries its own messages, so there's nothing per-request to keep separate. Constructing a new client (and its socket pool) for each turn would be wasteful. In ASP.NET apps you'll often see `AddScoped` as the default for "things created per request", but for an HTTP client like this, singleton is the right call.

---

## Step 3: Update Program.cs

Three discrete edits on top of the existing file. The starting state has a `using FinanceAssistant.Data;` line, a `using Microsoft.Extensions.Configuration;` line, the EF Core block, the `ConfigurationBuilder` block, and an echo loop.

### 3.1: Add three usings

At the top of `Program.cs`, alongside the two `using` lines already there, add:

```csharp
using FinanceAssistant;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
```

### 3.2: Build the service collection and resolve IChatClient

Right after the `var config = new ConfigurationBuilder()...Build();` block, before the `Console.WriteLine("Finance assistant. ...")` line, add this block:

```csharp
var services = new ServiceCollection();
services.AddChatClient(config);
var provider = services.BuildServiceProvider();

var chatClient = provider.GetRequiredService<IChatClient>();

var systemPrompt = await File.ReadAllTextAsync(
    Path.Combine(AppContext.BaseDirectory, "Prompts", "SystemPrompt.md"));
```

> `AddChatClient` is the only line in `Program.cs` that knows about the AI registration. Everything below uses the `IChatClient` abstraction. If you ever want to put the chat client behind a different provider, the change lives in `ServiceCollectionExtensions.cs`, not here.

### 3.3: Replace the echo line with a real call

Find the `Console.WriteLine($"(echo) {input}");` line inside the `while` loop. Replace it with:

```csharp
var messages = new List<ChatMessage>
{
    new(ChatRole.System, systemPrompt),
    new(ChatRole.User, input)
};

var response = await chatClient.GetResponseAsync(messages);
Console.WriteLine(response.Text);
```

The full `while` block now reads:

```csharp
while (true)
{
    Console.Write("> ");
    var input = Console.ReadLine();
    if (input is null || string.Equals(input.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    var messages = new List<ChatMessage>
    {
        new(ChatRole.System, systemPrompt),
        new(ChatRole.User, input)
    };

    var response = await chatClient.GetResponseAsync(messages);
    Console.WriteLine(response.Text);
}
```

> `response.Text` is the convenient accessor for the assistant's reply. There's also `response.Messages` if you want the full structured reply, but for this exercise the text is what we want.

---

## Step 4: Run

From the repo root:

```bash
dotnet run --project src/FinanceAssistant
```

Type `hello`. You should get a sentence back from your `gpt-4.1-mini` deployment. Type `exit` to quit.

If you do, the wiring is correct.

---

## Troubleshooting

### `Missing AzureOpenAI:...` thrown at startup

Your secrets aren't being read. You want to confirm the three Azure keys exist without printing their values into your terminal (handy if you're screen-sharing or pasting output into chat).

From `src/FinanceAssistant/`, run one of these.

On macOS/Linux (bash/zsh):

```bash
# Just the key names - values are masked.
dotnet user-secrets list | awk -F' = ' '{print $1}'

# Or: just count that all three keys are present.
dotnet user-secrets list | grep -c '^AzureOpenAI:'   # expect 3
```

On Windows (PowerShell):

```powershell
# Just the key names - values are masked.
dotnet user-secrets list | ForEach-Object { ($_ -split ' = ')[0] }

# Or: just count that all three keys are present.
(dotnet user-secrets list | Select-String '^AzureOpenAI:').Count   # expect 3
```

If you see fewer than three `AzureOpenAI:*` keys, you set them in the wrong folder (the user-secrets store is keyed off the project's `UserSecretsId`). Re-run the `dotnet user-secrets set` commands from `src/FinanceAssistant/`.

If a value looks wrong, prefer re-setting it with `dotnet user-secrets set` over reading it back with `dotnet user-secrets get` (the `get` command prints the secret in plaintext).

### `401 Unauthorized` from Azure

Either your key is wrong or your endpoint is wrong. Both come from the same resource's "Keys and Endpoint" pane in the Azure portal. Re-copy together.

### `404 Not Found` from Azure

Two flavours of this. First, the deployment name doesn't match. Compare your stored deployment value against the name in Foundry's "Models + endpoints", character-for-character. To print just that one secret without dumping the rest:

```bash
# macOS/Linux
dotnet user-secrets list | grep '^AzureOpenAI:Deployment'
```

```powershell
# Windows (PowerShell)
dotnet user-secrets list | Select-String '^AzureOpenAI:Deployment'
```

Second, the endpoint URL is missing a trailing slash and the SDK has constructed something like `https://your-resource.openai.azure.comopenai/v1/chat/completions`. Re-set the secret with a trailing slash.

### `AsIChatClient()` is not found

The `Microsoft.Extensions.AI.OpenAI` package isn't pulled in. Confirm `FinanceAssistant.csproj` references it, then run `dotnet clean` and `dotnet build` again.

### `OpenAIClient`, `ApiKeyCredential`, or `OpenAIClientOptions` is not found

`Microsoft.Extensions.AI.OpenAI` brings the OpenAI SDK in transitively, but the IDE may need `using OpenAI;` and `using System.ClientModel;` to see them. Both are at the top of `ServiceCollectionExtensions.cs` as written above.

---

## You can now

Type questions into the REPL and get real responses from `gpt-4.1-mini`.

The model has no tools yet, so factual questions about your finances will produce guesses. The system prompt that shapes the assistant's tone lives in `Prompts/SystemPrompt.md`. Edit that file, restart the REPL, and watch the assistant's personality shift. That's prompt-as-a-workflow in its smallest form.

---

## Summary

You've added:

- **Three packages**: `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI`, and `Microsoft.Extensions.DependencyInjection`.
- **`ServiceCollectionExtensions.cs`**: an extension method that registers `IChatClient` against your Azure deployment via the OpenAI SDK's Azure v1 endpoint.
- **Updated `Program.cs`**: three new `using`s, a `ServiceCollection`, the DI resolution, the `IChatClient` call inside the loop.
- **A working agent**: type a question, get a real reply.

---

## What's next

P2.01 is your first tool: a `ConvertCurrency` function with a hardcoded rate table. No DB, no embeddings. The point is to feel what `[Description]` quality means before the bigger transactions exercise lands later in the afternoon.

---

## Additional Resources

- [Microsoft.Extensions.AI documentation](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- [OpenAI .NET SDK on GitHub](https://github.com/openai/openai-dotnet)
- [Azure OpenAI v1 API surface](https://learn.microsoft.com/en-us/azure/ai-services/openai/api-version-deprecation)
