import { expect, it } from 'vitest';
import { canArchiveRoot, canArchiveSubmission, requireArchived } from '../src/domains/jobs/archive';

it('only offers whole-submission archive on identified terminal user roots', () => {
  const root = { jobId: 'root', submissionId: 'submission', role: 'user-root', lifecycleState: 'Terminal' } as const;
  expect(canArchiveRoot(root)).toBe(true);
  expect(canArchiveRoot({ ...root, parentJobId: 'parent' })).toBe(false);
  expect(canArchiveRoot({ ...root, role: 'semantic-result' })).toBe(false);
  expect(canArchiveRoot({ ...root, lifecycleState: 'Running' })).toBe(false);
  expect(canArchiveRoot({ ...root, submissionId: null })).toBe(false);
});

it('allows whole-submission Remove from a terminal child but not unowned or running work', () => {
  const child = { jobId: 'child', parentJobId: 'root', submissionId: 'submission', role: 'execution-child', lifecycleState: 'Terminal' } as const;
  expect(canArchiveSubmission(child)).toBe(true);
  expect(canArchiveSubmission({ ...child, lifecycleState: 'Running' })).toBe(false);
  expect(canArchiveSubmission({ ...child, submissionId: null })).toBe(false);
});

it('does not treat a successful HTTP response with a rejected archive as success', () => {
  expect(() => requireArchived({ submissionId: 'submission', archived: true, affectedSubmissionCount: 0, affectedJobCount: 0, rejectedSubmissionCount: 1, rejectionReasons: [{ reason: 'nonterminal-jobs', count: 1 }] })).toThrow('nonterminal-jobs');
  expect(() => requireArchived({ submissionId: 'submission', archived: true, affectedSubmissionCount: 1, affectedJobCount: 3, rejectedSubmissionCount: 0, rejectionReasons: [] })).not.toThrow();
});
