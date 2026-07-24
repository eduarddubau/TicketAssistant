import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Toolbar } from './components/toolbar';
import { Chat } from './components/chat';
import { SessionService } from './services/session.service';
import { LlmService } from './services/llm.service';
import { JiraService } from './services/jira.service';
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
  readonly jira = inject(JiraService);

  readonly conversation = signal<ConversationInfo | null>(null);

  readonly isJira = computed(() => this.conversation()?.ticketBackend === 'Jira');
  readonly chatEnabled = computed(() => !this.isJira() || this.jira.status().connected);

  async ngOnInit(): Promise<void> {
    await this.session.ensure();
    await Promise.all([this.llm.load(), this.jira.refresh()]);
    await this.newConversation();
  }

  // A change of user is a change of identity — mint a fresh session and start a clean conversation
  // so nothing leaks across users.
  async onUserChanged(name: string): Promise<void> {
    await this.session.ensure(name);
    await this.jira.refresh();
    await this.newConversation();
  }

  private async newConversation(): Promise<void> {
    this.conversation.set(null);                     // unmount the old chat so its log resets
    this.conversation.set(await this.api.createConversation());
  }
}
