import { Component, HostListener, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { Toolbar } from './components/toolbar';
import { Chat } from './components/chat';
import { DebugConsole } from './components/debug-console';
import { SessionService } from './services/session.service';
import { LlmService } from './services/llm.service';
import { JiraService } from './services/jira.service';
import { ProjectsService } from './services/projects.service';
import { ApiService } from './services/api.service';
import { DebugService } from './services/debug.service';
import { KindsService } from './services/kinds.service';
import { I18nService } from './services/i18n.service';
import { ConversationInfo } from './models';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [Toolbar, Chat, DebugConsole],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnInit {
  readonly session = inject(SessionService);
  private readonly llm = inject(LlmService);
  private readonly api = inject(ApiService);
  private readonly projects = inject(ProjectsService);
  readonly jira = inject(JiraService);
  readonly debug = inject(DebugService);
  readonly i18n = inject(I18nService);
  private readonly kinds = inject(KindsService);

  readonly conversation = signal<ConversationInfo | null>(null);

  constructor() {
    // Opening the panel — at startup or halfway through a chat — pulls in the system prompt so
    // the trace starts with the instructions everything else is a reaction to, plus a line saying
    // where the settings currently stand.
    // Opening the panel is the only thing this effect reacts to, so `enabled` is the only signal
    // read inside its tracking context. Everything else it does is untracked, and that is load
    // bearing rather than tidiness: both calls below *write* the trace, and traceSystemPrompt also
    // reads it (to check the prompt isn't already there). Tracked, that read makes the effect
    // depend on the very signal it goes on to write — it re-runs, writes, re-runs, and the tab
    // spins at 100% of a core before Angular has finished its first render, so the page never
    // appears at all. With the panel switched off nothing writes, which is why it only ever
    // showed up for someone who had opened the console once and had it remembered.
    effect(() => {
      if (!this.debug.enabled()) return;
      untracked(() => {
        void this.api.traceSystemPrompt();
        this.traceSettings();
      });
    });
  }

  /**
   * Where things stand the moment the panel opens: which model answers, whether an external account
   * is connected, and what the kind filter is. Every other line in the trace is an event, and events
   * from before the panel was opened were never recorded — so without this, a mid-chat trace reads as
   * if the turn had no settings behind it at all. Accounts by name, never a token: the browser holds
   * an opaque session id and nothing else.
   */
  private traceSettings(): void {
    const jira = this.jira.status();
    const kinds = this.kinds.active();
    const model = `${this.llm.provider() || '?'} · ${this.llm.model() || '?'}`;
    const connection = this.jiraEnabled()
      ? jira.connected
        ? `Jira connected${jira.accountEmail ? ` as ${jira.accountEmail}` : ''} · ${jira.sites?.length ?? 0} site(s)`
        : 'Jira enabled but not connected'
      : 'no external ticket account in use';

    this.debug.client(
      'settings',
      `${model} · ${connection} · kinds: ${kinds.length ? kinds.join(', ') : 'all'}`,
      {
        model: { provider: this.llm.provider(), model: this.llm.model(), compute: this.llm.compute() || 'auto' },
        jira: {
          backendEnabled: this.jiraEnabled(),
          connected: jira.connected,
          accountEmail: jira.accountEmail ?? null,
          sites: jira.sites?.map((s) => s.name) ?? [],
        },
        kindFilter: kinds.length ? kinds : 'all kinds',
        user: this.session.userName(),
      },
    );
  }

  // Whether the Jira backend is in play (drives the optional connect prompt). Chat itself is
  // never gated on it — the other backends (the mock) work without any login.
  readonly jiraEnabled = computed(() => this.conversation()?.jiraEnabled ?? false);

  async ngOnInit(): Promise<void> {
    await this.session.ensure();
    await Promise.all([this.llm.load(), this.jira.refresh(), this.projects.load()]);
    await this.newConversation();
  }

  // Ctrl+` (and Cmd+` on a Mac) toggles the debug console — the shortcut every dev console uses,
  // so it's reachable without leaving the keyboard mid-conversation.
  @HostListener('window:keydown', ['$event'])
  onKeydown(event: KeyboardEvent): void {
    if (event.key === '`' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      this.debug.toggle();
    }
  }

  /** Start a fresh conversation, clearing the transcript. */
  async newChat(): Promise<void> {
    await this.newConversation();
  }

  // A change of user is a change of identity — mint a fresh session and start a clean conversation
  // so nothing leaks across users.
  async onUserChanged(name: string): Promise<void> {
    await this.session.ensure(name);
    await Promise.all([this.jira.refresh(), this.projects.load()]);
    await this.newConversation();
  }

  private async newConversation(): Promise<void> {
    this.conversation.set(null);                     // unmount the old chat so its log resets
    this.conversation.set(await this.api.createConversation());
  }
}
