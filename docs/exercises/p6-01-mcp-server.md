# P6.01 - Expose Tools Over an MCP Server

> Pillar 6, Part 1. Individual.

## Mission

Take the finance tools we've spent the last two days building and serve them over the Model Context Protocol. By the end, a separate ASP.NET Core process exposes `GetTransactions` and `SearchTransactions` on `http://localhost:5050`, any MCP-aware client (Claude Code, Cursor, an inspector, another agent) can list the tools and invoke them, and the agent project keeps working exactly as before.

**Learning Objectives**:

- MCP as a transport-level contract for tools: same handler code, different audience
- `ModelContextProtocol.AspNetCore` as the .NET implementation: `AddMcpServer().WithHttpTransport().WithTools<T>()` and `app.MapMcp()`
- The `[McpServerToolType]` / `[McpServerTool]` annotation pair, and how service-type parameters resolve from DI

---

## Prerequisites

- P5.01 finished. The agent runs end-to-end with the confirmation gate on `TransferFundsTool`.
- A user-secrets entry exists for `AzureOpenAI:Endpoint`, `AzureOpenAI:ApiKey`, and `AzureOpenAI:EmbeddingDeployment` against the `finance-assistant-workshop` UserSecretsId. The agent project already uses this. The MCP server will share it.
- The `pgvector` Postgres container is running. The MCP server reads the same database as the agent.

---

## What we're solving

Everything we've built so far runs inside one process. The agent loads the tools, the agent invokes the tools, the agent returns the answer. That's a fine shape for a console REPL and a useful shape for understanding the loop. It's not the shape a real fleet of agents has.

Real fleets share tools. A "transactions" tool that knows how to read the user's data, return a date range, do a semantic search, is the kind of thing several agents in an org would want to call. Copy-pasting the code into each agent project is the worst answer. Putting the tool behind a network boundary, with a stable contract for "what tools exist", "what are their schemas", "invoke this one with these arguments", is the better one.

The Model Context Protocol is that contract. It's a JSON-RPC protocol over either stdio (for local subprocesses) or Streamable HTTP (for network services), with three core verbs the client cares about: `tools/list`, `tools/call`, and a couple for resources and prompts we'll ignore today. A server speaks MCP. A client speaks MCP. They negotiate a session, exchange capabilities, and the client calls tools the server advertises. That's the whole pattern.

The .NET implementation is `ModelContextProtocol.AspNetCore`. It plugs into the existing ASP.NET Core hosting model: register the server with DI, point it at a class of tools, map the HTTP transport, and the framework handles the protocol. Our existing tool methods (`GetTransactions`, `SearchTransactions`) become MCP tools with two attributes and one delegating wrapper.

We're going to wire it in three pieces:

1. **Project setup**: the `FinanceAssistant.McpServer` project needs the MCP package, a project reference to `FinanceAssistant` (so it can use the tools we already wrote), and the same `UserSecretsId` as the agent.
2. **A tools class**: `McpTools` is a class marked `[McpServerToolType]` with static methods. Each method is marked `[McpServerTool]` and delegates to the corresponding `FinanceAssistant.Tools.*` instance. Services like `IEmbeddingGenerator` resolve from DI through method parameters.
3. **Program.cs**: replace the placeholder with the standard ASP.NET Core boot sequence plus `AddMcpServer().WithHttpTransport().WithTools<McpTools>()` and `app.MapMcp()`.

> We expose `GetTransactions` and `SearchTransactions`. Not `ConvertCurrency` (a toy with no IO, not interesting to share) and definitely not `TransferFundsTool`. The console-prompt confirmation gate we built in P5.01 reads from `Console.ReadLine`, and an HTTP server has no console session to prompt against. A real MCP server would replace that with an elicitation request back to the client, but that's a separate exercise. The safe move is to not expose destructive tools at all until the elicitation flow is in place.

---

## If you're comfortable, do this

Five steps. Skip the rest if it works on the first try.

