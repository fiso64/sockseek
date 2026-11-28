import { expect, test } from 'vitest';
import { inheritedSelection, projectSelection } from '../src/app/tree-selection';
import { formatSoulseekLink, parseSoulseekLink, soulseekLinkPath } from '../src/domains/users/soulseek-link';
import { linkify } from '../src/lib/linkify';

const parents = new Map<string, string | null>([
  ['d:1', null], ['d:2', 'd:1'], ['d:3', 'd:2'], ['f:4', 'd:3'],
]);
const counts = new Map([['d:1', 10], ['d:2', 6], ['d:3', 2], ['f:4', 1]]);
const parentOf = (key: string) => parents.get(key) ?? null;
const countOf = (key: string) => counts.get(key) ?? 0;

test('counts nested exclusions once and restores explicitly re-included branches', () => {
  const included = new Set(['d:1']);
  const excluded = new Set(['d:2', 'd:3', 'f:4']);
  expect(projectSelection(included, excluded, parentOf, countOf).total).toBe(4);
  excluded.delete('d:3');
  included.add('d:3');
  expect(projectSelection(included, excluded, parentOf, countOf).total).toBe(5);
  const bytes = new Map([['d:1', 9007199254740998n], ['d:2', 500n], ['d:3', 300n], ['f:4', 1n]]);
  const projection = projectSelection(included, excluded, parentOf, key => ({ count: countOf(key), bytes: bytes.get(key)!.toString() }));
  expect(projection.byteDelta).toBe(9007199254740797n);
  expect(projection.subtreeCount('d:1')).toBe(5);
  expect(projection.subtreeCount('d:2')).toBe(1);
  expect(projection.subtreeCount('d:3')).toBe(1);
  expect(inheritedSelection('f:4', included, excluded, parentOf)).toBe(true);
  excluded.delete('f:4');
  expect(projectSelection(included, excluded, parentOf, countOf).total).toBe(6);
});

test('does not count redundant descendants and gives exclusion precedence at one identity', () => {
  expect(projectSelection(new Set(['d:1', 'd:2', 'f:4']), new Set(), parentOf, countOf).total).toBe(10);
  expect(projectSelection(new Set(['d:1', 'f:4']), new Set(['f:4']), parentOf, countOf).total).toBe(9);
});

test('preserves exact large IDs and terminates malformed cycles', () => {
  const first = 'd:9007199254740992';
  const second = 'd:9007199254740993';
  const parent = (key: string) => key === first ? second : first;
  expect(inheritedSelection(first, new Set([second]), new Set(), parent)).toBe(true);
  expect(inheritedSelection(first, new Set(), new Set(), parent)).toBe(false);
});

test('keeps selected and downloadable counts distinct for locked descendants', () => {
  const ancestry = new Map<string, string | null>([
    ['d:root', null], ['f:public', 'd:root'], ['f:locked', 'd:root'],
  ]);
  const parent = (key: string) => ancestry.get(key) ?? null;
  const total = (key: string) => key === 'd:root' ? 2 : 1;
  const publicCount = (key: string) => key === 'f:locked' ? 0 : 1;
  const included = new Set(['d:root']);
  const excluded = new Set<string>();

  expect(projectSelection(included, excluded, parent, total).total).toBe(2);
  expect(projectSelection(included, excluded, parent, publicCount).total).toBe(1);
  excluded.add('f:locked');
  expect(projectSelection(included, excluded, parent, total).total).toBe(1);
  expect(projectSelection(included, excluded, parent, publicCount).total).toBe(1);
  expect(projectSelection(new Set(['f:locked']), new Set(), parent, total).total).toBe(1);
  expect(projectSelection(new Set(['f:locked']), new Set(), parent, publicCount).total).toBe(0);
});

test('reads a Soulseek link as a peer and the shared path it points to', () => {
  expect(parseSoulseekLink('slsk://DJ%20Name/Music/Some%20Album/')).toEqual({ username: 'DJ Name', path: 'Music\\Some Album', profile: false });
  expect(parseSoulseekLink('SLSK://peer/Music/01.flac')).toEqual({ username: 'peer', path: 'Music\\01.flac', profile: false });
  // Without a slash a link is the user's profile; with one, the root of their shares.
  expect(parseSoulseekLink('slsk://peer')).toEqual({ username: 'peer', path: '', profile: true });
  expect(parseSoulseekLink('slsk://peer/')).toEqual({ username: 'peer', path: '', profile: false });
  expect(formatSoulseekLink('DJ Name')).toBe('slsk://DJ%20Name');
  expect(parseSoulseekLink('slsk:///Music')).toBeNull();
  expect(parseSoulseekLink('slsk://peer/%E0%A4%A')).toBeNull();
});

test('formats share links that read back as the same folder or file', () => {
  const folder = formatSoulseekLink('DJ Name', 'Music\\Some Album #1', true);
  expect(folder).toBe('slsk://DJ%20Name/Music/Some%20Album%20%231/');
  expect(parseSoulseekLink(folder)).toEqual({ username: 'DJ Name', path: 'Music\\Some Album #1', profile: false });
  expect(parseSoulseekLink(formatSoulseekLink('peer', 'Music\\01.flac', false))).toEqual({ username: 'peer', path: 'Music\\01.flac', profile: false });
});

test('keeps apostrophes inside Soulseek links in text, and copies them escaped', () => {
  const link = "slsk://some-peer/Music/Artist/02.%20Artist%20-%20I'm%20Here.flac";
  const links = linkify(`see ${link}, or 'slsk://peer/A/'`).filter(part => part.kind === 'link');
  expect(links.map(part => part.href)).toEqual([link, 'slsk://peer/A/']);
  // Shown readably; the target stays exact, and escaped separators stay escaped.
  expect(links[0]?.value).toBe("slsk://some-peer/Music/Artist/02. Artist - I'm Here.flac");
  // Web links, and Soulseek links hiding direction overrides, show as written.
  expect(linkify('https://example.com/a%20b')[0]).toMatchObject({ value: 'https://example.com/a%20b' });
  expect(linkify('slsk://peer/a%E2%80%AEgpj.exe')[0]).toMatchObject({ value: 'slsk://peer/a%E2%80%AEgpj.exe' });
  expect(formatSoulseekLink('peer', "Music\\I'm (Live).flac", false)).toBe('slsk://peer/Music/I%27m%20%28Live%29.flac');
});

test('opens a link at the app address with the same path, or a profile without one', () => {
  expect(soulseekLinkPath('slsk://some%20peer/Music/I%27m%20Here/')).toBe('/shares/some%20peer/Music/I%27m%20Here/');
  expect(soulseekLinkPath('slsk://some-peer/')).toBe('/shares/some-peer/');
  expect(soulseekLinkPath('slsk://some-peer')).toBe('/users/some-peer');
  expect(soulseekLinkPath('slsk:///Music')).toBeNull();
});
