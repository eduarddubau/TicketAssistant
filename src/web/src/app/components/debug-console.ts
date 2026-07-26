import { Component, ElementRef, EventEmitter, Output, ViewChild, computed, effect, inject, signal } from '@angular/core';
import { DebugEntry } from '../models';
import { DebugService } from '../services/debug.service';

/** One message as it appears inside an llm_request/llm_response snapshot. */
interface TraceMessage {
  role: string;
  name?: string | null;
  text?: string | null;
  parts: TracePart[];
}

interface TracePart {
  kind: string;
  callId?: string | null;
  name?: string | null;
  arguments?: unknown;
  result?: unknown;
  value?: unknown;
}

/** One tool as the model was offered it. */
interface TraceTool {
  name: string;
  description?: string;
  requiresConfirmation?: boolean;
  schema?: unknown;
}

// Plain-English names for the stages, so the panel doesn't make anyone learn the wire format.
const STAGE_LABELS: Record<string, string> = {
  conversation: 'conversation',
  system_prompt: 'system prompt',
  user_prompt: 'user prompt',
  http_request: 'request',
  http_response: 'response',
  sse: 'stream',
  llm_request: 'to model',
  llm_response: 'from model',
  llm: 'model choice',
  connection: 'connection',
  settings: 'settings',
  filter: 'filter',
  tool_call: 'tool call',
  tool_result: 'tool result',
  guardrail: 'guardrail',
  confirmation: 'confirmation',
  undo: 'undo',
  decision: 'decision',
  error: 'error',
};

/**
 * The debug console: a dockable panel that shows exactly what happened on every turn, in order —
 * the system prompt, each user message, the full context sent to the model, its raw reply, every
 * tool call with its arguments and result, each guardrail that fired, and every frame that crossed
 * the wire in both directions.
 *
 * It renders what it's given rather than interpreting it: known shapes (a conversation snapshot, a
 * tool menu) get a readable view, and the untouched JSON is always one click away underneath, so
 * nothing in the trace is hidden by the presentation.
 */
