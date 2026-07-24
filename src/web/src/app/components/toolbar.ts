import { Component, EventEmitter, Input, Output, inject, signal } from '@angular/core';
import { LlmService } from '../services/llm.service';
import { SessionService } from '../services/session.service';
import { JiraService } from '../services/jira.service';
import { ProjectsService } from '../services/projects.service';

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
        <select class="sel" [value]="llm.compute()" (change)="llm.setCompute($any($event.target).value)">
          <option value="">Auto</option>
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
    :host { display: block; }
    .bar { display: flex; align-items: center; gap: 0.5rem; flex-wrap: wrap; justify-content: flex-end; }

    .ctl {
      display: flex; align-items: center; gap: 0.45rem;
      padding: 0.3rem 0.6rem;
      background: var(--surface); border: 1px solid var(--border); border-radius: var(--r-full);
      transition: border-color 0.15s var(--ease), background 0.15s var(--ease);
    }
    .ctl:focus-within { border-color: var(--border-strong); background: var(--surface-2); }
    .lbl { font-size: 0.6rem; text-transform: uppercase; letter-spacing: 0.07em; color: var(--text-faint); font-weight: 700; }
    .inp, .sel { background: transparent; border: 0; color: var(--text); font-size: 0.8rem; padding: 0; }
    .inp:focus, .sel:focus { outline: none; }
    .inp.model { width: 8.5rem; }
    .inp.user { width: 5rem; }
    .sel { cursor: pointer; }
    .sel option { background: #12141c; color: var(--text); }

    .badge {
      display: flex; align-items: center; gap: 0.4rem;
      font-size: 0.68rem; font-weight: 700; letter-spacing: 0.02em;
      padding: 0.34rem 0.62rem; border-radius: var(--r-full);
      background: var(--surface); border: 1px solid var(--border); color: var(--text-dim);
    }
    .badge.gpu { background: rgba(52, 211, 153, 0.12); border-color: rgba(52, 211, 153, 0.35); color: #7ff0c4; }
    .badge .spark { width: 6px; height: 6px; border-radius: 50%; background: currentColor; box-shadow: 0 0 8px currentColor; }

    .sep { width: 1px; height: 22px; background: var(--border); margin: 0 0.1rem; }

    .cta {
      display: flex; align-items: center; gap: 0.45rem;
      padding: 0.46rem 0.9rem; border: 0; border-radius: var(--r-full);
      color: #fff; font-weight: 600; font-size: 0.8rem;
      background: var(--grad); box-shadow: var(--glow);
      transition: transform 0.15s var(--ease), box-shadow 0.2s var(--ease), filter 0.2s;
    }
    .cta:hover:not(:disabled) { transform: translateY(-1px); box-shadow: 0 16px 46px -10px rgba(124, 108, 255, 0.75); filter: brightness(1.05); }
    .cta:active { transform: translateY(0); }
    .cta:disabled { opacity: 0.65; cursor: default; }

    .ghost { padding: 0.42rem 0.75rem; border-radius: var(--r-full); background: var(--surface); border: 1px solid var(--border); color: var(--text-dim); font-size: 0.78rem; transition: 0.15s var(--ease); }
    .ghost:hover { background: var(--surface-2); color: var(--text); border-color: var(--border-strong); }

    .jira-pill {
      display: flex; align-items: center; gap: 0.4rem;
      font-size: 0.75rem; color: #cfeadd;
      padding: 0.36rem 0.65rem; border-radius: var(--r-full);
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
