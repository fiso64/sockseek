import type { JobSummaryDto } from '../../api/types';

export interface SearchJobStat {
  count: number | string;
  label: string;
}

/** Job lists show collected facts; projection/group counts belong to the results view. */
export function searchJobStats(job: JobSummaryDto): SearchJobStat[] {
  const files = job.discoveryPublicFileCount ?? job.discoveryRawResultCount;
  const peers = job.discoveryObservedPeerCount;
  const counts = [
    { count: files, label: files === 1 || files === '1' ? 'file' : 'files' },
    { count: job.discoveryLockedFileCount, label: 'locked' },
    { count: peers, label: peers === 1 || peers === '1' ? 'peer' : 'peers' },
  ];
  return counts.filter((item): item is { count: number | string; label: string } => item.count != null);
}
