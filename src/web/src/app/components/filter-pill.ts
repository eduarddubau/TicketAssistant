import { Component, EventEmitter, HostListener, Input, Output, signal } from '@angular/core';

/**
 * A header pill that opens a list of toggles: the shape both reading filters use — which systems to
 * read, and which kinds of item. Nothing ticked always means everything, so an untouched console
 * hides nothing.
 *
 * Presentational only: it holds the open/closed state and reports clicks, while what the options are
 * and what ticking one does belong to the service behind each filter. A popover rather than inline
 * chips because the lists are as long as the connected backends make them — a Jira site can offer
 * half a dozen issue types, which would wrap the header onto a second row.
 */
@Component({
  selector: 'app-filter-pill',
  standalone: true,
  template: `
    <div class="wrap" [class.open]="open()">
      <button class="pill" [class.on]="selected.length" (click)="toggleOpen($event)" [title]="hint">
        <ng-content />
        {{ summary }}
      </button>

      @if (open()) {
        <div class="pop" (click)="$event.stopPropagation()">
          @if (options.length) {
            <button class="all" [class.on]="!selected.length" (click)="cleared.emit()">{{ allLabel }}</button>
            @for (o of options; track o.value) {
              <label class="row">
                <input type="checkbox" [checked]="isOn(o.value)" (change)="toggled.emit(o.value)" />
                <span class="name">{{ o.label }}</span>
              </label>
            }
            <p class="note">Nothing ticked means all of them.</p>
          } @else {
            <p class="note">{{ emptyHint }}</p>
          }
        </div>
      }
    </div>
  `,
  styles: [`
    :host { display: block; }
    .wrap { position: relative; }

    .pill {
      display: flex; align-items: center; gap: 0.4rem;
      height: var(--pill-h, 34px); padding: 0 0.85rem; max-width: 13rem;
      border-radius: var(--r-full); background: var(--surface); border: 1px solid var(--border);
      color: var(--text-dim); font-size: 0.78rem; font-weight: 600; line-height: 1;
      transition: 0.15s var(--ease);
    }
    .pill:hover { background: var(--surface-2); color: var(--text); border-color: var(--border-strong); }
    /* Lit while it hides something — a filter you've forgotten is a filter that misleads you. */
    .pill.on {
      background: rgba(124, 108, 255, 0.16); border-color: rgba(150, 120, 255, 0.6); color: #ded8ff;
    }
    .wrap.open .pill { border-color: var(--border-strong); }

    .pop {
      position: absolute; top: calc(100% + 0.4rem); left: 0; z-index: 30;
      min-width: 13rem; max-width: 22rem; max-height: 60vh; overflow: auto;
      padding: 0.45rem; border-radius: var(--r); text-align: left;
      background: rgba(14, 16, 24, 0.96); border: 1px solid var(--border-strong);
      backdrop-filter: var(--blur); -webkit-backdrop-filter: var(--blur);
      box-shadow: var(--shadow-lg); animation: rise 0.15s var(--ease);
    }
    .all {
      display: block; width: 100%; text-align: left; margin-bottom: 0.2rem;
      padding: 0.34rem 0.45rem; border-radius: var(--r-sm); border: 1px solid transparent;
      background: transparent; color: var(--text-dim); font-size: 0.76rem; font-weight: 600;
    }
    .all:hover { background: var(--surface-2); color: var(--text); }
    .all.on { background: rgba(124, 108, 255, 0.16); border-color: rgba(150, 120, 255, 0.5); color: #ded8ff; }

    .row {
      display: flex; align-items: center; gap: 0.5rem;
      padding: 0.3rem 0.45rem; border-radius: var(--r-sm); cursor: pointer;
      font-size: 0.78rem; color: var(--text);
    }
    .row:hover { background: var(--surface-2); }
    .row input { accent-color: var(--accent); flex-shrink: 0; }
    .name { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .note { margin: 0.35rem 0.45rem 0.15rem; font-size: 0.68rem; color: var(--text-faint); line-height: 1.45; }
  `],
})
export class FilterPill {
  /** What the pill says when closed — the filter at a glance, without opening it. */
  @Input({ required: true }) summary = '';
  @Input({ required: true }) options: { value: string; label: string }[] = [];
  @Input() selected: readonly string[] = [];
  @Input() allLabel = 'All';
  @Input() emptyHint = 'Nothing to choose from yet.';
  @Input() hint = '';

  @Output() toggled = new EventEmitter<string>();
  @Output() cleared = new EventEmitter<void>();

  readonly open = signal(false);

  isOn(value: string): boolean {
    return this.selected.includes(value);
  }

  toggleOpen(event: Event): void {
    event.stopPropagation();   // this click must not reach the document handler below
    this.open.update((open) => !open);
  }

  // A click anywhere else closes the list, and Escape does too — a panel dismissable only by the
  // button that opened it is a panel people leave open by accident.
  @HostListener('document:click')
  closeOnOutsideClick(): void {
    this.open.set(false);
  }

  @HostListener('document:keydown.escape')
  closeOnEscape(): void {
    this.open.set(false);
  }
}
