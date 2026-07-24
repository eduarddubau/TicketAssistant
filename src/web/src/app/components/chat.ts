import { Component, Input, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { ApiService } from '../services/api.service';
import { ProjectsService } from '../services/projects.service';
import { ConversationInfo, JiraProject, OrchestrationEvent } from '../models';
import { renderMarkdown } from '../markdown';
import { ConfirmationCard, Decision } from './confirmation-card';

type ConfirmationEvent = Extract<OrchestrationEvent, { type: 'confirmation_required' }>;
type LogItem =
  | { id: number; kind: 'msg'; role: 'user' | 'assistant' | 'tool' | 'error'; html: string }
  | { id: number; kind: 'confirm'; event: ConfirmationEvent };

/**
 * The conversation view: sends messages, streams the assistant's reply token by token, shows the
 * tools it ran and the editable confirmation cards, and links ticket ids. Consumes the same SSE
 * event stream the original console did.
 */
@Component({
  selector: 'app-chat',
  standalone: true,
  imports: [ConfirmationCard],
  template: `
    <div class="thread">
      <div class="col">
        @for (item of log(); track item.id) {
          @if (item.kind === 'msg') {
            @if (item.role === 'tool' || item.role === 'error') {
              <div class="note {{ item.role }}" [innerHTML]="item.html"></div>
            } @else {
              <div class="row {{ item.role }}">
                @if (item.role === 'assistant') { <div class="avatar"></div> }
                <div class="bubble {{ item.role }}" [innerHTML]="item.html"></div>
              </div>
            }
          } @else {
            <div class="row assistant">
              <div class="avatar"></div>
              <app-confirmation-card [event]="item.event" [projects]="cardProjects()" (decision)="onDecision($event)" />
            </div>
          }
        }

        @if (streaming() !== null) {
          <div class="row assistant">
            <div class="avatar"></div>
            <div class="bubble assistant streaming">{{ streaming() }}</div>
          </div>
        }
        @if (thinking()) {
          <div class="row assistant">
            <div class="avatar"></div>
            <div class="bubble assistant thinking">
              <span class="tdot"></span><span class="tdot"></span><span class="tdot"></span>
            </div>
          </div>
        }
      </div>
    </div>

    <div class="composer-wrap">
      <div class="col">
        @if (!started()) {
          <div class="suggestions">
            @for (s of suggestions; track s) {
              <button class="chip" (click)="useSuggestion(s)">{{ s }}</button>
            }
          </div>
        }
        <form class="composer" (submit)="send($event)">
          <input [value]="draft()" (input)="draft.set($any($event.target).value)"
                 [disabled]="busy()"
                 placeholder="Describe an issue, or ask about your tickets…" />
          <button class="send" type="submit" [disabled]="busy() || !draft().trim()" aria-label="Send">
            <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round">
              <path d="M5 12h14M13 6l6 6-6 6"/>
            </svg>
          </button>
        </form>
      </div>
    </div>
  `,
  styles: [`
    :host { display: flex; flex-direction: column; min-height: 0; flex: 1; }

    .thread { flex: 1; overflow-y: auto; padding: 1.6rem 1rem 0.5rem; }
    .col { width: 100%; max-width: 780px; margin: 0 auto; display: flex; flex-direction: column; gap: 1rem; }

    .row { display: flex; gap: 0.7rem; align-items: flex-start; max-width: 100%; animation: rise 0.35s var(--ease); }
    .row.user { flex-direction: row-reverse; }

    .avatar {
      width: 30px; height: 30px; border-radius: 50%; flex-shrink: 0; margin-top: 2px;
      display: grid; place-items: center;
      background: var(--grad); box-shadow: var(--glow);
    }
    .avatar::after {
      content: ''; width: 15px; height: 15px; background: #fff;
      -webkit-mask: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'%3E%3Cpath d='M12 2c.5 4.6 2.6 6.7 8 8-5.4 1.3-7.5 3.4-8 8-.5-4.6-2.6-6.7-8-8 5.4-1.3 7.5-3.4 8-8z'/%3E%3C/svg%3E") center/contain no-repeat;
      mask: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'%3E%3Cpath d='M12 2c.5 4.6 2.6 6.7 8 8-5.4 1.3-7.5 3.4-8 8-.5-4.6-2.6-6.7-8-8 5.4-1.3 7.5-3.4 8-8z'/%3E%3C/svg%3E") center/contain no-repeat;
    }

    .bubble {
      padding: 0.72rem 0.95rem; border-radius: 16px; font-size: 0.9rem; line-height: 1.55;
      max-width: 80%; word-wrap: break-word; overflow-wrap: anywhere;
    }
    .bubble.assistant {
      background: var(--surface-2); border: 1px solid var(--border);
      border-top-left-radius: 6px; box-shadow: var(--shadow);
    }
    .bubble.user {
      background: var(--grad); color: #fff; border-top-right-radius: 6px; box-shadow: var(--glow);
    }
    .bubble.streaming::after { content: '▍'; margin-left: 1px; opacity: 0.7; animation: blink 1s steps(2) infinite; }

    .bubble p:first-child { margin-top: 0; } .bubble p:last-child { margin-bottom: 0; }
    .bubble a { color: #cdc6ff; text-decoration: underline; text-underline-offset: 2px; }
    .bubble.user a { color: #fff; }
    .bubble code { background: rgba(255, 255, 255, 0.1); padding: 0.1rem 0.34rem; border-radius: 5px; font-size: 0.85em; }
    .bubble.user code { background: rgba(255, 255, 255, 0.2); }

    .thinking { display: flex; gap: 5px; align-items: center; padding: 0.85rem 1rem; }
    .tdot { width: 7px; height: 7px; border-radius: 50%; background: var(--text-dim); animation: bounce 1.2s var(--ease) infinite; }
    .tdot:nth-child(2) { animation-delay: 0.15s; } .tdot:nth-child(3) { animation-delay: 0.3s; }

    .note {
      align-self: center; font-size: 0.75rem; padding: 0.3rem 0.75rem; border-radius: var(--r-full);
      animation: rise 0.3s var(--ease); font-weight: 500;
    }
    .note.tool { background: var(--surface); border: 1px solid var(--border); color: var(--text-dim); }
    .note.error { background: rgba(244, 63, 94, 0.12); border: 1px solid rgba(244, 63, 94, 0.3); color: #ffb0b0; }

    app-confirmation-card { display: block; flex: 1; min-width: 0; max-width: 82%; animation: rise 0.35s var(--ease); }

    .composer-wrap { position: relative; padding: 0.4rem 1rem 1.1rem; }
    .composer-wrap::before {
      content: ''; position: absolute; left: 0; right: 0; top: -30px; height: 30px;
      background: linear-gradient(transparent, var(--bg)); pointer-events: none;
    }

    .suggestions { display: flex; flex-wrap: wrap; gap: 0.5rem; margin-bottom: 0.7rem; animation: rise 0.4s var(--ease); }
    .chip {
      padding: 0.5rem 0.85rem; border-radius: var(--r-full);
      background: var(--surface); border: 1px solid var(--border); color: var(--text-dim); font-size: 0.82rem;
      transition: 0.15s var(--ease);
    }
    .chip:hover { background: var(--surface-2); color: var(--text); border-color: var(--border-strong); transform: translateY(-1px); }

    .composer {
      display: flex; align-items: center; gap: 0.5rem;
      padding: 0.35rem 0.35rem 0.35rem 1.1rem;
      background: var(--surface-2); border: 1px solid var(--border); border-radius: var(--r-full);
      box-shadow: var(--shadow-lg); transition: border-color 0.2s var(--ease), box-shadow 0.2s var(--ease);
    }
    .composer:focus-within { border-color: rgba(124, 108, 255, 0.5); box-shadow: var(--shadow-lg), 0 0 0 4px rgba(124, 108, 255, 0.12); }
    .composer input { flex: 1; background: transparent; border: 0; color: var(--text); font-size: 0.92rem; padding: 0.55rem 0; }
    .composer input:focus { outline: none; }
    .composer input::placeholder { color: var(--text-faint); }

    .send {
      width: 40px; height: 40px; flex-shrink: 0; border: 0; border-radius: 50%;
      display: grid; place-items: center; color: #fff;
      background: var(--grad); box-shadow: var(--glow);
      transition: transform 0.15s var(--ease), filter 0.2s var(--ease);
    }
    .send:hover:not(:disabled) { transform: scale(1.07); filter: brightness(1.08); }
    .send:active:not(:disabled) { transform: scale(1); }
    .send:disabled { opacity: 0.45; background: var(--surface-3); box-shadow: none; color: var(--text-faint); }
  `],
})
export class Chat implements OnInit, OnDestroy {
  @Input({ required: true }) conversation!: ConversationInfo;

  private readonly api = inject(ApiService);
  private readonly projectsSvc = inject(ProjectsService);

  readonly log = signal<LogItem[]>([]);
  readonly streaming = signal<string | null>(null);
  readonly thinking = signal(false);
  readonly busy = signal(false);
  readonly draft = signal('');
  readonly started = signal(false);

  readonly suggestions = [
    'Show me my open tickets',
    'Anything urgent right now?',
    'Create a ticket: the login page returns a 500 error',
  ];

  private nextId = 1;

  ngOnInit(): void {
    if (this.conversation.greeting) this.push('assistant', this.conversation.greeting);
  }

  ngOnDestroy(): void {
    this.stopThinking();
  }

  useSuggestion(text: string): void {
    this.draft.set(text);
    this.send();
  }

  async send(e?: Event): Promise<void> {
    e?.preventDefault();
    const text = this.draft().trim();
    if (!text || this.busy()) return;

    this.started.set(true);
    this.push('user', text, /*raw*/ true);
    this.draft.set('');
    await this.run((onEvent) => this.api.sendMessage(this.conversation.conversationId, text, onEvent));
  }

  // Projects offered on the create card — across every active backend.
  cardProjects(): JiraProject[] {
    return this.projectsSvc.projects();
  }

  async onDecision(d: Decision): Promise<void> {
    const payload = d.approved
      ? { callId: d.callId, approved: true, edits: d.edits }
      : { callId: d.callId, approved: false };
    await this.run((onEvent) => this.api.confirm(this.conversation.conversationId, payload, onEvent));
  }

  private async run(call: (onEvent: (e: OrchestrationEvent) => void) => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.startThinking();
    try {
      await call((e) => this.handleEvent(e));
    } catch (e: any) {
      this.finalizeStreaming();
      this.push('error', e?.message ?? String(e), true);
    } finally {
      this.finalizeStreaming();
      this.stopThinking();
      this.busy.set(false);
    }
  }

  private handleEvent(e: OrchestrationEvent): void {
    if (e.type === 'assistant_delta') {
      this.stopThinking();
      this.streaming.set((this.streaming() ?? '') + e.text);
      return;
    }
    if (e.type === 'assistant_replace') {
      this.streaming.set(e.text ? e.text : null);
      return;
    }

    this.finalizeStreaming();
    this.stopThinking();

    if (e.type === 'assistant_text') this.push('assistant', e.text);
    else if (e.type === 'tool_executed') this.push('tool', `${e.toolName.replace(/_/g, ' ')} ${e.succeeded ? '✓' : '✗'}`, true);
    else if (e.type === 'confirmation_required') this.log.update((l) => [...l, { id: this.nextId++, kind: 'confirm', event: e }]);
  }

  private finalizeStreaming(): void {
    const text = this.streaming();
    if (text !== null) {
      this.push('assistant', text);
      this.streaming.set(null);
    }
  }

  private push(role: 'user' | 'assistant' | 'tool' | 'error', text: string, raw = false): void {
    const html = raw ? this.escape(text) : renderMarkdown(text, (id) => this.ticketHref(id));
    this.log.update((l) => [...l, { id: this.nextId++, kind: 'msg', role, html }]);
  }

  private escape(s: string): string {
    return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  }

  private ticketHref(id: string): string | null {
    // Jira ids route to their site via the project key; otherwise fall back to the mock template.
    const dash = id.lastIndexOf('-');
    const site = this.projectsSvc.siteUrlForProjectKey(dash > 0 ? id.slice(0, dash) : id);
    if (site) return `${site}/browse/${id}`;
    return this.conversation.ticketUrlTemplate?.replace('{id}', id) ?? null;
  }

  private startThinking(): void {
    this.thinking.set(true);
  }

  private stopThinking(): void {
    this.thinking.set(false);
  }
}
