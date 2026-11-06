import { expect, test } from 'vitest';
import type { BrowseSearchPageDto } from '../src/api/types';
import { filteredTreeRows } from '../src/domains/users/filter-tree';

type Entry = BrowseSearchPageDto['items'][number];
function directory(entryId: string, parentDirectoryId: string | null): Entry {
  return {
    kind: 'directory', entryId, directoryId: entryId, parentDirectoryId,
    name: 'Same name', displayPath: 'Not/a/hierarchy', visibility: 'public',
    publicMatchingFileCount: 1, publicMatchingBytes: 128,
    lockedMatchingFileCount: 0, lockedMatchingBytes: 0,
    fileSize: null, extension: null, bitRate: null, bitDepth: null, sampleRate: null, length: null,
  };
}

test('indents loaded ancestors and files by exact identities regardless of input order or display spelling', () => {
  const root = directory('9007199254740992', null);
  const child = directory('9007199254740993', String(root.entryId));
  const file: Entry = { ...child, kind: 'file', entryId: '7', name: 'song.flac' };
  const entries = [file, child, root];
  expect(filteredTreeRows(entries)).toEqual([
    { entry: root, depth: 0 }, { entry: child, depth: 1 }, { entry: file, depth: 2 },
  ]);
});

test('incorporates ancestors arriving on subsequent pages without inventing unloaded path segments', () => {
  const child = directory('2', '1');
  expect(filteredTreeRows([child])[0]?.depth).toBe(0);
  expect(filteredTreeRows([child, directory('1', null)])[1]?.depth).toBe(1);
});

test('keeps direct files before subfolders and each subtree contiguous', () => {
  const root = directory('1', null);
  const child = directory('2', '1');
  const sibling = directory('3', null);
  const file: Entry = { ...root, kind: 'file', entryId: '4' };
  expect(filteredTreeRows([root, child, sibling, file]).map(({ entry }) => entry.entryId)).toEqual(['1', '4', '2', '3']);
});

test('isolates cyclic hierarchy components without losing valid roots or looping', () => {
  const first = directory('1', '2');
  const second = directory('2', '1');
  const valid = directory('3', null);
  expect(filteredTreeRows([first, second, valid]).map(({ entry }) => entry.entryId)).toEqual(['3', '1', '2']);
});


test('collapsing filtered folders hides descendants without turning them into orphan roots', () => {
  const root = directory('1', null);
  const child = directory('2', '1');
  const sibling = directory('3', null);
  const direct: Entry = { ...root, kind: 'file', entryId: '4' };
  const nested: Entry = { ...child, kind: 'file', entryId: '5' };
  const items = [root, child, sibling, direct, nested];
  expect(filteredTreeRows(items, new Set(['1'])).map(({ entry }) => entry.entryId)).toEqual(['1', '3']);
  expect(filteredTreeRows(items, new Set(['2'])).map(({ entry }) => entry.entryId)).toEqual(['1', '4', '2', '3']);
  expect(filteredTreeRows(items).map(({ entry }) => entry.entryId)).toEqual(['1', '4', '2', '5', '3']);
});
