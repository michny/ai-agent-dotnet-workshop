# P1.01 - Configure Azure AI Services

> Pillar 1, Part 1. Individual.

## Mission

Set up an Azure OpenAI resource, deploy the chat model, validate it from Azure AI Foundry's chat playground, and wire the credentials into local user secrets.

By the end, your `gpt-4.1-mini` deployment replies to "hello" in the playground, and `dotnet user-secrets list` shows the three values your code will need in P1.02.

**Learning Objectives**:

- Azure portal navigation and Foundry model deployment
- .NET user secrets for local credential management
- Validating an LLM deployment without writing code

---

## Prerequisites

- An Azure account with permission to create resources
- .NET 10 SDK installed (`dotnet --version` reports 10.x)
- Repo cloned and `dotnet build` green from the repo root

---

## What we're solving

The starter project doesn't talk to any AI yet. The REPL just echoes whatever you type. P1.02 writes the code that calls Azure. P1.01 sets up the cloud resource that code will call.

Two things have to be in place before P1.02 makes sense:

1. **An Azure OpenAI resource exists in your subscription** with a chat model deployed. (Pillar 2 will add an embeddings model later, when the workshop actually needs vectors.)
2. **Your endpoint, key, and deployment name live in `dotnet user-secrets`** so when P1.02's code reads them, they're there.

You'll validate the resource by sending "hello" through Azure AI Foundry's chat playground. If the playground replies, your deployment works. If your local code can't reach it later, that's a wiring problem, not a cloud problem.

---

## If you're comfortable, do this

Five steps. Skip the rest if it works on the first try.

1. Create an Azure OpenAI resource (Standard).
2. Deploy `gpt-4.1-mini` in Azure AI Foundry.
3. Validate the deployment from Foundry's chat playground. Send "hello", get a reply.
4. Grab the endpoint URL and an API key from "Keys and Endpoint".
5. Run three `dotnet user-secrets set` commands inside `src/FinanceAssistant/`.

---

## Step 1: Create the Azure OpenAI resource

### 1.1: Open the portal

Go to https://portal.azure.com and sign in. Click "Create a resource" (the + icon, top-left).

### 1.2: Find Microsoft Foundry

Search for "Microsoft Foundry" in the marketplace. Pick "Microsoft Foundry" from the results. Click "Create".

> Azure renames things often. If you can't find "Microsoft Foundry", look for "Azure OpenAI" or "Azure AI Foundry". Same product.

### 1.3: Fill in the basics

- **Subscription**: whichever you have access to.
- **Resource group**: create a new one called `finance-assistant-workshop`. Keeps cleanup easy after the workshop.
- **Region**: Sweden Central, East US, or West Europe all work as of mid-2026. Pick East US if you're not sure.
- **Name**: anything unique. `finance-assistant-<your-initials>` is fine.
- **Pricing tier**: Standard is the default. Leave it. (S0 is pay-as-you-go; expect under $5 of usage across the whole workshop.)

Click "Review + create", then "Create". Click "Go to resource" once the deployment finishes.

---

## Step 2: Deploy the chat model

> Azure portal and Foundry rearrange labels and navigation regularly. The instructions below describe the intent. Adapt to whatever the current UI calls the equivalent action.

### 2.1: Open Azure AI Foundry Portal

In your resource's find the "Go to Foundry Portal". In the Portal, left navigation, find "Models + endpoints".

### 2.2: Deploy gpt-4.1-mini

In Model deployments, click "Deploy model". Pick **`gpt-4.1-mini`** from the list.

> The model we're using throughout the workshop is `gpt-4.1-mini`. Not `gpt-4o`, not `gpt-4o-mini`. Pick `gpt-4.1-mini` exactly.
>
> If you don't see it, your region doesn't support it. Go back to Step 1.3 and pick a different region.

In the deployment dialog:

- **Deployment name**: `gpt-4.1-mini`. Use exactly this name. It's the value you'll paste into user secrets in Step 5.
- **Deployment type**: Global Standard. (Foundry may also offer `Standard` and `DataZoneStandard`; they have different cost and availability tradeoffs, but Global Standard is the safe pick for this workshop.)
- **Model version**: take the auto-selected default unless Foundry flags it as a preview you don't want.
- Leave the rest at defaults.

Click "Deploy" and wait for the green check.

> The deployment name and the model name don't have to match. We're keeping them identical to remove a moving part. If you rename the deployment, that renamed value is what goes into your secrets.

---

## Step 3: Validate from the chat playground

This is your smoke test. No code yet. If the playground works, your deployment works.

### 3.1: Open the playground

In Foundry's left navigation, click "Playgrounds", then "Chat".

### 3.2: Pick your deployment

