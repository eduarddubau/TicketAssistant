import { Injectable, signal } from '@angular/core';
import { API_BASE } from '../config';

/**
 * Holds the browser's server-issued bearer session. The session id is the app's identity — every
 * request carries it as `Authorization: Bearer`. The user name is just a scope label for the mock
 * backend (and re-minting under a new name is how you test per-user scoping there).
 */
@Injectable({ providedIn: 'root' })
export class SessionService {
  /**
   * The people this demo has, plus the board's admin. A fixed list rather than a free-text box: the
   * name only means anything to the mock board, which has seeded work for exactly these two, so
   * typing anything else produced an empty screen with no hint as to why.
   *
   * "admin" is a reserved name on the mock, not a third person: it sees every ticket regardless of
   * who raised it — the same view its board page shows — which is the one honest way to offer
   * "show me everything" now that a *missing* identity deliberately shows nothing.
   */
  static readonly USERS = ['alice', 'bob', 'admin'] as const;

  readonly userName = signal(known(localStorage.getItem('ta-user')));
  readonly sessionId = signal<string | null>(null);

  /** Mint (or re-mint) a session for the current — or a new — user. */
  async ensure(name?: string): Promise<void> {
    const userName = known(name ?? this.userName());
    this.userName.set(userName);
    localStorage.setItem('ta-user', userName);

    const res = await fetch(`${API_BASE}/api/session`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: userName }),
    });
    this.sessionId.set((await res.json()).sessionId);
  }

  readonly users = SessionService.USERS;

  /** The Authorization header for this session, or empty when none has been minted yet. */
  authHeader(): Record<string, string> {
    const id = this.sessionId();
    return id ? { Authorization: `Bearer ${id}` } : {};
  }
}

/** Falls back to the first user for anything unrecognised — including a name left over in storage. */
function known(name: string | null | undefined): string {
  const trimmed = (name ?? '').trim().toLowerCase();
  return (SessionService.USERS as readonly string[]).includes(trimmed) ? trimmed : SessionService.USERS[0];
}
