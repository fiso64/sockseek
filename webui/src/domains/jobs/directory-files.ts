import type { JobSummaryDto, Schema, TransferStateDto } from '../../api/types';
import { audioAttributes, basename, type FolderItemFile } from '../presentation/items';
import { liveTransferPresentation } from '../transfers/model';
import { jobStatusLabel, jobTransferTone } from './status';
import { selectedJobFile } from './selected-file';

/** A zero known-byte sum is only a total when the owner confirms no unknown sizes. */
export function directoryKnownSize(directory: Schema<'DirectoryDownloadStateDto'>): number | null {
  if (directory.attemptNumber == null) return null;
  const bytes = Number(directory.totalKnownBytes);
  if (!Number.isFinite(bytes) || bytes < 0) return null;
  if (bytes === 0 && (directory.unknownFileSizeCount == null
    || Number(directory.unknownFileSizeCount) > 0)) return null;
  return bytes;
}

export function directoryFileRow({ id, relativePath, target, childId, child, file, live, ownerTerminal }: {
  id: string;
  relativePath: string;
  target?: Schema<'PeerFileTargetDto'> | null;
  childId?: string | null;
  child?: JobSummaryDto | null;
  file?: Schema<'FileDownloadStateDto'> | null;
  live?: TransferStateDto;
  ownerTerminal: boolean;
}): FolderItemFile {
  return {
    id, relativePath,
    sizeBytes: Number(target?.size ?? 0),
    sizeUnknown: target?.size == null,
    audio: audioAttributes(target),
    transfer: live ? liveTransferPresentation(live) : child ? {
      state: jobStatusLabel(child),
      tone: jobTransferTone(child),
      direction: 'download',
      progressPercent: child.terminalOutcome === 'Skipped' || file?.progressPercent == null
        ? undefined : Number(file.progressPercent),
    } : {
      state: ownerTerminal ? childId ? 'Outcome unavailable' : 'Not started' : 'Pending',
      direction: 'download',
    },
  };
}

/** Planned and supplemental files share one ordered page and one presentation. */
export function directoryManifestFileRow(entry: Schema<'DirectoryManifestEntryDto'>, {
  child, live, ownerTerminal,
}: { child?: JobSummaryDto | null; live?: TransferStateDto; ownerTerminal: boolean }): FolderItemFile {
  const observed = !entry.target && live ? selectedJobFile(null, live) : null;
  const target = entry.target ?? observed?.target;
  const relativePath = target
    ? [...entry.relativeDirectoryComponents, basename(target.filename)].join('/')
    : 'File details unavailable';
  return { ...directoryFileRow({
    id: String(entry.ordinal), relativePath,
    target, childId: entry.childJobId, child,
    file: entry.file ?? observed?.file, live, ownerTerminal,
  }), sourcePath: target?.filename };
}

