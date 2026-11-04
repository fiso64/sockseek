import type { Schema, TransferStateDto } from '../../api/types';
import { liveTransferPresentation } from '../transfers/model';
import { jobStatusLabel, jobTransferTone } from './status';
import type { TransferPresentation } from '../presentation/items';

export function selectedJobFileTransfer(live: TransferStateDto): TransferPresentation {
  const transfer = liveTransferPresentation(live);
  if (transfer.tone !== 'active') return transfer;
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
  target?: Schema<'PeerFileTargetDto'> | null, live?: TransferStateDto): TransferPresentation {
  const current = live ? selectedJobFileTransfer(live) : undefined;
  const terminal = job?.lifecycleState === 'Terminal';
  return {
    ...current, job: job ?? undefined, file: file ?? undefined, target: target ?? undefined,
    state: terminal || !current ? job ? jobStatusLabel(job) : 'Pending' : current.state,
    tone: terminal || !current ? jobTransferTone(job) : current.tone,
    direction: 'download',
    speed: terminal ? undefined : current?.speed,
    eta: terminal ? undefined : current?.eta,
    detail: job?.failureMessage ?? (terminal ? undefined : current?.detail),
    progressPercent: job?.terminalOutcome === 'Skipped' ? undefined
      : current?.progressPercent ?? (file?.progressPercent == null ? undefined : Number(file.progressPercent)),
  };
}

export function selectedJobFile(payload: Schema<'JobPayloadDto'> | null | undefined, live?: TransferStateDto): {
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
