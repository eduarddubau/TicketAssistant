import { Component, EventEmitter, Input, Output, inject, signal } from '@angular/core';
import { LlmService } from '../services/llm.service';
import { SessionService } from '../services/session.service';
import { JiraService } from '../services/jira.service';
import { ProjectsService } from '../services/projects.service';
import { DebugService } from '../services/debug.service';
import { KindsService } from '../services/kinds.service';
import { SourcesService } from '../services/sources.service';
import { FilterPill } from './filter-pill';
import { Appearance } from './appearance';
import { Language } from './language';
import { I18nService } from '../services/i18n.service';

/**
 * The header's controls, in two groups. On the left, what you're looking at: which systems, which
 * kinds of item, and who you are (which scopes the mock). On the right, how the machine is set up:
 * LLM provider, model,
 * GPU vs CPU with a live status badge, then — past the separator, where the settings stop being
 * about answers — appearance and the debug console, and, when Jira is enabled, the
 * connect/disconnect control. The split is the point: the left pair changes the answers you get, the
 * right group changes the machinery that produces them.
 *
 * All of it just tweaks headers or session state; only a change of user needs a fresh conversation,
 * which it signals to the parent.
 */
@Component({
  selector: 'app-toolbar',
  standalone: true,
  imports: [FilterPill, Appearance, Language],
  template: `
    <div class="bar">
      <div class="grp">
        <!-- Where the assistant reads from, then what it reads. Both are enforced by the API on
             every read, so they hold regardless of what the model decides to do; the wider question
             (which system) sits left of the narrower one (which kind of item). -->
        <app-filter-pill
          [summary]="sources.summary()" [options]="sources.available()"
          [selected]="sources.active()" [allLabel]="i18n.t('toolbar.sources.all')"
          [emptyHint]="i18n.t('toolbar.sources.empty')"
          [hint]="i18n.t('toolbar.sources.hint')"
          (toggled)="sources.toggle($event)" (cleared)="sources.clear()">
          <svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor"
               stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <ellipse cx="12" cy="6" rx="8" ry="3"/><path d="M4 6v6c0 1.7 3.6 3 8 3s8-1.3 8-3V6"/>
            <path d="M4 12v6c0 1.7 3.6 3 8 3s8-1.3 8-3v-6"/>
          </svg>
        </app-filter-pill>

        <app-filter-pill
          [summary]="kinds.summary()" [options]="kinds.options()"
          [selected]="kinds.active()" [allLabel]="i18n.t('toolbar.kinds.all')"
          [emptyHint]="i18n.t('toolbar.kinds.empty')"
          [hint]="i18n.t('toolbar.kinds.hint')"
          (toggled)="kinds.toggle($event)" (cleared)="kinds.clear()">
          <svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor"
               stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <path d="M3 5h18M6 12h12M10 19h4"/>
          </svg>
        </app-filter-pill>

        <!-- Who the mock board thinks you are. "admin" is its reserved name for the whole board. -->
        <div class="ctl" [title]="i18n.t('toolbar.userTitle')">
          <span class="lbl">{{ i18n.t('toolbar.user') }}</span>
          <!-- Which option is current is marked on the options, not as [value] on the select: the
               select's binding runs before @for has made any options, so it lands on an empty list
               and is dropped, leaving the box showing the first name whoever you actually are. -->
          <select class="sel user" (change)="onUser($any($event.target).value)">
            @for (u of session.users; track u) {
              <option [value]="u" [selected]="u === session.userName()">{{ u }}</option>
            }
          </select>
        </div>
      </div>

      <div class="grp right">
      <!-- Provider first, then its model: choosing a provider resets the model to that provider's
           default, so the wider choice belongs upstream of the narrower one. -->
      <div class="ctl">
        <span class="lbl">{{ i18n.t('toolbar.provider') }}</span>
        <select class="sel" (change)="onProvider($any($event.target).value)">
          @for (p of llm.info()?.providers ?? []; track p) {
            <option [value]="p" [selected]="p === llm.provider()" [disabled]="!(llm.info()?.configured?.[p])">
              {{ p }}{{ llm.info()?.configured?.[p] ? '' : ' (' + i18n.t('toolbar.noKey') + ')' }}
            </option>
          }
        </select>
      </div>

      <!-- The note beside each name is the point of this control: the tags differ by a decimal
           point, but what separates them is whether a listing comes back whole. -->
      <div class="ctl" [title]="modelHint()">
        <span class="lbl">{{ i18n.t('toolbar.model') }}</span>
        <select class="sel model" (change)="llm.setModel($any($event.target).value)">
          @for (m of llm.modelOptions(); track m.id) {
            <option [value]="m.id" [selected]="m.id === llm.model()">{{ m.id }}{{ m.note ? ' — ' + m.note : '' }}</option>
          }
        </select>
      </div>

      <div class="ctl">
        <span class="lbl">{{ i18n.t('toolbar.compute') }}</span>
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
          {{ cs.loaded ? cs.processor : (cs.gpuAttached ? i18n.t('toolbar.gpuIdle') : (cs.hostHasGpu ? i18n.t('toolbar.gpuOff') : 'CPU')) }}
        </span>
      }

      <span class="sep"></span>

      <!-- Light or dark, which accent scheme, and which language — all stored in the browser. Only
           the language reaches the API, and only as a hint about which language to answer in. -->
      <app-appearance />
      <app-language />

      <!-- Opens the debug console. Also what makes the API stream its trace at all, so it reads
           as a switch rather than a view: off means nothing extra is computed or sent. -->
      <button class="ghost dbg" [class.on]="debug.enabled()" (click)="debug.toggle()"
              [title]="debug.enabled() ? i18n.t('toolbar.debugHide') : i18n.t('toolbar.debugShow')">
        <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor"
             stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <path d="m8 8-4 4 4 4"/><path d="m16 8 4 4-4 4"/>
        </svg>
        {{ i18n.t('toolbar.debug') }}
      </button>

      @if (jiraEnabled) {
        @if (jira.status().connected) {
          <span class="jira-pill">
            <span class="on"></span>
            {{ jira.status().accountEmail || i18n.t('toolbar.connected') }}
            @if (jira.status().sites?.length) {
              <span class="muted">· {{ i18n.t('toolbar.sites', { count: jira.status().sites!.length }) }}</span>
            }
          </span>
          <button class="ghost" (click)="logout()">{{ i18n.t('toolbar.disconnect') }}</button>
        } @else {
          <button class="cta" (click)="connect()" [disabled]="connecting()">
            <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round">
              <path d="M9 12a3 3 0 0 1 3-3h3a3 3 0 1 1 0 6h-1"/><path d="M15 12a3 3 0 0 1-3 3H9a3 3 0 1 1 0-6h1"/>
            </svg>
            {{ connecting() ? i18n.t('toolbar.connecting') : i18n.t('toolbar.connect') }}
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
    .sel option { background: var(--menu); color: var(--text); }
    /* A select is as wide as its widest option, and the notes are a sentence long — so the closed
       pill is capped and elides, while the open list and the tooltip carry the whole thing. */
    .sel.model { max-width: 11rem; text-overflow: ellipsis; }

    .badge {
      display: flex; align-items: center; gap: 0.4rem;
      font-size: 0.68rem; font-weight: 700; letter-spacing: 0.02em; line-height: 1;
      height: var(--pill-h); padding: 0 0.7rem; border-radius: var(--r-full);
      background: var(--surface); border: 1px solid var(--border); color: var(--text-dim);
    }
    .badge.gpu { background: var(--ok-soft); border-color: var(--ok-line); color: var(--ok-fg); }
    .badge .spark { width: 6px; height: 6px; border-radius: 50%; background: currentColor; box-shadow: 0 0 8px currentColor; }

    .sep { width: 1px; height: 22px; background: var(--border); margin: 0 0.1rem; }

    .cta {
      display: flex; align-items: center; gap: 0.45rem;
      height: var(--pill-h); padding: 0 0.95rem; border: 0; border-radius: var(--r-full);
      color: var(--on-accent); font-weight: 600; font-size: 0.8rem; line-height: 1;
      background: var(--grad); box-shadow: var(--glow);
      transition: transform 0.15s var(--ease), box-shadow 0.2s var(--ease), filter 0.2s;
    }
    .cta:hover:not(:disabled) {
      transform: translateY(-1px); filter: brightness(1.05);
      box-shadow: 0 16px 46px -10px color-mix(in srgb, var(--accent) 75%, transparent);
    }
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
      background: var(--accent-soft); border-color: var(--accent-line); color: var(--accent-fg);
      box-shadow: inset 0 0 0 1px var(--accent-ring);
    }

    .jira-pill {
      display: flex; align-items: center; gap: 0.4rem;
      font-size: 0.75rem; color: var(--ok-fg); line-height: 1;
      height: var(--pill-h); padding: 0 0.75rem; border-radius: var(--r-full);
      background: var(--ok-soft); border: 1px solid var(--ok-line);
    }
    .jira-pill .on { width: 7px; height: 7px; border-radius: 50%; background: var(--ok); box-shadow: 0 0 8px var(--ok); }
    .jira-pill .muted { opacity: 0.65; }

    .err { margin-top: 0.4rem; font-size: 0.75rem; color: var(--danger-fg); text-align: right; }
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
  readonly i18n = inject(I18nService);
  private readonly projects = inject(ProjectsService);

  readonly connecting = signal(false);
  readonly error = signal<string | null>(null);

  /** The selected model's note, for the pill's tooltip — a native select truncates its own text. */
  modelHint(): string {
    const note = this.llm.noteFor();
    return note ? `${this.llm.model()} — ${note}` : this.i18n.t('toolbar.modelTitle');
  }


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