@Component({
  selector: 'app-debug-console',
  standalone: true,
  // The panel is as wide as the user last dragged it (see startResize).
  host: { '[style.width.px]': 'width()' },
  template: `
    <div class="grip" (pointerdown)="startResize($event)" title="Drag to resize"></div>

    <header class="head">
      <span class="live"></span>
      <h2>Debug console</h2>
      <span class="count">{{ filtered().length }}<span class="of">/{{ entries().length }}</span></span>
      <div class="acts">
        <button class="mini" (click)="copy()" [title]="copied() ? 'Copied' : 'Copy the whole trace as JSON'">
          {{ copied() ? 'Copied ✓' : 'Copy' }}
        </button>
        <button class="mini" (click)="download()" title="Download the trace as a .json file">Save</button>
        <button class="mini" (click)="debug.clear()" title="Clear the trace">Clear</button>
        <button class="mini x" (click)="closed.emit()" title="Close (Ctrl+\`)">✕</button>
      </div>
    </header>

    <div class="filters">
      <input class="search" [value]="query()" (input)="query.set($any($event.target).value)"
             placeholder="Filter by text — searches labels and payloads…" />
      <label class="follow" title="Scroll to the newest entry as it arrives">
        <input type="checkbox" [checked]="follow()" (change)="follow.set($any($event.target).checked)" />
        follow
      </label>
    </div>

    <div class="chips">
      @for (chip of stageChips(); track chip.stage) {
        <button class="chip {{ chip.stage }}" [class.muted]="hiddenStages().has(chip.stage)"
                (click)="toggleStage(chip.stage)" [title]="chip.stage">
          {{ stageLabel(chip.stage) }} <span class="n">{{ chip.count }}</span>
        </button>
      }
    </div>

    <div class="list" #list>
      @for (entry of filtered(); track entry.id) {
        <div class="entry {{ entry.stage }}" [class.open]="opened().has(entry.id)"
             [class.slow]="(entry.sinceMs ?? 0) >= 1000">
          <button class="row" (click)="toggle(entry.id)" [title]="rowTitle(entry)">
            <span class="chev">{{ opened().has(entry.id) ? '▾' : '▸' }}</span>
            <span class="time">{{ time(entry.at) }}</span>
            <!-- How long this step waited for the one before it. Present on every row, which is
                 what makes a slow turn readable: the gap sits on the step that caused it. -->
            <span class="gap">{{ gap(entry) }}</span>
            <span class="src {{ entry.source }}">{{ entry.source === 'server' ? 'api' : 'web' }}</span>
            <span class="tag">{{ stageLabel(entry.stage) }}</span>
            <span class="label">{{ entry.label }}</span>
            @if (entry.ms != null) { <span class="ms" title="Measured by the step itself">{{ dur(entry.ms) }}</span> }
          </button>

          @if (opened().has(entry.id)) {
            <div class="detail">
              @if (prompt(entry); as text) {
                <pre class="prose">{{ text }}</pre>
              }

              @if (messages(entry); as msgs) {
                <div class="sec">
                  <div class="sec-h">{{ msgs.length }} message(s) in context</div>
                  @for (m of msgs; track $index) {
                    <div class="msg {{ m.role }}">
                      <div class="msg-h">
                        <span class="role">{{ m.role }}</span>
                        @if (m.name) { <span class="who">{{ m.name }}</span> }
                        @if (m.text) { <span class="len">{{ m.text.length }} chars</span> }
                      </div>
                      @if (m.text) { <pre class="prose">{{ m.text }}</pre> }
                      @for (p of m.parts; track $index) {
                        <div class="part">
                          @if (p.kind === 'toolCall') {
                            <div class="part-h">→ calls <b>{{ p.name }}</b> <span class="cid">{{ p.callId }}</span></div>
                            <pre class="json" [innerHTML]="json(p.arguments)"></pre>
                          } @else if (p.kind === 'toolResult') {
                            <div class="part-h">← result <span class="cid">{{ p.callId }}</span></div>
                            <pre class="json" [innerHTML]="json(p.result)"></pre>
                          } @else {
                            <div class="part-h">{{ p.kind }}</div>
                            <pre class="json" [innerHTML]="json(p.value ?? p)"></pre>
                          }
                        </div>
                      }
                    </div>
                  }
                </div>
              }

              @if (tools(entry); as toolMenu) {
                <details class="sec">
                  <summary>{{ toolMenu.length }} tool(s) offered to the model</summary>
                  @for (t of toolMenu; track t.name) {
                    <div class="tool">
                      <div class="tool-h">
                        <b>{{ t.name }}</b>
                        @if (t.requiresConfirmation) { <span class="warn">needs confirmation</span> }
                      </div>
                      <p class="tool-d">{{ t.description }}</p>
                      <details>
                        <summary>schema</summary>
                        <pre class="json" [innerHTML]="json(t.schema)"></pre>
                      </details>
                    </div>
                  }
                </details>
              }

              <details class="sec" [open]="!structured(entry)">
                <summary>raw payload</summary>
                <pre class="json" [innerHTML]="json(entry.detail)"></pre>
              </details>
            </div>
          }
        </div>
      } @empty {
        <p class="empty">
          @if (entries().length) { Nothing matches this filter. }
          @else { Nothing yet — send a message and every step of the turn shows up here. }
        </p>
      }
    </div>
  `,
  styles: [`
    :host {
      display: flex; flex-direction: column; min-height: 0; flex-shrink: 0;
      border-left: 1px solid var(--border);
      background: var(--panel);
      backdrop-filter: var(--blur); -webkit-backdrop-filter: var(--blur);
      font-size: 0.78rem;
      position: relative;
      animation: rise 0.3s var(--ease);
    }

    /* Drag anywhere on the seam to widen the panel — a debug console is only as useful as the
       amount of JSON it can show at once. */
    .grip { position: absolute; left: -3px; top: 0; bottom: 0; width: 7px; cursor: col-resize; z-index: 5; }
    .grip:hover { background: linear-gradient(90deg, transparent, var(--accent-line), transparent); }

    .head { display: flex; align-items: center; gap: 0.5rem; padding: 0.6rem 0.75rem; border-bottom: 1px solid var(--border); }
    .head h2 { margin: 0; font-size: 0.8rem; font-weight: 700; letter-spacing: 0.01em; }
    .live { width: 7px; height: 7px; border-radius: 50%; background: var(--accent); box-shadow: 0 0 8px var(--accent); }
    .count { font-size: 0.68rem; color: var(--text-dim); font-variant-numeric: tabular-nums; }
    .count .of { color: var(--text-faint); }
    .acts { margin-left: auto; display: flex; gap: 0.3rem; }

    .mini {
      padding: 0.24rem 0.5rem; border-radius: var(--r-sm); font-size: 0.7rem; line-height: 1.2;
      background: var(--surface); border: 1px solid var(--border); color: var(--text-dim);
      transition: 0.15s var(--ease);
    }
    .mini:hover { background: var(--surface-2); color: var(--text); border-color: var(--border-strong); }
    .mini.x { padding: 0.24rem 0.42rem; }

    .filters { display: flex; align-items: center; gap: 0.5rem; padding: 0.5rem 0.75rem 0.35rem; }
    .search {
      flex: 1; min-width: 0; padding: 0.35rem 0.6rem; border-radius: var(--r-full);
      background: var(--surface); border: 1px solid var(--border); color: var(--text); font-size: 0.74rem;
    }
    .search:focus { outline: none; border-color: var(--accent-line); }
    .follow { display: flex; align-items: center; gap: 0.3rem; color: var(--text-dim); font-size: 0.7rem; white-space: nowrap; }
    .follow input { accent-color: var(--accent); }

    .chips { display: flex; flex-wrap: wrap; gap: 0.28rem; padding: 0.2rem 0.75rem 0.5rem; }
    .chip {
      display: flex; align-items: center; gap: 0.28rem;
      padding: 0.16rem 0.45rem; border-radius: var(--r-full); font-size: 0.66rem; font-weight: 600;
      background: var(--surface); border: 1px solid var(--border); color: var(--text-dim);
      transition: 0.15s var(--ease);
    }
    .chip:hover { border-color: var(--border-strong); color: var(--text); }
    .chip .n { color: var(--text-faint); font-variant-numeric: tabular-nums; }
    .chip.muted { opacity: 0.35; }

    .list { flex: 1; overflow: auto; padding: 0 0.4rem 0.8rem; }
    .empty { color: var(--text-faint); font-size: 0.74rem; padding: 1.2rem 0.6rem; text-align: center; line-height: 1.6; }

    .entry { border-radius: var(--r-sm); margin-bottom: 2px; border-left: 2px solid transparent; }
    .entry:hover { background: var(--hover); }
    .entry.open { background: var(--hover-2); }

    /* The stage's colour is the panel's index: the same hue on the chip, the left edge, and the
       tag, so a turn's shape is readable by colour alone before reading a word of it. */
    /* The hues are the app's category index rather than its accent scheme, so they stay put when the
       scheme changes: "purple means a tool call" would stop meaning anything if choosing the emerald
       scheme turned half the panel green. Each has a mark for the edge and a -fg for the text, since
       what reads on a dark panel and what reads on a light one are not the same colour. */
    .entry.llm_request { border-left-color: var(--h-blue); }
    .entry.llm_response { border-left-color: var(--h-cyan); }
    .entry.tool_call { border-left-color: var(--h-purple); }
    .entry.tool_result { border-left-color: var(--h-green); }
    .entry.guardrail { border-left-color: var(--h-amber); }
    .entry.confirmation { border-left-color: var(--h-pink); }
    .entry.undo { border-left-color: var(--h-gray); }
    .entry.user_prompt { border-left-color: var(--h-magenta); }
    .entry.system_prompt { border-left-color: var(--h-violet); }
    .entry.error { border-left-color: var(--h-red); }
    .entry.llm { border-left-color: var(--h-sky); }
    .entry.connection { border-left-color: var(--h-green); }
    .entry.filter { border-left-color: var(--h-orange); }
    .entry.settings { border-left-color: var(--h-gray); }

    .chip.llm_request, .entry.llm_request .tag { color: var(--h-blue-fg); }
    .chip.llm_response, .entry.llm_response .tag { color: var(--h-cyan-fg); }
    .chip.tool_call, .entry.tool_call .tag { color: var(--h-purple-fg); }
    .chip.tool_result, .entry.tool_result .tag { color: var(--h-green-fg); }
    .chip.guardrail, .entry.guardrail .tag { color: var(--h-amber-fg); }
    .chip.confirmation, .entry.confirmation .tag { color: var(--h-pink-fg); }
    .chip.user_prompt, .entry.user_prompt .tag { color: var(--h-magenta-fg); }
    .chip.system_prompt, .entry.system_prompt .tag { color: var(--h-violet-fg); }
    .chip.error, .entry.error .tag { color: var(--h-red-fg); }
    .chip.llm, .entry.llm .tag { color: var(--h-sky-fg); }
    .chip.connection, .entry.connection .tag { color: var(--h-green-fg); }
    .chip.filter, .entry.filter .tag { color: var(--h-orange-fg); }
    .chip.settings, .entry.settings .tag { color: var(--h-gray-fg); }

    .row {
      display: flex; align-items: baseline; gap: 0.4rem; width: 100%; text-align: left;
      padding: 0.3rem 0.45rem; background: none; border: 0; color: var(--text-dim);
      font-family: ui-monospace, 'SF Mono', 'JetBrains Mono', Menlo, monospace; font-size: 0.71rem; line-height: 1.5;
    }
    .chev { color: var(--text-faint); width: 0.7rem; flex-shrink: 0; }
    .time { color: var(--text-faint); font-variant-numeric: tabular-nums; flex-shrink: 0; }
    /* Fixed width so the numbers line up into a column that can be scanned for the slow step. */
    .gap { color: var(--text-faint); font-variant-numeric: tabular-nums; flex-shrink: 0; width: 4.2rem; text-align: right; }
    .entry.slow .gap { color: var(--h-amber-fg); font-weight: 600; }
    .src {
      flex-shrink: 0; font-size: 0.6rem; font-weight: 700; text-transform: uppercase; letter-spacing: 0.05em;
      padding: 0 0.28rem; border-radius: 4px; background: var(--surface-2); color: var(--text-faint);
    }
    .src.server { background: var(--accent-soft); color: var(--accent-fg); }
    .tag { flex-shrink: 0; font-weight: 600; }
    .label { color: var(--text); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; flex: 1; min-width: 0; }
    .entry.open .label { white-space: normal; overflow: visible; }
    .ms { flex-shrink: 0; color: var(--text-faint); font-variant-numeric: tabular-nums; }

    .detail { padding: 0.25rem 0.5rem 0.6rem 1.1rem; display: flex; flex-direction: column; gap: 0.5rem; }

    .sec { border: 1px solid var(--border); border-radius: var(--r-sm); padding: 0.45rem 0.55rem; background: var(--inset); }
    .sec-h, summary { font-size: 0.68rem; font-weight: 700; color: var(--text-dim); letter-spacing: 0.03em; text-transform: uppercase; }
    summary { cursor: pointer; }
    summary:hover { color: var(--text); }

    .msg { border-left: 2px solid var(--border-strong); padding-left: 0.5rem; margin-top: 0.45rem; }
    .msg.system { border-left-color: var(--h-violet); }
    .msg.user { border-left-color: var(--h-magenta); }
    .msg.assistant { border-left-color: var(--h-cyan); }
    .msg.tool { border-left-color: var(--h-green); }
    .msg-h { display: flex; align-items: baseline; gap: 0.4rem; font-size: 0.66rem; }
    .msg-h .role { font-weight: 700; text-transform: uppercase; letter-spacing: 0.05em; color: var(--text-dim); }
    .msg-h .who, .msg-h .len { color: var(--text-faint); }

    .part { margin-top: 0.35rem; }
    .part-h { font-size: 0.67rem; color: var(--text-dim); }
    .part-h b { color: var(--h-purple-fg); }
    .cid { color: var(--text-faint); }

    .tool { margin-top: 0.5rem; }
    .tool-h { display: flex; align-items: center; gap: 0.4rem; font-size: 0.72rem; }
    .tool-h b { color: var(--h-purple-fg); }
    .warn { font-size: 0.6rem; font-weight: 700; color: var(--h-amber-fg); }
    .tool-d { margin: 0.15rem 0 0.25rem; color: var(--text-dim); font-size: 0.7rem; line-height: 1.5; }

    /* Payloads keep their newlines and their indentation — a system prompt read as one wrapped
       blob is a system prompt nobody checks. Long lines scroll rather than reflow. */
    pre {
      margin: 0.3rem 0 0; padding: 0.45rem 0.55rem; border-radius: var(--r-sm);
      background: var(--inset-strong); border: 1px solid var(--border);
      font-family: ui-monospace, 'SF Mono', 'JetBrains Mono', Menlo, monospace;
      font-size: 0.7rem; line-height: 1.55; color: var(--text-dim);
      max-height: 22rem; overflow: auto; white-space: pre-wrap; word-break: break-word;
    }
    pre.prose { color: var(--text); }

    /* The coloured spans are written into the payload with [innerHTML], so they never carry the
       component's scoping attribute — ::ng-deep is what lets these rules reach them. */
    :host ::ng-deep .json .k { color: var(--h-blue-fg); }
    :host ::ng-deep .json .s { color: var(--h-green-fg); }
    :host ::ng-deep .json .n { color: var(--h-amber-fg); }
    :host ::ng-deep .json .b { color: var(--h-pink-fg); }
  `],
})
export class DebugConsole {
  /** Fired when the user closes the panel from its own header. */
  @Output() closed = new EventEmitter<void>();

