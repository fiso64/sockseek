import { describe, expect, it } from 'vitest';
import { audioAttributes, formatLength } from '../src/domains/presentation/items';

describe('wire audio metadata presentation', () => {
  it('treats omitted, null, and negative fields as absent rather than invalid audio facts', () => {
    expect(audioAttributes({})).toBeUndefined();
    expect(audioAttributes({ bitRate: null, sampleRate: null, bitDepth: null, length: null })).toBeUndefined();
    expect(formatLength(audioAttributes({})?.lengthSeconds)).toBe('—');
    const invalid = Object.freeze({ bitRate: -1, sampleRate: -1, bitDepth: -1, length: -2_147_483_648 });
    expect(audioAttributes(invalid)).toBeUndefined();
    expect(formatLength(audioAttributes(invalid)?.lengthSeconds)).toBe('—');
    expect(invalid.length).toBe(-2_147_483_648);
  });

  it('preserves partial attributes, integer strings, and a real zero duration', () => {
    expect(audioAttributes({ bitRate: 320, sampleRate: '44100', length: 0 })).toEqual({
      bitrateKbps: 320, sampleRateHz: 44100, bitDepth: undefined, lengthSeconds: 0,
    });
    expect(formatLength(audioAttributes({ length: '125' })?.lengthSeconds)).toBe('2:05');
    expect(audioAttributes({ length: 'not-a-number' })).toBeUndefined();
  });
});
