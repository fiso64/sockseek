import type { JobSummaryDto } from '../../api/types';
import { formatCount, formatCountWithNoun } from '../../app/format';
import { formatBytes } from '../presentation/items';

/** The list reads bounded daemon summary stats, never a collection of child details. */
export function automaticJobListStats(job: JobSummaryDto): string[] {
  const stats = job.stats;
  if (!stats) return [];
  const values: string[] = [];
  const memberCount = stats.memberCount == null ? null : Number(stats.memberCount);
  const hasProgress = memberCount != null && memberCount > 0 && stats.completedMemberCount != null;
  if (hasProgress)
    values.push(`${formatCount(stats.completedMemberCount)}/${formatCount(stats.memberCount)} complete`);

  switch (job.kind) {
    case 'song':
      if (Number(stats.candidateCount) > 0) values.push(formatCountWithNoun(stats.candidateCount, 'candidate'));
      break;
    case 'album':
      if (Number(stats.folderCount) > 0) values.push(`${formatCount(stats.folderCount)} folders`);
      if (!hasProgress && Number(stats.fileCount) > 0)
        values.push(`${formatCount(stats.fileCount)} files`);
      break;
    case 'song-groups':
      if (!values.length && memberCount != null) values.push(`${formatCount(stats.memberCount)} songs`);
      if (Number(stats.failedMemberCount) > 0) values.push(`${formatCount(stats.failedMemberCount)} failed`);
      break;
    case 'album-groups':
      if (!values.length && memberCount != null) values.push(`${formatCount(stats.memberCount)} albums`);
      if (Number(stats.failedMemberCount) > 0) values.push(`${formatCount(stats.failedMemberCount)} failed`);
      break;
    case 'job-list':
      if (!values.length && memberCount != null) values.push(`${formatCount(stats.memberCount)} jobs`);
      if (Number(stats.failedMemberCount) > 0) values.push(`${formatCount(stats.failedMemberCount)} failed`);
      break;
    case 'remote-file':
      if (stats.knownBytes != null) values.push(formatBytes(Number(stats.knownBytes)));
      break;
    case 'remote-directory':
      if (!values.length && stats.fileCount != null) values.push(`${formatCount(stats.fileCount)} files`);
      break;
    case 'retrieve-folder':
      if (stats.newFilesFoundCount != null)
        values.push(`${formatCount(stats.newFilesFoundCount)} new files`);
      break;
  }
  return values.slice(0, 3);
}

/** Compact child-row progress uses the same bounded summary as the root list. */
export function automaticJobProgressPercent(job: JobSummaryDto): number | undefined {
  if (job.lifecycleState === 'Terminal') return undefined;
  const stats = job.stats;
  if (!stats) return undefined;
  if ((job.kind === 'album' || job.kind === 'remote-directory')
    && stats.progressPercent != null && Number.isFinite(Number(stats.progressPercent)))
    return Math.max(0, Math.min(100, Number(stats.progressPercent)));
  if (!['album', 'remote-directory', 'song-groups', 'album-groups', 'job-list'].includes(job.kind ?? ''))
    return undefined;
  const total = Number(stats.memberCount);
  const completed = Number(stats.completedMemberCount);
  if (stats.memberCount == null || stats.completedMemberCount == null || !Number.isFinite(total)
    || !Number.isFinite(completed) || total <= 0) return undefined;
  return Math.max(0, Math.min(100, completed / total * 100));
}
