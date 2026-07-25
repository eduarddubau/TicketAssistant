// A deliberately tiny Markdown renderer — enough for the assistant's replies (emphasis, inline
// code, links, paragraphs) without pulling in a dependency. Everything is HTML-escaped first, so
// model output can't inject markup; then ticket ids are turned into links to the board/Jira and
// tagged with the system they live in.

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

  html = html
    .replace(/`([^`]+)`/g, '<code>$1</code>')
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
    .replace(/\*([^*]+)\*/g, '<em>$1</em>');

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

  html = html.replace(/\n{2,}/g, '</p><p>').replace(/\n/g, '<br>');

  return `<p>${html}</p>`;
}
