import { Injectable, signal } from '@angular/core';
import { Origin, revealFrom } from './view-transition';

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
 * The switch itself is the circular reveal in view-transition.ts, shared with the scheme and
 * language switches.
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
  toggle(origin?: Origin): void {
    this.set(this.theme() === 'dark' ? 'light' : 'dark', origin);
  }

  set(theme: Theme, origin?: Origin): void {
    if (theme === this.theme()) return;
    revealFrom(origin, () => this.apply(theme));
  }

  private apply(theme: Theme): void {
    this.theme.set(theme);
    document.documentElement.dataset['theme'] = theme;
    localStorage.setItem(STORAGE_KEY, theme);
  }

  private initial(): Theme {
    const stored = localStorage.getItem(STORAGE_KEY);
    if (stored === 'light' || stored === 'dark') return stored;
    return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }
}
