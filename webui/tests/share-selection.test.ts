import { expect, test } from 'vitest';
import { inheritedSelection, projectSelection, selectionCount } from '../src/domains/users/selection';

const parents = new Map<string, string | null>([
  ['d:1', null], ['d:2', 'd:1'], ['d:3', 'd:2'], ['f:4', 'd:3'],
]);
const counts = new Map([['d:1', 10], ['d:2', 6], ['d:3', 2], ['f:4', 1]]);
const parentOf = (key: string) => parents.get(key) ?? null;
const countOf = (key: string) => counts.get(key) ?? 0;

test('counts nested exclusions once and restores explicitly re-included branches', () => {
  const included = new Set(['d:1']);
  const excluded = new Set(['d:2', 'd:3', 'f:4']);
  expect(selectionCount(included, excluded, parentOf, countOf)).toBe(4);
  excluded.delete('d:3');
  included.add('d:3');
  expect(selectionCount(included, excluded, parentOf, countOf)).toBe(5);
  const projection = projectSelection(included, excluded, parentOf, countOf);
  expect(projection.descendantDeltas.get('d:1')).toBe(-5);
  expect(projection.descendantDeltas.get('d:2')).toBe(1);
  expect(projection.descendantDeltas.get('d:3')).toBe(-1);
  expect(inheritedSelection('f:4', included, excluded, parentOf)).toBe(true);
  excluded.delete('f:4');
  expect(selectionCount(included, excluded, parentOf, countOf)).toBe(6);
});

test('does not count redundant descendants and gives exclusion precedence at one identity', () => {
  expect(selectionCount(new Set(['d:1', 'd:2', 'f:4']), new Set(), parentOf, countOf)).toBe(10);
  expect(selectionCount(new Set(['d:1', 'f:4']), new Set(['f:4']), parentOf, countOf)).toBe(9);
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

  expect(selectionCount(included, excluded, parent, total)).toBe(2);
  expect(selectionCount(included, excluded, parent, publicCount)).toBe(1);
  excluded.add('f:locked');
  expect(selectionCount(included, excluded, parent, total)).toBe(1);
  expect(selectionCount(included, excluded, parent, publicCount)).toBe(1);
  expect(selectionCount(new Set(['f:locked']), new Set(), parent, total)).toBe(1);
  expect(selectionCount(new Set(['f:locked']), new Set(), parent, publicCount)).toBe(0);
});
