import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { expect, test } from 'vitest';
import { manualPages } from '../src/components/ManualLink.svelte';

// The UI links into the manual by path; a moved page or renamed heading must fail here.
test.each(Object.entries(manualPages))('manual topic %s exists', (_topic, path) => {
  const [page, anchor] = path.split('#');
  const source = resolve(__dirname, '../../docs/pages', page!.replace(/\.html$/, '.md'));
  expect(existsSync(source), source).toBe(true);
  if (!anchor) return;
  const headings = readFileSync(source, 'utf8').split('\n').filter(line => line.startsWith('#'))
    .map(line => line.replace(/^#+\s*/, '').toLowerCase().replace(/[^a-z0-9 -]/g, '').trim().replace(/\s+/g, '-'));
  expect(headings).toContain(anchor);
});