  readonly debug = inject(DebugService);

  @ViewChild('list') private listRef?: ElementRef<HTMLElement>;

  readonly entries = this.debug.entries;
  readonly query = signal('');
  readonly hiddenStages = signal<ReadonlySet<string>>(new Set());
  readonly opened = signal<ReadonlySet<number>>(new Set());
  readonly follow = signal(true);
  readonly copied = signal(false);
  readonly width = signal(Number(localStorage.getItem('ta-debug-width')) || 460);

  /** Every stage seen, with how many entries carry it — the filter row. */
  readonly stageChips = computed(() => {
    const counts = new Map<string, number>();
    for (const e of this.entries()) counts.set(e.stage, (counts.get(e.stage) ?? 0) + 1);
    return [...counts].map(([stage, count]) => ({ stage, count }));
  });

  readonly filtered = computed(() => {
    const hidden = this.hiddenStages();
    const q = this.query().trim().toLowerCase();
    return this.entries().filter((e) => {
      if (hidden.has(e.stage)) return false;
      if (!q) return true;
      return `${e.stage} ${e.label}`.toLowerCase().includes(q) || this.text(e).toLowerCase().includes(q);
    });
  });

  // JSON is stringified once per entry and reused: the panel re-renders on every change
  // detection pass, and re-serializing a full conversation snapshot each time would be felt.
  // The text cache is dropped wholesale once it outgrows the trace itself, and the highlighted
  // markup is held weakly, so neither outlives the entries they came from.
  private readonly textCache = new Map<number, string>();
  private readonly htmlCache = new WeakMap<object, string>();

