import { wireInteger, type TransferStateDto, type Schema, type WireInteger } from '../../api/types';
import type { AudioAttributes, FolderItemFile, TransferPresentation } from '../presentation/items';
import { audioAttributes as audio } from '../presentation/items';
import { formatBytes } from '../../app/format';
import { compareTransferPosition } from './history';

export interface TransferTimelineFileEntry {
  kind: 'file';
  id: string;
  peer: string;
  username: string | null;
  path: string;
  sizeBytes: number | null;
  audio?: AudioAttributes;
  sourceTransferIds: string[];
  transfer: TransferPresentation;
}

export interface TransferTimelineFolderEntry {
  kind: 'folder';
  id: string;
  peer: string;
  username: string | null;
  groupRef: string;
  path: string;
  sizeBytes: number | null;
  files: FolderItemFile[];
  totalFileCount: number;
  filesComplete: boolean;
  summaryAvailable: boolean;
  sourceTransferIds: string[];
  transfer: TransferPresentation;
}

export type TransferTimelineEntry = TransferTimelineFileEntry | TransferTimelineFolderEntry;

export interface TransferTimelinePeerGroup {
  key: string;
  peer: string;
  username: string | null;
  transferCount: number;
  items: TransferTimelineEntry[];
}

interface UnifiedTransfer {
  id: string;
  revision: WireInteger;
  peer: string;
  username: string | null;
  path: string;
  groupRef: string | null;
  groupDisplayPath: string | null;
  groupRelativePath: string | null;
  sizeBytes: number | null;
  audio?: AudioAttributes;
  transfer: TransferPresentation;
  createdAt: string | null;
  totalBytes: number | null;
  bytesTransferred: number | null;
}

/** Membership/order is determined by fetched timeline rows, never byte progress. */
export interface TransferEntryLayout {
  id: string;
  kind: 'file' | 'folder';
  rows: readonly UnifiedTransfer[];
  summary?: Schema<'TransferGroupSummaryDto'>;
  direction: 'download' | 'upload';
}

export interface TransferPeerLayout extends Omit<TransferTimelinePeerGroup, 'items'> {
  items: TransferEntryLayout[];
}

/** Page-owned projection cache. Fetched membership owns ordering; immutable row
 * projections are shared by the layout, cards and actions. Progress reads only
 * the IDs of its card, so unrelated cards are not reactive dependencies. */
export class TransferProjection {
  private orderKeys = new Map<string, string | null>();
  private orderedIds: string[] = [];
  private layouts = new Map<string, TransferEntryLayout>();

  keepLayouts(entries: TransferEntryLayout[]): TransferEntryLayout[] {
    const next = new Map<string, TransferEntryLayout>();
    for (let index = 0; index < entries.length; index++) {
      const entry = entries[index]!;
      const previous = this.layouts.get(entry.id);
      // An unrelated arrival must not recreate every card's row subscriptions.
      // Drop departed entries with the page; this cache has no separate lifetime.
      const retained = previous && previous.kind === entry.kind && previous.direction === entry.direction
        && sameGroupSummary(previous.summary, entry.summary) && previous.rows.length === entry.rows.length
        && entry.rows.every((row, index) => row === previous.rows[index]) ? previous : entry;
      entries[index] = retained;
      next.set(entry.id, retained);
    }
    this.layouts = next;
    return entries;
  }

  order(rows: UnifiedTransfer[]): UnifiedTransfer[] {
    const unchanged = rows.length === this.orderKeys.size
      && rows.every(row => this.orderKeys.has(row.id) && this.orderKeys.get(row.id) === row.createdAt);
    if (unchanged) {
      const byId = new Map(rows.map(row => [row.id, row]));
      return this.orderedIds.map(id => byId.get(id)!);
    }
    rows.sort((left, right) => compareTransferPosition(right.createdAt, right.id, left.createdAt, left.id));
    this.orderKeys = new Map(rows.map(row => [row.id, row.createdAt]));
    this.orderedIds = rows.map(row => row.id);
    return rows;
  }

  private readonly rows = new WeakMap<TransferStateDto, UnifiedTransfer>();
  private readonly folderFiles = new WeakMap<UnifiedTransfer, { path: string; file: FolderItemFile }>();
  private readonly fileEntries = new WeakMap<UnifiedTransfer, TransferTimelineFileEntry>();
  private readonly groups = new WeakMap<Schema<'TransferGroupSummaryDto'>, {
    rows: UnifiedTransfer[]; entry: TransferTimelineFolderEntry;
  }>();

