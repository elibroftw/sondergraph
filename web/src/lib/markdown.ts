/**
 * Minimal markdown renderer for note display.
 *
 * Deliberately tiny rather than a full parser. Notes are the user's own text shown back to
 * the person who wrote them, so headings, emphasis, inline code, links, lists and paragraphs
 * cover almost everything. A dependency would be justified only if notes needed the full
 * CommonMark surface.
 *
 * Safety: the input is HTML-escaped before any markup is added, so a note containing a tag is
 * displayed literally rather than interpreted. The text comes from a file on disk that the
 * user may not have authored, so it must never be trusted as markup.
 *
 * Not supported, on purpose: raw HTML, tables, images, nested lists, reference links. Where
 * these appear the text is shown as-is, which is a reasonable failure.
 */

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
}

/** Renders inline constructs within one already-escaped line. */
function renderInline(text: string): string {
  let result = escapeHtml(text)

  // Inline code first, so its contents are not treated as emphasis or links.
  const codeSpans: string[] = []
  result = result.replace(/`([^`]+)`/g, (_match, code: string) => {
    codeSpans.push(`<code>${code}</code>`)
    return `\u0000${codeSpans.length - 1}\u0000`
  })

  result = result
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
    .replace(/(^|[^*])\*([^*\n]+)\*(?!\*)/g, '$1<em>$2</em>')
    // Links are restricted to http(s) so a `javascript:` URL cannot be produced.
    .replace(
      /\[([^\]]+)\]\((https?:\/\/[^\s)]+)\)/g,
      '<a href="$2" target="_blank" rel="noreferrer noopener">$1</a>',
    )

  // Restore the code spans.
  return result.replace(/\u0000(\d+)\u0000/g, (_match, index: string) => codeSpans[Number(index)])
}

/**
 * Renders markdown to HTML.
 *
 * @param markdown The note body.
 * @returns HTML safe to inject with `{@html}`.
 */
export function renderMarkdown(markdown: string): string {
  if (!markdown || !markdown.trim()) {
    return ''
  }

  const blocks = markdown.replace(/\r\n/g, '\n').split(/\n{2,}/)
  const output: string[] = []

  for (const block of blocks) {
    const trimmed = block.trim()

    if (!trimmed) {
      continue
    }

    const heading = /^(#{1,6})\s+(.*)$/.exec(trimmed)

    if (heading) {
      // Notes appear inside a panel, so a level-1 heading would be visually louder than the
      // person's own name. Shift down by two to keep the hierarchy sane.
      const level = Math.min(heading[1].length + 2, 6)
      output.push(`<h${level}>${renderInline(heading[2])}</h${level}>`)
      continue
    }

    const lines = trimmed.split('\n')

    if (lines.every((line) => /^\s*[-*]\s+/.test(line))) {
      const items = lines
        .map((line) => `<li>${renderInline(line.replace(/^\s*[-*]\s+/, ''))}</li>`)
        .join('')
      output.push(`<ul>${items}</ul>`)
      continue
    }

    // Single newlines inside a paragraph become line breaks, which is what people expect
    // when they press Enter twice in a plain textarea rather than twice-for-newline.
    output.push(`<p>${renderInline(trimmed).replace(/\n/g, '<br>')}</p>`)
  }

  return output.join('')
}
