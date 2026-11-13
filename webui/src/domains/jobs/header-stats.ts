import type { Schema } from '../../api/types';
import { formatCount, formatCountWithNoun } from '../../app/format';
import { extractSourceLabel } from './presentation';
import { formatBytes } from '../presentation/items';

export function jobHeaderStats(payload: Schema<'JobPayloadDto'> | null | undefined): string[] {
  switch (payload?.kind) {
    case 'song': return Number(payload.candidateCount) > 0 ? [formatCountWithNoun(payload.candidateCount, 'candidate')] : [];
    case 'album': return [
      ...(Number(payload.resultCount) > 0 ? [formatCountWithNoun(payload.resultCount, 'folder')] : []),
      ...(Number(payload.directory.fileCount) > 0 ? directoryStats(payload.directory) : []),
    ];
    case 'song-groups': return [
      formatCountWithNoun(payload.songCount, 'song'),
      `${formatCount(payload.completedSongCount)} complete`,
      ...(Number(payload.skippedSongCount) > 0 ? [`${formatCount(payload.skippedSongCount)} skipped`] : []),
      ...(Number(payload.failedSongCount) > 0 ? [`${formatCount(payload.failedSongCount)} failed`] : []),
    ];
    case 'album-groups': return [
      formatCountWithNoun(payload.resultCount, 'album'),
      `${formatCount(payload.completedAlbumCount)} complete`,
      ...(Number(payload.skippedAlbumCount) > 0 ? [`${formatCount(payload.skippedAlbumCount)} skipped`] : []),
      ...(Number(payload.failedAlbumCount) > 0 ? [`${formatCount(payload.failedAlbumCount)} failed`] : []),
    ];
    case 'extract': return payload.inputType ? [extractSourceLabel(payload.inputType)] : [];
    case 'job-list': return [
      formatCountWithNoun(payload.count, 'job'),
      `${formatCount(payload.completedJobCount)} complete`,
      ...(Number(payload.skippedJobCount) > 0 ? [`${formatCount(payload.skippedJobCount)} skipped`] : []),
      ...(Number(payload.failedJobCount) > 0 ? [`${formatCount(payload.failedJobCount)} failed`] : []),
    ];
    case 'remote-file': {
      const size = payload.file.fileSize ?? payload.target.size;
      return size == null ? [] : [formatBytes(Number(size))];
    }
    case 'remote-directory': return payload.directory.attemptNumber == null && Number(payload.directory.fileCount) === 0
      ? [] : directoryStats(payload.directory);
    case 'retrieve-folder': return [formatCountWithNoun(payload.newFilesFoundCount, 'new file')];
    default: return [];
  }
}

function directoryStats(directory: Schema<'DirectoryDownloadStateDto'>): string[] {
  return [
    formatCountWithNoun(directory.fileCount, 'file'),
    ...(['downloaded', 'skipped', 'reused', 'failed'] as const).flatMap(outcome => {
      const count = directory[`${outcome}FileCount`];
      return Number(count) > 0 ? [`${formatCount(count)} ${outcome}`] : [];
    }),
  ];
}
