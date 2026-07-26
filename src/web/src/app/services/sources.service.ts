import { Injectable, computed, inject, signal } from '@angular/core';
import { providerLabel } from '../models';
import { DebugService } from './debug.service';
import { ProjectsService } from './projects.service';
import { I18nService } from './i18n.service';

const STORAGE_KEY = 'ta-sources';

/**
 * Which systems the assistant reads from — the header's source toggles. Nothing ticked means all of
 * them, which is the default: a filter that starts on would hide work silently.
 *
 * The sibling of [KindsService]. Reads fan out across every connected backend at once, which is the
 * point of the app but also means a question about real Jira work comes back mixed with the demo
 * board. This narrows it at the source, on an `X-Sources` header the API enforces, rather than
 * asking the model to ignore what it was handed.
 */
@Injectable({ providedIn: 'root' })
export class SourcesService {
  private readonly projects = inject(ProjectsService);
  private readonly debug = inject(DebugService);
  private readonly i18n = inject(I18nService);

  readonly selected = signal<ReadonlySet<string>>(load());

  /** The backends that actually answered — provider ids, in the order they're configured. */
  readonly available = computed(() =>
    this.projects.providers().map((id) => ({ value: id, label: providerLabel(id) })),
  );

  /**
   * A selected backend that's no longer there (Jira disconnected, say) is dropped rather than sent:
   * the API would filter every item out, and an empty screen is a worse answer than an unfiltered
   * one. Same reason the header is omitted entirely before the projects have loaded.
   */
  readonly active = computed(() => {
    const offered = this.available().map((o) => o.value);
    return offered.filter((id) => this.selected().has(id));
  });

  /** What the pill says: the filter at a glance, without opening it. */
  readonly summary = computed(() => {
    const on = this.active();
    if (!on.length) return this.i18n.t('toolbar.sources.all');
    // Backend names are what those systems are called, so they stay put; the counting word doesn't.
    return on.length === 1 ? providerLabel(on[0]) : this.i18n.t('toolbar.sources.count', { count: on.length });
  });

  /** Per-request header for the chat/confirm calls; absent when nothing is filtered. */
  headers(): Record<string, string> {
    const on = this.active();
    return on.length ? { 'X-Sources': on.join(',') } : {};
  }

  toggle(provider: string): void {
    const next = new Set(this.selected());
    next.has(provider) ? next.delete(provider) : next.add(provider);
    this.apply(next);
  }

  /** Back to every backend — the "All sources" reset. */
  clear(): void {
    if (this.selected().size) this.apply(new Set());
  }

  private apply(next: Set<string>): void {
    this.selected.set(next);
    localStorage.setItem(STORAGE_KEY, JSON.stringify([...next]));

    const on = this.active();
    this.debug.client(
      'filter',
      on.length ? `sources → ${on.map(providerLabel).join(', ')} only` : 'sources → all (filter off)',
      { selected: [...next], sentAsHeader: this.headers(), offered: this.available() },
    );
  }
}

function load(): ReadonlySet<string> {
  try {
    const raw = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '[]');
    return new Set(Array.isArray(raw) ? raw.filter((s) => typeof s === 'string') : []);
  } catch {
    return new Set();
  }
}
