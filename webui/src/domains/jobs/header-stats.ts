import type { Schema } from '../../api/types';
import { formatCount, formatCountWithNoun } from '../../app/format';
import { extractSourceLabel } from './presentation';
import { formatBytes } from '../presentation/items';

export function jobHeaderStats(payload: Schema<'JobPayloadDto'> | null | undefined): string[] {
  switch (payload?.kind) {
    case 'song': return Number(payload.candidateCount) > 0 ? [formatCountWithNoun(payload.candidateCount, 'candidate')] : [];
    case 'album': return [
      ...(Number(payload.resultCount) > 0 ? [`${formatCount(payload.resultCount)} folders`] : []),
      ...(Number(payload.directory.fileCount) > 0 ? [`${formatCount(payload.directory.fileCount)} files`] : []),
    ];
    case 'aggregate': return [
      `${formatCount(payload.songCount)} songs`,
      `${formatCount(payload.succeededSongCount)} complete`,
      ...(Number(payload.failedSongCount) > 0 ? [`${formatCount(payload.failedSongCount)} failed`] : []),
    ];
    case 'album-aggregate': return [
      `${formatCount(payload.resultCount)} albums`,
      `${formatCount(payload.succeededAlbumCount)} complete`,
      ...(Number(payload.failedAlbumCount) > 0 ? [`${formatCount(payload.failedAlbumCount)} failed`] : []),
    ];
    case 'extract': return payload.inputType ? [extractSourceLabel(payload.inputType)] : [];
    case 'job-list': return [
      `${formatCount(payload.count)} jobs`,
      `${formatCount(payload.succeededJobCount)} complete`,
      ...(Number(payload.failedJobCount) > 0 ? [`${formatCount(payload.failedJobCount)} failed`] : []),
    ];
    case 'remote-file': {
      const size = payload.file.fileSize ?? payload.target.size;
      return size == null ? [] : [formatBytes(Number(size))];
    }
    case 'remote-directory': return payload.directory.attemptNumber == null && Number(payload.directory.fileCount) === 0
      ? [] : [`${formatCount(payload.directory.fileCount)} files`];
    case 'retrieve-folder': return [`${formatCount(payload.newFilesFoundCount)} new files`];
    default: return [];
  }
}
