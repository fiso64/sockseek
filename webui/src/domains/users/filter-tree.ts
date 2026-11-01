import type { BrowseSearchPageDto } from '../../api/types';

type Entry = BrowseSearchPageDto['items'][number];
type Row = { entry: Entry; depth: number };

/** Loaded hierarchy in prototype order: folder, direct files, then subfolders.
 * Exact parent IDs are authoritative, not parsed display paths.
 */
export function filteredTreeRows(items: readonly Entry[]): Row[] {
  const directories = new Set(items.filter(item => item.kind === 'directory').map(item => String(item.entryId)));
  const children = new Map<string, { files: Entry[]; directories: Entry[] }>();
  const roots: Entry[] = [];
  for (const entry of items) {
    const parent = entry.kind === 'directory' ? entry.parentDirectoryId : entry.directoryId;
    if (parent == null || !directories.has(String(parent))) {
      roots.push(entry);
      continue;
    }
    const key = String(parent);
    let bucket = children.get(key);
    if (!bucket) children.set(key, bucket = { files: [], directories: [] });
    bucket[entry.kind === 'file' ? 'files' : 'directories'].push(entry);
  }
  const result: Row[] = [];
  const visited = new Set<string>();
  function append(entries: readonly Entry[]): void {
    const pending: Row[] = entries.map(entry => ({ entry, depth: 0 })).reverse();
    while (pending.length) {
      const row = pending.pop()!;
      const key = `${row.entry.kind}:${row.entry.entryId}`;
      if (visited.has(key)) continue;
      visited.add(key);
      result.push(row);
      if (row.entry.kind !== 'directory') continue;
      const bucket = children.get(String(row.entry.entryId));
      if (!bucket) continue;
      // Reverse stack order avoids recursion on deeply nested shares.
      for (const entries of [bucket.directories, bucket.files]) {
        for (let index = entries.length - 1; index >= 0; index--) {
          pending.push({ entry: entries[index]!, depth: row.depth + 1 });
        }
      }
    }
  }
  append(roots);
  // Isolate malformed cyclic components without dropping unrelated results.
  append(items.filter(entry => !visited.has(`${entry.kind}:${entry.entryId}`)));
  return result;
}
