import { Component, EventEmitter, Input, Output, inject, signal } from '@angular/core';
import { LlmService } from '../services/llm.service';
import { SessionService } from '../services/session.service';
import { JiraService } from '../services/jira.service';
import { ProjectsService } from '../services/projects.service';
import { DebugService } from '../services/debug.service';
import { KindsService } from '../services/kinds.service';
import { SourcesService } from '../services/sources.service';
import { FilterPill } from './filter-pill';

/**
 * The header's controls, in two groups. On the left, what you're looking at: which systems, which
 * kinds of item, and who you are (which scopes the mock). On the right, how the machine is set up:
 * LLM provider, model,
 * GPU vs CPU with a live status badge, the debug console, and — when Jira is enabled — the
 * connect/disconnect control. The split is the point: the left pair changes the answers you get, the
 * right group changes the machinery that produces them.
 *
 * All of it just tweaks headers or session state; only a change of user needs a fresh conversation,
 * which it signals to the parent.
 */
@Component({
  selector: 'app-toolbar',
  standalone: true,
  imports: [FilterPill],
  template: `
    <div class="bar">
      <div class="grp">
        <!-- Where the assistant reads from, then what it reads. Both are enforced by the API on
             every read, so they hold regardless of what the model decides to do; the wider question
             (which system) sits left of the narrower one (which kind of item). -->
        <app-filter-pill
          [summary]="sources.summary()" [options]="sources.available()"
          [selected]="sources.active()" allLabel="All sources"
          emptyHint="No backends have answered yet."
          hint="Which systems the assistant reads from"
          (toggled)="sources.toggle($event)" (cleared)="sources.clear()">
          <svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor"
               stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <ellipse cx="12" cy="6" rx="8" ry="3"/><path d="M4 6v6c0 1.7 3.6 3 8 3s8-1.3 8-3V6"/>
            <path d="M4 12v6c0 1.7 3.6 3 8 3s8-1.3 8-3v-6"/>
          </svg>
        </app-filter-pill>

        <app-filter-pill
          [summary]="kinds.summary()" [options]="kinds.options()"
          [selected]="kinds.active()" allLabel="All kinds"
          emptyHint="No kinds yet — connect a backend first."
          hint="Which kinds of item the assistant reads"
          (toggled)="kinds.toggle($event)" (cleared)="kinds.clear()">
          <svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor"
               stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <path d="M3 5h18M6 12h12M10 19h4"/>
          </svg>
        </app-filter-pill>

        <!-- Who the mock board thinks you are. "admin" is its reserved name for the whole board. -->
        <div class="ctl" title="Who you are on the mock board — it scopes tickets to what you raised or were assigned. 'admin' sees the whole board.">
          <span class="lbl">User</span>
          <select class="sel user" [value]="session.userName()" (change)="onUser($any($event.target).value)">
            @for (u of session.users; track u) { <option [value]="u">{{ u }}</option> }
          </select>
        </div>
      </div>

      <div class="grp right">
      <!-- Provider first, then its model: choosing a provider resets the model to that provider's
           default, so the wider choice belongs upstream of the narrower one. -->
      <div class="ctl">
        <span class="lbl">Provider</span>
        <select class="sel" [value]="llm.provider()" (change)="onProvider($any($event.target).value)">
          @for (p of llm.info()?.providers ?? []; track p) {
            <option [value]="p" [disabled]="!(llm.info()?.configured?.[p])">
              {{ p }}{{ llm.info()?.configured?.[p] ? '' : ' (no key)' }}
            </option>
          }
        </select>
      </div>

      <div class="ctl">
        <span class="lbl">Model</span>
        <select class="sel model" [value]="llm.model()" (change)="llm.setModel($any($event.target).value)">
          @for (m of llm.modelsFor(); track m) { <option [value]="m">{{ m }}</option> }
        </select>
      </div>

      <div class="ctl">
        <span class="lbl">Compute</span>
        <!-- "GPU" is the empty value: no override, which means Ollama uses the GPU when one is
             attached and falls back to CPU when not. "CPU" forces CPU-only inference. -->
        <select class="sel" [value]="llm.compute()" (change)="llm.setCompute($any($event.target).value)">
          <option value="">GPU</option>
          <option value="cpu">CPU</option>
        </select>
      </div>

      @if (llm.computeStatus(); as cs) {
        <span class="badge" [class.gpu]="cs.processor === 'GPU'">
          <span class="spark"></span>
          {{ cs.loaded ? cs.processor : (cs.gpuAttached ? 'GPU idle' : (cs.hostHasGpu ? 'GPU off' : 'CPU')) }}
        </span>
      }

      <span class="sep"></span>

      <!-- Opens the debug console. Also what makes the API stream its trace at all, so it reads
           as a switch rather than a view: off means nothing extra is computed or sent. -->
      <button class="ghost dbg" [class.on]="debug.enabled()" (click)="debug.toggle()"
              [title]="debug.enabled() ? 'Hide the debug console (Ctrl+\`)' : 'Show what the assistant is doing under the hood (Ctrl+\`)'">
        <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor"
             stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <path d="m8 8-4 4 4 4"/><path d="m16 8 4 4-4 4"/>
        </svg>
        Debug
      </button>

      @if (jiraEnabled) {
        @if (jira.status().connected) {
          <span class="jira-pill">
            <span class="on"></span>
            {{ jira.status().accountEmail || 'Connected' }}
            @if (jira.status().sites?.length) { <span class="muted">· {{ jira.status().sites!.length }} site(s)</span> }
          </span>
          <button class="ghost" (click)="logout()">Disconnect</button>
        } @else {
          <button class="cta" (click)="connect()" [disabled]="connecting()">
            <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round">
              <path d="M9 12a3 3 0 0 1 3-3h3a3 3 0 1 1 0 6h-1"/><path d="M15 12a3 3 0 0 1-3 3H9a3 3 0 1 1 0-6h1"/>
            </svg>
            {{ connecting() ? 'Connecting…' : 'Connect Jira' }}
          </button>
        }
      }
      </div>
    </div>
    @if (error()) { <div class="err">{{ error() }}</div> }
  `,
  styles: [`
    /* One height for everything in the header, the New chat button included (app.css uses the same
       34px). Every pill takes it explicitly and centres its contents, so nothing depends on a
       control's intrinsic line box any more — which is what used to leave the text in a pill sitting
       a pixel off its middle. */
    :host { display: block; --pill-h: 34px; }
    .bar { display: flex; align-items: center; gap: 0.5rem; flex-wrap: wrap; }
    /* Two clusters, pushed apart: reading scope on the left, machinery on the right. Each wraps on
       its own so a narrow window stacks the groups instead of interleaving them. */
    .grp { display: flex; align-items: center; gap: 0.5rem; flex-wrap: wrap; }
    .grp.right { margin-left: auto; justify-content: flex-end; }

    /* Label and value are centred together as one row. Now that both are the same kind of control
       (a select) with the same line box, centring lines them up without the baseline gymnastics the
       text inputs needed — and the row as a whole sits in the middle of the pill. */
    .ctl {
      display: flex; align-items: center; gap: 0.45rem;
      height: var(--pill-h); padding: 0 0.7rem;
      background: var(--surface); border: 1px solid var(--border); border-radius: var(--r-full);
      transition: border-color 0.15s var(--ease), background 0.15s var(--ease);
    }
    .ctl:focus-within { border-color: var(--border-strong); background: var(--surface-2); }
    /* With the label and both control types on equal, explicit line boxes, centring puts every
       string's optical centre on the pill's centre — which keeps the label level with its value
       *and* the input pills level with the select pills. */
    .lbl {
      font-size: 0.6rem; text-transform: uppercase; letter-spacing: 0.07em;
      color: var(--text-faint); font-weight: 700;
      line-height: 1; white-space: nowrap;
    }
    .sel {
      background: transparent; border: 0; color: var(--text); font-size: 0.8rem;
      padding: 0; margin: 0; line-height: 1.15; height: 1.15rem;
    }
    .sel:focus { outline: none; }
    /* A native select won't honour line-height (the UA lays out its own inner box), which is what
       kept the dropdowns sitting low next to their labels. Strip the native appearance and draw
       the caret ourselves, so the text uses our line box like every other control. */
    .sel {
      cursor: pointer;
      appearance: none; -webkit-appearance: none; -moz-appearance: none;
      padding-right: 1.05rem;
      background-image: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 12 12'%3E%3Cpath d='M2.5 4.5 6 8l3.5-3.5' fill='none' stroke='%239aa5b1' stroke-width='1.6' stroke-linecap='round' stroke-linejoin='round'/%3E%3C/svg%3E");
      background-repeat: no-repeat;
      background-position: right center;
      background-size: 11px 11px;
    }
    .sel option { background: #12141c; color: var(--text); }

    .badge {
      display: flex; align-items: center; gap: 0.4rem;
      font-size: 0.68rem; font-weight: 700; letter-spacing: 0.02em; line-height: 1;
      height: var(--pill-h); padding: 0 0.7rem; border-radius: var(--r-full);
      background: var(--surface); border: 1px solid var(--border); color: var(--text-dim);
    }
    .badge.gpu { background: rgba(52, 211, 153, 0.12); border-color: rgba(52, 211, 153, 0.35); color: #7ff0c4; }
    .badge .spark { width: 6px; height: 6px; border-radius: 50%; background: currentColor; box-shadow: 0 0 8px currentColor; }

    .sep { width: 1px; height: 22px; background: var(--border); margin: 0 0.1rem; }

    .cta {
      display: flex; align-items: center; gap: 0.45rem;
      height: var(--pill-h); padding: 0 0.95rem; border: 0; border-radius: var(--r-full);
      color: #fff; font-weight: 600; font-size: 0.8rem; line-height: 1;
      background: var(--grad); box-shadow: var(--glow);
      transition: transform 0.15s var(--ease), box-shadow 0.2s var(--ease), filter 0.2s;
    }
    .cta:hover:not(:disabled) { transform: translateY(-1px); box-shadow: 0 16px 46px -10px rgba(124, 108, 255, 0.75); filter: brightness(1.05); }
    .cta:active { transform: translateY(0); }
    .cta:disabled { opacity: 0.65; cursor: default; }

    .ghost {
      display: flex; align-items: center; height: var(--pill-h); padding: 0 0.85rem;
      border-radius: var(--r-full); background: var(--surface); border: 1px solid var(--border);
      color: var(--text-dim); font-size: 0.78rem; line-height: 1; transition: 0.15s var(--ease);
    }
    .ghost:hover { background: var(--surface-2); color: var(--text); border-color: var(--border-strong); }

    .ghost.dbg { gap: 0.4rem; font-weight: 600; }
    /* Lit while it's on: this one has a running cost (the API traces every turn), so it shouldn't
       be possible to leave it enabled without noticing. */
    .ghost.dbg.on {
      background: rgba(124, 108, 255, 0.16); border-color: rgba(150, 120, 255, 0.6); color: #ded8ff;
      box-shadow: inset 0 0 0 1px rgba(124, 108, 255, 0.15);
    }

    .jira-pill {
      display: flex; align-items: center; gap: 0.4rem;
      font-size: 0.75rem; color: #cfeadd; line-height: 1;
      height: var(--pill-h); padding: 0 0.75rem; border-radius: var(--r-full);
      background: rgba(52, 211, 153, 0.1); border: 1px solid rgba(52, 211, 153, 0.28);
    }
    .jira-pill .on { width: 7px; height: 7px; border-radius: 50%; background: var(--ok); box-shadow: 0 0 8px var(--ok); }
    .jira-pill .muted { color: rgba(207, 234, 221, 0.6); }

    .err { margin-top: 0.4rem; font-size: 0.75rem; color: #ffb4b4; text-align: right; }
  `],
})
export class Toolbar {
  @Input() jiraEnabled = false;
  @Output() userChanged = new EventEmitter<string>();

  readonly llm = inject(LlmService);
  readonly session = inject(SessionService);
  readonly jira = inject(JiraService);
  readonly debug = inject(DebugService);
  readonly kinds = inject(KindsService);
  readonly sources = inject(SourcesService);
  private readonly projects = inject(ProjectsService);

  readonly connecting = signal(false);
  readonly error = signal<string | null>(null);


  onUser(name: string): void {
    const trimmed = name.trim();
    if (trimmed) this.userChanged.emit(trimmed);
  }

  onProvider(p: string): void {
    this.llm.setProvider(p);
  }

  async connect(): Promise<void> {
    this.error.set(null);
    this.connecting.set(true);
    try {
      await this.jira.connect();
      await this.projects.load();   // Jira projects are now available
    } catch (e: any) {
      this.error.set(e?.message ?? String(e));
    } finally {
      this.connecting.set(false);
    }
  }

  async logout(): Promise<void> {
    await this.jira.logout();
    await this.projects.load();     // drop the disconnected account's Jira projects
  }
}
