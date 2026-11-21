import type { AppIconName } from '../../app/icons';

const audioFileExtensions = new Set([
  'FLAC', 'MP3', 'WAV', 'OGG', 'OPUS', 'M4A', 'M4B', 'AAC', 'ALAC', 'APE', 'AIFF', 'AIF', 'WMA',
]);

function normalizeExtension(value: string | null | undefined): string {
  return value?.trim().replace(/^\./, '').toUpperCase() ?? '';
}

export function extension(pathOrFilename: string): string {
  const name = pathOrFilename.split(/[\\/]/).at(-1) ?? pathOrFilename;
  const dot = name.lastIndexOf('.');
  return dot >= 0 ? normalizeExtension(name.slice(dot + 1)) : '';
}

export function resolveFileExtension(extensionHint: string | null | undefined, filename: string | null | undefined): string {
  return normalizeExtension(extensionHint) || extension(filename ?? '');
}

export function fileTypeIcon(options: {
  extension?: string | null;
  filename?: string | null;
  hasAudioMetadata?: boolean;
}): AppIconName {
  const resolvedExtension = resolveFileExtension(options.extension, options.filename);
  if (options.hasAudioMetadata || audioFileExtensions.has(resolvedExtension)) return 'track';
  return 'file';
}
