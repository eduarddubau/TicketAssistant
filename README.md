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
- **Guardrails around a fallible model** — the loop, not the model, enforces the rules:
  - asks for **missing fields** instead of letting the model guess;
  - detects likely **duplicate** tickets, offering to reopen/update the existing one (and
    linking the two if you create a separate ticket anyway);
  - catches **malformed tool calls** (the model printing JSON instead of calling the tool),
    scrubs them off the screen, and retries;
  - after a **declined** confirmation, stops the model from replaying the declined ticket
    when your next message describes a different problem — while still allowing it when you
    say "actually, go ahead".
- **Per-user scoping** — a user only sees tickets they created.
- **Local GPU/CPU choice** — Ollama can run on the host's NVIDIA GPU, auto-detected at
  startup and switchable live from the console.

### What the assistant can do

Look up, search, and **list by status/priority** · **summarize** where everything stands ·
create tickets · **resolve with a note** · change status (reopen) · comment · **assign** ·
set **due dates** and flag anything **overdue** · **undo** the last change. Every ticket keeps
an **audit trail** of what changed and when.

## Architecture

```
                          ┌─────────────────────────────┐
   Angular console        │      TicketAssistant.Api     │        LLM (IChatClient)
  http://localhost:4200  ─┼─▶  Orchestration loop  ◀────┼──▶  Ollama / Anthropic /
   SSE chat + Bearer      │        │         ▲          │        OpenAI / Google
                          │        ▼         │          │
                          │   ITicketProvider (abstraction)
                          └────────┼─────────────────────┘
                    mock: REST     │      Jira: OAuth (per-user token)
                          ▼        │        ▼
      ┌─────────────────────────┐  │  ┌──────────────────────────┐
      │     TicketingMock.Api    │  │  │   api.atlassian.com       │
      │  in-memory tickets+board │  │  │   real Jira Cloud site    │
      │  http://localhost:5090   │  │  └──────────────────────────┘
      └─────────────────────────┘  └── each user connects via a login popup
```

Three services (two ASP.NET Core .NET 10 + one Angular app):

| Project | What it is |
| --- | --- |
| **`src/TicketAssistant.Api`** | The assistant: chat endpoints, the LLM tool-calling loop, the ticket-provider abstraction, and the auth layer (bearer sessions + Jira OAuth). |
| **`src/TicketingMock.Api`** | A stand-in "external ticketing system" (Jira/Zendesk-like) with an in-memory store, REST API, and a live board UI — so you can watch tickets land in a separate app. |
| **`src/web`** | The Angular front-end console. |

### Inside `TicketAssistant.Api`

- **`Orchestration/`** — the core.
  - `OrchestrationLoop.cs` — the send-model → run-tools → repeat loop, plus the confirmation
    pause and all the guardrails (missing fields, duplicates, malformed-tool-call recovery,
    declined-ticket replay).
  - `TicketTools.cs` — the ticket operations exposed to the model as callable tools.
  - `ChatClientFactory.cs` — resolves which LLM serves each request; the console's
    provider/model/compute switchers work by sending headers this factory reads.
  - `ConversationStore.cs` — per-chat message history, the system prompt, and the greeting.
  - `UndoStore.cs` — remembers, per user, how to reverse the last write for "undo that".
  - `OrchestrationEvent.cs` — the events streamed to the browser (assistant text/deltas,
    tool ran, confirmation required, replace-streamed-text).
