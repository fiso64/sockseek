import { describe, expect, it } from 'vitest';
import type { JobSummaryDto } from '../src/api/types';
import { canReplaySearch, canReplaySearchRoot } from '../src/domains/search/replay';

describe('single-search replay eligibility', () => {
  const root = {
    jobId: 'search-job', submissionId: 'submission', parentJobId: null,
    role: 'user-root', kind: 'search',
  } as JobSummaryDto;

  it('allows only a directly submitted search root', () => {
    expect(canReplaySearchRoot(root)).toBe(true);
    expect(canReplaySearchRoot({ ...root, parentJobId: 'import', role: 'execution-child' })).toBe(false);
    expect(canReplaySearchRoot({ ...root, submissionId: null })).toBe(false);
    expect(canReplaySearchRoot({ ...root, kind: 'extract' })).toBe(false);
  });

  it('also allows a retained search child without allowing unrelated jobs', () => {
    expect(canReplaySearch({ ...root, parentJobId: 'import', role: 'execution-child' })).toBe(true);
    expect(canReplaySearch({ ...root, submissionId: null })).toBe(false);
    expect(canReplaySearch({ ...root, kind: 'extract' })).toBe(false);
  });
});
