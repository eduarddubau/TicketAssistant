import { Component, HostListener, inject, signal } from '@angular/core';
import { ThemeService } from '../services/theme.service';
import { PALETTES, Palette, PaletteService } from '../services/palette.service';
import { I18nService } from '../services/i18n.service';
import { originOf } from '../services/view-transition';

/**
 * How the app looks, as the two questions it actually is: light or dark, and which accent scheme.
 *
 * The mode is a one-click toggle because that is how it gets used — you flip it when the room
 * changes, not once a quarter. The scheme is behind a coin that opens the five choices, because it
 * is picked rarely and there is nothing to check at a glance once it is picked: the whole app is
 * already wearing it.
 */
@Component({
  selector: 'app-appearance',
  standalone: true,
  template: `
    <button class="mode" (click)="toggleTheme($event)"
            [title]="modeHint()" [attr.aria-label]="modeHint()">
      @if (theme.theme() === 'dark') {
        <svg class="ico moon" viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor"
             stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/>
        </svg>
      } @else {
        <svg class="ico sun" viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor"
             stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <circle cx="12" cy="12" r="4"/>
          <path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>
        </svg>
      }
    </button>

    <div class="wrap" [class.open]="open()">
      <button class="pill" (click)="toggleOpen($event)" [title]="i18n.t('appearance.scheme')"
              aria-haspopup="true" [attr.aria-expanded]="open()">
        <span class="coin" [style.background]="current().preview"></span>
        {{ current().label }}
      </button>

      @if (open()) {
        <div class="pop" role="radiogroup" [attr.aria-label]="i18n.t('appearance.scheme')"
             (click)="$event.stopPropagation()">
          @for (p of palettes; track p.id) {
            <button class="row" role="radio" [class.on]="palette.palette() === p.id"
                    [attr.aria-checked]="palette.palette() === p.id" (click)="pick(p.id, $event)">
              <span class="coin" [style.background]="p.preview"></span>
              <span class="name">{{ p.label }}</span>
            </button>
          }
          <p class="note">{{ i18n.t('appearance.schemeNote') }}</p>
        </div>
      }
    </div>
  `,
  styles: [`
    :host { display: flex; align-items: center; gap: 0.4rem; }

    .mode {
      display: grid; place-items: center; flex-shrink: 0;
      width: var(--pill-h, 34px); height: var(--pill-h, 34px);
      border-radius: var(--r-full); background: var(--surface); border: 1px solid var(--border);
      color: var(--text-dim); transition: 0.15s var(--ease);
    }
    .mode:hover { background: var(--surface-2); color: var(--text); border-color: var(--border-strong); }
    /* The icon arrives turning: the button swaps glyph at the same moment the theme sweeps across
       the page, and a glyph that just appears reads as a glitch next to that. */
    .ico { animation: ico-in 0.4s var(--ease); }
    @keyframes ico-in {
      from { opacity: 0; transform: rotate(-70deg) scale(0.6); }
      to { opacity: 1; transform: rotate(0) scale(1); }
    }
    @media (prefers-reduced-motion: reduce) { .ico { animation: none; } }

    .wrap { position: relative; }

    .pill {
      display: flex; align-items: center; gap: 0.45rem;
      height: var(--pill-h, 34px); padding: 0 0.8rem;
      border-radius: var(--r-full); background: var(--surface); border: 1px solid var(--border);
      color: var(--text-dim); font-size: 0.78rem; font-weight: 600; line-height: 1;
      transition: 0.15s var(--ease);
    }
    .pill:hover { background: var(--surface-2); color: var(--text); border-color: var(--border-strong); }
    .wrap.open .pill { border-color: var(--border-strong); color: var(--text); }

    /* The scheme itself, as an object: its primary across most of the coin, blending to its second
       colour — enough to tell the five apart without a legend. */
    .coin {
      width: 15px; height: 15px; border-radius: 50%; flex-shrink: 0;
      box-shadow: inset 0 0 0 1px light-dark(rgba(18, 22, 48, 0.18), rgba(255, 255, 255, 0.22));
    }

    .pop {
      position: absolute; top: calc(100% + 0.4rem); right: 0; z-index: 30;
      min-width: 11rem; padding: 0.45rem; border-radius: var(--r); text-align: left;
      background: var(--pop); border: 1px solid var(--border-strong);
      backdrop-filter: var(--blur); -webkit-backdrop-filter: var(--blur);
      box-shadow: var(--shadow-lg); animation: rise 0.15s var(--ease);
    }
    .row {
      display: flex; align-items: center; gap: 0.5rem; width: 100%;
      padding: 0.34rem 0.45rem; border-radius: var(--r-sm);
      background: transparent; border: 1px solid transparent;
      color: var(--text); font-size: 0.78rem; text-align: left;
    }
    .row:hover { background: var(--surface-2); }
    .row.on { background: var(--accent-soft); border-color: var(--accent-line); color: var(--accent-fg); }
    .name { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .note { margin: 0.35rem 0.45rem 0.15rem; font-size: 0.68rem; color: var(--text-faint); line-height: 1.45; }
  `],
})
export class Appearance {
  readonly theme = inject(ThemeService);
  readonly palette = inject(PaletteService);
  readonly i18n = inject(I18nService);

  readonly palettes = PALETTES;
  readonly open = signal(false);

  /** What a click would do, not what the theme currently is — it's a button, not a badge. */
  modeHint(): string {
    return this.i18n.t(this.theme.theme() === 'dark' ? 'appearance.toLight' : 'appearance.toDark');
  }

  current(): (typeof PALETTES)[number] {
    return PALETTES.find((p) => p.id === this.palette.palette()) ?? PALETTES[0];
  }

  // Both reveals start at the control that caused them, so the change reads as coming from the
  // thing that was clicked rather than from nowhere.
  toggleTheme(event: MouseEvent): void {
    this.theme.toggle(originOf(event));
  }

  pick(palette: Palette, event: MouseEvent): void {
    const origin = originOf(event);   // taken before the popover closes and the row is gone
    this.open.set(false);
    this.palette.set(palette, origin);
  }

  toggleOpen(event: Event): void {
    event.stopPropagation();   // this click must not reach the document handler below
    this.open.update((open) => !open);
  }

  @HostListener('document:click')
  closeOnOutsideClick(): void {
    this.open.set(false);
  }

  @HostListener('document:keydown.escape')
  closeOnEscape(): void {
    this.open.set(false);
  }
}
