// A deliberately tiny Markdown renderer — enough for the assistant's replies (emphasis, inline
// code, links, paragraphs, lists and headings) without pulling in a dependency. Everything is
// HTML-escaped first, so model output can't inject markup; then ticket ids are turned into links to
// the board/Jira and tagged with the system they live in.
//
// Lists and headings are here because the assistant's answers are mostly lists — a grouped ticket
// listing is a heading plus bullets per group. Without them the markup arrived on screen as literal
// "###" and "-" characters, which is what made a long reply look broken rather than formatted.

const TICKET_ID = /\b([A-Z][A-Z0-9]+-\d+)\b/g;
const IS_TICKET_ID = /^[A-Z][A-Z0-9]+-\d+$/;

function escapeHtml(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function anchor(href: string, label: string): string {
  return `<a href="${href}" target="_blank" rel="noopener">${label}</a>`;
}

export interface TicketRendering {
  /** Link target for a ticket id, or null when we can't resolve one. */
  href: (id: string) => string | null;
  /** Short label for the system it lives in ("Mock board", "Jira · acme"), or null if unknown. */
  origin?: (id: string) => string | null;
}

export function renderMarkdown(text: string, ticket: TicketRendering): string {
  let html = escapeHtml(text);

  // A ticket id, rendered as a link plus a badge naming the system it belongs to. The badge is
  // what makes the provider visible without depending on the model to mention it.
  const renderTicket = (id: string, fallback: string): string => {
    const href = ticket.href(id);
    const origin = ticket.origin?.(id);
    const body = href ? anchor(href, id) : fallback;
    // Literal space before the badge so it never runs into the id, whatever the CSS does.
    return origin ? `${body} <span class="src">${escapeHtml(origin)}</span>` : body;
  };

  // Markdown links first, so a link the model wrote is handled as one unit. When its label is a
  // ticket id we substitute *our* URL: models sometimes invent a placeholder host
  // (…/your-ticket-system-url/PROJ-1001), which would otherwise render as a dead link.
  html = html.replace(/\[([^\]]+)\]\((https?:[^)\s]+)\)/g, (whole, label: string, url: string) => {
    const id = label.trim();
    return IS_TICKET_ID.test(id) ? renderTicket(id, label) : anchor(url, label);
  });

  // Kept within a single line: emphasis spanning a line break is never what the model meant, and a
  // greedy match across newlines would swallow a "* item" bullet list into one italic run.
  html = html
    .replace(/`([^`\n]+)`/g, '<code>$1</code>')
    .replace(/\*\*([^*\n]+)\*\*/g, '<strong>$1</strong>')
    .replace(/\*([^*\n]+)\*/g, '<em>$1</em>');

  // Then bare ticket ids — but only outside the anchors created above, so ids already linked
  // aren't wrapped a second time. Splitting on a capturing group yields alternating
  // outside/inside segments.
  html = html
    .split(/(<a\b[^>]*>.*?<\/a>)/g)
    .map((segment, i) =>
      i % 2 === 1
        ? segment
        : segment.replace(TICKET_ID, (whole, id: string) => renderTicket(id, whole)))
    .join('');

  return toBlocks(html);
}

/**
 * Turns the inline-rendered text into block markup, line by line: `#`-prefixed lines become a
 * heading, `-`/`*`/`•` and `1.` lines become list items (grouped into one list per run), and
 * everything else collects into paragraphs with single newlines kept as breaks.
 *
 * Every heading level collapses to one size: these live inside a chat bubble, where an `<h1>`
 * would tower over the conversation, and a model's choice between `##` and `####` carries no
 * meaning worth honouring.
 */
function toBlocks(html: string): string {
  const out: string[] = [];
  let paragraph: string[] = [];
  let list: 'ul' | 'ol' | null = null;

  const flushParagraph = () => {
    if (paragraph.length) {
      out.push(`<p>${paragraph.join('<br>')}</p>`);
      paragraph = [];
    }
  };
  const closeList = () => {
    if (list) {
      out.push(`</${list}>`);
      list = null;
    }
  };

  for (const raw of html.split('\n')) {
    const line = raw.trim();
    if (!line) {
      flushParagraph();
      closeList();
      continue;
    }

    // A rule ("---") is a separator, not text and not a bullet — models use them between sections.
    if (/^([-*_])\1{2,}$/.test(line)) {
      flushParagraph();
      closeList();
      out.push('<hr>');
      continue;
    }

    const heading = /^#{1,6}\s+(.+)$/.exec(line);
    if (heading) {
      flushParagraph();
      closeList();
      out.push(`<h4>${heading[1]}</h4>`);
      continue;
    }

    // A bullet needs whitespace after its marker, so a "---" rule or a stray "*" isn't one.
    const bullet = /^[-*•]\s+(.+)$/.exec(line);
    const numbered = bullet ? null : /^\d+[.)]\s+(.+)$/.exec(line);
    if (bullet || numbered) {
      flushParagraph();
      const wanted = bullet ? 'ul' : 'ol';
      if (list !== wanted) {
        closeList();
        out.push(`<${wanted}>`);
        list = wanted;
      }
      out.push(`<li>${(bullet ?? numbered)![1]}</li>`);
      continue;
    }

    closeList();
    paragraph.push(line);
  }

  flushParagraph();
  closeList();
  return out.join('');
}
