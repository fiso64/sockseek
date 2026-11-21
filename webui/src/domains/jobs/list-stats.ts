import type { JobSummaryDto } from '../../api/types';
import { formatCount, formatCountWithNoun } from '../../app/format';
import { formatBytes } from '../presentation/items';
import { jobOutcomeDetail } from './status';

/** Summarize the current stage using only authoritative, bounded job facts. */
export function automaticJobCountStats(job: JobSummaryDto): string[] {
  const stats = job.stats;
  const values: string[] = [];
  const terminal = job.lifecycle.lifecycleState === 'Terminal';
  const discovering = job.lifecycle.lifecycleState === 'AwaitingSelection' || job.lifecycle.lifecycleState === 'Running'
    && ['WaitingForSearchConcurrency', 'SearchRateLimited', 'Searching', 'ProcessingSearchResults'].includes(job.lifecycle.activityPhase ?? '');
  const directory = job.display.kind === 'album' || job.display.kind === 'remote-directory';
  const noun = directory ? 'file' : job.display.kind === 'song-groups' ? 'song'
    : job.display.kind === 'album-groups' ? 'album' : job.display.kind === 'job-list' ? 'job' : null;

  if (discovering && (job.display.kind === 'song' || job.display.kind === 'album')) {
    const count = job.display.kind === 'song' ? stats?.candidateCount : stats?.folderCount;
    if (count != null && Number(count) > 0)
      values.push(formatCountWithNoun(count, job.display.kind === 'song' ? 'candidate' : 'candidate folder'));
  } else if (noun && stats) {
    const total = stats.memberCount ?? (directory ? stats.fileCount : null);
    const finished = stats.completedMemberCount;
    // Zero before discovery finishes is not an empty download plan.
    if (total != null && (Number(total) > 0 || job.display.kind === 'job-list'
      || terminal && job.lifecycle.terminalOutcome === 'Succeeded')) {
      const extent = formatCountWithNoun(total, noun);
      const showProgress = finished != null && Number(total) > 0
        && (terminal ? Number(finished) < Number(total) : job.lifecycle.lifecycleState !== 'Pending');
      values.push(showProgress ? `${formatCount(finished)} / ${extent} finished` : extent);
    }
    if (job.lifecycle.lifecycleState !== 'Pending') {
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
  } else if (job.display.kind === 'remote-file' && stats?.knownBytes != null) {
    values.push(formatBytes(Number(stats.knownBytes)));
  } else if (job.display.kind === 'retrieve-folder' && stats?.newFilesFoundCount != null
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
  if (job.lifecycle.lifecycleState === 'Terminal') return undefined;
  const stats = job.stats;
  if (!stats) return undefined;
  if ((job.display.kind === 'album' || job.display.kind === 'remote-directory')
    && stats.progressPercent != null && Number.isFinite(Number(stats.progressPercent)))
    return Math.max(0, Math.min(100, Number(stats.progressPercent)));
  if (!['album', 'remote-directory', 'song-groups', 'album-groups', 'job-list'].includes(job.display.kind ?? ''))
    return undefined;
  const total = Number(stats.memberCount);
  const completed = Number(stats.completedMemberCount);
  if (stats.memberCount == null || stats.completedMemberCount == null || !Number.isFinite(total)
    || !Number.isFinite(completed) || total <= 0) return undefined;
  return Math.max(0, Math.min(100, completed / total * 100));
}

export interface SearchJobStat {
  count: number | string;
  label: string;
}

/** Job lists show collected facts; projection/group counts belong to the results view. */
export function searchJobStats(job: JobSummaryDto): SearchJobStat[] {
  const files = job.discovery.publicFileCount ?? job.discovery.rawResultCount;
  const peers = job.discovery.observedPeerCount;
  const counts = [
    { count: files, label: files === 1 || files === '1' ? 'file' : 'files' },
    { count: job.discovery.lockedFileCount, label: 'locked' },
    { count: peers, label: peers === 1 || peers === '1' ? 'peer' : 'peers' },
  ];
  return counts.filter((item): item is { count: number | string; label: string } => item.count != null);
}
