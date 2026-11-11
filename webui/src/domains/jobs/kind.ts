import type { JobSummaryDto, Schema } from '../../api/types';
import { humanizeStateValue } from '../../app/format';
import type { AppIconName } from '../../app/icons';
import type { SearchResultMode } from '../search/model';

export function searchJobMode(job: JobSummaryDto): SearchResultMode | null {
  if (job.kind !== 'search' || job.searchDefaultProjection == null || job.searchGroupResults == null) return null;
  switch (job.searchDefaultProjection) {
    case 'GenericFile': return 'generic';
    case 'Track': return job.searchGroupResults ? 'song-groups' : 'track';
    case 'Album': return job.searchGroupResults ? 'album-groups' : 'album';
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

export function jobKindIcon(kind: Schema<'ServerJobKind'> | undefined): AppIconName {
  switch (kind) {
    case 'search': return 'search';
    case 'song': return 'track';
    case 'album': return 'album';
    case 'song-groups': return 'group-job';
    case 'album-groups': return 'album-groups-job';
    case 'extract': return 'extract';
    case 'job-list': return 'job-list';
    case 'remote-file': return 'file';
    case 'remote-directory': return 'folder';
    case 'retrieve-folder': return 'retrieve-folder';
    default: return 'jobs';
  }
}
