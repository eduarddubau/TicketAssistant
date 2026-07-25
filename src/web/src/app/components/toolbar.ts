import { Component, EventEmitter, Input, Output, inject, signal } from '@angular/core';
import { LlmService } from '../services/llm.service';
import { SessionService } from '../services/session.service';
import { JiraService } from '../services/jira.service';
import { ProjectsService } from '../services/projects.service';
import { DebugService } from '../services/debug.service';

/**
 * The config strip: who you are (scopes the mock), which LLM/model to use, GPU vs CPU for Ollama
 * with a live status badge, and — when Jira is enabled — the connect/disconnect control. All of it
 * just tweaks headers or session state; only a change of user needs a fresh conversation, which it
 * signals to the parent.
 */
@Component({
  selector: 'app-toolbar',
  standalone: true,
  template: `
    <div class="bar">
      <div class="ctl">
        <span class="lbl">Model</span>
        <input list="ollama-models" class="inp model" [value]="llm.model()" (change)="llm.setModel($any($event.target).value)" />
        <datalist id="ollama-models">
          @for (m of llm.ollamaModels(); track m) { <option [value]="m"></option> }
        </datalist>
      </div>

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

      <div class="ctl">
        <span class="lbl">User</span>
        <input class="inp user" [value]="session.userName()" (change)="onUser($any($event.target).value)" />
      </div>

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
    @if (error()) { <div class="err">{{ error() }}</div> }
  `,
  styles: [`
    /* One height for every pill in the bar. The .ctl pills can't take it directly — they're sized
       by padding on purpose (see below) — so it mirrors what their padding adds up to:
       control line box (1.15rem) + vertical padding (2 x 0.5rem) + border (2 x 1px). Anything with
       an explicit height uses this so it sits level with them. */
    :host { display: block; --pill-h: calc(1.15rem + 1rem + 2px); }
    .bar { display: flex; align-items: center; gap: 0.5rem; flex-wrap: wrap; justify-content: flex-end; }

    /* A fixed height plus a shared line-height on every child is what actually lines the tiny
       uppercase label up with the input/select text — flex centring alone leaves them optically
       off, because form controls and spans have different intrinsic line boxes. */
    /* Height comes from padding, not a fixed value: baseline alignment inside a fixed-height flex
       box packs the content to the top. With both control types on equal explicit heights, padding
       sizing still yields identical pills. */
    .ctl {
      display: flex; align-items: baseline; gap: 0.45rem;
      padding: 0.5rem 0.7rem;
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
    /* Inputs and selects must share an explicit height: a select's UA box is taller than an
       input's, which under baseline alignment pushed the select pills' text ~2.5px higher than
       the input pills'. Equal boxes give both the same baseline position inside the pill. */
    .inp, .sel {
      background: transparent; border: 0; color: var(--text); font-size: 0.8rem;
      padding: 0; margin: 0; line-height: 1.15; height: 1.15rem;
    }
    .inp:focus, .sel:focus { outline: none; }
    .inp.model { width: 8.5rem; }
    .inp.user { width: 5rem; }
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
