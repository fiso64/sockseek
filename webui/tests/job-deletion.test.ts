import { expect, it } from 'vitest';
import { canDeleteSubmission, requireDeleted } from '../src/domains/jobs/deletion';

it('only offers whole-submission deletion on identified terminal user roots', () => {
  const root = { jobId: 'root', submissionId: 'submission', role: 'user-root', lifecycleState: 'Terminal' } as const;
  expect(canDeleteSubmission(root)).toBe(true);
  expect(canDeleteSubmission({ ...root, parentJobId: 'parent' })).toBe(false);
  expect(canDeleteSubmission({ ...root, role: 'semantic-result' })).toBe(false);
  expect(canDeleteSubmission({ ...root, lifecycleState: 'Running' })).toBe(false);
  expect(canDeleteSubmission({ ...root, submissionId: null })).toBe(false);
});

it('does not treat a successful HTTP response with a rejected deletion as success', () => {
  expect(() => requireDeleted({ submissionId: 'submission', affectedSubmissionCount: 0, affectedJobCount: 0, rejectedSubmissionCount: 1, rejectionReasons: [{ reason: 'nonterminal-jobs', count: 1 }] })).toThrow('nonterminal-jobs');
  expect(() => requireDeleted({ submissionId: 'submission', affectedSubmissionCount: 1, affectedJobCount: 3, rejectedSubmissionCount: 0, rejectionReasons: [] })).not.toThrow();
});
