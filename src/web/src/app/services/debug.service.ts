import { Injectable, computed, signal } from '@angular/core';
import { DebugEntry, ServerDebugEvent } from '../models';

/**
 * The debug console's store: one timeline of everything a turn involved, from both ends of the
 * wire. The browser records what it sent and received; the API streams what the loop did with it
 * (the exact prompt, the raw reply, every tool call, every guardrail) when this is switched on.
 *
 * Switched on is the operative part: the flag rides along as an `X-Debug` header, so with the
 * console closed the server builds no snapshots and nothing extra crosses the network.
 */
@Injectable({ providedIn: 'root' })
export class DebugService {
  /** Ring-buffer bound. A long session shouldn't be able to grow the tab's memory without limit. */
  private static readonly MAX_ENTRIES = 1000;

  readonly enabled = signal(localStorage.getItem('ta-debug') === '1');
  readonly entries = signal<DebugEntry[]>([]);

  /** Every stage seen so far, in first-seen order — the panel's filter chips. */
  readonly stages = computed(() => [...new Set(this.entries().map((e) => e.stage))]);

  private nextId = 1;

  /** The header that asks the API to stream its trace, when the console is on. */
  headers(): Record<string, string> {
    return this.enabled() ? { 'X-Debug': '1' } : {};
  }

  setEnabled(on: boolean): void {
    this.enabled.set(on);
    localStorage.setItem('ta-debug', on ? '1' : '0');
  }

  toggle(): void {
    this.setEnabled(!this.enabled());
  }

  clear(): void {
    this.entries.set([]);
  }

  /** Record something the browser itself did. No-op while the console is off. */
  client(stage: string, label: string, detail?: unknown, ms?: number | null): void {
    this.add('client', stage, label, detail, ms);
  }

  /** Record one trace event as it arrives from the loop. */
  server(event: ServerDebugEvent): void {
    this.add('server', event.stage, event.label, event.detail, event.elapsedMs);
  }

  /** The whole timeline as pretty JSON, for the panel's copy/download buttons. */
  export(): string {
    return JSON.stringify(
      this.entries().map((e) => ({ ...e, at: new Date(e.at).toISOString() })),
      null,
      2,
    );
  }

  private add(source: 'client' | 'server', stage: string, label: string, detail?: unknown, ms?: number | null): void {
    if (!this.enabled()) return;

    const entry: DebugEntry = { id: this.nextId++, at: Date.now(), source, stage, label, detail, ms };
    this.entries.update((list) => {
      const next = [...list, entry];
      return next.length > DebugService.MAX_ENTRIES ? next.slice(next.length - DebugService.MAX_ENTRIES) : next;
    });
  }
}
