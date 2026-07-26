import { Injectable, signal } from '@angular/core';
import { Origin, revealFrom } from './view-transition';

export type Palette = 'violet' | 'indigo' | 'emerald' | 'rose' | 'slate';

const STORAGE_KEY = 'ta-palette';

/**
 * The five schemes, in the order they are offered. `preview` is the swatch the picker draws: the
 * scheme's primary held across the first stretch and then blended to its second colour, so a coin
 * reads as the colour it is called (violet looks violet) while still showing that a scheme is a
 * gradient. The names double as the data-palette values and as the keys in styles.css.
 */
export const PALETTES: { id: Palette; label: string; preview: string }[] = [
  { id: 'violet', label: 'Violet', preview: 'linear-gradient(135deg, #7c6bff 0%, #7c6bff 45%, #b45cff 100%)' },
  { id: 'indigo', label: 'Indigo', preview: 'linear-gradient(135deg, #5468f2 0%, #5468f2 45%, #38e1ff 100%)' },
  { id: 'emerald', label: 'Emerald', preview: 'linear-gradient(135deg, #10b07f 0%, #10b07f 45%, #a3e635 100%)' },
  { id: 'rose', label: 'Rose', preview: 'linear-gradient(135deg, #f0416b 0%, #f0416b 45%, #fbbf24 100%)' },
  { id: 'slate', label: 'Slate', preview: 'linear-gradient(135deg, #5f7091 0%, #5f7091 45%, #7dd3fc 100%)' },
];

const IDS = PALETTES.map((p) => p.id);

/**
 * The accent scheme, which is a separate question from light or dark: every scheme is defined in
 * both modes, so picking one never says anything about the other. Violet is what :root already
 * declares, so it is the absence of the attribute rather than a value of it — one less thing for
 * the boot script in index.html to write before first paint.
 */
@Injectable({ providedIn: 'root' })
export class PaletteService {
  readonly palette = signal<Palette>(this.initial());

  /** `origin` is where the reveal starts — the swatch that was clicked. */
  set(palette: Palette, origin?: Origin): void {
    if (palette === this.palette()) return;
    revealFrom(origin, () => this.apply(palette));
  }

  private apply(palette: Palette): void {
    this.palette.set(palette);
    if (palette === 'violet') {
      document.documentElement.removeAttribute('data-palette');
    } else {
      document.documentElement.dataset['palette'] = palette;
    }
    localStorage.setItem(STORAGE_KEY, palette);
  }

  private initial(): Palette {
    const stored = localStorage.getItem(STORAGE_KEY) as Palette | null;
    return stored && IDS.includes(stored) ? stored : 'violet';
  }
}
