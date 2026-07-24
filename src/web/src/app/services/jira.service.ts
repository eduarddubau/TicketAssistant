import { Injectable, inject, signal } from '@angular/core';
import { API_BASE } from '../config';
import { JiraStatus } from '../models';
import { SessionService } from './session.service';

/**
 * Drives the Jira OAuth popup login and reports connection status (which account, which sites).
 * `connect()` opens the popup and waits for the callback page to post back a completion message
 * (validated to have come from our API's origin) before re-reading status. The tokens themselves
 * never touch the browser. Projects live in ProjectsService (they span all backends, not just Jira).
 */
@Injectable({ providedIn: 'root' })
export class JiraService {
  private readonly session = inject(SessionService);

  readonly status = signal<JiraStatus>({ connected: false });

  async refresh(): Promise<void> {
    try {
      const res = await fetch(`${API_BASE}/api/auth/jira/status`, { headers: this.session.authHeader() });
      if (res.ok) this.status.set(await res.json());
    } catch { /* leave as disconnected */ }
  }

  async connect(): Promise<void> {
    const res = await fetch(`${API_BASE}/api/auth/jira/login`, { headers: this.session.authHeader() });
    if (!res.ok) throw new Error('Could not start the Jira login. Is a session active?');
    const { authorizeUrl } = await res.json();

    const popup = window.open(authorizeUrl, 'jira-login', 'width=520,height=720');
    const result = await new Promise<{ ok: boolean; message?: string }>((resolve) => {
      const onMessage = (e: MessageEvent) => {
        if (e.origin !== API_BASE) return; // only trust the callback page from our own API
        if (e.data?.type === 'jira-connected') { cleanup(); resolve({ ok: true }); }
        else if (e.data?.type === 'jira-error') { cleanup(); resolve({ ok: false, message: e.data.message }); }
      };
      const timer = setInterval(() => {
        if (popup?.closed) { cleanup(); resolve({ ok: false, message: 'Login window was closed.' }); }
      }, 500);
      const cleanup = () => { window.removeEventListener('message', onMessage); clearInterval(timer); };
      window.addEventListener('message', onMessage);
    });

    await this.refresh();
    if (!this.status().connected) {
      throw new Error(result.message || 'Jira connection did not complete.');
    }
  }

  async logout(): Promise<void> {
    await fetch(`${API_BASE}/api/auth/jira/logout`, { method: 'POST', headers: this.session.authHeader() });
    this.status.set({ connected: false });
  }
}