  group(summary: Schema<'TransferGroupSummaryDto'> | undefined, rows: UnifiedTransfer[]): TransferTimelineFolderEntry | undefined {
    const previous = summary && this.groups.get(summary);
    return previous && previous.rows.length === rows.length && rows.every((row, index) => row === previous.rows[index])
      ? previous.entry : undefined;
  }

  rememberGroup(summary: Schema<'TransferGroupSummaryDto'> | undefined, rows: UnifiedTransfer[], entry: TransferTimelineFolderEntry): void {
    if (summary) this.groups.set(summary, { rows, entry });
  }

  row(value: TransferStateDto): UnifiedTransfer {
    let row = this.rows.get(value);
    if (!row) { row = fromTransfer(value); this.rows.set(value, row); }
    return row;
  }

  file(row: UnifiedTransfer): TransferTimelineFileEntry {
    let entry = this.fileEntries.get(row);
    if (!entry) {
      entry = { kind: 'file', id: row.id, peer: row.peer, username: row.username, path: row.path,
        sizeBytes: row.sizeBytes, audio: row.audio, sourceTransferIds: [row.id], transfer: row.transfer };
      this.fileEntries.set(row, entry);
    }
    return entry;
  }

  folderFile(row: UnifiedTransfer, path: string): FolderItemFile {
    const previous = this.folderFiles.get(row);
    if (previous?.path === path) return previous.file;
    const file = { id: row.id, relativePath: row.groupRelativePath ?? relativePath(path, row.path), sizeBytes: row.sizeBytes ?? 0,
      sizeUnknown: row.sizeBytes == null, audio: row.audio, transfer: row.transfer };
    this.folderFiles.set(row, { path, file });
    return file;
  }
}

function sameGroupSummary(left: Schema<'TransferGroupSummaryDto'> | undefined,
  right: Schema<'TransferGroupSummaryDto'> | undefined): boolean {
  if (left === right) return true;
  if (!left || !right) return false;
  // The layout ID already includes the exact group identity. Summary refreshes
  // return new DTOs even for unchanged groups; compare their scalar facts.
  const keys = Object.keys(right) as (keyof typeof right)[];
  return Object.keys(left).length === keys.length && keys.every(key => key === 'key' || left[key] === right[key]);
}

export function arrangeTransferGroups(
  items: readonly TransferStateDto[],
  direction: 'download' | 'upload',
  summaries: readonly Schema<'TransferGroupSummaryDto'>[],
  projection: TransferProjection,
): TransferPeerLayout[] {
  return arrangeRows(items.filter(row => row.identity.direction.toLowerCase() === direction)
    .map(row => projection.row(row)), direction, summaries, projection);
}

function arrangeRows(
  input: UnifiedTransfer[], direction: 'download' | 'upload',
  summaries: readonly Schema<'TransferGroupSummaryDto'>[], projection: TransferProjection,
): TransferPeerLayout[] {
  const rows = projection.order(input);
  const summaryByKey = new Map(summaries
    .map(summary => [groupKey(summary.key.direction, summary.key.username, summary.key.groupRef), summary]));
  const groupedRows = new Map<string, UnifiedTransfer[]>();
  for (const row of rows) {
    if (!row.groupRef) continue;
    const key = groupKey(direction, row.username, row.groupRef);
    const members = groupedRows.get(key) ?? [];
    members.push(row);
    groupedRows.set(key, members);
  }
  const renderedGroups = new Set<string>();
  const entries: TransferEntryLayout[] = [];
  // Group identity is authoritative even while optional totals are unavailable.
  for (const row of rows) {
    if (!row.groupRef) {
      entries.push({ kind: 'file', id: row.id, rows: [row], direction });
      continue;
    }

    const key = groupKey(direction, row.username, row.groupRef);
    if (renderedGroups.has(key)) continue;
    const run = groupedRows.get(key)!;
    renderedGroups.add(key);
    const summary = summaryByKey.get(key);
    // Whole-group totals, rather than the fetched page, prove a one-file upload.
    const singleUpload = direction === 'upload' && summary && Number(summary.fileCount) === 1 && run.length === 1;
    entries.push({
      kind: singleUpload ? 'file' : 'folder',
      id: singleUpload ? row.id : key,
      rows: run, summary, direction,
    });
  }

  const groups: TransferPeerLayout[] = [];
  const peerOccurrences = new Map<string | null, number>();
  for (const entry of projection.keepLayouts(entries)) {
    const row = entry.rows[0]!;
    const count = entry.kind === 'folder' ? Number(entry.summary?.fileCount ?? entry.rows.length) : 1;
    const previous = groups.at(-1);
    if (previous?.username === row.username) {
      previous.items.push(entry);
      previous.transferCount += count;
    } else {
      // A new head card or an unrelated peer must not recreate this peer's
      // whole section (or reset its collapse state). Repeated chronological
      // runs still need distinct keys; exact usernames also distinguish null.
      const occurrence = peerOccurrences.get(row.username) ?? 0;
      peerOccurrences.set(row.username, occurrence + 1);
      groups.push({ key: JSON.stringify([direction, row.username, occurrence]), peer: row.peer,
        username: row.username, transferCount: count, items: [entry] });
    }
  }
  return groups;
}

