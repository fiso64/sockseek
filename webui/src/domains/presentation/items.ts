import type { Schema } from '../../api/types';

export function audioAttributes(file?: Partial<Pick<Schema<'FileMetadataDto'>, 'bitRate' | 'sampleRate' | 'bitDepth' | 'length'>> | null): AudioAttributes | undefined {
  if (!file) return undefined;
  const optionalNumber = (value: number | string | null | undefined): number | undefined => {
    if (value == null) return undefined;
    const parsed = Number(value);
    return Number.isFinite(parsed) && parsed >= 0 ? parsed : undefined;
  };
  const audio = {
    bitrateKbps: optionalNumber(file.bitRate),
    sampleRateHz: optionalNumber(file.sampleRate),
    bitDepth: optionalNumber(file.bitDepth),
    lengthSeconds: optionalNumber(file.length),
  };
  return Object.values(audio).some(value => value !== undefined) ? audio : undefined;
}

export interface AudioAttributes {
  bitrateKbps?: number;
  sampleRateHz?: number;
  bitDepth?: number;
  lengthSeconds?: number;
}

export interface ItemPeerInfo {
  username: string;
  uploadSpeedMbps?: number;
  freeUploadSlot?: boolean;
  queueLength?: number;
}

export function basename(path: string): string {
  return path.split(/[\\/]/).at(-1) ?? path;
}


export function formatBytes(bytes: number): string {
  if (bytes >= 1_000_000_000) return `${(bytes / 1_000_000_000).toFixed(2)} GB`;
  return `${(bytes / 1_000_000).toFixed(bytes >= 100_000_000 ? 0 : 1)} MB`;
}

export function formatLength(seconds?: number): string {
  if (seconds === undefined) return '—';
  const minutes = Math.floor(seconds / 60);
  return `${minutes}:${String(seconds % 60).padStart(2, '0')}`;
}

export function sampleRateLabel(hz?: number): string {
  if (!hz) return '—';
  return hz % 1000 === 0 ? `${hz / 1000} kHz` : `${(hz / 1000).toFixed(1)} kHz`;
}

export function audioSummary(audio?: AudioAttributes): string {
  if (!audio) return '—';
  const parts: string[] = [];
  if (audio.bitDepth) parts.push(`${audio.bitDepth}-bit`);
  if (audio.sampleRateHz) parts.push(sampleRateLabel(audio.sampleRateHz));
  if (audio.bitrateKbps) parts.push(`${audio.bitrateKbps} kbps`);
  return parts.length ? parts.join(' · ') : '—';
}

export interface TransferPresentation {
  // Keep the canonical facts for on-demand details; do not reformat every row
  // into a second detail model on each progress update.
  record?: Readonly<Schema<'TransferStateDto'>>;
  job?: Readonly<Schema<'JobSummaryDto'>>;
  file?: Readonly<Schema<'FileDownloadStateDto'>>;
  directory?: Readonly<Schema<'DirectoryDownloadStateDto'>>;
  target?: Readonly<Schema<'PeerFileTargetDto'>>;
  group?: Readonly<Schema<'TransferGroupSummaryDto'>>;
  state: string;
  tone?: 'active' | 'queued' | 'complete' | 'failed' | 'cancelled';
  cancellable?: boolean;
  progressPercent?: number;
  progressText?: string;
  speed?: string;
  eta?: string;
  detail?: string;
  direction?: 'download' | 'upload';
  createdAt?: string;
  completedAt?: string;
}

/** One immutable file presentation. Replace it when data changes; selection is separate. */
export interface FolderItemFile {
  readonly id: string;
  readonly relativePath: string;
  readonly sourcePath?: string;
  readonly locked?: boolean;
  readonly sizeBytes: number;
  readonly sizeUnknown?: boolean;
  readonly audio?: Readonly<AudioAttributes>;
  readonly transfer?: Readonly<TransferPresentation>;
}

/** Exact containing folders of the available files; display roots describe placement instead. */
export function folderSources(files: readonly FolderItemFile[], filesComplete: boolean) {
  const folders = new Set<string>();
  let peer: string | undefined;
  let complete = filesComplete;
  for (const member of files) {
    const source = member.transfer;
    peer ??= source?.target?.username ?? source?.record?.identity.username ?? undefined;
    const remote = source?.target?.filename ?? source?.record?.identity.remotePath ?? member.sourcePath;
    if (remote == null) { complete = false; continue; }
    const separator = Math.max(remote.lastIndexOf('\\'), remote.lastIndexOf('/'));
    folders.add(separator < 0 ? '(peer root)' : remote.slice(0, separator) || remote.slice(0, 1));
  }
  return { folders: [...folders], peer, complete };
}
