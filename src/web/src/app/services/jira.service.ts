import { Injectable, inject, signal } from '@angular/core';
import { API_BASE } from '../config';
import { JiraStatus } from '../models';
import { SessionService } from './session.service';
import { DebugService } from './debug.service';

/**
 * Drives the Jira OAuth popup login and reports connection status (which account, which sites).
 * `connect()` opens the popup and waits for the callback page to post back a completion message
 * (validated to have come from our API's origin) before re-reading status. The tokens themselves
 * never touch the browser. Projects live in ProjectsService (they span all backends, not just Jira).
 *
 * Connecting and disconnecting are written to the debug trace, because "which account is this
 * answering as" changes what every following read returns — and a connection made before the panel
 * was opened is otherwise invisible. Which account and how many sites, never a token: the browser
 * has none to leak.
 */
@Injectable({ providedIn: 'root' })
export class JiraService {
  private readonly session = inject(SessionService);
  private readonly debug = inject(DebugService);

  readonly status = signal<JiraStatus>({ connected: false });

  async refresh(): Promise<void> {
    const was = this.status().connected;
    try {
      const res = await fetch(`${API_BASE}/api/auth/jira/status`, { headers: this.session.authHeader() });
      if (res.ok) this.status.set(await res.json());
    } catch { /* leave as disconnected */ }

    // Only on a change: this runs on every load and after each connect, and a line per poll would
    // say nothing. A session that was already connected still gets one line, on the first read.
    if (this.status().connected !== was) {
      this.trace(this.status().connected ? 'Jira connected' : 'Jira connection ended');
    }
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
      this.debug.client('connection', `Jira login did not complete — ${result.message ?? 'no reason given'}`, {
        provider: 'jira',
        connected: false,
        reason: result.message ?? null,
      });
      throw new Error(result.message || 'Jira connection did not complete.');
    }
  }

  async logout(): Promise<void> {
    await fetch(`${API_BASE}/api/auth/jira/logout`, { method: 'POST', headers: this.session.authHeader() });
    this.status.set({ connected: false });
    this.trace('Jira disconnected');
  }

  /** One trace line for a connection change: which account, which sites, and no secrets to leak. */
  private trace(what: string): void {
    const status = this.status();
    const sites = status.sites?.map((s) => s.name) ?? [];
    const who = status.accountEmail ? ` · ${status.accountEmail}` : '';
    const where = sites.length ? ` · ${sites.length} site(s)` : '';
    this.debug.client('connection', `${what}${who}${where}`, {
      provider: 'jira',
      connected: status.connected,
      accountEmail: status.accountEmail ?? null,
      sites,
      tokens: 'held server-side against the session; never sent to the browser',
    });
  }
}
