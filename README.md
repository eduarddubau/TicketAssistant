# TicketAssistant

> ⚠️ **Proof of concept.** This is an exploratory prototype for learning and demonstration,
> **not** production software. It uses in-memory storage, a spoofable header for user
> identity (no real authentication), and a small local model by default. See
> [Caveats](#caveats-its-a-poc) before reading too much into it.

A chat assistant that manages support tickets in plain language. You describe an issue and
the assistant looks up, searches, creates, updates, and comments on tickets on your behalf —
calling a real ticketing backend through an LLM tool-calling loop, and pausing for your
approval before it changes anything.

## What it demonstrates

- **LLM orchestration** — a hand-written tool-calling loop (send messages + tools → run the
  tools the model asks for → feed results back → repeat) rather than a black-box framework,
  with replies **streamed** token by token.
- **Provider-agnostic design** — the assistant talks to abstractions, so you can swap the
  **LLM** (Ollama / Anthropic / OpenAI / Google — switchable live from the console) or the
  **ticket backend** without touching the orchestration logic.
- **Human-in-the-loop writes** — every action that changes a ticket pauses for a confirmation
  card the user can **edit** before approving, and the last change can be **undone**.
- **Guardrails** — the assistant asks for missing fields instead of guessing, and detects
  likely **duplicate** tickets, offering to reopen/update the existing one (and linking the
  two if you create a separate ticket anyway).
- **Per-user scoping** — a user only sees tickets they created.

### What the assistant can do

Look up, search, and **list by status/priority** · **summarize** where everything stands ·
create tickets · **resolve with a note** · change status (reopen) · comment · **assign** ·
set **due dates** and flag anything **overdue** · **undo** the last change. Every ticket keeps
an **audit trail** of what changed and when.

## Architecture

```
                          ┌─────────────────────────────┐
  browser (test console)  │      TicketAssistant.Api     │        LLM (IChatClient)
  http://localhost:5080  ─┼─▶  Orchestration loop  ◀────┼──▶  Ollama / Anthropic /
        SSE chat          │        │         ▲          │        OpenAI / Google
                          │        ▼         │          │
                          │   ITicketProvider (abstraction)
                          └────────┼─────────────────────┘
                                   │ REST + X-User-Id
                                   ▼
                          ┌─────────────────────────────┐
                          │       TicketingMock.Api      │   stands in for a real
                          │   in-memory tickets + board  │   Jira/Zendesk/etc.
                          │   http://localhost:5090      │
                          └─────────────────────────────┘
```

Two ASP.NET Core (.NET 10) services:

| Project | What it is |
| --- | --- |
| **`src/TicketAssistant.Api`** | The assistant: chat endpoints, the LLM tool-calling loop, and the ticket-provider abstraction. |
| **`src/TicketingMock.Api`** | A stand-in "external ticketing system" (Jira/Zendesk-like) with an in-memory store, REST API, and a live board UI — so you can watch tickets land in a separate app. |

### Inside `TicketAssistant.Api`

- **`Orchestration/`** — the core.
  - `OrchestrationLoop.cs` — the send-model → run-tools → repeat loop, plus the confirmation
    pause, missing-field and duplicate guardrails.
  - `TicketTools.cs` — the ticket operations exposed to the model as callable tools.
  - `ConversationStore.cs` — per-chat message history and the system prompt.
  - `OrchestrationEvent.cs` — the events streamed to the browser (assistant text / tool ran /
    confirmation required).
- **`Providers/`** — `ITicketProvider` (the backend seam) and its implementations:
  `HttpTicketProvider` (calls the mock over REST), `InMemoryTicketProvider` (offline stub),
  and `UserIdForwardingHandler` (forwards the user id to the backend).
- **`Models/`** — the canonical ticket model shared across the app.
- **`wwwroot/index.html`** — a minimal browser chat console for testing (streams SSE, renders
  the editable confirmation cards). A proper Angular frontend is planned but not built yet.

## Running it

### With Podman / Docker Compose (recommended)

```bash
# 1. build + start the assistant, the mock ticketing system, and Ollama
podman compose up -d          # or: docker compose up -d

# 2. pull a tool-calling-capable model into the Ollama container (one time, a few GB)
podman compose exec ollama ollama pull llama3.2:3b
```

Then open:

- **Chat console** → <http://localhost:5080/>
- **Ticket board** (the "external" system) → <http://localhost:5090/>
- **API reference (Scalar)** → <http://localhost:5080/scalar/v1>

Try: *"Create a ticket: the login page returns a 500 error when I submit."* — the assistant
will gather any missing details, warn if a similar ticket already exists, and show a
confirmation card you can edit before it creates anything. Watch it appear on the board.

To test per-user isolation, change the **user** field in the console header (it defaults to
`alice`, who owns the seed tickets) — a different user sees only their own tickets.

### Locally with the .NET SDK

Requires the .NET 10 SDK and a local Ollama (`ollama serve` + `ollama pull llama3.2:3b`).

```bash
dotnet run --project src/TicketingMock.Api     # ticket backend on :5090
dotnet run --project src/TicketAssistant.Api   # assistant on :5080
```

## Configuration

Set via `appsettings.json`, environment variables, or `.env` (see `.env.example`).

| Setting | Purpose | Default |
| --- | --- | --- |
| `Llm:Provider` | `Ollama` \| `Anthropic` \| `OpenAI` \| `Google` | `Ollama` |
| `Ollama:Model` | Local model (needs tool-calling support) | `llama3.2:3b` |
| `Anthropic/OpenAI/Google :ApiKey` | Key for the chosen hosted provider | — |
| `Tickets:Backend` | `Http` (the mock) \| `InMemory` (offline stub) | `Http` |

Only Ollama runs with no API key. `qwen3:4b` / `qwen3:8b` are heavier local alternatives with
more reliable tool calling.

## Caveats (it's a PoC)

- **No persistence** — conversations and tickets live in memory; a restart wipes everything.
- **Not real auth** — "who the user is" comes from a client-supplied `X-User-Id` header, which
  is trivially spoofable. It demonstrates data scoping, not security.
- **Small-model reliability** — `llama3.2:3b` sometimes emits a malformed tool call and needs a
  retry. The orchestration logic is deterministic; the model is the flaky part. Hosted
  providers or a larger local model behave more consistently.
- **Duplicate detection is a heuristic** — title keyword overlap, not semantic similarity, so
  very differently-worded duplicates can slip through.
- **Single instance only** — the in-memory stores assume one running process.
