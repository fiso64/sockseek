import type { JobSummaryDto } from '../../api/types';
import { searchJobMode } from './kind';

export interface SearchJobStat {
  count: number | string | null;
  label: string;
}

/** Show the missing default aggregate count explicitly; never infer it from a loaded result page. */
export function searchJobStats(job: JobSummaryDto): SearchJobStat[] {
  const mode = searchJobMode(job);
  const aggregate = mode === 'song-aggregate' || mode === 'album-aggregate';
  const files = job.discoveryPublicFileCount ?? job.discoveryRawResultCount;
  const peers = job.discoveryObservedPeerCount;
  const counts = [
    { count: files, label: files === 1 || files === '1' ? 'file' : 'files' },
    { count: job.discoveryLockedFileCount, label: 'locked' },
    { count: peers, label: peers === 1 || peers === '1' ? 'peer' : 'peers' },
  ];
  return [
    ...(aggregate ? [{ count: null, label: 'groups' }] : []),
    ...counts.filter((item): item is { count: number | string; label: string } => item.count != null),
  ];
}
