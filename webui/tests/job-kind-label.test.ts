import { expect, test } from 'vitest';
import type { Schema } from '../src/api/types';
import { jobKindLabel } from '../src/domains/jobs/kind';

test.each([
  ['song', 'Song'],
  ['album', 'Album'],
  ['aggregate', 'Song Aggregate'],
  ['album-aggregate', 'Album Aggregate'],
  ['extract', 'Import'],
  ['job-list', 'Job List'],
  ['remote-file', 'Remote File'],
  ['remote-directory', 'Remote Directory'],
  ['retrieve-folder', 'Retrieve Folder'],
  ['generic', 'Generic'],
  ['search', 'Search'],
] satisfies [Schema<'ServerJobKind'>, string][])(
  'presents %s as %s across jobs and review', (kind, label) => {
    expect(jobKindLabel(kind)).toBe(label);
  },
);
