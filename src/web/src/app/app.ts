import { Component, HostListener, OnInit, computed, effect, inject, signal } from '@angular/core';
import { Toolbar } from './components/toolbar';
import { Chat } from './components/chat';
import { DebugConsole } from './components/debug-console';
import { SessionService } from './services/session.service';
import { LlmService } from './services/llm.service';
import { JiraService } from './services/jira.service';
import { ProjectsService } from './services/projects.service';
import { ApiService } from './services/api.service';
import { DebugService } from './services/debug.service';
import { ConversationInfo } from './models';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [Toolbar, Chat, DebugConsole],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnInit {
  private readonly session = inject(SessionService);
  private readonly llm = inject(LlmService);
  private readonly api = inject(ApiService);
  private readonly projects = inject(ProjectsService);
  readonly jira = inject(JiraService);
  readonly debug = inject(DebugService);

  readonly conversation = signal<ConversationInfo | null>(null);

  constructor() {
    // Opening the panel — at startup or halfway through a chat — pulls in the system prompt so
    // the trace starts with the instructions everything else is a reaction to.
    effect(() => {
      if (this.debug.enabled()) void this.api.traceSystemPrompt();
    });
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