function currentRow(row: UnifiedTransfer, projection: TransferProjection,
  readTransfer?: (id: string) => TransferStateDto | undefined): UnifiedTransfer {
  const current = readTransfer?.(row.id);
  return current && wireInteger(current.revision) >= wireInteger(row.revision)
    ? projection.row(current) : row;
}

/** Called inside a card's reactive scope. A transfer update touches this card's
 * members only; it cannot sort the timeline or regroup unrelated entries. */
export function presentTransferEntry(
  entry: TransferEntryLayout, projection: TransferProjection,
  readTransfer?: (id: string) => TransferStateDto | undefined,
): TransferTimelineEntry {
  const run = entry.rows.map(row => currentRow(row, projection, readTransfer));
  const row = run[0]!;
  if (entry.kind === 'file') return projection.file(row);
  const { summary, direction } = entry;
  const cached = projection.group(summary, run);
  if (cached) return cached;
  const folderPath = row.groupDisplayPath ?? parentPath(row.path);
  const totalFileCount = Number(summary?.fileCount ?? run.length);
  const result: TransferTimelineFolderEntry = {
    kind: 'folder', id: entry.id, peer: row.peer, username: row.username,
    groupRef: row.groupRef!, path: folderPath,
    sizeBytes: groupKnownSize(run, summary),
    files: run.map(item => projection.folderFile(item, folderPath)),
    totalFileCount, filesComplete: Boolean(summary) && run.length === totalFileCount,
    summaryAvailable: Boolean(summary), sourceTransferIds: run.map(item => item.id),
    transfer: summary ? combineGroupSummary(summary, direction, row.createdAt)
      : combineFolderPresentation(run, direction, row.createdAt),
  };
  projection.rememberGroup(summary, run, result);
  return result;
}

/** Bulk-action eligibility needs status, not file-card arrays or folder sorting. */
export function* transferEntryActions(
  entry: TransferEntryLayout, projection: TransferProjection,
  readTransfer: (id: string) => TransferStateDto | undefined,
): Generator<TransferPresentation> {
  if (entry.kind === 'folder') {
    if (entry.summary) yield combineGroupSummary(entry.summary, entry.direction, entry.rows[0]!.createdAt);
    else yield combineFolderPresentation(entry.rows.map(row => currentRow(row, projection, readTransfer)),
      entry.direction, entry.rows[0]!.createdAt);
  }
  for (const row of entry.rows) yield currentRow(row, projection, readTransfer).transfer;
}

export function isTerminalTransfer(transfer?: TransferPresentation): boolean {
  return transfer?.tone === 'complete' || transfer?.tone === 'failed' || transfer?.tone === 'cancelled';
}

export function isQueuedTransfer(transfer?: TransferPresentation): boolean {
  return transfer?.tone === 'queued';
}

function fromTransfer(value: TransferStateDto): UnifiedTransfer {
  const path = value.identity.remotePath ?? value.status.localPath ?? value.file?.name ?? 'Unknown file';
  const createdAt = value.createdAtUtc;
  return {
    id: value.transferId,
    revision: value.revision,
    peer: value.identity.username ?? 'Unknown peer',
    username: value.identity.username ?? null,
    path,
    groupRef: value.identity.groupRef ?? null,
    groupDisplayPath: value.identity.groupDisplayPath ?? null,
    groupRelativePath: value.identity.groupRelativePath ?? null,
    sizeBytes: knownNumber(value.file?.size) ?? knownNumber(value.progress.totalBytes),
    audio: audio(value.file),
    createdAt,
    totalBytes: knownNumber(value.progress.totalBytes) ?? knownNumber(value.file?.size),
    bytesTransferred: knownNumber(value.progress.bytesTransferred),
    transfer: liveTransferPresentation(value, value.scheduling?.requestedAtUtc),
  };
}

