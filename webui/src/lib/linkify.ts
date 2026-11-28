export type Segment = { kind: 'text'; value: string } | { kind: 'link'; value: string; href: string };

/**
 * A Soulseek link as people read it: escapes decoded, except those that separate
 * its parts. Web links stay as written, like other messengers show them. Anything
 * invisible or able to reorder text (controls, bidi overrides, odd whitespace)
 * keeps the whole link as written, so it can't disguise the target.
 */
function readable(href: string): string {
  if (!/^slsk:/i.test(href)) return href;
  try {
    const decoded = decodeURI(href);
    return /[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]|[^\S ]/u.test(decoded) ? href : decoded;
  } catch {
    return href;
  }
}

/** Splits text into plain runs and web or Soulseek links. */
export function linkify(value: string): Segment[] {
  const segments: Segment[] = [];
  // slsk:// links open in the app's Shares page; App handles their clicks.
  // Soulseek paths may contain apostrophes ("I'm"); web links end at one.
  const matcher = /\b(?:https?:\/\/|www\.)[^\s<>"']+|\bslsk:\/\/[^\s<>"]+/gi;
  let cursor = 0;
  for (const match of value.matchAll(matcher)) {
    const index = match.index ?? 0;
    if (index > cursor) segments.push({ kind: 'text', value: value.slice(cursor, index) });
    const raw = match[0];
    const trimmed = raw.replace(/[.,!?;:']+$/, '');
    segments.push({ kind: 'link', value: readable(trimmed), href: trimmed.startsWith('www.') ? `https://${trimmed}` : trimmed });
    if (raw.length > trimmed.length) segments.push({ kind: 'text', value: raw.slice(trimmed.length) });
    cursor = index + raw.length;
  }
  if (cursor < value.length) segments.push({ kind: 'text', value: value.slice(cursor) });
  return segments;
}
