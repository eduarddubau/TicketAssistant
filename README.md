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
  `HttpTicketProvider` (calls the mock over REST), `InMemoryTicketProvider` (offline stub),
  and `UserIdForwardingHandler` (forwards the user id to the backend).
- **`Models/`** — the canonical ticket model shared across the app.
- **`wwwroot/index.html`** — a minimal browser chat console for testing: streams SSE,
  renders Markdown, links ticket IDs to the board, shows the editable confirmation cards,
  and hosts the user / provider / model / GPU-CPU switchers. A proper Angular frontend is
  planned but not built yet.

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
5. **Chat model download** — `llama3.2:3b` (the default) plus `qwen2.5:3b` (an alternative
   with steadier tool calling, for A/B-ing from the console's model dropdown); a few GB on
   the first run, streamed to your terminal, instant after. Change `OLLAMA_MODEL` /
   `OLLAMA_EXTRA_MODELS` in `.env` and re-run to pull different ones.

Every download is retried automatically on failure or stall, so a network blip doesn't
mean debugging — worst case the script fails loudly after several attempts. It ends with
"✔ Ready." when the assistant can actually answer.

Plain compose works too (no GPU offer, CPU unless `OLLAMA_GPU_DEVICE` is set in `.env`,
model downloads in the background — watch it with `podman compose logs -f ollama-pull`):

```bash
podman compose up -d          # or: docker compose up -d
```

Then open:

- **Chat console** → <http://localhost:5080/>
- **Ticket board** (the "external" system) → <http://localhost:5090/>
- **API reference (Scalar)** → <http://localhost:5080/scalar/v1>

Try: *"Create a ticket: the login page returns a 500 error when I submit."* — the assistant
will gather any missing details, warn if a similar ticket already exists, and show a
confirmation card you can edit before it creates anything. Watch it appear on the board.

The console header has four switchers, all taking effect on the next message:

- **user** — who you are; the API scopes tickets to this user. It defaults to `alice`, who
  owns the seed tickets — change it to test isolation.
- **provider** and **model** — which LLM answers. Ollama needs no key; the hosted providers
  use the API keys from `.env`. For Ollama the model field is a **dropdown of the models
  actually installed** (fast switching, no typos); hosted providers get a free-text field.
- **⚙️ GPU/CPU** (Ollama only) — where inference *should* run, when the container has a
  GPU. Next to it, a small status badge shows where the loaded model is *actually* running
  right now (e.g. `· on GPU`, `· on CPU`, or a split when it doesn't fully fit in VRAM),
  straight from Ollama's own report.

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
prompt — the `PROCESSOR` column should say `GPU` (or a GPU/CPU split if the model doesn't
fully fit in VRAM). llama3.2:3b (~2 GB) fits comfortably in 6 GB of VRAM; qwen3:8b (~5 GB)
is a tight fit.

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
| `Ollama:Model` | Default local model (needs tool-calling support) | `llama3.2:3b` |
| `OLLAMA_EXTRA_MODELS` (.env, compose only) | Extra models to auto-download for the console's dropdown (space-separated) | `qwen2.5:3b` |
| `Anthropic/OpenAI/Google :ApiKey` | Key for the chosen hosted provider | — |
| `Anthropic/OpenAI/Google :Model` | Model for that provider | `claude-sonnet-5` / `gpt-4o-mini` / `gemini-flash-latest` |
| `Tickets:Backend` | `Http` (the mock) \| `InMemory` (offline stub) | `Http` |
| `OLLAMA_GPU_DEVICE` (.env, compose only) | Device handed to the Ollama container; the up scripts set it automatically | empty = CPU |

Only Ollama runs with no API key. `qwen3:4b` / `qwen3:8b` are heavier local alternatives with
more reliable tool calling. All of these are defaults — the console's switchers override the
provider, model, and GPU/CPU choice per request without touching configuration.

## Caveats (it's a PoC)

- **No persistence** — conversations and tickets live in memory; a restart wipes everything.
- **Not real auth** — "who the user is" comes from a client-supplied `X-User-Id` header, which
  is trivially spoofable. It demonstrates data scoping, not security.
- **Small-model reliability** — `llama3.2:3b` sometimes writes a tool call as text, replays a
  declined action, or narrates things that didn't happen. The orchestration layer catches and
  corrects the first two (retry, guardrail) and keeps tool calls and confirmation cards
  correct, but the model's *prose* can still be muddled. Hosted providers or a larger local
  model behave more consistently.
- **Duplicate detection is a heuristic** — title keyword overlap, not semantic similarity, so
  very differently-worded duplicates can slip through.
- **Single instance only** — the in-memory stores assume one running process.
