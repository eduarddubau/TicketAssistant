import { Component, EventEmitter, Input, OnInit, Output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { JiraProject, OrchestrationEvent } from '../models';

type ConfirmationEvent = Extract<OrchestrationEvent, { type: 'confirmation_required' }>;
export interface Decision { approved: boolean; callId: string; edits?: Record<string, unknown>; }

interface OptionGroup { label: string; options: { value: string; label: string }[]; }
interface Field { key: string; label: string; kind: 'text' | 'textarea' | 'select' | 'date'; options?: string[]; optionLabels?: Record<string, string>; groups?: OptionGroup[]; required?: boolean; readonly?: boolean; }
interface Spec { heading: string; verb: string; fields: Field[]; initial: Record<string, any>; edits: (v: Record<string, any>) => Record<string, unknown>; }

const SEVERITIES = ['Low', 'Medium', 'High', 'Urgent'];
const STATUSES = ['Open', 'InProgress', 'Blocked', 'Resolved', 'Closed'];

// Groups projects by their source so the picker shows which provider each belongs to: the mock
// board, then one group per Jira site. Each group becomes an <optgroup> (a labelled divider).
function groupProjects(projects: JiraProject[]): OptionGroup[] {
  const groups = new Map<string, { value: string; label: string }[]>();
  for (const p of projects) {
    const source = p.siteName ? `Jira · ${p.siteName}` : 'Mock board';
    (groups.get(source) ?? groups.set(source, []).get(source)!)
      .push({ value: p.key, label: `${p.key} — ${p.name}` });
  }
  return [...groups.entries()].map(([label, options]) => ({ label, options }));
}

// One editable card per write tool — the same field sets the original console offered, so the
// user can review and tweak what the model proposed before it runs. When creating and the user
// has projects (Jira), a project picker is prepended so the ticket lands in the right place.
function specFor(evt: ConfirmationEvent, projects: JiraProject[]): Spec {
  const a = evt.arguments ?? {};
  const ticket: Field = { key: 'ticketId', label: 'Ticket', kind: 'text', readonly: true };

  switch (evt.toolName) {
    case 'create_ticket': {
      const fields: Field[] = [
        { key: 'title', label: 'Title', kind: 'text', required: true },
        { key: 'description', label: 'Description', kind: 'textarea', required: true },
        { key: 'priority', label: 'Severity', kind: 'select', options: SEVERITIES },
      ];
      if (projects.length) {
        fields.unshift({ key: 'project', label: 'Project', kind: 'select', required: true, groups: groupProjects(projects) });
      }
      return {
        heading: '⚠️ Review & confirm new ticket', verb: 'Create ticket',
        fields,
        initial: {
          project: a['project'] ?? (projects.length === 1 ? projects[0].key : ''),
          title: a['title'] ?? '', description: a['description'] ?? '', priority: a['priority'] ?? 'Medium',
        },
        edits: (v) => ({
          title: v['title'], description: v['description'], priority: v['priority'],
          ...(projects.length ? { project: v['project'] } : {}),
        }),
      };
    }
    case 'update_ticket_status':
      return {
        heading: '⚠️ Confirm status change', verb: 'Update status',
        fields: [ticket, { key: 'status', label: 'New status', kind: 'select', options: STATUSES }],
        initial: { ticketId: a['ticketId'] ?? '', status: a['status'] ?? 'Open' },
        edits: (v) => ({ status: v['status'] }),
      };
    case 'set_due_date':
      return {
        heading: '⚠️ Confirm due date', verb: 'Set due date',
        fields: [ticket, { key: 'dueAt', label: 'Due date (blank = none)', kind: 'date' }],
        initial: { ticketId: a['ticketId'] ?? '', dueAt: (a['dueAt'] ?? '').slice(0, 10) },
        edits: (v) => ({ dueAt: v['dueAt'] || null }),
      };
    case 'assign_ticket':
      return {
        heading: '⚠️ Confirm assignment', verb: 'Assign ticket',
        fields: [ticket, { key: 'assignee', label: 'Assign to (blank = unassigned)', kind: 'text' }],
        initial: { ticketId: a['ticketId'] ?? '', assignee: a['assignee'] ?? '' },
        edits: (v) => ({ assignee: v['assignee'] }),
      };
    case 'resolve_ticket':
      return {
        heading: '⚠️ Confirm resolve', verb: 'Resolve ticket',
        fields: [ticket, { key: 'note', label: 'Resolution note', kind: 'textarea', required: true }],
        initial: { ticketId: a['ticketId'] ?? '', note: a['note'] ?? '' },
        edits: (v) => ({ note: v['note'] }),
      };
    case 'add_comment':
      return {
        heading: '⚠️ Confirm comment', verb: 'Add comment',
        fields: [ticket, { key: 'body', label: 'Comment', kind: 'textarea', required: true }],
        initial: { ticketId: a['ticketId'] ?? '', body: a['body'] ?? '' },
        edits: (v) => ({ body: v['body'] }),
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
      @for (f of spec.fields; track f.key) {
        <label class="fld">
          {{ f.label }}
          @if (f.kind === 'textarea') {
            <textarea rows="3" [disabled]="!!f.readonly || decided()" [(ngModel)]="values[f.key]"></textarea>
          } @else if (f.kind === 'select') {
            <select [disabled]="!!f.readonly || decided()" [(ngModel)]="values[f.key]">
              @if (f.groups) {
                <option value="" disabled>Select a project…</option>
                @for (g of f.groups; track g.label) {
                  <optgroup [label]="g.label">
                    @for (o of g.options; track o.value) { <option [value]="o.value">{{ o.label }}</option> }
                  </optgroup>
                }
              } @else {
                @for (o of f.options; track o) { <option [value]="o">{{ f.optionLabels?.[o] || o }}</option> }
              }
            </select>
          } @else {
            <input [type]="f.kind === 'date' ? 'date' : 'text'"
                   [disabled]="!!f.readonly || decided()" [(ngModel)]="values[f.key]" />
          }
        </label>
      }
      @if (!decided()) {
        <div class="actions">
          <button class="approve" [disabled]="!valid()" (click)="approve()">{{ spec.verb }}</button>
          <button class="decline" (click)="declineIt()">Cancel</button>
        </div>
      } @else {
        <div class="decided">{{ approvedChoice ? '✓ ' + spec.verb : '✗ Cancelled' }}</div>
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

  spec!: Spec;
  values: Record<string, any> = {};
  decided = signal(false);
  approvedChoice = false;

  ngOnInit(): void {
    this.spec = specFor(this.event, this.projects);
    this.values = { ...this.spec.initial };
  }

  valid(): boolean {
    return this.spec.fields.every((f) => !f.required || `${this.values[f.key] ?? ''}`.trim().length > 0);
  }

  approve(): void {
    this.approvedChoice = true;
    this.decided.set(true);
    this.decision.emit({ approved: true, callId: this.event.callId, edits: this.spec.edits(this.values) });
  }

  declineIt(): void {
    this.approvedChoice = false;
    this.decided.set(true);
    this.decision.emit({ approved: false, callId: this.event.callId });
  }
}
