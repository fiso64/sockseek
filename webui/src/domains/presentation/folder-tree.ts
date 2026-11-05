import type { FolderItemFile } from './items';

interface FolderTreeFile {
  file: FolderItemFile;
  name: string;
}

export interface FolderTreeNode {
  name: string;
  relativePath: string;
  folders: Map<string, FolderTreeNode>;
  files: FolderTreeFile[];
  fileCount: number;
  selectedCount: number;
  sizeBytes: number;
  unknownSizeCount: number;
}

export type FolderDisplayRow =
  | Readonly<{ kind: 'folder'; key: string; name: string; relativePath: string; depth: number; node: FolderTreeNode }>
  | Readonly<{ kind: 'file'; key: string; depth: number; displayName: string; file: FolderItemFile }>;

const naturalName = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });

type FileDisplayRow = Extract<FolderDisplayRow, { kind: 'file' }>;
// Files are immutable presentation snapshots. Keep their row identity too, so
// progress in one file does not invalidate every binding in a keyed folder list.
// Weak keys keep this reuse tied to the snapshot's ordinary reader lifetime.
const flatRows = new WeakMap<FolderItemFile, FileDisplayRow>();
const treeRows = new WeakMap<FolderItemFile, FileDisplayRow>();
function fileRow(file: FolderItemFile, depth: number, displayName: string, tree: boolean): FileDisplayRow {
  const cache = tree ? treeRows : flatRows;
  const previous = cache.get(file);
  const key = `file:${file.id}`;
  if (previous?.key === key && previous.depth === depth && previous.displayName === displayName) return previous;
  const row: FileDisplayRow = { kind: 'file', key, depth, displayName, file };
  cache.set(file, row);
  return row;
}

export function buildFolderTreeRows(
  source: FolderItemFile[],
  selectedFileIds: ReadonlySet<string>,
  layout: 'flat' | 'tree',
): FolderDisplayRow[] {
  if (layout === 'flat') {
    return source.map(file => fileRow(file, 0, file.relativePath, false));
  }

  const createNode = (name: string, relativePath: string): FolderTreeNode => ({
    name, relativePath, folders: new Map(), files: [], fileCount: 0,
    selectedCount: 0, sizeBytes: 0, unknownSizeCount: 0,
  });
  const root = createNode('', '');
  for (const file of source) {
    const parts = file.relativePath.split(/[\\/]+/).filter(Boolean);
    const filename = parts.pop() ?? file.relativePath;
    let node = root;
    for (const part of parts) {
      const relativePath = node.relativePath ? `${node.relativePath}/${part}` : part;
      let child = node.folders.get(part);
      if (!child) {
        child = createNode(part, relativePath);
        node.folders.set(part, child);
      }
      node = child;
    }
    node.files.push({ file, name: filename });
  }

  const summarize = (node: FolderTreeNode): void => {
    node.fileCount = node.files.length;
    node.selectedCount = 0;
    node.sizeBytes = 0;
    node.unknownSizeCount = 0;
    for (const { file } of node.files) {
      if (selectedFileIds.has(file.id)) node.selectedCount++;
      if (file.sizeUnknown) node.unknownSizeCount++;
      else node.sizeBytes += file.sizeBytes;
    }
    for (const child of node.folders.values()) {
      summarize(child);
      node.fileCount += child.fileCount;
      node.selectedCount += child.selectedCount;
      node.sizeBytes += child.sizeBytes;
      node.unknownSizeCount += child.unknownSizeCount;
    }
  };
  summarize(root);

  const rows: FolderDisplayRow[] = [];
  const append = (node: FolderTreeNode, depth: number): void => {
    const folders = Array.from(node.folders.values()).sort((a, b) => naturalName.compare(a.name, b.name));
    const files = [...node.files].sort((a, b) => naturalName.compare(a.name, b.name));
    for (const folder of folders) {
      rows.push({
        kind: 'folder', key: `folder:${folder.relativePath}`, name: folder.name,
        relativePath: folder.relativePath, depth, node: folder,
      });
      append(folder, depth + 1);
    }
    for (const entry of files) {
      rows.push(fileRow(entry.file, depth, entry.name, true));
    }
  };
  append(root, 0);
  return rows;
}