1. Update `src/FinanceAssistant.McpServer/FinanceAssistant.McpServer.csproj`: add `ModelContextProtocol.AspNetCore`, a `ProjectReference` to `FinanceAssistant`, and `<UserSecretsId>finance-assistant-workshop</UserSecretsId>`.
2. Create `src/FinanceAssistant.McpServer/McpTools.cs`: a class marked `[McpServerToolType]` with two static `[McpServerTool]` methods that delegate to `GetTransactionsTool` and `SearchTransactionsTool`.
3. Replace `src/FinanceAssistant.McpServer/Program.cs`: ASP.NET Core boot, user secrets, `AddEmbeddingGenerator`, `AddMcpServer().WithHttpTransport().WithTools<McpTools>()`, `app.MapMcp()`, `app.Run("http://localhost:5050")`.
4. Run the server. Confirm the listening banner. A plain `curl http://localhost:5050` returning HTTP 400 with a JSON-RPC envelope means the port is open and the route is mounted.
5. Verify with the MCP Inspector (`npx @modelcontextprotocol/inspector`). Connect over Streamable HTTP to `http://localhost:5050`. The Tools tab should list both tools; invoking `get_transactions` on a known date range should return transactions.

If you finish with time to spare, there's an optional stretch section below that adds a **prompt** and a **resource** so the server exercises all three MCP primitives.

---

## Step 1: Update the McpServer csproj

Open `src/FinanceAssistant.McpServer/FinanceAssistant.McpServer.csproj`. Three small edits.

**1.1: Add `UserSecretsId` inside the existing `<PropertyGroup>`**, on a new line after `<RootNamespace>`:

```xml
<UserSecretsId>finance-assistant-workshop</UserSecretsId>
```

**1.2: Add a new `<ItemGroup>`** for the MCP package, after the closing `</PropertyGroup>`:

```xml
<ItemGroup>
  <PackageReference Include="ModelContextProtocol.AspNetCore" Version="1.3.0" />
</ItemGroup>
```

**1.3: Add a second `<ItemGroup>`** for the project reference, after the one you just added:

```xml
<ItemGroup>
  <ProjectReference Include="..\FinanceAssistant\FinanceAssistant.csproj" />
</ItemGroup>
```

You can collapse those two `<ItemGroup>` blocks into one if you prefer; the convention in this repo is to keep package references and project references in separate groups so the diff stays readable.

Three things worth reading carefully:

**The `UserSecretsId` is the same one the agent uses.** Both projects now read from the same secret store. You set up the `AzureOpenAI:*` secrets in P1.01 against `finance-assistant-workshop`. The MCP server picks them up without you re-running `dotnet user-secrets set`.

**The project reference pulls in the tools, the DB context, the embedding extension, everything.** We aren't going to rewrite anything. The same `GetTransactionsTool` class the agent calls, the same `SearchTransactionsTool` with its embedder dependency, the same `ServiceCollectionExtensions.AddEmbeddingGenerator`. The MCP server is a new front door over the same building.

**The package version moves fast.** `ModelContextProtocol.AspNetCore` reached `1.x` in May 2026 and ships minor versions roughly monthly. We pin `1.3.0` for the workshop. If a newer minor is on NuGet by the time you read this, bump it: the public API in this guide is stable across the `1.x` line.

> No need to run `dotnet restore` here. `dotnet restore` is cache-aware and may report "All projects are up-to-date for restore" if it considers the existing `obj/project.assets.json` from the placeholder project fresh. The `dotnet run` in Step 4 does its own restore and will pull the package then.

---

## Step 2: Create McpTools.cs

Create `src/FinanceAssistant.McpServer/McpTools.cs`:

```csharp
using System.ComponentModel;
using FinanceAssistant.Tools;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace FinanceAssistant.McpServer;

[McpServerToolType]
public class McpTools
{
    [McpServerTool]
    [Description("List transactions in a given date range. The dateExpression must be ISO 8601: a single date '2026-05-06' or a range '2026-01-01..2026-01-31'. Natural language like 'yesterday' or 'last month' is not supported. Convert any relative date to ISO 8601 first.")]
    public static Task<object> GetTransactions(
        [Description("ISO 8601 date or range, e.g. '2026-01-15' for one day or '2026-01-01..2026-01-31' for a range.")] string dateExpression,
        CancellationToken ct = default)
    {
        return new GetTransactionsTool().GetTransactions(dateExpression, ct);
    }

    [McpServerTool]
    [Description("Search transactions by free-text similarity over merchant and description fields. Returns the top K matching transactions ordered by relevance. Use this when the user asks about purchases by topic, theme, or fuzzy description, like 'coffee shops', 'subscriptions I might cancel', or 'flights last quarter'.")]
    public static Task<object> SearchTransactions(
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        [Description("Free-text query. Examples: 'coffee shops in december', 'subscription cancellations', 'restaurants in Lisbon'.")] string query,
        [Description("How many top matches to return. Default 5. Maximum 20.")] int topK = 5,
        CancellationToken ct = default)
    {
        return new SearchTransactionsTool(embedder).SearchTransactions(query, topK, ct);
    }
}
```

