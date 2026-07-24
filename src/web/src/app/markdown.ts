// A deliberately tiny Markdown renderer — enough for the assistant's replies (emphasis, inline
// code, links, paragraphs) without pulling in a dependency. Everything is HTML-escaped first, so
// model output can't inject markup; then ticket ids are turned into links to the board/Jira.

function escapeHtml(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

export function renderMarkdown(text: string, ticketHref: (id: string) => string | null): string {
  let html = escapeHtml(text);

  // Ticket ids first, while the string is plain text (no anchors to corrupt), e.g. PROJ-1042.
  html = html.replace(/\b([A-Z][A-Z0-9]+-\d+)\b/g, (whole, id: string) => {
    const href = ticketHref(id);
    return href ? `<a href="${href}" target="_blank" rel="noopener">${id}</a>` : whole;
  });

  html = html
    .replace(/`([^`]+)`/g, '<code>$1</code>')
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
    .replace(/\*([^*]+)\*/g, '<em>$1</em>')
    .replace(/\[([^\]]+)\]\((https?:[^)\s]+)\)/g, '<a href="$2" target="_blank" rel="noopener">$1</a>')
    .replace(/\n{2,}/g, '</p><p>')
    .replace(/\n/g, '<br>');

  return `<p>${html}</p>`;
}
