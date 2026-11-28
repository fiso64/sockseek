import type { JobSummaryDto, Schema } from '../../api/types';
import { formatCountWithNoun, humanizeStateValue } from '../../app/format';
import { automaticJobCountStats } from './list-stats';
import { jobOutcomeDetail, jobSkipExplanation, jobStatusLabel, rejectionLines } from './status';

export interface JobDetailInfo {
  message: string;
  facts: { label: string; value: string; peer?: boolean }[];
  counts: string[];
  /** Why no result matched, when that is how the job failed. */
  rejections: string[];
  note?: string;
  progress?: { completed: number; total: number; percent: number };
}

/** Extra context only: content cards own file/folder progress and successful results.
 * Collections need a card while active or for an unsuccessful outcome. Imports/retrievals have no content card, so keep their input.
 * Never derive totals from the currently loaded child page.
 */
export function jobDetailInfo(job: JobSummaryDto, payload: Schema<'JobPayloadDto'> | null | undefined,
  hasContent: boolean): JobDetailInfo | null {
  const terminal = job.lifecycle.lifecycleState === 'Terminal';
  const collection = ['job-list', 'song-groups', 'album-groups'].includes(job.display.kind ?? '');
  const stats = job.stats;
  const countsKnown = stats?.completedMemberCount != null && stats.failedMemberCount != null
    && stats.skippedMemberCount != null;
  const info: JobDetailInfo = { message: '', facts: [], counts: [], rejections: rejectionLines(payload) };
  const childFailuresCounted = job.lifecycle.failureReason === 'ChildJobsFailed' && Number(stats?.failedMemberCount) > 0;
  if (terminal && job.lifecycle.terminalOutcome !== 'Succeeded') {
    // Lists show the short reason; the card has room for the owner's full
    // explanation when one was recorded.
    const concise = childFailuresCounted && collection ? '' : jobOutcomeDetail(job);
    const failed = job.lifecycle.terminalOutcome === 'Failed' || job.lifecycle.terminalOutcome === 'PartialSuccess';
    info.message = concise && failed && job.lifecycle.failureMessage?.trim() || concise;
  }

  if (collection) {
    if (!terminal || job.lifecycle.terminalOutcome !== 'Succeeded') {
      info.counts = automaticJobCountStats(job);
      if (stats?.memberCount != null && stats.completedMemberCount != null && Number(stats.memberCount) > 0) {
        const total = Number(stats.memberCount), completed = Number(stats.completedMemberCount);
        if (!terminal) info.progress = { total, completed, percent: Math.max(0, Math.min(100, completed / total * 100)) };
      }
      if (terminal && !countsKnown) info.note = 'Outcome totals are unavailable; the loaded jobs may be only part of this collection.';
    }
  }

  if (job.lifecycle.terminalOutcome === 'Skipped' && terminal) {
    const output = payload?.kind === 'song' ? payload.file : payload?.kind === 'album' ? payload.directory : null;
    info.message = jobSkipExplanation(job, output?.existingFileSource);
    if (output?.downloadPath) info.facts.push({ label: 'Existing path', value: output.downloadPath });
  }
  if (payload?.kind === 'extract') {
    info.facts.push({ label: 'Input', value: payload.input });
    if (!info.message && terminal && !payload.resultJobId) info.message = 'The source did not produce any jobs.';
  } else if (payload?.kind === 'retrieve-folder') {
    info.facts.push({ label: 'From', value: payload.username, peer: true }, { label: 'Folder', value: payload.folderPath });
    if (terminal) {
      info.counts = [formatCountWithNoun(payload.newFilesFoundCount, 'new file')];
      if (!info.message && payload.retrievalOutcome !== 'completed' && payload.retrievalOutcome !== 'none') info.message = humanizeStateValue(payload.retrievalOutcome);
    }
  } else if (payload?.kind === 'generic' && payload.text !== job.display.itemName && payload.text !== info.message) {
    info.facts.push({ label: 'Detail', value: payload.text });
  }

  if (!terminal && !hasContent) {
    if (!collection) info.counts = automaticJobCountStats(job);
  } else if (terminal && !hasContent && !info.message && !collection && job.lifecycle.terminalOutcome !== 'Skipped') {
    if (payload?.kind === 'song') info.message = 'This job has no downloaded file to show.';
    if (payload?.kind === 'album') info.message = 'This job has no downloaded folder to show.';
  }
  // An empty successful collection is useful information, not a missing result.
  if (collection && terminal && Number(stats?.memberCount) === 0 && stats?.memberCount != null && !info.message)
    info.message = `No ${job.display.kind === 'song-groups' ? 'songs' : job.display.kind === 'album-groups' ? 'albums' : 'jobs'} were produced.`;
  // The label already conveys the state; diagnostics remain in its modal.
  if (info.message === jobStatusLabel(job)) info.message = '';
  return info.message || info.facts.length || info.counts.length || info.rejections.length || info.note || (!terminal && !hasContent) ? info : null;
}
