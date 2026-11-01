import { expect, it } from 'vitest';
import { effectiveSelection, selectedPreviewCount, selectedSubtreeCount, toggleSubtree } from '../src/domains/jobs/preview-selection';

const counts = new Map([['0', 5], ['0/0', 3], ['0/0/1', 1], ['0/1', 2]]);

it('selects paged descendants through compact parent rules with nested exceptions', () => {
  let rules = toggleSubtree('0/0', false, true, []);
  expect(selectedPreviewCount(5, counts, true, rules)).toBe(2);
  expect(selectedSubtreeCount('0', counts, true, rules)).toBe(2);
  expect(selectedSubtreeCount('0/0', counts, true, rules)).toBe(0);
  rules = toggleSubtree('0/0/1', true, true, rules);
  expect(selectedPreviewCount(5, counts, true, rules)).toBe(3);
  expect(selectedSubtreeCount('0/0', counts, true, rules)).toBe(1);
  expect(effectiveSelection('0/0/1/hidden', true, rules)).toBe(true);
  rules = toggleSubtree('0/0', true, true, rules);
  expect(rules).toEqual([]);
  expect(selectedPreviewCount(5, counts, true, rules)).toBe(5);
});

it('supports select all from an initially empty review', () => {
  const rules = toggleSubtree('0', true, false, []);
  expect(selectedPreviewCount(5, counts, false, rules)).toBe(5);
  expect(effectiveSelection('0/1/unloaded', false, rules)).toBe(true);
});
