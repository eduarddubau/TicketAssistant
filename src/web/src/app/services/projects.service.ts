import { Injectable, computed, inject, signal } from '@angular/core';
import { API_BASE } from '../config';
import { JiraProject, providerLabel } from '../models';
import { SessionService } from './session.service';

/**
 * The projects the user can file tickets in, across *every* active backend (mock, Jira, …) —
 * fetched from /api/projects. Feeds the create card's project picker and the ticket-link lookup.
 * Jira projects carry a site URL (for links); the mock's synthetic project doesn't.
 */
@Injectable({ providedIn: 'root' })
export class ProjectsService {
  private readonly session = inject(SessionService);

  readonly projects = signal<JiraProject[]>([]);

  // project key → its site's browser URL, for turning "SUP-1" into a link to the right site.
  private readonly projectSites = computed(() => {
    const map = new Map<string, string>();
    for (const p of this.projects()) {
      if (p.siteUrl) map.set(p.key.toUpperCase(), p.siteUrl);
    }
    return map;
  });

  siteUrlForProjectKey(projectKey: string): string | null {
    return this.projectSites().get(projectKey.toUpperCase()) ?? null;
  }

  /** The project a ticket id belongs to ("SUP-12" → the SUP project), if we know it. */
  projectForTicketId(ticketId: string): JiraProject | null {
    const dash = ticketId.lastIndexOf('-');
    const key = (dash > 0 ? ticketId.slice(0, dash) : ticketId).toUpperCase();
    return this.projects().find((p) => p.key.toUpperCase() === key) ?? null;
  }

  /** The distinct backends that have projects, for the provider picker. */
  providers(): string[] {
    return [...new Set(this.projects().map((p) => p.provider))];
  }

  /** Sites (workspaces) a provider exposes — empty for providers that have no such concept. */
  sitesFor(provider: string): string[] {
    return [...new Set(
      this.projects().filter((p) => p.provider === provider && p.siteName).map((p) => p.siteName!),
    )];
  }

  /** Projects within a provider (and site, when it has them). */
  projectsFor(provider: string, site?: string | null): JiraProject[] {
    return this.projects().filter(
      (p) => p.provider === provider && (!site || p.siteName === site),
    );
  }

  /**
   * Short badge for a ticket id — the system it lives in, plus its site when the provider has
   * one. The project itself is already legible from the id's prefix (PROJ-1002 → PROJ), so this
   * carries the part that would otherwise be invisible with several backends connected.
   */
  providerBadge(ticketId: string): string | null {
    const p = this.projectForTicketId(ticketId);
    if (!p) return null;
    const label = providerLabel(p.provider);
    return p.siteName ? `${label} · ${p.siteName}` : label;
  }

  async load(): Promise<void> {
    try {
      const res = await fetch(`${API_BASE}/api/projects`, { headers: this.session.authHeader() });
      if (res.ok) this.projects.set(await res.json());
    } catch { this.projects.set([]); }
  }
}