At the top of the playground, in the "Deployment" dropdown, pick `gpt-4.1-mini`. (If you used a different deployment name in Step 2.2, pick that one.)

### 3.3: Send "hello"

In the chat box at the bottom, type:

```
hello
```

Press Enter. You should get a reply. Something like "Hi! How can I help you today?".

If you do, the cloud side works. Anything else, see the troubleshooting section.

---

## Step 4: Grab the endpoint and the key

Switch back to the Azure portal tab (not Foundry). On your resource's overview page, click "Keys and Endpoint" in the left navigation.

Copy two things:

- **KEY 1** (either key works; KEY 1 is fine).
- **Endpoint (OpenAI)**. A URL like `https://your-resource.openai.azure.com/`. Copy it including the trailing slash.

> Keep the trailing slash. Different SDK versions assemble the request URL differently, and the safe assumption is that the endpoint ends with `/`. Drop it and you'll see opaque request-construction failures (typically `404 Not Found`, occasionally a routing error) once P1.02 starts calling the API.

---

## Step 5: Wire your secrets locally

### 5.1: Move into the project folder

```bash
cd src/FinanceAssistant
```

User secrets are scoped to a project. The agent project's `UserSecretsId` is `finance-assistant-workshop`, declared in `FinanceAssistant.csproj`. Run the commands below from the repo root and you'll get an MSBuild error (no `.csproj` to read the ID from); run them from a *different* project's directory and they'll silently write to the wrong scope. Either way, `cd src/FinanceAssistant/` first.

### 5.2: Set three secrets

Replace placeholders with the values you copied in Step 4:

```bash
dotnet user-secrets set "AzureOpenAI:Endpoint"   "https://your-resource.openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:ApiKey"     "<KEY 1 you copied>"
dotnet user-secrets set "AzureOpenAI:Deployment" "gpt-4.1-mini"
```

`dotnet user-secrets` writes to a JSON file in your user profile. It's never in the repo. It can't be committed by accident.

> The instinct here is to drop the key into `appsettings.json` because it's faster. Don't. The first time you commit `appsettings.json` with a real key, GitHub's secret scanner notices, the key is auto-revoked, and you're explaining the rotation to your team. User secrets is the same convenience without the drama.

### 5.3: Verify

```bash
dotnet user-secrets list
```

You should see all three keys, printed in plaintext:

```
AzureOpenAI:ApiKey = <your-key-value>
AzureOpenAI:Deployment = gpt-4.1-mini
AzureOpenAI:Endpoint = https://your-resource.openai.azure.com/
```

If you only want to confirm the names without echoing the API key value (handy when sharing your screen), pipe through `awk -F= '{print $5}'`.

---

## Troubleshooting

### Foundry playground doesn't return a reply

Your deployment isn't ready, or your region doesn't support the model. Check the deployment status in Foundry's "Models + endpoints" view. Status should be "Succeeded".

### Can't find "Microsoft Foundry" in the portal

Try "Azure OpenAI" or "Azure AI Foundry". Microsoft has renamed the service multiple times.

### `dotnet user-secrets list` is empty after the set commands

You ran the set commands outside `src/FinanceAssistant/`. The secrets are scoped to the project's `UserSecretsId`. Move into `src/FinanceAssistant/` and re-run.

### Deployment fails with a quota error

Fresh subscriptions sometimes ship with zero TPM (tokens-per-minute) quota for `gpt-4.1-mini` in your chosen region. Either request quota in the portal under "Quotas" → "Cognitive Services - Azure OpenAI", or pick a different region from Step 1.3.

### Anything else

Raise your hand. The instructor has seen this fail in unusual ways.

---

## Summary

You've set up:

- **Azure OpenAI resource**: created in Azure portal, region picked.
- **Chat deployment**: `gpt-4.1-mini` deployed and verified through the Foundry playground.
- **Local user secrets**: three keys stored outside the repo.

---

## What's next

P1.02 wires `IChatClient` through `Microsoft.Extensions.Hosting` and replaces the echo line in `Program.cs` with a real call to your `gpt-4.1-mini` deployment. Stretch goal: swap to OpenAI, Anthropic or Gemini in one line.

---

## Cleanup (after the workshop)

Delete the `finance-assistant-workshop` resource group from the Azure portal. That tears down the Azure OpenAI resource and the deployment together and stops any further charges. Pillar 2 will add an embeddings deployment to the same group, so wait until you're done with the whole workshop before deleting.

---

## Additional Resources

- [Microsoft.Extensions.AI documentation](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- [Azure OpenAI .NET SDK](https://learn.microsoft.com/en-us/dotnet/api/overview/azure/ai.openai-readme)
- [.NET User Secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets)
