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
    <div class="log">
      @for (item of log(); track item.id) {
        @if (item.kind === 'msg') {
          <div class="msg {{ item.role }}" [innerHTML]="item.html"></div>
        } @else {
          <app-confirmation-card [event]="item.event" [projects]="cardProjects()" (decision)="onDecision($event)" />
        }
      }
      @if (streaming() !== null) {
        <div class="msg assistant streaming">{{ streaming() }}</div>
      }
      @if (thinking()) {
        <div class="thinking"><span class="spinner"></span> thinking… {{ thinkingSecs() }}s</div>
      }
    </div>

    <form class="composer" (submit)="send($event)">
      <input [value]="draft()" (input)="draft.set($any($event.target).value)"
             [disabled]="!enabled || busy()"
             [placeholder]="enabled ? 'Describe an issue, or ask about your tickets…' : 'Connect Jira to start chatting'" />
      <button type="submit" [disabled]="!enabled || busy() || !draft().trim()">Send</button>
    </form>
  `,
  styles: [`
    :host { display: flex; flex-direction: column; height: 100%; min-height: 0; }
    .log { flex: 1; overflow-y: auto; padding: 1rem; display: flex; flex-direction: column; gap: 0.5rem; }
    .msg { padding: 0.55rem 0.8rem; border-radius: 10px; max-width: 80%; font-size: 0.9rem; line-height: 1.4; white-space: pre-wrap; }
    .msg.user { align-self: flex-end; background: #2563eb; color: #fff; }
    .msg.assistant { align-self: flex-start; background: #323f4b; color: #e4e7eb; }
    .msg.assistant.streaming { opacity: 0.9; }
    .msg.tool { align-self: center; background: transparent; color: #7b8794; font-size: 0.75rem; padding: 0.1rem; }
    .msg.error { align-self: center; background: #7f1d1d; color: #fecaca; font-size: 0.8rem; }
    .msg :first-child { margin-top: 0; } .msg :last-child { margin-bottom: 0; }
    .msg a { color: inherit; text-decoration: underline; }
    .thinking { align-self: flex-start; color: #7b8794; font-size: 0.8rem; display: flex; align-items: center; gap: 0.4rem; }
    .spinner { width: 12px; height: 12px; border: 2px solid #52606d; border-top-color: #cbd2d9; border-radius: 50%; animation: spin 0.8s linear infinite; }
    @keyframes spin { to { transform: rotate(360deg); } }
    .composer { display: flex; gap: 0.5rem; padding: 0.75rem; border-top: 1px solid #323f4b; background: #1f2933; }
    .composer input { flex: 1; padding: 0.6rem; border-radius: 8px; border: 1px solid #52606d; background: #111827; color: #e4e7eb; }
    .composer button { padding: 0.6rem 1.1rem; border-radius: 8px; border: 0; background: #2563eb; color: #fff; font-weight: 600; cursor: pointer; }
    .composer button:disabled { opacity: 0.5; cursor: not-allowed; }
  `],
})
export class Chat implements OnInit, OnDestroy {
  @Input({ required: true }) conversation!: ConversationInfo;
  @Input() enabled = true;

  private readonly api = inject(ApiService);
  private readonly projectsSvc = inject(ProjectsService);

  readonly log = signal<LogItem[]>([]);
  readonly streaming = signal<string | null>(null);
  readonly thinking = signal(false);
  readonly thinkingSecs = signal(0);
  readonly busy = signal(false);
  readonly draft = signal('');

  private nextId = 1;
  private timer?: ReturnType<typeof setInterval>;

  ngOnInit(): void {
    if (this.conversation.greeting) this.push('assistant', this.conversation.greeting);
  }

  ngOnDestroy(): void {
    this.stopThinking();
  }

  async send(e: Event): Promise<void> {
    e.preventDefault();
    const text = this.draft().trim();
    if (!text || this.busy() || !this.enabled) return;

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

  // Shared machinery for a message turn and a confirmation resume: spinner on, stream events,
  // finalize, spinner off.
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
    else if (e.type === 'tool_executed') this.push('tool', `🔧 ${e.toolName} ${e.succeeded ? '✓' : '✗'}`, true);
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
    this.stopThinking();
    const since = Date.now();
    this.thinking.set(true);
    this.thinkingSecs.set(0);
    this.timer = setInterval(() => this.thinkingSecs.set(+((Date.now() - since) / 1000).toFixed(1)), 100);
  }

  private stopThinking(): void {
    this.thinking.set(false);
    if (this.timer) { clearInterval(this.timer); this.timer = undefined; }
  }
}
