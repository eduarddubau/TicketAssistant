import { Injectable, signal } from '@angular/core';

export type Theme = 'light' | 'dark';

const STORAGE_KEY = 'ta-theme';

/**
 * Light or dark, as one signal and one attribute.
 *
 * Until the toggle is touched there is no stored choice and the OS decides — including live, if it
 * flips at sunset. The first toggle is a decision, so from then on the choice is stamped on <html>
 * as data-theme and persisted; the boot script in index.html re-applies it before first paint, which
 * is what stops a reload flashing the other theme.
 *
 * The switch itself is a circular reveal from the toggle button (View Transitions API): the new
 * theme is clipped in over a still frame of the old one, so the eye follows one edge instead of
 * every surface changing at once. Where the API is missing, colours cross-fade instead; where the
 * reader has asked for less motion, it just switches.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  readonly theme = signal<Theme>(this.initial());

  constructor() {
    // Only while the choice is still the OS's to make: once something is stored, the OS flipping
    // at sunset must not undo what the reader picked.
    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', (e) => {
      if (!localStorage.getItem(STORAGE_KEY)) this.theme.set(e.matches ? 'dark' : 'light');
    });
  }

  /** Switch to the other theme. `origin` is where the reveal starts — the button that was clicked. */
  toggle(origin?: { x: number; y: number }): void {
    this.set(this.theme() === 'dark' ? 'light' : 'dark', origin);
  }

  set(theme: Theme, origin?: { x: number; y: number }): void {
    if (theme === this.theme() || prefersReducedMotion()) {
      this.apply(theme);
      return;
    }

    const doc = document as Document & {
      startViewTransition?: (update: () => void) => { ready: Promise<void> };
    };

    if (doc.startViewTransition) {
      const transition = doc.startViewTransition(() => this.apply(theme));
      if (origin) void transition.ready.then(() => this.reveal(origin));
    } else {
      document.documentElement.classList.add('theme-transition');
      this.apply(theme);
      window.setTimeout(() => document.documentElement.classList.remove('theme-transition'), 450);
    }
  }

  private apply(theme: Theme): void {
    this.theme.set(theme);
    document.documentElement.dataset['theme'] = theme;
    localStorage.setItem(STORAGE_KEY, theme);
  }

  // The circle has to reach the furthest corner from the click, or the old theme is left in a
  // corner of the screen when the animation ends.
  private reveal(origin: { x: number; y: number }): void {
    const radius = Math.hypot(
      Math.max(origin.x, window.innerWidth - origin.x),
      Math.max(origin.y, window.innerHeight - origin.y),
    );
    document.documentElement.animate(
      {
        clipPath: [
          `circle(0px at ${origin.x}px ${origin.y}px)`,
          `circle(${radius}px at ${origin.x}px ${origin.y}px)`,
        ],
      },
      {
        duration: 550,
        easing: 'cubic-bezier(0.22, 1, 0.36, 1)',
        pseudoElement: '::view-transition-new(root)',
      },
    );
  }

  private initial(): Theme {
    const stored = localStorage.getItem(STORAGE_KEY);
    if (stored === 'light' || stored === 'dark') return stored;
    return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }
}

export function prefersReducedMotion(): boolean {
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}