export function liveTransferPresentation(value: TransferStateDto, createdAt?: string, completedAt?: string | null): TransferPresentation {
  completedAt = value.status.completedAtUtc ?? completedAt;
  const total = number(value.progress.totalBytes);
  const transferred = number(value.progress.bytesTransferred);
  const speed = number(value.progress.bytesPerSecond);
  const transferTone = tone(value.status.isTerminal, value.status.terminalOutcome ?? 'None',
    value.status.state, transferred, value.scheduling?.startedAtUtc);
  const direction = value.identity.direction.toLowerCase() === 'upload' ? 'upload' : 'download';
  const percent = total > 0 ? clamp(transferred / total * 100) : undefined;
  return {
    state: transferTone === 'active' ? activeTransferState(direction)
      : transferState(value.status.state, value.status.terminalOutcome ?? 'None'),
    tone: transferTone,
    cancellable: value.status.availableActions?.some((action) => action.kind === 'cancel') ?? false,
    progressPercent: percent,
    progressText: direction === 'download' && transferTone === 'complete' && !completedAt
      ? 'Complete' : fileProgressText(direction, transferTone, transferred, percent),
    speed: transferTone === 'active' && speed > 0 ? `${formatBytes(speed)}/s` : undefined,
    eta: transferTone === 'active' ? remainingEta(total - transferred, speed) : undefined,
    detail: value.status.failureMessage ?? undefined,
    direction,
    createdAt,
    completedAt: direction === 'download' && transferTone === 'complete' ? completedAt ?? undefined : undefined,
  };
}

function activeTransferState(direction: 'download' | 'upload'): string {
  return direction === 'upload' ? 'Uploading' : 'Downloading';
}

function fileProgressText(
  direction: 'download' | 'upload',
  transferTone: TransferPresentation['tone'],
  transferred: number,
  percent: number | undefined,
): string | undefined {
  if (direction === 'upload') {
    if (transferTone === 'queued') return 'Queued';
    if (transferTone === 'cancelled') return 'Cancelled';
    return percent === undefined ? undefined : `${Math.round(percent)}%`;
  }
  if (transferTone === 'queued') return 'Waiting for peer';
  if (transferTone === 'cancelled') return 'Cancelled';
  if (transferTone === 'complete') return undefined;
  if (percent !== undefined) return `${formatBytes(transferred)} · ${Math.round(percent)}%`;
  return transferred > 0 ? formatBytes(transferred) : undefined;
}

function combineFolderPresentation(rows: UnifiedTransfer[], direction: 'download' | 'upload', createdAt: string | null): TransferPresentation {
  const values = rows.map((row) => row.transfer);
  const terminal = values.every(isTerminalTransfer);
  const failed = values.some((item) => item.tone === 'failed');
  const cancelled = !failed && values.some((item) => item.tone === 'cancelled');
  const active = values.some((item) => item.tone === 'active');
  const toneValue: TransferPresentation['tone'] = terminal
    ? failed ? 'failed' : cancelled ? 'cancelled' : 'complete'
    : active ? 'active' : 'queued';
  const totalsKnown = rows.every((row) => row.totalBytes !== null && row.bytesTransferred !== null);
  const totalBytes = rows.reduce((total, row) => total + (row.totalBytes ?? 0), 0);
  const transferred = rows.reduce((total, row) => total + (row.bytesTransferred ?? 0), 0);
  return {
    state: terminal ? failed ? 'Failed' : cancelled ? 'Cancelled' : 'Complete'
      : active ? direction === 'upload' ? 'Uploading' : 'Downloading' : 'Queued',
    tone: toneValue,
    cancellable: values.some((item) => item.cancellable),
    progressPercent: totalsKnown && totalBytes > 0 ? clamp(transferred / totalBytes * 100) : undefined,
    direction,
    createdAt: createdAt ?? undefined,
  };
}

