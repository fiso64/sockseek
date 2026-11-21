import { jobSummary } from './job-fixture';
import { expect, it } from 'vitest';
import { canDeleteSubmission, requireDeleted } from '../src/domains/jobs/deletion';

it('only offers whole-submission deletion on identified terminal user roots', () => {
  const root = jobSummary({ jobId: 'root', display: { submissionId: 'submission', role: 'user-root' }, lifecycle: { lifecycleState: 'Terminal' } });
  expect(canDeleteSubmission(root)).toBe(true);
  expect(canDeleteSubmission(jobSummary({ relationships: { parentJobId: 'parent' } }, root))).toBe(false);
  expect(canDeleteSubmission(jobSummary({ display: { role: 'semantic-result' } }, root))).toBe(false);
  expect(canDeleteSubmission(jobSummary({ lifecycle: { lifecycleState: 'Running' } }, root))).toBe(false);
  expect(canDeleteSubmission(jobSummary({ display: { submissionId: null } }, root))).toBe(false);
});

it('does not treat a successful HTTP response with a rejected deletion as success', () => {
  expect(() => requireDeleted({ submissionId: 'submission', affectedSubmissionCount: 0, affectedJobCount: 0, rejectedSubmissionCount: 1, rejectionReasons: [{ reason: 'nonterminal-jobs', count: 1 }] })).toThrow('nonterminal-jobs');
  expect(() => requireDeleted({ submissionId: 'submission', affectedSubmissionCount: 1, affectedJobCount: 3, rejectedSubmissionCount: 0, rejectionReasons: [] })).not.toThrow();
});
