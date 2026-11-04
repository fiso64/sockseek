import type { Schema } from '../api/types';

export function humanizeStateValue(value: string): string {
  const normalized = value
    .replace(/[_-]+/g, ' ')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
    .replace(/\s+/g, ' ')
    .trim();
  return normalized ? normalized[0]!.toUpperCase() + normalized.slice(1) : normalized;
}

export function userInitials(value: string): string {
  const pieces = value.split(/[^a-z0-9]+/i).filter(Boolean);
  return pieces.length
    ? pieces.slice(0, 2).map((piece) => piece[0]!.toUpperCase()).join('')
    : '?';
}

export function soulseekStatusLabel(
  status: Schema<'SoulseekClientStatusDto'> | null | undefined,
  reachable: boolean,
): string {
  if (!reachable) return 'Unavailable';
  if (!status) return 'Connecting';
  const flags = status.flags.filter((flag) => flag && flag !== 'None');
  const values = flags.length
    ? flags
    : status.state.split(',').map((part) => part.trim()).filter((part) => part && part !== 'None');
  return values.length ? values.map(humanizeStateValue).join(', ') : 'None';
}

export function formatCount(value: number | string | null | undefined): string {
  if (typeof value === 'string' && /^-?\d+$/.test(value)) return BigInt(value).toLocaleString();
  return Number(value ?? 0).toLocaleString();
}

export function formatCountWithNoun(value: number | string | null | undefined, singular: string, plural = `${singular}s`): string {
  return `${formatCount(value)} ${value === 1 || value === '1' ? singular : plural}`;
}

export function formatBytes(value: number | string | null | undefined): string {
  const bytes = Number(value ?? 0);
  if (bytes >= 1_000_000_000_000) return `${(bytes / 1_000_000_000_000).toFixed(2)} TB`;
  if (bytes >= 1_000_000_000) return `${(bytes / 1_000_000_000).toFixed(2)} GB`;
  if (bytes >= 1_000_000) return `${(bytes / 1_000_000).toFixed(bytes >= 100_000_000 ? 0 : 1)} MB`;
  if (bytes >= 1_000) return `${(bytes / 1_000).toFixed(1)} KB`;
  return `${bytes} B`;
}

export function formatSpeed(value: number | string | null | undefined): string | null {
  if (value === null || value === undefined) return null;
  let speed = Number(value);
  if (!Number.isFinite(speed) || speed <= 0) return null;
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let unit = 0;
  while (speed >= 1000 && unit < units.length - 1) {
    speed /= 1000;
    unit += 1;
  }
  const digits = speed >= 100 || unit === 0 ? 0 : speed >= 10 ? 1 : 2;
  return `${speed.toFixed(digits)} ${units[unit]}/s`;
}

export function formatDuration(value: number | string | null | undefined): string {
  if (value === null || value === undefined) return '—';
  const seconds = Number(value);
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
}
