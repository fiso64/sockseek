<script module lang="ts">
  export { matchedText, matchedPath };

  function foldAccents(value: string): string {
    return value.normalize('NFD').replace(/\p{M}/gu, '');
  }

  export function filterPattern(value?: string | null): RegExp | undefined {
    const literal = foldAccents(value?.trim() ?? '').replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    return literal ? new RegExp(literal, 'giu') : undefined;
  }

  function textParts(text: string, pattern?: RegExp) {
    if (!pattern) return;
    const parts: { start: number; end: number; matched: boolean }[] = [];
    const folded = foldAccents(text);
    // Map matches back to the original spelling, including any combining accents.
    const original: { start: number; end: number }[] = [];
    if (folded !== text) {
      for (const cluster of text.matchAll(/\P{M}\p{M}*/gu)) {
        const span = { start: cluster.index, end: cluster.index + cluster[0].length };
        for (let i = 0, length = foldAccents(cluster[0]).length; i < length; i++) original.push(span);
      }
    }
    let end = 0;
    for (const match of folded.matchAll(pattern)) {
      const start = Math.max(end, original[match.index]?.start ?? match.index);
      const next = match.index + match[0].length;
      const stop = original[next - 1]?.end ?? next;
      if (stop <= start) continue;
      if (end < start) parts.push({ start: end, end: start, matched: false });
      parts.push({ start, end: stop, matched: true });
      end = stop;
    }
    if (parts.length && end < text.length) parts.push({ start: end, end: text.length, matched: false });
    return parts;
  }
</script>

{#snippet slice(text: string, parts: ReturnType<typeof textParts>, start: number, end: number)}
  {#if parts?.length}
    {#each parts as part}
      {#if part.end > start && part.start < end}
        {@const value = text.slice(Math.max(start, part.start), Math.min(end, part.end))}
        {#if part.matched}<mark class="filter-match">{value}</mark>{:else}{value}{/if}
      {/if}
    {/each}
  {:else}{text.slice(start, end)}{/if}
{/snippet}

{#snippet matchedText(text: string, pattern?: RegExp, start = 0)}
  {@render slice(text, textParts(text, pattern), start, text.length)}
{/snippet}

{#snippet matchedPath(text: string, pattern: RegExp | undefined, parentLength: number, transfer = false)}
  {@const parts = textParts(text, pattern)}
  <span class="folder-path-ancestors">{@render slice(text, parts, 0, parentLength)}</span><b class:folder-transfer-name={transfer}>{@render slice(text, parts, parentLength, text.length)}</b>
{/snippet}