Four things worth noticing:

**`[McpServerToolType]` marks the class so the framework knows to scan it.** Without that attribute, `WithTools<McpTools>()` would find nothing. The same scanning rule applies to `WithToolsFromAssembly()` if you'd rather discover tool classes by attribute instead of naming them. We name it explicitly. With two tools, the indirection isn't worth it.

**`[McpServerTool]` marks each method.** The method's parameters become the tool's JSON schema, and `[Description]` attributes on the method and parameters become the human-readable text the client sees. Same pattern as `AIFunctionFactory.Create(...)` over in the agent project. That's deliberate. `Microsoft.Extensions.AI` and `ModelContextProtocol` both look at the same attribute shapes, so the same method signature can be exposed locally as an `AIFunction` and remotely as an MCP tool.

> The wire-format tool name is the method name converted to snake_case. `GetTransactions` shows up as `get_transactions` in `tools/list`, and that's the name a client passes to `tools/call`. Parameter names are preserved as written (so `dateExpression` stays `dateExpression`). If you ever want to override the wire name, the `[McpServerTool]` attribute takes a `Name` argument: `[McpServerTool(Name = "list_transactions")]`.

**`IEmbeddingGenerator` is a method parameter, not a constructor parameter.** The methods are static, so there is no constructor for DI to call. The MCP framework looks at each method parameter, and if the type is registered in DI, it gets injected at call time. The `string query` and `int topK = 5` parameters come from the MCP client (they're in the tool's input schema), but `IEmbeddingGenerator<string, Embedding<float>>` is a service: the framework resolves it from the service provider for the lifetime of the call. Same pattern that minimal API endpoints use.

**The methods delegate to the existing `Tools/` classes.** `new GetTransactionsTool().GetTransactions(...)` is a one-liner over the agent's tool. We pay a small allocation per call (one cheap object) to avoid duplicating the database logic. If `GetTransactionsTool` had heavy state, we'd register it as a singleton and inject it through a parameter too. It doesn't, so we don't.

> `GetTransactionsTool` and `SearchTransactionsTool` both open their own `FinanceDbContext` inside the method body. That isn't ideal in a long-running server (creating contexts per call is fine, but a real version would pool them via `AddDbContextPool`). The shortcut works because the methods are short-lived and the connection pool inside Npgsql handles the rest. Reach for `AddDbContextPool` if and when you wire the server up to real traffic.

---

## Step 3: Wire Program.cs

Open `src/FinanceAssistant.McpServer/Program.cs`. Currently it's a placeholder:

```csharp
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => "FinanceAssistant.McpServer placeholder. Pillar 6 fills this in.");
app.Run();
```

Five small edits turn it into the MCP server.

**3.1: Add two using directives** at the very top of the file, above `var builder = ...`:

```csharp
using FinanceAssistant;
using FinanceAssistant.McpServer;
```

The first one gives you `AddEmbeddingGenerator` (the extension method we wrote back in P2.02). The second one gives you `McpTools` (the class you wrote in Step 2).

**3.2: Add configuration sources** between `var builder = WebApplication.CreateBuilder(args);` and `var app = builder.Build();`:

```csharp
builder.Configuration
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables();
```

`AddUserSecrets<Program>` resolves to the same secrets file the agent reads because we set `UserSecretsId="finance-assistant-workshop"` on the csproj in Step 1.1. No new setup, no new secrets, no `appsettings.json`.

**3.3: Register the embedding generator** on the next line:

```csharp
builder.Services.AddEmbeddingGenerator(builder.Configuration);
```

`SearchTransactions` (the method you wrote in Step 2) takes `IEmbeddingGenerator<string, Embedding<float>>` as a parameter. The MCP framework will resolve it from DI at call time; this line is what puts it in DI.

**3.4: Register the MCP server itself**, right after the embedding generator line:

```csharp
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<McpTools>();
```

This is the line that turns the project into an MCP server. `AddMcpServer()` registers the framework, `WithHttpTransport()` says "speak Streamable HTTP, not stdio", and `WithTools<McpTools>()` points the scanner at your tools class.

**3.5: Register CORS** on the next line, before `builder.Build()`:

```csharp
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .AllowAnyMethod()
        .WithExposedHeaders("Mcp-Session-Id"));
});
```

