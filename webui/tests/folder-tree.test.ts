import { describe, expect, it } from 'vitest';
import { buildFolderTreeRows } from '../src/domains/presentation/folder-tree';
import type { FolderItemFile } from '../src/domains/presentation/items';

describe('folder tree projection', () => {
  it('keeps unknown sizes distinct from real zeroes in nested folder totals', () => {
    const rows = buildFolderTreeRows([
      { id: 'unknown', relativePath: 'Nested/Deep/unknown.txt', sizeBytes: 0, sizeUnknown: true },
      { id: 'zero', relativePath: 'Nested/zero.txt', sizeBytes: 0 },
      { id: 'known', relativePath: 'Nested/known.txt', sizeBytes: 128 },
      { id: 'sibling', relativePath: 'Other/zero.txt', sizeBytes: 0 },
    ], new Set(['unknown', 'known']), 'tree');

    expect(rows.map((row) => row.key)).toEqual([
      'folder:Nested', 'folder:Nested/Deep', 'file:unknown', 'file:known',
      'file:zero', 'folder:Other', 'file:sibling',
    ]);
    const folders = rows.filter((row) => row.kind === 'folder');
    expect(folders.map((row) => ({
      path: row.relativePath,
      files: row.node.fileCount,
      selected: row.node.selectedCount,
      knownBytes: row.node.sizeBytes,
      unknownFiles: row.node.unknownSizeCount,
    }))).toEqual([
      { path: 'Nested', files: 3, selected: 2, knownBytes: 128, unknownFiles: 1 },
      { path: 'Nested/Deep', files: 1, selected: 1, knownBytes: 0, unknownFiles: 1 },
      { path: 'Other', files: 1, selected: 0, knownBytes: 0, unknownFiles: 0 },
    ]);
  });

  it('leaves flat rows in source order without creating synthetic folders', () => {
    const rows = buildFolderTreeRows([
      { id: 'b', relativePath: 'B/one.txt', sizeBytes: 0, sizeUnknown: true },
      { id: 'a', relativePath: 'A/two.txt', sizeBytes: 0 },
    ], new Set(), 'flat');
    expect(rows.map((row) => row.key)).toEqual(['file:b', 'file:a']);
  });

  it.each(['flat', 'tree'] as const)('uses replacement file data and current selection in %s layout', layout => {
    const original: FolderItemFile = {
      id: 'file', relativePath: 'Folder/Old.txt', sizeBytes: 128,
      transfer: { state: 'Downloading', tone: 'active' },
    };
    const before = buildFolderTreeRows([original], new Set(), layout);
    const replacement: FolderItemFile = {
      ...original, relativePath: 'Folder/New.txt', sizeBytes: 256,
      transfer: { state: 'Complete', tone: 'complete' },
    };
    const after = buildFolderTreeRows([replacement], new Set(['file']), layout);
    const file = after.find(row => row.kind === 'file')!;
    expect(file.file.relativePath).toBe('Folder/New.txt');
    expect(file.file.sizeBytes).toBe(256);
    expect(file.file.transfer?.state).toBe('Complete');
    expect(file.displayName).toBe(layout === 'flat' ? 'Folder/New.txt' : 'New.txt');
    if (layout === 'tree') {
      const folder = after.find(row => row.kind === 'folder')!;
      expect(folder.node.selectedCount).toBe(1);
      expect(folder.node.sizeBytes).toBe(256);
    }
    expect(before.find(row => row.kind === 'file')!.file.transfer?.state).toBe('Downloading');
  });
});
