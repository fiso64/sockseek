import type { Schema, TransferStateDto } from '../../api/types';
import { liveTransferPresentation } from '../transfers/model';
import { jobStatusLabel, jobTransferTone } from './status';
import type { TransferPresentation } from '../presentation/items';

export function selectedJobFileTransfer(live: TransferStateDto): TransferPresentation {
  const transfer = liveTransferPresentation(live);
  if (transfer.tone !== 'active' || live.identity.source === 'Fallback'
    || live.status.state === 'AttemptCompleted') return transfer;
  // The selected-file detail shows only the determinate percentage, while the
  // shared transfer timeline also includes transferred bytes.
  return {
    ...transfer,
    state: 'Downloading',
    progressText: transfer.progressPercent == null
      ? transfer.progressText
      : `${Math.round(transfer.progressPercent)}%`,
    eta: transfer.eta?.replace(/ remaining$/, ''),
  };
}

/** Job outcomes own skip/finalization/cancellation facts that a network attempt cannot describe. */
export function jobFileTransfer(job: Schema<'JobSummaryDto'> | null | undefined,
  file: Schema<'FileDownloadStateDto'> | null | undefined,
  target?: Schema<'PeerFileTargetDto'> | null, live?: TransferStateDto, source?: Schema<'FallbackSourceDto'> | null): TransferPresentation {
  const current = live ? selectedJobFileTransfer(live) : undefined;
  const terminal = job?.lifecycleState === 'Terminal';
  // A transport describes the job only while it is transferring. Finalization,
  // fallback and search are owned by the job, even if an attempt is still open.
  const transferring = !job || job.lifecycleState === 'Running' && job.activityPhase === 'Downloading';
  const showTransfer = !terminal && transferring && current != null;
  const showProgress = terminal || transferring;
  // Fallback phases already hide progress through showProgress. An unscoped
  // fallback transport must also not inherit an earlier peer's file progress.
  const fileProgress = live?.identity.source === 'Fallback' || file?.progressPercent == null
    ? undefined : Number(file.progressPercent);
  let state: string;
  if (terminal && file?.reused) state = 'Reused';
  else if (showTransfer) state = current.state;
  else state = job ? jobStatusLabel(job) : 'Pending';
  return {
    ...current, job: job ?? undefined, file: file ?? undefined, target: target ?? undefined,
    fallback: source ?? live?.identity.fallback ?? undefined,
    state,
    tone: showTransfer ? current.tone : jobTransferTone(job),
    progressText: showTransfer ? current.progressText : undefined,
    direction: 'download',
    speed: showTransfer ? current.speed : undefined,
    eta: showTransfer ? current.eta : undefined,
    detail: job?.failureMessage ?? (terminal ? undefined : current?.detail),
    progressPercent: !showProgress || job?.terminalOutcome === 'Skipped' || file?.reused ? undefined
      : current?.progressPercent ?? fileProgress,
  };
}

export function selectedJobFile(payload: Schema<'JobPayloadDto'> | null | undefined, live?: TransferStateDto): {
  path: string;
  target?: Schema<'PeerFileTargetDto'>;
  sourceName?: string;
  file: Schema<'FileDownloadStateDto'>;
  peer?: Schema<'PeerInfoDto'>;
} | null {
  if (live?.identity.source === 'Fallback' || payload?.kind === 'song' &&
    (payload.downloadSource === 'Fallback' || payload.fallback != null)) {
    const source = live?.identity.fallback ?? (payload?.kind === 'song' ? payload.fallback : null);
    const file = payload?.kind === 'song' ? payload.file : null;
    const complete = payload?.kind === 'song' && payload.downloadSource === 'Fallback';
    return {
      path: (complete ? file?.downloadPath : null) ?? source?.title ?? source?.reference ?? 'Fallback download',
      sourceName: source?.provider ?? 'Fallback',
      file: { reused: false, downloadPath: complete ? file?.downloadPath : null,
        fileSize: complete ? file?.fileSize : null, bytesTransferred: complete ? file?.bytesTransferred ?? 0 : 0,
        progressPercent: complete ? file?.progressPercent : null },
    };
  }
  const peerFile = selectedPeerFile(payload, live);
  return peerFile ? { ...peerFile, path: peerFile.target.filename } : null;
}

function selectedPeerFile(payload: Schema<'JobPayloadDto'> | null | undefined, live?: TransferStateDto): {
  target: Schema<'PeerFileTargetDto'>;
  file: Schema<'FileDownloadStateDto'>;
  peer?: Schema<'PeerInfoDto'>;
} | null {
  if (payload?.kind === 'remote-file') {
    const sameTarget = live?.identity.username === payload.target.username
      && live.identity.remotePath === payload.target.filename;
    const observedSize = sameTarget
      ? knownSize(live.file?.size) ?? knownSize(live.progress.totalBytes) : null;
    return {
      target: payload.target.size == null && observedSize != null
        ? { ...payload.target, size: observedSize } : payload.target,
      file: payload.file,
    };
  }
  if (payload && payload.kind !== 'song') return null;
  // The caller scopes live data by job ID. The active attempt is authoritative
  // even when the retained job snapshot has not captured its selected target.
  if (live?.identity.username && live.identity.remotePath) return {
    target: {
      username: live.identity.username, filename: live.identity.remotePath,
      size: live.file?.size ?? (Number(live.progress.totalBytes) >= 0 ? live.progress.totalBytes : null),
      extension: live.file?.extension ?? null,
      bitRate: live.file?.bitRate, bitDepth: live.file?.bitDepth,
      sampleRate: live.file?.sampleRate, length: live.file?.length,
      attributes: live.file?.attributes,
    },
    file: payload?.file ?? {
      reused: false,
      downloadPath: live.status.localPath,
      bytesTransferred: live.progress.bytesTransferred,
      fileSize: live.file?.size ?? (Number(live.progress.totalBytes) >= 0 ? live.progress.totalBytes : null),
      progressPercent: liveTransferPresentation(live).progressPercent ?? null,
    },
  };
  if (!payload) return null;
  if (!payload.resolvedUsername || !payload.resolvedFilename) {
    if (payload.exactTarget) return { target: payload.exactTarget, file: payload.file };
    return payload.lastAttemptedFile ? {
      target: payload.lastAttemptedFile.target, peer: payload.lastAttemptedFile.peer, file: payload.file,
    } : null;
  }
  const attribute = (type: string) => payload.resolvedAttributes?.find((value) => value.type === type)?.value ?? null;
  return {
    target: {
      username: payload.resolvedUsername,
      filename: payload.resolvedFilename,
      size: payload.resolvedSize ?? null,
      extension: payload.resolvedExtension ?? null,
      bitRate: attribute('BitRate'),
      bitDepth: attribute('BitDepth'),
      sampleRate: payload.resolvedSampleRate ?? attribute('SampleRate'),
      length: attribute('Length'),
      attributes: payload.resolvedAttributes ?? null,
    },
    file: payload.file,
  };
}

function knownSize(value: number | string | null | undefined): number | string | null {
  if (value == null) return null;
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed >= 0 ? value : null;
}
