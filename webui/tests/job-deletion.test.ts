import { expect, it } from 'vitest';
import { canDeleteRoot, canDeleteSubmission, requireDeleted } from '../src/domains/jobs/deletion';

it('only offers whole-submission deletion on identified terminal user roots', () => {
  const root = { jobId: 'root', submissionId: 'submission', role: 'user-root', lifecycleState: 'Terminal' } as const;
  expect(canDeleteRoot(root)).toBe(true);
  expect(canDeleteRoot({ ...root, parentJobId: 'parent' })).toBe(false);
  expect(canDeleteRoot({ ...root, role: 'semantic-result' })).toBe(false);
  expect(canDeleteRoot({ ...root, lifecycleState: 'Running' })).toBe(false);
  expect(canDeleteRoot({ ...root, submissionId: null })).toBe(false);
});

it('allows whole-submission Remove from a terminal child but not unowned or running work', () => {
  const child = { jobId: 'child', parentJobId: 'root', submissionId: 'submission', role: 'execution-child', lifecycleState: 'Terminal' } as const;
  expect(canDeleteSubmission(child)).toBe(true);
  expect(canDeleteSubmission({ ...child, lifecycleState: 'Running' })).toBe(false);
  expect(canDeleteSubmission({ ...child, submissionId: null })).toBe(false);
});

it('does not treat a successful HTTP response with a rejected deletion as success', () => {
  expect(() => requireDeleted({ submissionId: 'submission', affectedSubmissionCount: 0, affectedJobCount: 0, rejectedSubmissionCount: 1, rejectionReasons: [{ reason: 'nonterminal-jobs', count: 1 }] })).toThrow('nonterminal-jobs');
  expect(() => requireDeleted({ submissionId: 'submission', affectedSubmissionCount: 1, affectedJobCount: 3, rejectedSubmissionCount: 0, rejectionReasons: [] })).not.toThrow();
});
