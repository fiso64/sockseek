import type { CursorPage } from '../api/client';
import { wireInteger, type Schema } from '../api/types';

/** Reconcile only at a snapshot boundary, not on every live sequence change. */
export function readPrecedesSnapshot(
  read?: Schema<'StateStreamPositionDto'> | null,
  snapshot?: Schema<'StateStreamPositionDto'> | null,
): boolean {
  return Boolean(read && snapshot
    && (read.epoch !== snapshot.epoch || wireInteger(read.sequence) < wireInteger(snapshot.sequence)));
}

/** Replace only the loaded extent, starting with fresh moving-keyset cursors. */
export async function refreshVisibleExtent<T, Page extends CursorPage<T>>(
  visibleCount: number,
  fetchPage: (cursor: string | null) => Promise<Page>,
  identity: (item: T) => string,
): Promise<Page> {
  const rows = new Map<string, T>();
  let cursor: string | null = null;
  let readPosition: CursorPage<T>['readPosition'];
  do {
    const page = await fetchPage(cursor);
    // Later keyset pages cannot certify arrivals above the original head.
    if (cursor === null) readPosition = page.readPosition;
    for (const item of page.items) rows.set(identity(item), item);
    cursor = page.nextCursor ?? null;
    if (!cursor || rows.size >= visibleCount) return { ...page, items: [...rows.values()], readPosition };
  } while (cursor);
  throw new Error('History refresh ended without a page.');
}
