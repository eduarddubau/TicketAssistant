import { Component, HostListener, inject, signal } from '@angular/core';
import { I18nService, LANGUAGES, Lang } from '../services/i18n.service';

/**
 * The language switcher: a two-letter pill that opens the three languages, each written in itself.
 * Sits beside the appearance controls because it is the same kind of setting — how the console is
 * presented, decided in the browser and never sent anywhere except as a hint to the model about
 * which language to answer in.
 */
@Component({
  selector: 'app-language',
  standalone: true,
  template: `
    <div class="wrap" [class.open]="open()">
      <button class="pill" (click)="toggleOpen($event)" [title]="i18n.t('language.label')"
              aria-haspopup="true" [attr.aria-expanded]="open()">
        <span class="flag" [style.background-image]="current().flag"></span>
        {{ current().short }}
      </button>

      @if (open()) {
        <div class="pop" role="radiogroup" [attr.aria-label]="i18n.t('language.label')"
             (click)="$event.stopPropagation()">
          @for (l of languages; track l.id) {
            <button class="row" role="radio" [class.on]="i18n.lang() === l.id"
                    [attr.aria-checked]="i18n.lang() === l.id" (click)="pick(l.id)">
              <span class="flag" [style.background-image]="l.flag"></span>
              <span class="name">{{ l.label }}</span>
              <span class="code">{{ l.short }}</span>
            </button>
          }
          <p class="note">{{ i18n.t('language.note') }}</p>
        </div>
      }
    </div>
  `,
  styles: [`
    :host { display: block; }
    .wrap { position: relative; }

    .pill {
      display: flex; align-items: center; gap: 0.4rem;
      height: var(--pill-h, 34px); padding: 0 0.75rem;
      border-radius: var(--r-full); background: var(--surface); border: 1px solid var(--border);
      color: var(--text-dim); font-size: 0.78rem; font-weight: 700; line-height: 1;
      letter-spacing: 0.03em; transition: 0.15s var(--ease);
    }
    .pill:hover { background: var(--surface-2); color: var(--text); border-color: var(--border-strong); }
    .wrap.open .pill { border-color: var(--border-strong); color: var(--text); }

    .pop {
      position: absolute; top: calc(100% + 0.4rem); right: 0; z-index: 30;
      min-width: 11rem; padding: 0.45rem; border-radius: var(--r); text-align: left;
      background: var(--pop); border: 1px solid var(--border-strong);
      backdrop-filter: var(--blur); -webkit-backdrop-filter: var(--blur);
      box-shadow: var(--shadow-lg); animation: rise 0.15s var(--ease);
    }
    .row {
      display: flex; align-items: center; gap: 0.55rem; width: 100%;
      padding: 0.34rem 0.45rem; border-radius: var(--r-sm);
      background: transparent; border: 1px solid transparent;
      color: var(--text); font-size: 0.78rem; text-align: left;
    }
    .row:hover { background: var(--surface-2); }
    .row.on { background: var(--accent-soft); border-color: var(--accent-line); color: var(--accent-fg); }
    /* Flags are 3:2, drawn from a data URI. The hairline keeps a white or near-white edge (the
       Union Jack's field, Germany's gold) from bleeding into the surface behind it. */
    .flag {
      flex-shrink: 0; width: 18px; height: 12px; border-radius: 2px;
      background-size: cover; background-position: center;
      box-shadow: inset 0 0 0 1px light-dark(rgba(18, 22, 48, 0.22), rgba(255, 255, 255, 0.26));
    }

    /* Pushed to the end and dimmed: in the list the name is what you read, and the code is only
       there to tie a row to the two letters on the closed pill. */
    .code {
      flex-shrink: 0; margin-left: auto; font-size: 0.62rem; font-weight: 700; letter-spacing: 0.06em;
      color: var(--text-faint);
    }
    .row.on .code { color: inherit; opacity: 0.75; }
    .name { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .note { margin: 0.35rem 0.45rem 0.15rem; font-size: 0.68rem; color: var(--text-faint); line-height: 1.45; }
  `],
})
export class Language {
  readonly i18n = inject(I18nService);
  readonly languages = LANGUAGES;
  readonly open = signal(false);

  current(): (typeof LANGUAGES)[number] {
    return LANGUAGES.find((l) => l.id === this.i18n.lang()) ?? LANGUAGES[0];
  }

  pick(lang: Lang): void {
    this.open.set(false);
    this.i18n.set(lang);
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
