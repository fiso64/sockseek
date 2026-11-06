import { expect, it } from 'vitest';
import { directoryTotals } from '../src/domains/search/model';

it('uses matching totals before retrieval and authoritative retrieved totals afterwards', () => {
  const directory = { publicMatchingFileCount: '2', lockedMatchingFileCount: 1,
    publicMatchingBytes: '200', lockedMatchingBytes: 100, retrievedFileCount: null, retrievedBytes: null };
  expect(directoryTotals(directory)).toEqual({ fileCount: 3, bytes: 300 });
  expect(directoryTotals({ ...directory, retrievedFileCount: '5', retrievedBytes: '900' }))
    .toEqual({ fileCount: 5, bytes: 900 });
  expect(directoryTotals({ ...directory, retrievedFileCount: 0, retrievedBytes: 0 }))
    .toEqual({ fileCount: 0, bytes: 0 });
});
