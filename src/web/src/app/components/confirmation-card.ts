import { Component, EventEmitter, Input, OnInit, Output, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { JiraProject, OrchestrationEvent, providerLabel } from '../models';
import { ProjectsService } from '../services/projects.service';

type ConfirmationEvent = Extract<OrchestrationEvent, { type: 'confirmation_required' }>;
export interface Decision { approved: boolean; callId: string; edits?: Record<string, unknown>; }

interface Field { key: string; label: string; kind: 'text' | 'textarea' | 'select' | 'date'; options?: string[]; required?: boolean; readonly?: boolean; }
interface Spec { heading: string; verb: string; fields: Field[]; initial: Record<string, any>; edits: (v: Record<string, any>) => Record<string, unknown>; }

const SEVERITIES = ['Low', 'Medium', 'High', 'Urgent'];
const STATUSES = ['Open', 'InProgress', 'Blocked', 'Resolved', 'Closed'];

// One editable card per write tool — the same field sets the original console offered, so the
// user can review and tweak what the model proposed before it runs. When creating and the user
// has projects (Jira), project and kind pickers are prepended so the item lands in the right place
// as the right kind of thing — a model that heard "task" as "ticket" is fixable here.
function specFor(evt: ConfirmationEvent): Spec {
  const a = evt.arguments ?? {};
  // Editable: if the model picked the wrong ticket, the user corrects it here. The card shows the
  // ticket's provider/project live underneath, so it's obvious which system is about to change.
  const ticket: Field = { key: 'ticketId', label: 'Ticket', kind: 'text', required: true };

  switch (evt.toolName) {
    case 'create_ticket': {
      const fields: Field[] = [
        { key: 'title', label: 'Title', kind: 'text', required: true },
        { key: 'description', label: 'Description', kind: 'textarea', required: true },
        { key: 'priority', label: 'Severity', kind: 'select', options: SEVERITIES },
      ];
      return {
        heading: '⚠️ Review & confirm new item', verb: 'Create',
        fields,
        initial: { title: a['title'] ?? '', description: a['description'] ?? '', priority: a['priority'] ?? 'Medium' },
        edits: (v) => ({ title: v['title'], description: v['description'], priority: v['priority'] }),
      };
    }
    case 'update_ticket_status':
      return {
        heading: '⚠️ Confirm status change', verb: 'Update status',
        fields: [ticket, { key: 'status', label: 'New status', kind: 'select', options: STATUSES }],
        initial: { ticketId: a['ticketId'] ?? '', status: a['status'] ?? 'Open' },
        edits: (v) => ({ ticketId: v['ticketId'], status: v['status'] }),
      };
    case 'set_due_date':
      return {
        heading: '⚠️ Confirm due date', verb: 'Set due date',
        fields: [ticket, { key: 'dueAt', label: 'Due date (blank = none)', kind: 'date' }],
        initial: { ticketId: a['ticketId'] ?? '', dueAt: (a['dueAt'] ?? '').slice(0, 10) },
        edits: (v) => ({ ticketId: v['ticketId'], dueAt: v['dueAt'] || null }),
      };
    case 'assign_ticket':
      return {
        heading: '⚠️ Confirm assignment', verb: 'Assign ticket',
        fields: [ticket, { key: 'assignee', label: 'Assign to (blank = unassigned)', kind: 'text' }],
        initial: { ticketId: a['ticketId'] ?? '', assignee: a['assignee'] ?? '' },
        edits: (v) => ({ ticketId: v['ticketId'], assignee: v['assignee'] }),
      };
    case 'resolve_ticket':
      return {
        heading: '⚠️ Confirm resolve', verb: 'Resolve ticket',
        fields: [ticket, { key: 'note', label: 'Resolution note', kind: 'textarea', required: true }],
        initial: { ticketId: a['ticketId'] ?? '', note: a['note'] ?? '' },
        edits: (v) => ({ ticketId: v['ticketId'], note: v['note'] }),
      };
    case 'add_comment':
      return {
        heading: '⚠️ Confirm comment', verb: 'Add comment',
        fields: [ticket, { key: 'body', label: 'Comment', kind: 'textarea', required: true }],
        initial: { ticketId: a['ticketId'] ?? '', body: a['body'] ?? '' },
        edits: (v) => ({ ticketId: v['ticketId'], body: v['body'] }),
      };
    default:
      return {
        heading: '⚠️ Confirm: ' + evt.toolName, verb: 'Confirm',
        fields: [{ key: 'raw', label: 'Arguments', kind: 'textarea', readonly: true }],
        initial: { raw: JSON.stringify(a, null, 2) },
        edits: () => ({}),
      };
  }
}

@Component({
  selector: 'app-confirmation-card',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="card">
      <h3>{{ spec.heading }}</h3>

      @if (isCreate) {
        <!-- Where the new item goes and what kind it is. Provider is always asked for; site,
             project and kind only appear for backends that actually have them, so a flat backend
             isn't padded with empty pickers. -->
        <div class="target">
          <label class="fld">
            Provider
            <select [disabled]="decided()" [(ngModel)]="target.provider" (ngModelChange)="onProviderChange()">
              @for (p of providerOptions(); track p.id) { <option [value]="p.id">{{ p.label }}</option> }
            </select>
          </label>

          @if (siteOptions().length) {
            <label class="fld">
              Site
              <select [disabled]="decided()" [(ngModel)]="target.site" (ngModelChange)="onSiteChange()">
                @for (s of siteOptions(); track s) { <option [value]="s">{{ s }}</option> }
              </select>
            </label>
          }

          @if (projectOptions().length) {
            <label class="fld">
              Project
              <select [disabled]="decided()" [(ngModel)]="target.project" (ngModelChange)="onProjectChange()">
                @for (p of projectOptions(); track p.key) {
                  <option [value]="p.key">{{ p.key }}{{ p.name && p.name !== p.key ? ' — ' + p.name : '' }}</option>
                }
              </select>
            </label>
          }

          <!-- What kind of thing this is. Only the kinds the chosen project actually accepts are
               offered, so an approved card can't be rejected by the backend for an unknown type. -->
          @if (typeOptions().length) {
            <label class="fld">
              Kind
              <select [disabled]="decided()" [(ngModel)]="target.type">
                @for (t of typeOptions(); track t) { <option [value]="t">{{ t }}</option> }
              </select>
            </label>
          }
        </div>
      }

      @for (f of spec.fields; track f.key) {
        <label class="fld">
          {{ f.label }}
          @if (f.kind === 'textarea') {
            <textarea rows="3" [disabled]="!!f.readonly || decided()" [(ngModel)]="values[f.key]"></textarea>
          } @else if (f.kind === 'select') {
            <select [disabled]="!!f.readonly || decided()" [(ngModel)]="values[f.key]">
              @for (o of f.options; track o) { <option [value]="o">{{ o }}</option> }
            </select>
          } @else {
            <input [type]="f.kind === 'date' ? 'date' : 'text'"
                   [disabled]="!!f.readonly || decided()" [(ngModel)]="values[f.key]" />
          }
        </label>
      }
      @if (!isCreate) {
        <!-- Where this change will land, resolved live from the ticket id above. Only the parts
             the owning provider actually has are shown. -->
        @if (ticketOrigin(); as o) {
          <div class="origin">
            <span><b>Provider</b>{{ o.provider }}</span>
            @if (o.site) { <span><b>Site</b>{{ o.site }}</span> }
            @if (o.project) { <span><b>Project</b>{{ o.project }}</span> }
          </div>
        } @else if (hasTicketId()) {
          <div class="origin unknown">Unrecognised ticket — check the id</div>
        }
      }
      @if (!decided()) {
        <div class="actions">
          <button class="approve" [disabled]="!valid()" (click)="approve()">{{ verbLabel() }}</button>
          <button class="decline" (click)="declineIt()">Cancel</button>
        </div>
      } @else {
        <div class="decided">{{ approvedChoice ? '✓ ' + verbLabel() : '✗ Cancelled' }}</div>
      }
    </div>
  `,
  styles: [`
    .card {
      position: relative; overflow: hidden;
      border: 1px solid var(--border); border-radius: var(--r);
      padding: 1rem 1.1rem; background: var(--surface-2);
      backdrop-filter: var(--blur); -webkit-backdrop-filter: var(--blur);
      box-shadow: var(--shadow);
    }
    .card::before { content: ''; position: absolute; left: 0; top: 0; bottom: 0; width: 3px; background: var(--grad); }

    h3 { margin: 0 0 0.9rem; font-size: 0.9rem; font-weight: 700; letter-spacing: -0.01em; }

    .fld {
      display: block; margin-bottom: 0.7rem;
      font-size: 0.66rem; text-transform: uppercase; letter-spacing: 0.06em; color: var(--text-faint); font-weight: 700;
    }
    .fld input, .fld textarea, .fld select {
      display: block; width: 100%; margin-top: 0.35rem; padding: 0.55rem 0.65rem;
      border-radius: var(--r-sm); border: 1px solid var(--border); background: rgba(0, 0, 0, 0.28); color: var(--text);
      font-size: 0.85rem; font-weight: 400; text-transform: none; letter-spacing: 0; box-sizing: border-box;
      transition: border-color 0.15s var(--ease), box-shadow 0.15s var(--ease);
    }
    .fld input:focus, .fld textarea:focus, .fld select:focus {
      outline: none; border-color: rgba(124, 108, 255, 0.55); box-shadow: 0 0 0 3px rgba(124, 108, 255, 0.14);
    }
    .fld input:disabled, .fld textarea:disabled, .fld select:disabled { opacity: 0.6; }
    .fld select option, .fld select optgroup { background: #12141c; color: var(--text); }

    /* Provider / Site / Project sit on one row when they fit; each keeps its own label so it is
       obvious which is which. */
    .target { display: flex; flex-wrap: wrap; gap: 0.6rem; }
    .target .fld { flex: 1 1 8rem; min-width: 7rem; }

    .origin {
      display: flex; flex-wrap: wrap; gap: 0.45rem;
      margin-top: 0.15rem; font-size: 0.72rem; color: var(--text-dim);
    }
    .origin span {
      display: inline-flex; align-items: center; gap: 0.35rem;
      background: var(--surface); border: 1px solid var(--border);
      border-radius: var(--r-sm); padding: 0.3rem 0.5rem;
    }
    .origin b {
      font-size: 0.6rem; text-transform: uppercase; letter-spacing: 0.06em;
      color: var(--text-faint); font-weight: 700;
    }
    .origin.unknown { color: #ffcf9a; border: 1px solid rgba(240, 180, 41, 0.45);
      background: rgba(240, 180, 41, 0.10); border-radius: var(--r-sm); padding: 0.35rem 0.55rem; }

    .actions { display: flex; gap: 0.55rem; margin-top: 0.95rem; }
    button { padding: 0.55rem 1.05rem; border-radius: var(--r-sm); border: 0; font-weight: 600; font-size: 0.83rem; transition: 0.15s var(--ease); }
    .approve { background: var(--grad); color: #fff; box-shadow: var(--glow); }
    .approve:hover:not(:disabled) { transform: translateY(-1px); filter: brightness(1.06); }
    .approve:disabled { opacity: 0.5; box-shadow: none; cursor: default; }
    .decline { background: var(--surface); border: 1px solid var(--border); color: var(--text-dim); }
    .decline:hover { background: var(--surface-2); color: var(--text); }
    .decided { font-size: 0.83rem; color: var(--text-dim); font-weight: 600; }
  `],
})
export class ConfirmationCard implements OnInit {
  @Input({ required: true }) event!: ConfirmationEvent;
  @Input() projects: JiraProject[] = [];
  @Output() decision = new EventEmitter<Decision>();

  private readonly projectsSvc = inject(ProjectsService);

  /** Where a new item goes and what kind it is. Provider is always required; the rest depend on it. */
  target: { provider: string; site: string | null; project: string | null; type: string | null } =
    { provider: '', site: null, project: null, type: null };

  get isCreate(): boolean { return this.event.toolName === 'create_ticket'; }

  providerOptions(): { id: string; label: string }[] {
    return this.projectsSvc.providers().map((id) => ({ id, label: providerLabel(id) }));
  }

  siteOptions(): string[] {
    return this.target.provider ? this.projectsSvc.sitesFor(this.target.provider) : [];
  }

  projectOptions(): JiraProject[] {
    return this.target.provider
      ? this.projectsSvc.projectsFor(this.target.provider, this.target.site)
      : [];
  }

  /** The kinds the chosen project accepts; empty means the backend didn't say, so we don't ask. */
  typeOptions(): string[] {
    return this.projectsSvc.itemTypesFor(this.target.project);
  }

  // Changing provider (or site, or project) invalidates the narrower choices below it — reset to the
  // first available so the card never sits on a combination that doesn't exist.
  onProviderChange(): void {
    this.target.site = this.siteOptions()[0] ?? null;
    this.onSiteChange();
  }

  onSiteChange(): void {
    this.target.project = this.projectOptions()[0]?.key ?? null;
    this.onProjectChange();
  }

  // Kinds are per project, so keep the current one only if the new project has it too.
  onProjectChange(): void {
    const kinds = this.typeOptions();
    this.target.type = this.matchType(this.target.type, kinds) ?? kinds[0] ?? null;
  }

  /** The offered kind matching a proposed one, case-insensitively ("task" -> "Task"). */
  private matchType(proposed: string | null | undefined, options: string[]): string | null {
    const wanted = `${proposed ?? ''}`.trim().toLowerCase();
    return options.find((o) => o.toLowerCase() === wanted) ?? null;
  }

  /**
   * The button's words. On a create they follow the chosen kind — "Create task" rather than a
   * generic "Create" — so the button states what is about to happen, including after the user
   * changes the kind.
   */
  verbLabel(): string {
    return this.isCreate ? `Create ${(this.target.type ?? 'item').toLowerCase()}` : this.spec.verb;
  }

  hasTicketId(): boolean {
    return `${this.values['ticketId'] ?? ''}`.trim().length > 0;
  }

  /** The provider/site/project a ticket belongs to, resolved live from the id the user can edit. */
  ticketOrigin(): { provider: string; site: string | null; project: string | null } | null {
    const id = `${this.values['ticketId'] ?? ''}`.trim();
    if (!id) return null;
    const p = this.projectsSvc.projectForTicketId(id);
    if (!p) return null;
    return { provider: providerLabel(p.provider), site: p.siteName ?? null, project: p.key };
  }

  spec!: Spec;
  values: Record<string, any> = {};
  decided = signal(false);
  approvedChoice = false;

  ngOnInit(): void {
    this.spec = specFor(this.event);
    this.values = { ...this.spec.initial };

    if (this.isCreate) {
      // Start from whatever project the model proposed; otherwise the first available combination.
      const proposed = `${this.event.arguments?.['project'] ?? ''}`.trim();
      const match = proposed ? this.projectsSvc.projectForTicketId(`${proposed}-0`) : null;
      this.target.provider = match?.provider ?? this.providerOptions()[0]?.id ?? '';
      this.target.site = match?.siteName ?? this.siteOptions()[0] ?? null;
      this.target.project = match?.key ?? this.projectOptions()[0]?.key ?? null;
      // Then the kind the model proposed, if this project has it; otherwise its first.
      const kinds = this.typeOptions();
      this.target.type = this.matchType(`${this.event.arguments?.['type'] ?? ''}`, kinds) ?? kinds[0] ?? null;
    }
  }

  valid(): boolean {
    const fieldsOk = this.spec.fields.every((f) => !f.required || `${this.values[f.key] ?? ''}`.trim().length > 0);
    if (!this.isCreate) return fieldsOk;
    // A create needs a provider, and a project whenever the chosen provider offers any.
    return fieldsOk && !!this.target.provider && (this.projectOptions().length === 0 || !!this.target.project);
  }

  approve(): void {
    this.approvedChoice = true;
    this.decided.set(true);
    const edits = this.spec.edits(this.values);
    if (this.isCreate && this.target.project) {
      edits['project'] = this.target.project;
    }
    if (this.isCreate && this.target.type) {
      edits['type'] = this.target.type;
    }
    this.decision.emit({ approved: true, callId: this.event.callId, edits });
  }

  declineIt(): void {
    this.approvedChoice = false;
    this.decided.set(true);
    this.decision.emit({ approved: false, callId: this.event.callId });
  }
}
