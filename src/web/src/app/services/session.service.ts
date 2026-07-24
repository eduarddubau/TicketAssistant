import { Injectable, signal } from '@angular/core';
import { API_BASE } from '../config';

/**
 * Holds the browser's server-issued bearer session. The session id is the app's identity — every
 * request carries it as `Authorization: Bearer`. The user name is just a scope label for the mock
 * backend (and re-minting under a new name is how you test per-user scoping there).
 */
@Injectable({ providedIn: 'root' })
export class SessionService {
  readonly userName = signal(localStorage.getItem('ta-user') || 'alice');
  readonly sessionId = signal<string | null>(null);

  /** Mint (or re-mint) a session for the current — or a new — user name. */
  async ensure(name?: string): Promise<void> {
    const userName = (name ?? this.userName()).trim() || 'alice';
    this.userName.set(userName);
    localStorage.setItem('ta-user', userName);

    const res = await fetch(`${API_BASE}/api/session`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: userName }),
    });
    this.sessionId.set((await res.json()).sessionId);
  }

  /** The Authorization header for this session, or empty when none has been minted yet. */
  authHeader(): Record<string, string> {
    const id = this.sessionId();
    return id ? { Authorization: `Bearer ${id}` } : {};
  }
}