  constructor() {
    // Keep the newest entry in view while following, so a running turn reads like a log tail.
    effect(() => {
      this.filtered();
      if (!this.follow()) return;
      queueMicrotask(() => {
        const el = this.listRef?.nativeElement;
        if (el) el.scrollTop = el.scrollHeight;
      });
    });
  }

  time(at: number): string {
    const d = new Date(at);
    return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}:${String(
      d.getSeconds(),
    ).padStart(2, '0')}.${String(d.getMilliseconds()).padStart(3, '0')}`;
  }

  stageLabel(stage: string): string {
    return STAGE_LABELS[stage] ?? stage.replace(/_/g, ' ');
  }

  /**
   * A duration as a person reads it. A local 3B model can take half a minute for one turn, and
   * "31284 ms" is a number you have to decode before you can compare it to anything.
   */
  dur(ms: number): string {
    if (ms < 1000) return `${Math.round(ms)} ms`;
    if (ms < 60_000) return `${(ms / 1000).toFixed(ms < 10_000 ? 2 : 1)} s`;
    const seconds = Math.round(ms / 1000);
    return `${Math.floor(seconds / 60)}m ${String(seconds % 60).padStart(2, '0')}s`;
  }

  /** The step's own cost: the wait between the previous row and this one. Blank on the first row. */
  gap(entry: DebugEntry): string {
    return entry.sinceMs == null ? '' : `+${this.dur(entry.sinceMs)}`;
  }

  /** Hover text: where in the turn this step happened, and how long the wait before it was. */
  rowTitle(entry: DebugEntry): string {
    const parts: string[] = [];
    if (entry.turnMs != null) parts.push(`${this.dur(entry.turnMs)} into the turn`);
    if (entry.sinceMs != null) parts.push(`${this.dur(entry.sinceMs)} after the previous step`);
    if (entry.ms != null) parts.push(`step itself took ${this.dur(entry.ms)}`);
    return parts.join(' · ');
  }

  toggle(id: number): void {
    this.opened.update((open) => {
      const next = new Set(open);
      next.has(id) ? next.delete(id) : next.add(id);
      return next;
    });
  }

  toggleStage(stage: string): void {
    this.hiddenStages.update((hidden) => {
      const next = new Set(hidden);
      next.has(stage) ? next.delete(stage) : next.add(stage);
      return next;
    });
  }

  // ----- detail views -----

  /** A standalone block of prose to show above the payload (a user message, the system prompt). */
  prompt(entry: DebugEntry): string | null {
    const d = entry.detail as any;
    if (!d || typeof d !== 'object') return null;
    return d.systemPrompt ?? d.text ?? null;
  }

  /** The conversation carried by a model request/response snapshot, if this entry has one. */
  messages(entry: DebugEntry): TraceMessage[] | null {
    const raw = (entry.detail as any)?.messages;
    if (!Array.isArray(raw) || raw.length === 0) return null;
    return raw.map((m: any) => ({
      role: m?.role ?? 'unknown',
      name: m?.authorName,
      text: m?.text,
      // The plain text is already shown above, so only the non-text parts are listed here.
      parts: (Array.isArray(m?.contents) ? m.contents : []).filter((c: TracePart) => c?.kind !== 'text'),
    }));
  }

  /** The tool menu carried by a model request snapshot, if this entry has one. */
  tools(entry: DebugEntry): TraceTool[] | null {
    const raw = (entry.detail as any)?.tools;
    return Array.isArray(raw) && raw.length ? raw : null;
  }

  /** Whether this entry has a readable view, which decides if the raw payload starts open. */
  structured(entry: DebugEntry): boolean {
    return !!(this.messages(entry) || this.tools(entry) || this.prompt(entry));
  }

  /** The entry's payload as pretty JSON — cached, and reused by the text filter. */
  text(entry: DebugEntry): string {
    let cached = this.textCache.get(entry.id);
    if (cached === undefined) {
      if (this.textCache.size > 2000) this.textCache.clear();
      cached = pretty(entry.detail);
      this.textCache.set(entry.id, cached);
    }
    return cached;
  }

  /** Pretty JSON with keys/strings/numbers coloured. Escaped before any markup is added. */
  json(value: unknown): string {
    const key = value !== null && typeof value === 'object' ? (value as object) : null;
    if (key) {
      const cached = this.htmlCache.get(key);
      if (cached !== undefined) return cached;
    }

    const html = highlight(pretty(value));
    if (key) this.htmlCache.set(key, html);
    return html;
  }

  // ----- actions -----

  async copy(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.debug.export());
      this.copied.set(true);
      setTimeout(() => this.copied.set(false), 1500);
    } catch {
      /* clipboard blocked (no permission / insecure origin) — Save still works */
    }
  }

  download(): void {
    const blob = new Blob([this.debug.export()], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `ticketassistant-trace-${new Date().toISOString().replace(/[:.]/g, '-')}.json`;
    a.click();
    URL.revokeObjectURL(url);
  }

  /** Drag the seam: the panel's width follows the pointer until it's released. */
  startResize(event: PointerEvent): void {
    event.preventDefault();
    const move = (e: PointerEvent) => {
      const width = Math.min(Math.max(window.innerWidth - e.clientX, 320), Math.min(1100, window.innerWidth - 320));
      this.width.set(Math.round(width));
    };
    const up = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
      localStorage.setItem('ta-debug-width', String(this.width()));
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
  }
}

function pretty(value: unknown): string {
  if (value === undefined) return '';
  if (typeof value === 'string') return value;
  try {
    return JSON.stringify(value, null, 2) ?? String(value);
  } catch {
    return String(value);
  }
}

/**
 * Colours a JSON string for display. HTML is escaped first and the spans added after, so a payload
 * containing markup is shown as text rather than rendered.
 */
function highlight(json: string): string {
  const escaped = json.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  return escaped.replace(
    /("(?:\\.|[^"\\])*")(\s*:)?|\b(true|false|null)\b|(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)/g,
    (_match, str: string, colon: string, literal: string, num: string) => {
      if (str) return colon ? `<span class="k">${str}</span>${colon}` : `<span class="s">${str}</span>`;
      if (literal) return `<span class="b">${literal}</span>`;
      return `<span class="n">${num}</span>`;
    },
  );
}
