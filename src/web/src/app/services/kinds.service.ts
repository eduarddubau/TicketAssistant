import { Injectable, computed, inject, signal } from '@angular/core';
import { DebugService } from './debug.service';
import { ProjectsService } from './projects.service';

const STORAGE_KEY = 'ta-kinds';

/**
 * Which kinds of item the console is looking at — the header's kind toggles. Nothing ticked means
 * everything, which is the default and the honest one: a filter that starts on would silently hide
 * work.
 *
 * The choice rides on an `X-Item-Types` header (like the LLM switchers) and is enforced by the API
 * on the two fanned-out reads, so it holds whatever the model decides to do. The options aren't
 * hardcoded either: they're the kinds the connected projects actually accept, so a Jira site with
 * Bugs and Stories offers Bugs and Stories.
 */
@Injectable({ providedIn: 'root' })
export class KindsService {
  private readonly projects = inject(ProjectsService);
  private readonly debug = inject(DebugService);

  readonly selected = signal<ReadonlySet<string>>(load());

  /** The kinds on offer, in the order the backends report them (mock first, then each Jira site). */
  readonly available = computed(() => this.projects.itemTypes());

  /** The same list in the filter pill's shape — a kind is its own label. */
  readonly options = computed(() => this.available().map((kind) => ({ value: kind, label: kind })));

  /**
   * A selected kind no longer on offer (Jira disconnected, say) is dropped rather than sent: the API
   * would filter every item out, and an empty screen is a worse answer than an unfiltered one. Same
   * reason the header is omitted entirely before the projects have loaded.
   */
  readonly active = computed(() => {
    const offered = this.available();
    return offered.filter((kind) => this.selected().has(kind));
  });

  /** What the toolbar pill says: the filter at a glance, without opening it. */
  readonly summary = computed(() => {
    const on = this.active();
    if (!on.length) return 'All kinds';
    return on.length <= 2 ? on.join(', ') : `${on.length} kinds`;
  });

  /** Per-request header for the chat/confirm calls; absent when nothing is filtered. */
  headers(): Record<string, string> {
    const on = this.active();
    return on.length ? { 'X-Item-Types': on.join(',') } : {};
  }

  isOn(kind: string): boolean {
    return this.selected().has(kind);
  }

  toggle(kind: string): void {
    const next = new Set(this.selected());
    next.has(kind) ? next.delete(kind) : next.add(kind);
    this.apply(next);
  }

  /** Back to everything — the "All kinds" reset. */
  clear(): void {
    if (this.selected().size) this.apply(new Set());
  }

  private apply(next: Set<string>): void {
    this.selected.set(next);
    localStorage.setItem(STORAGE_KEY, JSON.stringify([...next]));

    const on = this.active();
    this.debug.client(
      'filter',
      on.length ? `kinds → ${on.join(', ')} only` : 'kinds → all (filter off)',
      { selected: [...next], sentAsHeader: this.headers(), offered: this.available() },
    );
  }
}

function load(): ReadonlySet<string> {
  try {
    const raw = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '[]');
    return new Set(Array.isArray(raw) ? raw.filter((k) => typeof k === 'string') : []);
  } catch {
    return new Set();
  }
}