We need this because the MCP Inspector you'll connect in Step 5 runs in a browser at `http://localhost:6274` and calls your server at `http://localhost:5050`. That's a cross-origin request; without a CORS policy in the pipeline, the browser's preflight fails and the Inspector never gets past Connect.

The non-obvious line is `WithExposedHeaders("Mcp-Session-Id")`. Browsers hide non-safelisted response headers from JavaScript by default. Without the opt-in, `fetch()` would not see the `Mcp-Session-Id` header on the initialize response, and the Inspector would fail on its second call with a confusing "session not found" error instead of a clear preflight failure. `AllowAnyOrigin()` is fine for a workshop running on `localhost`; tighten to `.WithOrigins("https://your-client.example.com")` for any real deployment.

**3.6: Replace the two lines below `builder.Build()`**.

Find:

```csharp
var app = builder.Build();
app.MapGet("/", () => "FinanceAssistant.McpServer placeholder. Pillar 6 fills this in.");
app.Run();
```

Drop the `MapGet` line, add `app.UseCors();` and `app.MapMcp();` in its place, and add a URL to `Run`:

```csharp
var app = builder.Build();
app.UseCors();
app.MapMcp();
app.Run("http://localhost:5050");
```

The `MapGet` line has to go because it collides with the MCP root route the next call mounts. `app.UseCors()` wires the CORS policy you registered in 3.5 into the request pipeline; it must come before `MapMcp()` so the CORS middleware runs on MCP requests. `app.MapMcp()` adds the protocol endpoints (`/`, `/sse`, the internal Streamable HTTP endpoint) and wires them to the MCP server you just registered. `app.Run("http://localhost:5050")` pins the host to a specific URL. The port is arbitrary; 5050 is just a memorable choice that doesn't clash with the usual ASP.NET dev ports.

After all six edits, your `Program.cs` should look like:

```csharp
using FinanceAssistant;
using FinanceAssistant.McpServer;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables();

builder.Services.AddEmbeddingGenerator(builder.Configuration);

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<McpTools>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .AllowAnyMethod()
        .WithExposedHeaders("Mcp-Session-Id"));
});

var app = builder.Build();
app.UseCors();
app.MapMcp();
app.Run("http://localhost:5050");
```

One thing worth reading carefully:

**`WithHttpTransport()` is the network mode.** MCP also has a stdio mode, where the client launches the server as a subprocess and they pipe JSON-RPC over stdin/stdout. That's how a lot of bundled-with-the-client tools load locally. Stdio is fine for "tools that ship with the client" use-cases. HTTP is what you want for "tools that live behind a service the rest of your fleet can call". We chose HTTP because the goal here is "expose tools so other agents can find them".

> If you have an existing minimal API in this project, `MapMcp()` is just another endpoint mapping and coexists with `MapGet`, `MapPost`, etc. We dropped the placeholder `MapGet("/", ...)` in Step 3.6 because it would have collided with the MCP root route that `MapMcp()` mounts.

---

## Step 4: Run the server

From the repo root:

```bash
dotnet run --project src/FinanceAssistant.McpServer
```

You should see ASP.NET Core's normal startup banner, plus the host telling you it's listening:

```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:5050
info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to shut down.
```

That's it. No console prompt, no REPL. The MCP server is a service. It sits there and waits for clients.

> A quick port-open sanity check: `curl http://localhost:5050` returns HTTP 400 with a JSON-RPC error envelope. That's the correct behaviour, not a bug. The root route only accepts MCP-framed POSTs, and a plain GET isn't one. A 400 here means the server is up.

Leave it running. Step 5 connects to it.

---

## Step 5: Verify with the MCP Inspector

The MCP Inspector is a small web UI maintained alongside the protocol. It speaks Streamable HTTP, lists tools, lets you invoke them, and shows the raw JSON-RPC traffic. For a workshop it's the fastest path from "server's running" to "I can see it works".

In a second terminal, run it without installing globally:

```bash
npx @modelcontextprotocol/inspector
```

A browser tab opens. In the connection panel, set:

- **Transport**: `Streamable HTTP`
- **URL**: `http://localhost:5050`

Click **Connect**. Three things to verify, in order.