function combineGroupSummary(
  summary: Schema<'TransferGroupSummaryDto'>,
  direction: 'download' | 'upload',
  createdAt: string | null,
): TransferPresentation {
  const queued = Number(summary.queuedCount);
  const active = Number(summary.inProgressCount);
  const failed = Number(summary.failedCount);
  const cancelled = Number(summary.cancelledCount) + Number(summary.interruptedCount);
  const succeeded = Number(summary.succeededCount);
  const toneValue: TransferPresentation['tone'] = active > 0 ? 'active'
    : queued > 0 ? 'queued'
    : failed > 0 ? 'failed'
    : cancelled > 0 ? 'cancelled'
    : 'complete';
  const totalBytes = Number(summary.knownTotalBytes);
  const transferred = Number(summary.transferredBytes);
  const speedBytes = Number(summary.speedBytesPerSecond);
  const totalsKnown = Number(summary.unknownTotalCount) === 0;
  const percent = totalsKnown && totalBytes > 0 ? clamp(transferred / totalBytes * 100) : undefined;
  const statusParts: string[] = [];
  if (succeeded && !failed && !cancelled) statusParts.push(
    direction === 'download' && toneValue === 'complete'
      ? `${succeeded} ${succeeded === 1 ? 'file' : 'files'} complete`
      : `${succeeded} of ${Number(summary.fileCount)} files complete`);
  else {
    if (succeeded) statusParts.push(`${succeeded} complete`);
    if (failed) statusParts.push(`${failed} failed`);
    if (cancelled) statusParts.push(`${cancelled} cancelled`);
  }
  if (!statusParts.length && percent !== undefined) statusParts.push(`${Math.round(percent)}%`);
  const eta = toneValue === 'active' && Number(summary.unknownActiveTotalCount) === 0
    ? remainingEta(Number(summary.knownRemainingActiveBytes), speedBytes) : undefined;
  return {
    state: toneValue === 'active' ? direction === 'upload' ? 'Uploading' : 'Downloading'
      : toneValue === 'complete' ? 'Complete'
      : toneValue === 'failed' ? 'Failed'
      : toneValue === 'cancelled' ? 'Cancelled' : 'Queued',
    tone: toneValue,
    cancellable: active + queued > 0,
    progressPercent: percent,
    progressText: statusParts.join(' · ') || formatBytes(transferred),
    speed: toneValue === 'active' && speedBytes > 0 ? `${formatBytes(speedBytes)}/s` : undefined,
    eta,
    direction,
    createdAt: createdAt ?? undefined,
  };
}

function remainingEta(remainingBytes: number, bytesPerSecond: number): string | undefined {
  if (!Number.isFinite(remainingBytes) || !Number.isFinite(bytesPerSecond)
      || remainingBytes <= 0 || bytesPerSecond <= 0) return undefined;
  const seconds = Math.ceil(remainingBytes / bytesPerSecond);
  return seconds < 60 ? `${seconds}s remaining`
    : `${Math.floor(seconds / 60)}m ${String(seconds % 60).padStart(2, '0')}s remaining`;
}

function groupKey(direction: string, username: string | null | undefined, groupRef: string): string {
  return JSON.stringify([direction.toLowerCase(), username ?? null, groupRef]);
}

function transferState(state: string, outcome: string): string {
  if (outcome && outcome.toLowerCase() !== 'none') return outcome === 'Succeeded' ? 'Complete' : outcome;
  return state.replace(/([a-z])([A-Z])/g, '$1 $2');
}

function tone(terminal: boolean, outcome: string, state: string, transferred: number, startedAt?: string | null): TransferPresentation['tone'] {
  if (terminal) {
    if (outcome.toLowerCase() === 'succeeded') return 'complete';
    if (outcome.toLowerCase() === 'cancelled' || outcome.toLowerCase() === 'interrupted') return 'cancelled';
    return 'failed';
  }
  // A resumed transfer can re-enter the peer queue with bytes and a previous
  // start timestamp. Its current owner state, not that history, decides tone.
  if (state.toLowerCase().startsWith('queued')) return 'queued';
  if (state.toLowerCase() === 'inprogress') return 'active';
  return transferred > 0 || startedAt ? 'active' : 'queued';
}

function number(value: number | string | null | undefined): number {
  const parsed = Number(value ?? 0);
  return Number.isFinite(parsed) ? parsed : 0;
}

function knownNumber(value: number | string | null | undefined): number | null {
  if (value == null) return null;
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : null;
}

function groupKnownSize(
  rows: UnifiedTransfer[],
  summary?: Schema<'TransferGroupSummaryDto'>,
): number | null {
  const bytes = summary ? Number(summary.knownTotalBytes)
    : rows.reduce((total, item) => total + (item.sizeBytes ?? 0), 0);
  const hasUnknown = summary ? Number(summary.unknownTotalCount) > 0
    : rows.some((item) => item.sizeBytes == null);
  return bytes === 0 && hasUnknown ? null : bytes;
}

function clamp(value: number): number { return Math.max(0, Math.min(100, value)); }

function parentPath(path: string): string {
  const normalized = path.replace(/\\/g, '/');
  const index = normalized.lastIndexOf('/');
  return index < 0 ? normalized : normalized.slice(0, index);
}

function relativePath(folder: string, path: string): string {
  const normalizedFolder = folder.replace(/\\/g, '/').replace(/\/$/, '');
  const normalizedPath = path.replace(/\\/g, '/');
  return normalizedPath.startsWith(`${normalizedFolder}/`)
    ? normalizedPath.slice(normalizedFolder.length + 1)
    : normalizedPath.split('/').at(-1) ?? normalizedPath;
}
