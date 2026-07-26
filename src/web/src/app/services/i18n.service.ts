import { Injectable, signal } from '@angular/core';
import { StringKey, Strings, de, en, ro } from '../i18n/strings';
import { prefersReducedMotion } from './theme.service';

export type Lang = 'en' | 'ro' | 'de';

/**
 * Offered in their own language — a reader looking for "Deutsch" is not looking for "German".
 *
 * The flags are inline SVG rather than the regional-indicator emoji (🇷🇴, 🇩🇪): Windows ships no
 * glyphs for those, so on the platform up.ps1 exists to support they would render as the bare
 * letters "RO" and "DE" beside a column that already says RO and DE. Drawn as data URIs for the
 * same reason the scheme coins are gradients — they are small enough to be values, and it keeps
 * the app free of an asset pipeline. English gets the Union Jack, simplified: at 16px the
 * counterchanged diagonals of the real thing are a smudge either way.
 */
export const LANGUAGES: { id: Lang; label: string; short: string; flag: string }[] = [
  {
    id: 'en', label: 'English', short: 'EN',
    flag:
      "url(\"data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 60 30'%3E" +
      "%3Crect width='60' height='30' fill='%23012169'/%3E" +
      "%3Cpath d='M0 0 60 30M60 0 0 30' stroke='%23fff' stroke-width='6'/%3E" +
      "%3Cpath d='M0 0 60 30M60 0 0 30' stroke='%23C8102E' stroke-width='3'/%3E" +
      "%3Cpath d='M30 0V30M0 15H60' stroke='%23fff' stroke-width='10'/%3E" +
      "%3Cpath d='M30 0V30M0 15H60' stroke='%23C8102E' stroke-width='6'/%3E%3C/svg%3E\")",
  },
  {
    id: 'ro', label: 'Română', short: 'RO',
    flag:
      "url(\"data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 3 2'%3E" +
      "%3Crect width='3' height='2' fill='%23002B7F'/%3E" +
      "%3Crect x='1' width='2' height='2' fill='%23FCD116'/%3E" +
      "%3Crect x='2' width='1' height='2' fill='%23CE1126'/%3E%3C/svg%3E\")",
  },
  {
    id: 'de', label: 'Deutsch', short: 'DE',
    flag:
      "url(\"data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 5 3'%3E" +
      "%3Crect width='5' height='3' fill='%23000'/%3E" +
      "%3Crect y='1' width='5' height='2' fill='%23DD0000'/%3E" +
      "%3Crect y='2' width='5' height='1' fill='%23FFCE00'/%3E%3C/svg%3E\")",
  },
];

const DICTIONARIES: Record<Lang, Strings> = { en, ro, de };

const STORAGE_KEY = 'ta-lang';

/**
 * What language the console speaks. English unless the browser asks for one of the others, and
 * unless the reader has picked one — a stored choice always wins, because someone who chose
 * English on a German machine meant it.
 *
 * `t()` reads the signal, so every template that calls it re-renders on a switch; nothing needs to
 * subscribe or reload. The switch itself crossfades the page (the same View Transitions machinery
 * the theme toggle uses) because unlike a theme change, the text moves — sentences change length
 * and the layout shifts under the reader, which is much easier to follow through a fade than as a
 * jump.
 *
 * The choice also rides on X-Language, so the assistant answers in the same language it is being
 * read in. The debug console stays English on purpose — see i18n/strings.ts.
 */
@Injectable({ providedIn: 'root' })
export class I18nService {
  readonly lang = signal<Lang>(this.initial());

  /** Per-request header, so the model is told which language to answer in. */
  headers(): Record<string, string> {
    return { 'X-Language': this.lang() };
  }

  /**
   * The string for a key in the current language, with `{name}` placeholders filled in. Falls back
   * to English for a key a translation somehow lacks — the typed dictionaries make that impossible,
   * but it costs nothing to not throw.
   */
  t(key: StringKey, params?: Record<string, string | number>): string {
    const text = DICTIONARIES[this.lang()][key] ?? en[key] ?? key;
    return params
      ? text.replace(/\{(\w+)\}/g, (whole, name) => String(params[name] ?? whole))
      : text;
  }

  set(lang: Lang): void {
    if (lang === this.lang()) return;

    if (prefersReducedMotion()) {
      this.apply(lang);
      return;
    }

    const doc = document as Document & {
      startViewTransition?: (update: () => void) => { ready: Promise<void> };
    };

    if (doc.startViewTransition) {
      const transition = doc.startViewTransition(() => this.apply(lang));
      void transition.ready.then(() => this.crossfade());
    } else {
      this.apply(lang);
    }
  }

  private apply(lang: Lang): void {
    this.lang.set(lang);
    document.documentElement.lang = lang;
    localStorage.setItem(STORAGE_KEY, lang);
  }

  // styles.css turns the default view-transition animation off (the theme toggle's circular reveal
  // needs that), so the crossfade is driven here instead: the old text dissolves while the new
  // settles in from just below.
  private crossfade(): void {
    const root = document.documentElement;
    const timing: KeyframeAnimationOptions = { duration: 320, easing: 'cubic-bezier(0.22, 1, 0.36, 1)' };
    root.animate({ opacity: [1, 0] }, { ...timing, pseudoElement: '::view-transition-old(root)' });
    root.animate(
      { opacity: [0, 1], transform: ['translateY(8px)', 'translateY(0)'] },
      { ...timing, pseudoElement: '::view-transition-new(root)' },
    );
  }

  private initial(): Lang {
    const stored = localStorage.getItem(STORAGE_KEY);
    if (isLang(stored)) return stored;

    // Only the primary tag matters: "de-AT" and "de" both want German.
    for (const tag of navigator.languages ?? [navigator.language]) {
      const primary = tag?.toLowerCase().split('-')[0];
      if (isLang(primary)) return primary;
    }
    return 'en';
  }
}

function isLang(value: string | null | undefined): value is Lang {
  return LANGUAGES.some((l) => l.id === value);
}
