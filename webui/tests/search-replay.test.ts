import { jobSummary } from './job-fixture';
import { describe, expect, it } from 'vitest';
import type { JobSummaryDto } from '../src/api/types';
import { canReplaySearch, canReplaySearchRoot } from '../src/domains/search/replay';

describe('single-search replay eligibility', () => {
  const root = jobSummary({ jobId: 'search-job', display: { submissionId: 'submission', role: 'user-root', kind: 'search' }, relationships: { parentJobId: null } });

  it('allows only a directly submitted search root', () => {
    expect(canReplaySearchRoot(root)).toBe(true);
    expect(canReplaySearchRoot(jobSummary({ relationships: { parentJobId: 'import' }, display: { role: 'execution-child' } }, root))).toBe(false);
    expect(canReplaySearchRoot(jobSummary({ display: { submissionId: null } }, root))).toBe(false);
    expect(canReplaySearchRoot(jobSummary({ display: { kind: 'extract' } }, root))).toBe(false);
  });

  it('also allows a retained search child without allowing unrelated jobs', () => {
    expect(canReplaySearch(jobSummary({ relationships: { parentJobId: 'import' }, display: { role: 'execution-child' } }, root))).toBe(true);
    expect(canReplaySearch(jobSummary({ display: { submissionId: null } }, root))).toBe(false);
    expect(canReplaySearch(jobSummary({ display: { kind: 'extract' } }, root))).toBe(false);
  });
});
