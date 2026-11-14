import type { JobSummaryDto } from '../../api/types';
import { formatCount, formatCountWithNoun } from '../../app/format';
import { formatBytes } from '../presentation/items';
import { jobOutcomeDetail } from './status';

/** Summarize the current stage using only authoritative, bounded job facts. */
export function automaticJobCountStats(job: JobSummaryDto): string[] {
  const stats = job.stats;
  const values: string[] = [];
  const terminal = job.lifecycleState === 'Terminal';
  const discovering = job.lifecycleState === 'AwaitingSelection' || job.lifecycleState === 'Running'
    && ['WaitingForSearchConcurrency', 'SearchRateLimited', 'Searching', 'ProcessingSearchResults'].includes(job.activityPhase ?? '');
  const directory = job.kind === 'album' || job.kind === 'remote-directory';
  const noun = directory ? 'file' : job.kind === 'song-groups' ? 'song'
    : job.kind === 'album-groups' ? 'album' : job.kind === 'job-list' ? 'job' : null;

  if (discovering && (job.kind === 'song' || job.kind === 'album')) {
    const count = job.kind === 'song' ? stats?.candidateCount : stats?.folderCount;
    if (count != null && Number(count) > 0)
      values.push(formatCountWithNoun(count, job.kind === 'song' ? 'candidate' : 'candidate folder'));
  } else if (noun && stats) {
    const total = stats.memberCount ?? (directory ? stats.fileCount : null);
    const finished = stats.completedMemberCount;
    // Zero before discovery finishes is not an empty download plan.
    if (total != null && (Number(total) > 0 || job.kind === 'job-list'
      || terminal && job.terminalOutcome === 'Succeeded')) {
      const extent = formatCountWithNoun(total, noun);
      const showProgress = finished != null && Number(total) > 0
        && (terminal ? Number(finished) < Number(total) : job.lifecycleState !== 'Pending');
      values.push(showProgress ? `${formatCount(finished)} / ${extent} finished` : extent);
    }
    if (job.lifecycleState !== 'Pending') {
      if (directory) {
        if (Number(stats.downloadedMemberCount) > 0) values.push(`${formatCount(stats.downloadedMemberCount)} downloaded`);
        if (Number(stats.reusedMemberCount) > 0) values.push(`${formatCount(stats.reusedMemberCount)} reused`);
      } else if (finished != null && stats.failedMemberCount != null && stats.skippedMemberCount != null) {
        // These are mutually exclusive terminal categories. "Succeeded" does
        // not imply every nested job performed a file transfer.
        const succeeded = Number(finished) - Number(stats.failedMemberCount) - Number(stats.skippedMemberCount);
        if (succeeded > 0) values.push(`${formatCount(succeeded)} succeeded`);
      }
      if (Number(stats.skippedMemberCount) > 0) values.push(`${formatCount(stats.skippedMemberCount)} skipped`);
      // The server's failure aggregate includes cancellations and partial outcomes.
      if (Number(stats.failedMemberCount) > 0) values.push(`${formatCount(stats.failedMemberCount)} unsuccessful`);
    }
  } else if (job.kind === 'remote-file' && stats?.knownBytes != null) {
    values.push(formatBytes(Number(stats.knownBytes)));
  } else if (job.kind === 'retrieve-folder' && stats?.newFilesFoundCount != null
    && (terminal || Number(stats.newFilesFoundCount) > 0)) {
    values.push(formatCountWithNoun(stats.newFilesFoundCount, 'new file'));
  }

  return values;
}

export function automaticJobListStats(job: JobSummaryDto): string[] {
  const values = automaticJobCountStats(job);
  const outcome = jobOutcomeDetail(job);
  if (outcome) values.push(outcome);
  return values;
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
