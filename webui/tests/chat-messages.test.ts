import { expect, test } from 'vitest';
import type { ChatMessageDto } from '../src/api/types';
import { mergeMessages, snapshotNeedsHistory } from '../src/domains/chat/messages';

function message(messageId: string, sequence: string, state: ChatMessageDto['state'] = 'Sent'): ChatMessageDto {
  return {
    messageId, sequence, state, targetKind: 'Direct', targetId: 'conversation',
    sender: 'Peer', direction: 'Outgoing', text: messageId,
    occurredAtUtc: '2026-09-10T00:00:00Z', recordedAtUtc: '2026-09-10T00:00:00Z', failureReason: null,
  };
}

test('merges history and live updates without rounding sequence numbers or duplicating messages', () => {
  const earlier = message('earlier', '9007199254740992');
  const later = message('later', '9007199254740993', 'Failed');
  const updated = { ...later, state: 'Sent' as const };
  expect(mergeMessages([later], [earlier, updated])).toEqual([earlier, updated]);
});

test('identifies a bounded snapshot that begins beyond paged history', () => {
  const boundary = message('known', '9007199254740992');
  expect(snapshotNeedsHistory(boundary, [message('later', '9007199254740994')])).toBe(true);
  expect(snapshotNeedsHistory(boundary, [boundary, message('later', '9007199254740994')])).toBe(false);
  expect(snapshotNeedsHistory(undefined, [message('first', '1')])).toBe(true);
  expect(snapshotNeedsHistory(boundary, [])).toBe(false);
});
