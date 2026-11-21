import type { JobSummaryDto } from '../src/api/types';

type JobFields = { [K in keyof JobSummaryDto]?: K extends 'display' | 'lifecycle' | 'discovery' | 'relationships'
  ? Partial<JobSummaryDto[K]> : JobSummaryDto[K] };

const base: JobSummaryDto = {
  jobId: 'job', revision: 1,
  display: { displayId: 1, workflowId: 'workflow', kind: 'generic', appliedProfiles: [], printOption: 0, role: 'user-root' },
  lifecycle: { lifecycleState: 'Pending', activityPhase: 'None', terminalOutcome: 'None', skipReason: 'None',
    hasFailureDetail: false, cancellationSource: 'None', availableActions: [] },
  discovery: {}, relationships: {},
};

export function jobSummary(fields: JobFields = {}, previous = base): JobSummaryDto {
  return { ...previous, ...fields, display: { ...previous.display, ...fields.display },
    lifecycle: { ...previous.lifecycle, ...fields.lifecycle }, discovery: { ...previous.discovery, ...fields.discovery },
    relationships: { ...previous.relationships, ...fields.relationships } };
}