**Tools tab lists both tools.** You should see `get_transactions` and `search_transactions` (snake_case is the on-the-wire form; the C# method names you wrote were `GetTransactions` and `SearchTransactions`). Each shows its description and JSON schema. If only one shows up, or neither does, the scanner missed something: see the troubleshooting entry on empty `tools/list`.

**Call `get_transactions`.** Pick it, set `dateExpression` to a range that exists in your seed data (`2026-01-01..2026-01-31` is a safe bet), and click **Run**. You should see the same JSON the agent sees when it calls the tool, top-level shape `{ "transactions": [ ... ] }`. If you get an empty list, the date range doesn't intersect your seed.

**Call `search_transactions`.** Query `"coffee shops"`, `topK = 5`. Five results come back, each with a `distance` field, ordered ascending. If this errors out, it's almost always the embedding deployment misconfigured or the pgvector container down: see troubleshooting.

That's the verification. A process you didn't write (the Inspector) just listed and invoked your tools over the wire, against the same database and the same embedding generator the agent uses. The server works.

> No Node, no browser, no Inspector available? You can verify the same flow over `curl` with a four-call sequence (`initialize` → `notifications/initialized` → `tools/list` → `tools/call`). See the "Verify without the Inspector" entry in Troubleshooting for the script.

### Wire it into your editor or chat client

The Inspector proves the server works. To actually use it day-to-day, drop a small JSON file in the right place and your editor or chat client picks the server up alongside its other tools. Keep the server running (`dotnet run --project src/FinanceAssistant.McpServer`) any time you want the client to be able to call it.

**Cursor**. Create `.cursor/mcp.json` in your project root, or `~/.cursor/mcp.json` for a user-wide config:

```json
{
  "mcpServers": {
    "finance-assistant": {
      "url": "http://localhost:5050"
    }
  }
}
```

Open Cursor's Settings → MCP page. `finance-assistant` should show as connected with both tools listed. Cursor's agent can now invoke `get_transactions` and `search_transactions` from chat.

**VS Code**. Create `.vscode/mcp.json` in your project root:

```json
{
  "servers": {
    "finance-assistant": {
      "type": "http",
      "url": "http://localhost:5050"
    }
  }
}
```

Two differences from Cursor worth noting: the top-level key is `servers` (not `mcpServers`), and you have to set `"type": "http"` explicitly so VS Code knows to use Streamable HTTP instead of stdio.

**Claude Code**. The Claude CLI has a dedicated `mcp add` subcommand, so you don't have to hand-edit any JSON:

```bash
claude mcp add --transport http financeassistant http://localhost:5050
```

That registers the server under the name `financeassistant` for HTTP transport at the given URL. Open Claude Code in a project directory and ask "What did I spend on coffee in January 2026?": the CLI lists `financeassistant` among its tools and calls your server. `claude mcp list` shows what's registered, and `claude mcp remove financeassistant` undoes it.

> Two operational caveats. **The server has to be running** for any of these clients to talk to it. If you close the terminal where `dotnet run` is, the client will silently fail to connect on its next request. And **the JSON shapes are not standardised**. `mcpServers` vs `servers`, `"url"` vs `"transport": { "type": "http", "url": ... }`, and required-vs-optional fields all vary between clients and between versions. If your client doesn't pick the server up, the answer is almost always in its own docs, not in MCP's spec.

---

## Stretch goal: add a prompt and a resource

Tools aren't the only primitive MCP defines. The protocol has three, each with its own audience:

- **Tools** are for the model to invoke during a conversation. That's what we just shipped.
- **Prompts** are for the user. The client surfaces them as one-click conversation starters with parameter inputs.
- **Resources** are read-only data the server publishes for the client (or the model) to pull on demand. Useful for reference content you don't want to dump into every chat.

The .NET SDK mirrors the tools pattern for both: a class marked with a `*Type` attribute, methods marked with the per-item attribute, and a `With*<T>()` call in `Program.cs`. If you have time left in the workshop, wire one of each onto the server.

### A prompt: `monthly_spending_report`

Create `src/FinanceAssistant.McpServer/McpPrompts.cs`:

```csharp
using System.ComponentModel;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace FinanceAssistant.McpServer;

[McpServerPromptType]
public class McpPrompts
{
    [McpServerPrompt(Name = "monthly_spending_report")]
    [Description("Kick off a guided review of the user's spending for a given month. The client offers this as a one-click prompt and the user fills in the month.")]
    public static ChatMessage MonthlySpendingReport(
        [Description("Month in YYYY-MM format, e.g. '2026-01'.")] string month)
    {
        return new ChatMessage(
            ChatRole.User,
            $"Walk me through my spending in {month}. List the top three categories by total spend, " +
            $"flag any unusually large purchases, and call out any recurring subscriptions you find.");
    }
}
```

In `Program.cs`, find the `.WithTools<McpTools>()` line you wrote in Step 3.4. Add `.WithPrompts<McpPrompts>()` to the chain on the next line, before the closing semicolon:

```csharp
    .WithTools<McpTools>()
    .WithPrompts<McpPrompts>();
```

A prompt isn't a tool. The model doesn't call it. The client lists it as a saved template, the user picks it, the client expands the parameters into a real user message, and only then does the conversation start. The Inspector's Prompts tab shows this flow: pick the prompt, fill in the month, see the rendered user message.

### A resource: `finance://categories` (static and templated)

MCP defines two flavours of resource. A **static** resource has a fixed URI and always returns the same kind of data. A **resource template** has a parameterised URI with `{placeholders}`; the placeholders bind to method parameters at read time, so one method serves a whole family of URIs. We'll add one of each.

Create `src/FinanceAssistant.McpServer/McpResources.cs`:

```csharp
using System.ComponentModel;
using FinanceAssistant.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace FinanceAssistant.McpServer;

[McpServerResourceType]
public class McpResources
{
    [McpServerResource(UriTemplate = "finance://categories", Name = "categories", MimeType = "text/plain")]
    [Description("The distinct list of categories present in the user's transactions, one per line. Use this to learn what category names are valid before filtering.")]
    public static async Task<string> Categories(CancellationToken ct = default)
    {
        await using var db = new FinanceDbContext();
        var categories = await db.Transactions
            .Select(t => t.Category)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync(ct);

        return string.Join('\n', categories);
    }

    [McpServerResource(UriTemplate = "finance://categories/{category}", Name = "category_transactions", MimeType = "text/plain")]
    [Description("The most recent transactions in a given category, one per line. Pair with finance://categories: list the categories first, then drill into one.")]
    public static async Task<string> CategoryTransactions(
        string category,
        CancellationToken ct = default)
    {
        await using var db = new FinanceDbContext();
        var transactions = await db.Transactions
            .Where(t => t.Category == category)
            .OrderByDescending(t => t.Date)
            .Select(t => new { t.Date, t.Amount, t.Merchant, t.Description })
            .Take(50)
            .ToListAsync(ct);

        if (transactions.Count == 0)
        {
            return $"No transactions found in category '{category}'.";
        }

        return string.Join('\n', transactions.Select(t =>
            $"{t.Date:yyyy-MM-dd}  {t.Amount,10:0.00}  {t.Merchant,-30}  {t.Description}"));
    }
}
```

Two things worth noticing:

**The `{category}` placeholder in the URI binds to the `string category` parameter.** Same name, automatic binding. If you renamed the parameter to `cat` while leaving the URI as `{category}`, the framework would fail to resolve the binding. Keep the names aligned, or rename both.

**One method, infinite URIs.** A client that reads `finance://categories/Groceries` and a client that reads `finance://categories/Travel` both land in the same C# method. The placeholder is the variable. That's the difference between a tool and a resource template: the tool takes arguments in the JSON-RPC params, the template takes them in the URI path. Same idea, different surface.

In `Program.cs`, add `.WithResources<McpResources>()` to the same chain, on a new line before the closing semicolon. The full tail should now read:

```csharp
    .WithTools<McpTools>()
    .WithPrompts<McpPrompts>()
    .WithResources<McpResources>();
```

Restart the server and reconnect from the Inspector. The Prompts tab now shows `monthly_spending_report` with a `month` input. The Resources tab shows `finance://categories` (static, single click to read) and `finance://categories/{category}` (templated, with a `category` input field). Try the static one first to see the available categories, then pick one and read the templated URI to drill in.

> Resources are the right primitive for "static-ish reference data the model should be aware of, but not on every turn". The categories list, the seed file's date range, a `README` describing the schema, are all natural fits. Things that change per call ("transactions in this range") belong in tools, because the model needs to pass arguments to get them.

> The same `[Description]` you put on the method body becomes the resource's description in `resources/list`. Good descriptions are the difference between the model knowing to pull a resource and the resource sitting unused. Treat them with the same care as tool descriptions.

### Where this goes next

Three primitives, three audiences, one server. The pattern scales: you can wire dozens of tools, prompts, and resources behind the same `MapMcp` endpoint, share them across agents in your org, version them with normal .NET package practices, and gate them with auth middleware the same way you'd gate any ASP.NET Core route.

> When the number of `With*<T>()` calls grows past two or three classes per primitive, the SDK ships an assembly-scanning variant: `.WithToolsFromAssembly()`, `.WithPromptsFromAssembly()`, `.WithResourcesFromAssembly()`. Each scans the calling assembly (or one you pass) and registers every class marked with the matching `[McpServer*Type]` attribute. The trade-off is the usual one for auto-registration: less code, less visible coupling between `Program.cs` and the classes that wire it up. Also useful: the assembly-scanning path accepts `static class`, while the explicit generic `With*<T>()` rejects it (`CS0718`). If you'd rather author `McpTools` as `public static class McpTools`, switch to `WithToolsFromAssembly()` and the constraint disappears.

The harder questions (auth, multi-tenancy, observability, elicitation for human-in-the-loop tools) are out of scope for the workshop. The pattern is the part you take home.

---

## Troubleshooting

### Server fails to start with "AzureOpenAI:Endpoint is missing"

The user secrets aren't being read. Three things to check:

1. The `UserSecretsId` in `FinanceAssistant.McpServer.csproj` is exactly `finance-assistant-workshop`. A typo or different ID points at an empty store.
2. The secrets actually exist. Run `dotnet user-secrets list --project src/FinanceAssistant` and confirm `AzureOpenAI:Endpoint`, `AzureOpenAI:ApiKey`, and `AzureOpenAI:EmbeddingDeployment` are all present.
3. `Program.cs` calls `AddUserSecrets<Program>` before `AddEmbeddingGenerator(builder.Configuration)`. Configuration is read in order; if you call `AddEmbeddingGenerator` first, the values aren't there yet.

### `tools/list` returns an empty list

The `McpTools` class isn't being scanned. Check:

1. `McpTools` is decorated with `[McpServerToolType]`. Without the attribute the scanner ignores it.
2. The methods are decorated with `[McpServerTool]`. Without the attribute the method is just a regular static method.
3. `Program.cs` calls `WithTools<McpTools>()` and the type matches the actual class name.

### `SearchTransactions` throws when called

Almost always one of two things:

1. `AzureOpenAI:EmbeddingDeployment` is missing or points at a deployment that doesn't exist in Foundry. The embedder calls Azure when invoked; a misconfigured deployment surfaces here, not at startup.
2. The pgvector container isn't running. `docker ps` should show `finance-assistant-postgres` healthy. If it isn't, `docker compose up -d`.

### Port 5050 is already in use

Something else is listening on it. Either stop the other process, or change the port in `Program.cs`:

```csharp
app.Run("http://localhost:5151");
```

Update the client URL to match.

### Client reports the MCP server failed to connect

Generic failure that applies to Claude Code, Cursor, and VS Code. Two common causes:

1. The server isn't actually running. Check the terminal where you started it with `dotnet run`.
2. The URL you registered includes a trailing path component. MCP servers expose the protocol at the root of the URL you point at; `http://localhost:5050` is right, `http://localhost:5050/mcp` is wrong. If you used `claude mcp add`, re-add with the bare URL; if you edited JSON for Cursor or VS Code, fix the `"url"` field there.

### Inspector reports `No 'Access-Control-Allow-Origin' header`

You missed or partially applied Step 3.5 (CORS registration) or Step 3.6 (the `app.UseCors()` line before `app.MapMcp()`). Diff your `Program.cs` against the "after all six edits" reference block at the end of Step 3 and reapply.

Two non-obvious bits worth double-checking: `WithExposedHeaders("Mcp-Session-Id")` (browsers hide non-safelisted response headers from JavaScript by default; without it, the Inspector loses the session ID and fails on its second call with a confusing "session not found" rather than a clean preflight error), and the *order* of `UseCors()` and `MapMcp()` in the pipeline: `UseCors` must come first so the policy actually applies to MCP requests.

### Inspector returns `406 Not Acceptable` on the initialize request

Rare, but possible if you're on a build of `ModelContextProtocol.AspNetCore` that requires the client to advertise `text/event-stream`. The Inspector does this by default; if you bridged through a proxy that strips Accept headers, that's the cause. Bypass the proxy.

### Verify without the Inspector

No Node, no browser, or you'd rather see the raw JSON-RPC traffic? Use `curl`. Streamable HTTP holds a session, so the dance is four calls: the first response carries an `Mcp-Session-Id` header the rest have to echo back.

```bash
# 1. Initialize. Capture the session ID from the response headers.
SESSION_ID=$(curl -sS -D - -o /dev/null -X POST http://localhost:5050 \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"curl","version":"0.0.1"}}}' \
  | grep -i '^mcp-session-id:' | awk '{print $2}' | tr -d '\r\n')

echo "Session: $SESSION_ID"

# 2. Tell the server initialization is complete (notification, no id).
curl -sS -X POST http://localhost:5050 \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H "Mcp-Session-Id: $SESSION_ID" \
  -d '{"jsonrpc":"2.0","method":"notifications/initialized"}'

# 3. List the registered tools.
curl -sS -X POST http://localhost:5050 \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H "Mcp-Session-Id: $SESSION_ID" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'

# 4. Call get_transactions.
curl -sS -X POST http://localhost:5050 \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H "Mcp-Session-Id: $SESSION_ID" \
  -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_transactions","arguments":{"dateExpression":"2026-01-01..2026-01-31"}}}'
```

The response body comes back as a Server-Sent Event frame (lines prefixed with `event:` and `data:`) rather than a plain JSON body, because we asked for `text/event-stream`. The JSON-RPC payload is the `data:` line. Pipe through `grep '^data:'` if you'd rather only see the payload. CORS doesn't apply here: that's a browser concern, and curl isn't a browser.

### Prompt or resource doesn't appear in the Inspector (stretch goal only)

Same pattern as the empty `tools/list` case. Check three things:

1. The class is marked with the right type attribute: `[McpServerPromptType]` for `McpPrompts`, `[McpServerResourceType]` for `McpResources`. Cross-pairing (wrong attribute on the wrong class) silently no-ops.
2. The method is marked with `[McpServerPrompt]` or `[McpServerResource]`.
3. `Program.cs` calls `.WithPrompts<McpPrompts>()` and `.WithResources<McpResources>()` after `.WithTools<McpTools>()`. Forgetting one of the registrations means the framework never scans the class.

If `finance://categories` is listed but returns an empty body, the `Transactions` table is empty or your seed didn't run. Confirm with `SELECT COUNT(*) FROM "Transactions";` against the pgvector container.

---

## You can now

Take any tool you've built in `Microsoft.Extensions.AI` and expose it over the network as an MCP server with three additions: a `[McpServerTool]` method, a server registration in DI, and a single endpoint mapping. The same `[Description]` attributes drive both the in-process tool schema and the over-the-wire MCP schema, so the tool definition is the source of truth in both directions.

The MCP server is now a separate front door over the same building. Another agent (yours, a colleague's, Claude Code, Cursor, a CLI) can list and call your tools without taking a code dependency on your project.

---

## Summary

You've added:

- **`FinanceAssistant.McpServer.csproj` updated**: `ModelContextProtocol.AspNetCore` package, `ProjectReference` to `FinanceAssistant`, and `UserSecretsId="finance-assistant-workshop"` to share secrets with the agent.
- **`McpTools.cs`**: a class marked `[McpServerToolType]` with two static `[McpServerTool]` methods. `GetTransactions` and `SearchTransactions` delegate to the agent project's tools. `IEmbeddingGenerator` flows in as a method parameter and resolves from DI per call.
- **`Program.cs` rewritten**: ASP.NET Core host, user secrets, `AddEmbeddingGenerator`, `AddMcpServer().WithHttpTransport().WithTools<McpTools>()`, CORS (so the Inspector and any other browser-based client can connect), `app.UseCors()` and `app.MapMcp()` in the pipeline, listening on `http://localhost:5050`.
- **A working MCP server**: verified end-to-end from the MCP Inspector by listing both tools and invoking `get_transactions` and `search_transactions` against the same database and embedding generator the agent uses.

---

## Additional Resources

- [Model Context Protocol specification](https://modelcontextprotocol.io/specification): protocol verbs, transports, JSON-RPC envelope.
- [ModelContextProtocol .NET SDK on GitHub](https://github.com/modelcontextprotocol/csharp-sdk): source of the package, examples for stdio and HTTP transports.
- [Microsoft Learn: MCP servers with ASP.NET Core](https://learn.microsoft.com/en-us/dotnet/ai/get-started-mcp): the SDK as Microsoft documents it.
- [MCP Inspector](https://github.com/modelcontextprotocol/inspector): the web UI we used in Step 5.

This is the workshop's last hands-on exercise. The remaining time in Pillar 6 covers graduating from the hand-written agent loop to Microsoft Agent Framework (MAF), which we'll walk through together as a lecture-and-demo segment rather than another guided build. Your agent works. Your tools are shareable. You can ship.
