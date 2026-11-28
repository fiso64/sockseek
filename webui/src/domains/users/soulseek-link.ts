import { copyText } from '../../lib/clipboard';
import { notify } from '../../lib/notifications.svelte';

/**
 * A `slsk://` link. Like folders and files (trailing `/` or not), `slsk://user`
 * is the user's profile and `slsk://user/` the root of their shares. Paths match
 * the daemon's reading of Soulseek link inputs.
 */
export function parseSoulseekLink(href: string): { username: string; path: string; profile: boolean } | null {
  if (!/^slsk:\/\//i.test(href)) return null;
  const rest = href.slice('slsk://'.length);
  const separator = rest.indexOf('/');
  try {
    const username = decodeURIComponent(separator < 0 ? rest : rest.slice(0, separator));
    const path = separator < 0 ? '' : decodeURIComponent(rest.slice(separator + 1));
    return username ? { username, path: path.replace(/\//g, '\\').replace(/\\+$/, ''), profile: separator < 0 } : null;
  } catch {
    return null;
  }
}

/**
 * The app address a link opens: the link without its scheme, under /shares,
 * or /users for a profile. Both use the same encoding, so the path carries over.
 */
export function soulseekLinkPath(href: string): string | null {
  if (!parseSoulseekLink(href)) return null;
  const rest = href.slice('slsk://'.length);
  return rest.includes('/') ? `/shares/${rest}` : `/users/${rest}`;
}

/** Like the daemon's escaping: also `!'()*`, so a link survives being pasted into text. */
const escape = (value: string) => encodeURIComponent(value)
  .replace(/[!'()*]/g, character => `%${character.charCodeAt(0).toString(16).toUpperCase()}`);

/** A `slsk://` link to a peer's shared folder or file, as the daemon formats them, or with no path to their profile. */
export function formatSoulseekLink(username: string, path = '', folder = false): string {
  const components = path.split(/[\\/]/).filter(Boolean).map(escape);
  if (!components.length && !folder) return `slsk://${escape(username)}`;
  return `slsk://${escape(username)}/${components.join('/')}${folder ? '/' : ''}`;
}

/** Copies a link and says so briefly; a failure shows the link to copy by hand. */
export async function copySoulseekLink(link: string): Promise<void> {
  notify(await copyText(link) ? { key: 'soulseek-link', tone: 'success', title: 'Link copied' }
    : { key: 'soulseek-link', tone: 'error', title: 'Could not copy the link', details: [link] });
}
