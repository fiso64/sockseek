import { expect, it } from 'vitest';
import { formatCount, formatCountWithNoun, formatSpeed, userInitials } from '../src/app/format';

it('uses one avatar-initial rule in profiles and conversations', () => {
  expect(userInitials('a-user')).toBe('AU');
  expect(userInitials('single')).toBe('S');
  expect(userInitials('...')).toBe('?');
});

it('formats string-encoded integer counts without losing wire precision', () => {
  expect(formatCount('9007199254740993')).toBe(9007199254740993n.toLocaleString());
  expect(formatCount('9223372036854775807')).toBe(9223372036854775807n.toLocaleString());
  expect(formatCount(1234)).toBe((1234).toLocaleString());
  expect(formatCount('0')).toBe('0');
});

it('keeps exact large count labels and singular grammar', () => {
  expect(formatCountWithNoun('1', 'file')).toBe('1 file');
  expect(formatCountWithNoun(1, 'directory', 'directories')).toBe('1 directory');
  expect(formatCountWithNoun('2', 'directory', 'directories')).toBe('2 directories');
  expect(formatCountWithNoun('9007199254740993', 'peer')).toBe(`${9007199254740993n.toLocaleString()} peers`);
});

it('formats observed transfer speed with the prototype precision', () => {
  expect(formatSpeed(8_450_000)).toBe('8.45 MB/s');
  expect(formatSpeed(14_800_000)).toBe('14.8 MB/s');
  expect(formatSpeed(400)).toBe('400 B/s');
  expect(formatSpeed(0)).toBeNull();
  expect(formatSpeed(null)).toBeNull();
});
