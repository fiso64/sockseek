import { wireInteger, type ChatMessageDto } from '../../api/types';

/** Later observations replace the same message, including send-state changes. */
export function mergeMessages(left: ChatMessageDto[], right: ChatMessageDto[]): ChatMessageDto[] {
  const values = new Map(left.map((item) => [item.messageId, item]));
  for (const item of right) values.set(item.messageId, item);
  return [...values.values()].sort((a, b) => {
    const first = wireInteger(a.sequence);
    const second = wireInteger(b.sequence);
    return first < second ? -1 : first > second ? 1 : 0;
  });
}

/** A bounded live snapshot cannot prove continuity beyond this paged boundary. */
export function snapshotNeedsHistory(boundary: ChatMessageDto | undefined, tail: ChatMessageDto[]): boolean {
  if (!tail.length) return false;
  if (!boundary) return true;
  return wireInteger(tail[0]!.sequence) > wireInteger(boundary.sequence);
}
