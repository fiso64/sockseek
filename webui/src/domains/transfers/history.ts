import type { TransferHistoryDto, TransferStateDto, Schema } from '../../api/types';
import { wireInteger } from '../../api/types';

export function transferGroupKeyId(key: Schema<'TransferGroupKeyDto'>): string {
  return JSON.stringify([key.direction, key.username ?? null, key.groupRef]);
}

/** Merge changed rows without replacing the loaded timeline or regressing revisions. */
export function mergeTransferHistory(
  previous: readonly TransferHistoryDto[],
  updates: readonly TransferHistoryDto[],
  removedIds: readonly string[] = [],
  windowIds?: readonly string[] | null,
): TransferHistoryDto[] {
  const rows = new Map(previous.map(row => [row.transferId, row]));
  for (const id of removedIds) rows.delete(id);
  for (const row of updates) {
    const old = rows.get(row.transferId);
    if (!old || wireInteger(row.revision) >= wireInteger(old.revision)) rows.set(row.transferId, row);
  }
  // Membership comes from the daemon's requested extent, including after a
  // concurrent HTTP read. This applies its result; it adds no client page size.
  const members = windowIds ? new Set(windowIds) : null;
  return [...rows.values()].filter(row => !members || members.has(row.transferId))
    .sort((left, right) => comparePosition(right, left));
}

/** Exact visible identities, shared by pagination admission and summary reads. */
export function transferGroupKeys(
  direction: 'download' | 'upload',
  history: readonly TransferHistoryDto[],
  live: readonly TransferStateDto[],
): Schema<'TransferGroupKeyDto'>[] {
  const keys = new Map<string, Schema<'TransferGroupKeyDto'>>();
  const wireDirection = direction === 'download' ? 'Download' : 'Upload';
  const add = (username: string | null | undefined, groupRef: string | null | undefined): void => {
    if (!groupRef) return;
    const key: Schema<'TransferGroupKeyDto'> = { direction: wireDirection, username: username ?? null, groupRef };
    keys.set(transferGroupKeyId(key), key);
  };
  for (const row of history) if (row.direction.toLowerCase() === direction) add(row.username, row.groupRef);
  for (const row of live) if (row.identity.direction.toLowerCase() === direction)
    add(row.identity.username, row.identity.groupRef);
  return [...keys.values()];
}

/** Track the loaded group's membership, not its accidental share of a global page. */
export function transferGroupMemberScope(
  summary: Schema<'TransferGroupSummaryDto'>,
  history: readonly TransferHistoryDto[],
  cached: readonly TransferHistoryDto[] = [],
  globalBoundary: TransferHistoryDto | null = history.at(-1) ?? null,
): string {
  const direction = summary.key.direction.toLowerCase();
  const inGroup = (row: TransferHistoryDto): boolean => row.direction.toLowerCase() === direction
    && (row.username ?? null) === (summary.key.username ?? null) && row.groupRef === summary.key.groupRef;
  const cachedGroup = cached.filter(inGroup);
  const oldestCached = cachedGroup.reduce<TransferHistoryDto | undefined>((oldest, row) =>
    !oldest || comparePosition(row, oldest) < 0 ? row : oldest, undefined);
  // Revealing older global rows does not change the cached folder prefix.
  // Replacements/removals inside that prefix must still invalidate it, even
  // when their aggregate byte/status counts happen to remain identical.
  const ids = new Set(history.filter(row => inGroup(row)
    && (!oldestCached || comparePosition(row, oldestCached) >= 0)).map(row => row.transferId));
  const boundary = globalBoundary;
  if (boundary) {
    for (const row of cachedGroup) {
      // Only rows below the fresh keyset boundary may be absent harmlessly.
      // A missing newer row was removed, not displaced by an unrelated arrival.
      if (comparePosition(row, boundary) <= 0) ids.add(row.transferId);
    }
  }
  return JSON.stringify([
    [...ids].sort(), summary.fileCount, summary.knownTotalBytes,
    summary.unknownTotalCount, summary.queuedCount, summary.inProgressCount,
    summary.succeededCount, summary.failedCount, summary.cancelledCount, summary.interruptedCount,
  ]);
}

function comparePosition(left: TransferHistoryDto, right: TransferHistoryDto): number {
  return compareTransferPosition(left.createdAtUtc, left.transferId, right.createdAtUtc, right.transferId);
}

export function compareTransferPosition(
  leftTime: string | null | undefined, leftTransferId: string,
  rightTime: string | null | undefined, rightTransferId: string,
): number {
  const byTime = (Date.parse(leftTime ?? '') || 0) - (Date.parse(rightTime ?? '') || 0);
  if (byTime) return byTime;
  const leftId = leftTransferId.toLowerCase(), rightId = rightTransferId.toLowerCase();
  return leftId < rightId ? -1 : leftId > rightId ? 1 : 0;
}

/** Both cursors use the same moving keyset; skip the farther fetched prefix. */
export function transferGroupCursor(
  cached: readonly TransferHistoryDto[],
  memberCursor: string | null,
  globalBoundary: TransferHistoryDto | null,
  globalCursor: string | null,
): string | null {
  const oldest = cached.reduce<TransferHistoryDto | null>((oldest, row) =>
    !oldest || comparePosition(row, oldest) < 0 ? row : oldest, null);
  return globalCursor && globalBoundary && (!oldest || comparePosition(globalBoundary, oldest) < 0)
    ? globalCursor : memberCursor ?? globalCursor;
}