- **`Providers/`** — `ITicketProvider` (the backend seam) and its implementations:
  `HttpTicketProvider` (calls the mock over REST), `JiraTicketProvider` (a real Jira Cloud site
  via per-user OAuth — see [Connecting a real Jira](#connecting-a-real-jira-jira-cloud-per-user-oauth)),
  `InMemoryTicketProvider` (offline stub), `CompositeTicketProvider` (runs **several backends at
  once** — `Tickets:Backends`, e.g. `Http Jira` — fanning reads across all and routing each write
  to the backend that owns the target), and `UserIdForwardingHandler` (forwards the session's user
  id to the mock).
- **`Auth/`** — the identity layer: opaque bearer sessions (`SessionStore` / `CurrentSession`)
  that replace the old `X-User-Id` header, and the Jira OAuth flow (`JiraOAuthClient`,
  `JiraAccessTokenResolver`, `JiraAuthEndpoints`) that logs each user into their own Jira.
- **`Models/`** — the canonical ticket model shared across the app.
- **`wwwroot/index.html`** — a minimal single-file chat console kept for quick API testing.
- **`src/web`** — the **Angular console** (`ng` app): the real front-end, with streaming chat,
  editable confirmation cards, the provider / model / GPU-CPU switchers, and the Jira connect
  UI. Runs at <http://localhost:4200>.

## Running it

### With Podman / Docker Compose (recommended)

```bash
./up.sh          # Linux / macOS / WSL2
.\up.ps1         # Windows PowerShell
```

The up script runs in the foreground and walks through five visible stages, so you always
see what's happening:

1. **GPU check** — attaches the NVIDIA GPU to Ollama when the host is set up for it; if it
   finds a GPU that *isn't* set up yet, it **offers to do the one-time setup right there**
   (see "GPU acceleration" below). Saying no just means CPU.
2. **Ollama image download** (cached after the first run)
3. **Build** of the two services
4. **Container start**
5. **Chat model download** — everything in the `OLLAMA_MODELS` list, by default
   `qwen2.5:3b` (the assistant's default — steadier at multi-step tool calling) and
   `qwen2.5:1.5b` (the smaller, faster sibling); a couple of GB on the first run,
   streamed to your terminal, instant after. The first model in the list is the default;
   edit the list in `.env` and re-run to change the lineup. The default model is then
   **loaded and warmed up** as part of startup — loading takes about a minute, and models
   stay loaded afterwards (`OLLAMA_KEEP_ALIVE=-1`), so messages answer in seconds rather
   than paying that minute on the first chat after every pause.

Every download is retried automatically on failure or stall, so a network blip doesn't
mean debugging — worst case the script fails loudly after several attempts. It ends with
"✔ Ready." when the assistant can actually answer.

Plain compose works too (no GPU offer, CPU unless `OLLAMA_GPU_DEVICE` is set in `.env`,
model downloads in the background — watch it with `podman compose logs -f ollama-pull`):

```bash
podman compose up -d          # or: docker compose up -d
```

Then open:

- **Angular console** (the front-end) → <http://localhost:4200/>
- **Ticket board** (the "external" system) → <http://localhost:5090/>
- **Single-file test console** (quick API poke) → <http://localhost:5080/>
- **API reference (Scalar)** → <http://localhost:5080/scalar/v1>

Try: *"Create a ticket: the login page returns a 500 error when I submit."* — the assistant
will gather any missing details, warn if a similar ticket already exists, and show a
confirmation card you can edit before it creates anything. Watch it appear on the board.

The console header has four switchers, all taking effect on the next message:

- **user** — who you are; the API scopes tickets to this user. It defaults to `alice`, who
  owns the seed tickets — change it to test isolation.
- **provider** and **model** — which LLM answers. The model field is a **dropdown of what's
  actually usable**: for Ollama, the locally installed models; for hosted providers, the
  configured model — or "⚠ no API key" when the key is missing from `.env`, so an unusable
  provider is obvious at a glance.
- **⚙️ compute** (Ollama only) — where inference *should* run: *⚡ GPU* (the default — used
  when the container has one, CPU otherwise) or *🐢 CPU* (forced). Next to it, a status
  badge shows where the loaded model is *actually* running,
  straight from Ollama's own report: `⚡ GPU attached`, `🐢 CPU only`, a split (`⚡🐢`)
  when the model doesn't fully fit in the card's memory, or `💤 idle` when nothing is
  loaded (still showing what the next message will use, e.g. `💤 idle (⚡ GPU ready)`).
  When the
  machine has an NVIDIA GPU that the container isn't using, the badge says so directly —
  `(GPU not attached)` — the fix being the one-time GPU setup below.

### GPU acceleration for Ollama (optional)

GPU inference makes local replies dramatically faster (a 3B model that takes ~30 s per
reply on a CPU answers in a few seconds fully in VRAM). The up scripts handle everything:

- **Already set up?** The GPU is attached automatically — no flags, nothing to remember.
- **GPU present but not set up?** The script **asks** whether to do the one-time setup and,
  on a yes, runs it right there: on Linux it installs the driver (if missing), the CUDA
  tools, NVIDIA's container toolkit, and the CDI spec via `sudo` (you'll be asked for your
  password); on Windows it installs the toolkit + CDI spec *inside the podman machine VM*
  (the Windows NVIDIA driver you already have is enough host-side — WSL2 projects it into
  the VM automatically). Saying no, or running non-interactively, just means CPU.
- **No NVIDIA GPU?** CPU, silently.

The attach decision happens at container-creation time — podman refuses to create a
container whose GPU device isn't available, so it can't be "tried" per request; that's why
the scripts detect up front. What *can* change per request is whether an attached GPU is
used: the console's ⚙️ selector switches between *GPU (auto)* and *CPU only* on the next
message, no restart (it maps to Ollama's `num_gpu` option; 0 = no layers offloaded). With
no GPU attached, both settings mean CPU.

To force the matter regardless of detection, set `OLLAMA_GPU_DEVICE=nvidia.com/gpu=all`
(or leave it empty for CPU) in `.env` and recreate: `podman compose up -d --force-recreate
ollama`. Pulled models survive recreation either way (they live in the `ollama-data` volume).

Prefer doing the setup by hand, or on a non-dnf distro? These are the steps the script
runs (Fedora shown; see the [NVIDIA container toolkit docs](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/latest/install-guide.html)
for other distros):

```bash
# 1. NVIDIA driver + CUDA userspace tools — afterwards `nvidia-smi` must print your GPU.
#    (On Fedora these come from RPM Fusion; the driver may already be installed.)
sudo dnf install akmod-nvidia xorg-x11-drv-nvidia-cuda

# 2. the container toolkit, from NVIDIA's own repo
curl -s -L https://nvidia.github.io/libnvidia-container/stable/rpm/nvidia-container-toolkit.repo \
  | sudo tee /etc/yum.repos.d/nvidia-container-toolkit.repo
sudo dnf install nvidia-container-toolkit

# 3. generate the CDI spec that lets podman see the GPU
sudo nvidia-ctk cdi generate --output=/etc/cdi/nvidia.yaml

# sanity check — should print your GPU from inside a container
podman run --rm --device nvidia.com/gpu=all ubuntu nvidia-smi
```

If the driver was freshly installed in step 1 (rather than already present), reboot once
before continuing so the kernel modules load.

**Windows** — the normal NVIDIA driver on Windows is step 1 (WSL2 automatically projects it
into every Linux VM). Unlike Docker Desktop, which bundles the container-runtime GPU glue,
podman's VM starts bare — so steps 2–3 happen *inside* the podman machine:

```powershell
# 2. the container toolkit, inside the machine's Fedora-based VM
podman machine ssh "sudo dnf install -y nvidia-container-toolkit"

# 3. generate the CDI spec inside the VM
podman machine ssh "sudo nvidia-ctk cdi generate --output=/etc/cdi/nvidia.yaml"

# sanity check — should print your GPU from inside a container
podman run --rm --device nvidia.com/gpu=all ubuntu nvidia-smi
```

This survives podman machine restarts, but a `podman machine rm` + `init` wipes the VM —
`.\up.ps1` will simply offer the setup again afterwards. Skipping setup never breaks
anything; it just means CPU.

Verify Ollama picked it up with `podman compose exec ollama ollama ps` after the first
prompt — the `PROCESSOR` column should say `GPU`, or a GPU/CPU split when the model
doesn't fully fit in the card's memory (bigger model = more memory needed: roughly, a
model's download size plus some headroom has to fit for it to run fully on the GPU — the
console's status badge shows what you're actually getting).

### Locally with the .NET SDK

Requires the .NET 10 SDK and a local Ollama (`ollama serve` + `ollama pull qwen2.5:3b`).

```bash
dotnet run --project src/TicketingMock.Api     # ticket backend on :5090
dotnet run --project src/TicketAssistant.Api   # assistant on :5080
```

For the Angular console you'll also need Node 20+; then `cd src/web && npm install && npm start`
serves it on :4200. (The single-file console at <http://localhost:5080/> needs no Node.)

## Configuration

Set via `appsettings.json`, environment variables, or `.env` (see `.env.example`).

| Setting | Purpose | Default |
| --- | --- | --- |
| `Llm:Provider` | `Ollama` \| `Anthropic` \| `OpenAI` \| `Google` | `Ollama` |
| `Ollama:Models` | Space-separated local models (need tool-calling support): the **first is the default**, all are auto-downloaded and offered in the console's dropdown | `qwen2.5:3b qwen2.5:1.5b` |
| `Anthropic/OpenAI/Google :ApiKey` | Key for the chosen hosted provider | — |
| `Anthropic/OpenAI/Google :Model` | Model for that provider | `claude-sonnet-5` / `gpt-4o-mini` / `gemini-flash-latest` |
| `Tickets:Backends` | Which backends to run **together** (space/comma separated): `Http` (the mock) · `Jira` (real Jira Cloud via per-user OAuth, see below) · `InMemory` (offline stub). e.g. `Http Jira` | `Http` |
| `Atlassian:ClientId` / `:ClientSecret` | OAuth 2.0 (3LO) app credentials (required for `Jira`) | — |
| `Atlassian:RedirectUri` / `:FrontendOrigin` | OAuth callback URL and the console's origin | `…:5080/api/auth/jira/callback` / `…:4200` |
| `Tickets:Jira:ProjectKey` | *Optional* default project for new tickets (projects are otherwise chosen per ticket in the UI); also `:IssueType` / `:ScopeToReporter` | — |
| `OLLAMA_GPU_DEVICE` (.env, compose only) | Device handed to the Ollama container; the up scripts set it automatically | empty = CPU |

Only Ollama runs with no API key. `qwen3:4b` / `qwen3:8b` are heavier local alternatives with
more reliable tool calling. All of these are defaults — the console's switchers override the
provider, model, and GPU/CPU choice per request without touching configuration.

### Connecting a real Jira (Jira Cloud, per-user OAuth)

The `ITicketProvider` seam means a real backend is a drop-in: `JiraTicketProvider` speaks to
Jira Cloud's REST v3 API, and the orchestration loop, tools, and console are unchanged. Rather
than one shared token, each user **logs into their own Jira** through an OAuth popup, and the
assistant then acts as that account — reading across **all their sites and projects** and
creating tickets into whichever project they choose, so they can hot-switch topics in one chat.

**1. Register an OAuth 2.0 (3LO) app** at
[developer.atlassian.com → your apps → Create → OAuth 2.0 integration](https://developer.atlassian.com/console/myapps/):
- **Permissions → Jira API** with scopes `read:jira-work`, `write:jira-work`, `read:jira-user`
  (add `offline_access` too — it's requested automatically for the refresh token).
- **Authorization → OAuth 2.0 (3LO)**, callback URL `http://localhost:5080/api/auth/jira/callback`.
- **Settings → Authorization → Access**: choose **Account-level** if you want the assistant to
  reach *every* site (workspace) on the account. **Resource-level** limits it to the one site the
  user picks at login.
- Copy the **Client ID** and **Secret** from **Settings**.

**2. Fill in `.env`** (copied from `.env.example`):
```dotenv
TICKETS_BACKENDS=Http Jira      # run the mock and Jira together (or just "Jira")
ATLASSIAN_CLIENT_ID=<your client id>
ATLASSIAN_CLIENT_SECRET=<your client secret>
# Optional: a default project for new tickets. Leave unset to pick per ticket in the UI.
#TICKETS_JIRA_PROJECT_KEY=SUP
```

**3. Run** `./up.sh` (or `.\up.ps1`), open the console at **http://localhost:4200**, and click
**Connect Jira**. A popup walks you through Atlassian's login/consent; once it closes you're
connected. Ask about your tickets (across all projects/sites), and "create a ticket for…" shows
a confirmation card with a **project picker** — approve and it lands a real issue, its id linking
to `…/browse/KEY` on the right site.

How it works: the popup returns to the API, which exchanges the code for tokens and stores them
**server-side**, attached to your session. The browser only ever holds an opaque bearer session
id; the Jira tokens (and their refresh) never leave the server. See
[`Auth/`](src/TicketAssistant.Api/Auth) for the flow.

What the provider maps for you: plain text ↔ **ADF** (Jira's rich body format) for descriptions
and comments; the app's priorities ↔ Jira's `Highest…Lowest`; a status change ↔ the matching
**workflow transition** (Jira won't let you set a status directly); an assignee name/email ↔ a
Jira `accountId` via user search; and "related" tickets ↔ best-effort *Relates* issue links.

Worth knowing:

- **Multi-site.** Reads fan out over every site the token can reach and merge; writes route to
  the site hosting the target project/issue. This assumes **project keys are unique across your
  sites** (the norm) — a colliding key resolves to the first site found. Needs **Account-level**
  access (above); Resource-level grants just the one selected site.
- **Genuinely per-user.** Each session acts as its own logged-in account, so `ScopeToReporter`
  (default `true`) means "only tickets you raised". Set `TICKETS_JIRA_SCOPE_TO_REPORTER=false`
  to include everything the account can see.
- **Status transitions depend on your workflow.** A status change only works if the ticket's
  current status has a transition reaching the target; otherwise you get a clear error naming
  the target. `Blocked`/`Closed` need a workflow that actually has such states.
- **Permissions.** The logged-in account needs Create, Transition, and (for undo) Delete on the
  relevant project. Undoing a create issues a real Jira delete.
- This targets **Jira Cloud** (`*.atlassian.net`, API v3 + ADF). Jira Server/Data Center uses
  different auth and body formats and isn't covered.

## Caveats (it's a PoC)

- **No persistence** — conversations and tickets live in memory; a restart wipes everything.
- **Lightweight auth** — identity is an opaque, server-issued bearer session (so you can't act
  as another user just by naming them), but `POST /api/session` mints one on request with no
  login, and sessions live in memory. Real Jira access *is* gated by OAuth; the mock's per-user
  scoping is still a demo, not a security boundary.
- **Small-model reliability** — a 3B-class local model sometimes writes a tool call as text, replays a
  declined action, or narrates things that didn't happen. The orchestration layer catches and
  corrects the first two (retry, guardrail) and keeps tool calls and confirmation cards
  correct, but the model's *prose* can still be muddled. Hosted providers or a larger local
  model behave more consistently.
- **Duplicate detection is a heuristic** — title keyword overlap, not semantic similarity, so
  very differently-worded duplicates can slip through.
- **Single instance only** — the in-memory stores assume one running process.
