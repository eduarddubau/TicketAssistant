import { Injectable, computed, inject, signal } from '@angular/core';
import { API_BASE } from '../config';
import { JiraProject } from '../models';
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

  async load(): Promise<void> {
    try {
      const res = await fetch(`${API_BASE}/api/projects`, { headers: this.session.authHeader() });
      if (res.ok) this.projects.set(await res.json());
    } catch { this.projects.set([]); }
  }
}
