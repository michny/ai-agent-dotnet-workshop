# Build Your First AI Agent in .NET

This is the companion repository for the **Build Your First AI Agent in .NET** workshop: a hands-on program where .NET developers learn the building blocks of agentic AI by shipping a real agent end-to-end.

A personal finance assistant agent built with .NET 10. Throughout the workshop, you'll extend this foundation with AI-powered features: tool-calling, semantic search over your transactions with pgvector (RAG), conversation memory, a human-in-the-loop safety gate, and exposing the agent to other clients over the Model Context Protocol.

**Built by [Gui Ferreira](https://github.com/gsferreira)**: Microsoft MVP, .NET educator, and Dometrain author.

> **Note**: This repository contains example credentials (database passwords, keys) for demo purposes only. These are intentionally simple values for local development. Never use these in production.

## Prerequisites

- Comfortable writing C# / .NET. We assume you can read and modify an ASP.NET / console app without hand-holding.
- An Azure account. The first exercise provisions an Azure OpenAI (Microsoft Foundry) resource. Walkthrough: [`docs/exercises/p1-01-configure-azure.md`](docs/exercises/p1-01-configure-azure.md).

## Required Tools

- **.NET 10 SDK**: at least `10.0.100` (pinned via `global.json`). Verify with `dotnet --version`.
- **Docker**: Docker Desktop, OrbStack, Rancher Desktop, or any other flavour. Verify with `docker compose version`.
- **Git**: to clone this repo and check out per-pillar checkpoints.
- **An IDE**: Visual Studio, JetBrains Rider, or VS Code with the C# Dev Kit. Open `FinanceAssistant.sln`.
- **A code assistant**: Claude Code, GitHub Copilot, Cursor, or your tool of choice. You'll lean on it during the exercises, so make sure it's installed, signed in, and working before you start.

## Getting started

1. Clone this repo.
2. From the repo root, bring up the database container: `docker compose up -d`. Verify with `docker compose ps`: you should see `finance-assistant-postgres` running and healthy. The container must be running before `dotnet run`.
3. From the repo root, `dotnet build` (or open `FinanceAssistant.sln` in your IDE). You should see `Build succeeded.` with zero warnings.
4. Optional smoke test: `dotnet run --project src/FinanceAssistant`. The REPL should start and echo whatever you type. Type `exit` to quit.

## Repo layout

- [`src/FinanceAssistant/`](src/FinanceAssistant): the agent. A single .NET 10 console app holding the domain (Models, Data, Services), the REPL (Program.cs), and the system prompt (`Prompts/SystemPrompt.md`). Evolves in place across all six pillars.
- [`src/FinanceAssistant.McpServer/`](src/FinanceAssistant.McpServer): empty ASP.NET Core minimal-API placeholder. Filled in during Pillar 6.
- `FinanceAssistant.sln`: solution file. Open this in Visual Studio, Rider, or VS Code.
- `global.json`: pins the .NET SDK to `10.0.100`.
- `scaffolding/transactions.csv`: bank-statement-style seed (400 rows) imported on first run.
- `docker-compose.yml`: local `pgvector/pgvector:pg16` container. Used by the agent for transactions storage and (from P2.02) embeddings.
- [`docs/exercises/`](docs/exercises): the per-exercise tutorials you follow through the workshop.

## Falling behind?

Each exercise's end-state is published as a branch in this repo. Branch names mirror the exercise filenames in [`docs/exercises/`](docs/exercises) with an `-end` suffix:

| Branch          | State at the end of exercise…                |
| --------------- | -------------------------------------------- |
| `p1-02-end`     | P1.02: chat client wired up                 |
| `p2-01-end`     | P2.01: currency conversion tool             |
| `p2-02-end`     | P2.02: database queries + pgvector RAG      |
| `p3-01-end`     | P3.01: hand-written agent loop              |
| `p4-01-end`     | P4.01: episodic conversation memory         |
| `p4-02-end`     | P4.02: history summarization + recall       |
| `p5-01-end`     | P5.01: human-in-the-loop safety gate        |
| `p6-01-end`     | P6.01: MCP server                           |

If you get stuck, fast-forward to the canonical state:

```bash
git stash                  # set your in-progress work aside
git checkout p2-02-end     # jump to the end of, e.g., exercise P2.02
```

