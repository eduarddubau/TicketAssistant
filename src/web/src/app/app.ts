import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Toolbar } from './components/toolbar';
import { Chat } from './components/chat';
import { SessionService } from './services/session.service';
import { LlmService } from './services/llm.service';
import { JiraService } from './services/jira.service';
import { ProjectsService } from './services/projects.service';
import { ApiService } from './services/api.service';
import { ConversationInfo } from './models';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [Toolbar, Chat],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnInit {
  private readonly session = inject(SessionService);
  private readonly llm = inject(LlmService);
  private readonly api = inject(ApiService);
  private readonly projects = inject(ProjectsService);
  readonly jira = inject(JiraService);

  readonly conversation = signal<ConversationInfo | null>(null);

  // Whether the Jira backend is in play (drives the optional connect prompt). Chat itself is
  // never gated on it — the other backends (the mock) work without any login.
  readonly jiraEnabled = computed(() => this.conversation()?.jiraEnabled ?? false);

  async ngOnInit(): Promise<void> {
    await this.session.ensure();
    await Promise.all([this.llm.load(), this.jira.refresh(), this.projects.load()]);
    await this.newConversation();
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
