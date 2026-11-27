import type { JobSummaryDto, Schema } from '../../api/types';
import { humanizeStateValue } from '../../app/format';
import type { AppIconName } from '../../app/icons';
import { searchModeLabel, type SearchResultMode } from '../search/model';

export function searchJobMode(job: JobSummaryDto): SearchResultMode | null {
  if (job.display.kind !== 'search' || job.display.searchDefaultProjection == null || job.display.searchGroupResults == null) return null;
  switch (job.display.searchDefaultProjection) {
    case 'GenericFile': return 'generic';
    case 'Track': return job.display.searchGroupResults ? 'song-groups' : 'track';
    case 'Album': return job.display.searchGroupResults ? 'album-groups' : 'album';
  }
}

export function jobKindLabel(kind: Schema<'ServerJobKind'> | null | undefined): string {
  switch (kind) {
    case 'extract': return 'Import';
    case 'job-list': return 'Job List';
    case 'remote-file': return 'Remote File';
    case 'remote-directory': return 'Remote Directory';
    case 'retrieve-folder': return 'Retrieve Folder';
    case 'song-groups': return 'Song Groups';
    case 'album-groups': return 'Album Groups';
    default: return humanizeStateValue(kind ?? 'Job');
  }
}

/** The name a job is shown by, when it has one. */
export function jobName(job: JobSummaryDto | null | undefined): string | null {
  return job?.display.itemName ?? job?.display.queryText ?? null;
}

/**
 * What a job is, as shown under its name. An import or a batch describes itself;
 * a song or album picked by hand adds the peer it came from.
 */
export function jobTypeLine(job: JobSummaryDto): string {
  const { kind, sourceDetail } = job.display;
  if (kind === 'extract') return sourceDetail ?? 'Import';
  const searchMode = searchJobMode(job);
  if (searchMode) return searchModeLabel(searchMode);
  const label = jobKindLabel(kind);
  if (kind === 'job-list') return sourceDetail ?? label;
  return (kind === 'song' || kind === 'album') && sourceDetail ? `${label} · ${sourceDetail}` : label;
}

export function jobKindIcon(kind: Schema<'ServerJobKind'> | undefined): AppIconName {
  switch (kind) {
    case 'search': return 'search';
    case 'song': return 'track';
    case 'album': return 'album';
    case 'song-groups': return 'group-job';
    case 'album-groups': return 'album-groups-job';
    case 'extract':
    case 'job-list': return 'job-list';
    case 'remote-file': return 'file';
    case 'remote-directory': return 'folder';
    case 'retrieve-folder': return 'retrieve-folder';
    default: return 'jobs';
  }
}
