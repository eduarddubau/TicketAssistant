import { Component, EventEmitter, Input, Output, inject, signal } from '@angular/core';
import { LlmService } from '../services/llm.service';
import { SessionService } from '../services/session.service';
import { JiraService } from '../services/jira.service';

/**
 * The config strip: who you are (scopes the mock), which LLM/model to use, GPU vs CPU for Ollama
 * with a live status badge, and — when the backend is Jira — the connect/disconnect control. All
 * of it just tweaks headers or session state; only a change of user needs a fresh conversation,
 * which it signals to the parent.
 */
@Component({
  selector: 'app-toolbar',
  standalone: true,
  template: `
    <div class="bar">
      <label>User
        <input [value]="session.userName()" (change)="onUser($any($event.target).value)" />
      </label>

      <label>Provider
        <select [value]="llm.provider()" (change)="onProvider($any($event.target).value)">
          @for (p of llm.info()?.providers ?? []; track p) {
            <option [value]="p" [disabled]="!(llm.info()?.configured?.[p])">
              {{ p }}{{ llm.info()?.configured?.[p] ? '' : ' (no key)' }}
            </option>
          }
        </select>
      </label>

      <label>Model
        <input list="ollama-models" [value]="llm.model()" (change)="llm.setModel($any($event.target).value)" />
        <datalist id="ollama-models">
          @for (m of llm.ollamaModels(); track m) { <option [value]="m"></option> }
        </datalist>
      </label>

      <label>Compute
        <select [value]="llm.compute()" (change)="llm.setCompute($any($event.target).value)">
          <option value="">Auto</option>
          <option value="cpu">CPU only</option>
        </select>
      </label>

      @if (llm.computeStatus(); as cs) {
        <span class="badge" [class.gpu]="cs.processor === 'GPU'">
          {{ cs.loaded ? (cs.processor + ' · ' + cs.model) : (cs.gpuAttached ? 'GPU idle' : (cs.hostHasGpu ? 'GPU not attached' : 'CPU')) }}
        </span>
      }

      @if (ticketBackend === 'Jira') {
        <span class="spacer"></span>
        @if (jira.status().connected) {
          <span class="jira ok">Jira: {{ jira.status().accountEmail || jira.status().siteUrl }}</span>
          <button (click)="logout()">Disconnect</button>
        } @else {
          <button class="connect" (click)="connect()" [disabled]="connecting()">
            {{ connecting() ? 'Connecting…' : 'Connect Jira' }}
          </button>
        }
      }
    </div>
    @if (error()) { <div class="err">{{ error() }}</div> }
  `,
  styles: [`
    .bar { display: flex; flex-wrap: wrap; gap: 0.75rem; align-items: flex-end; padding: 0.6rem 0.9rem;
      background: #1f2933; border-bottom: 1px solid #323f4b; }
    label { display: flex; flex-direction: column; font-size: 0.7rem; color: #9aa5b1; gap: 0.2rem; }
    input, select { padding: 0.35rem; border-radius: 6px; border: 1px solid #52606d; background: #111827; color: #e4e7eb; }
    input { min-width: 8rem; }
    .badge { font-size: 0.7rem; padding: 0.3rem 0.5rem; border-radius: 6px; background: #323f4b; color: #cbd2d9; align-self: center; }
    .badge.gpu { background: #14532d; color: #bbf7d0; }
    .spacer { flex: 1; }
    .jira.ok { font-size: 0.75rem; color: #bbf7d0; align-self: center; }
    button { padding: 0.4rem 0.8rem; border-radius: 6px; border: 0; cursor: pointer; background: #3e4c59; color: #e4e7eb; align-self: center; }
    button.connect { background: #2563eb; font-weight: 600; }
    .err { padding: 0.4rem 0.9rem; background: #7f1d1d; color: #fecaca; font-size: 0.8rem; }
  `],
})
export class Toolbar {
  @Input() ticketBackend = 'Http';
  @Output() userChanged = new EventEmitter<string>();

  readonly llm = inject(LlmService);
  readonly session = inject(SessionService);
  readonly jira = inject(JiraService);

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
    } catch (e: any) {
      this.error.set(e?.message ?? String(e));
    } finally {
      this.connecting.set(false);
    }
  }

  async logout(): Promise<void> {
    await this.jira.logout();
  }
}
