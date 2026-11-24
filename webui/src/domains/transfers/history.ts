import type { TransferStateDto, Schema } from '../../api/types';
import { wireInteger } from '../../api/types';

export function transferGroupKeyId(key: Schema<'TransferGroupKeyDto'>): string {
  return JSON.stringify([key.direction, key.username ?? null, key.groupRef]);
}

/** Merge changed rows without replacing the loaded timeline or regressing revisions. */
export function mergeTransferHistory(
  previous: readonly TransferStateDto[],
  updates: readonly TransferStateDto[],
  removedIds: readonly string[] = [],
  windowIds?: readonly string[] | null,
): TransferStateDto[] {
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
  history: readonly TransferStateDto[],
): Schema<'TransferGroupKeyDto'>[] {
  const keys = new Map<string, Schema<'TransferGroupKeyDto'>>();
  const wireDirection = direction === 'download' ? 'Download' : 'Upload';
  const add = (username: string | null | undefined, groupRef: string | null | undefined): void => {
    if (!groupRef) return;
    const key: Schema<'TransferGroupKeyDto'> = { direction: wireDirection, username: username ?? null, groupRef };
    keys.set(transferGroupKeyId(key), key);
  };
  for (const row of history) if (row.identity.direction.toLowerCase() === direction) add(row.identity.username, row.identity.groupRef);
  return [...keys.values()];
}

function comparePosition(left: TransferStateDto, right: TransferStateDto): number {
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
