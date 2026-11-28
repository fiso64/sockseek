import { expect, it } from 'vitest';
import { relativeTime } from '../src/app/relative-time';

it('formats prototype age labels and leaves unknown creation dates absent', () => {
  const now = Date.parse('2026-09-09T12:00:00Z');
  expect(relativeTime('2026-09-09T11:58:00Z', now)).toBe('2 min ago');
  expect(relativeTime('2026-09-09T11:00:00Z', now)).toBe('1 h ago');
  expect(relativeTime('2026-09-08T12:00:00Z', now)).toBe('1 d ago');
  expect(relativeTime('2026-09-09T12:02:00Z', now)).toBe('in 2 min');
  expect(relativeTime(null, now)).toBeNull();
  expect(relativeTime('invalid', now)).toBeNull();
});
