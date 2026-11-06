import { expect, it, vi } from 'vitest';
import { readPrecedesSnapshot, refreshVisibleExtent } from '../src/app/paging';

it('refreshes only the loaded job extent with fresh cursors and deduplicated identities', async () => {
  const readPosition = { epoch: 'same-daemon', sequence: '9007199254740992' };
  const fetch = vi.fn()
    .mockResolvedValueOnce({ items: [{ jobId: 'new' }, { jobId: 'a' }], nextCursor: 'fresh-1', readPosition })
    .mockResolvedValueOnce({ items: [{ jobId: 'a' }, { jobId: 'b' }], nextCursor: 'fresh-2' })
    .mockResolvedValueOnce({ items: [{ jobId: 'c' }], nextCursor: 'older',
      readPosition: { ...readPosition, sequence: '9007199254740995' } });
  const result = await refreshVisibleExtent(4, fetch, (job: { jobId: string }) => job.jobId);
  expect(result.items.map((job) => job.jobId)).toEqual(['new', 'a', 'b', 'c']);
  expect(result.nextCursor).toBe('older');
  expect(fetch.mock.calls).toEqual([[null], ['fresh-1'], ['fresh-2']]);
  expect(result.readPosition).toEqual(readPosition);
  expect(readPrecedesSnapshot(result.readPosition,
    { ...readPosition, sequence: '9007199254740993' })).toBe(true);
});

it('does not replace a loaded job extent with a partial successful refresh', async () => {
  const fetch = vi.fn()
    .mockResolvedValueOnce({ items: [{ jobId: 'new' }], nextCursor: 'fresh-1' })
    .mockRejectedValueOnce(new Error('History unavailable'));
  await expect(refreshVisibleExtent(2, fetch, (job: { jobId: string }) => job.jobId))
    .rejects.toThrow('History unavailable');
});
