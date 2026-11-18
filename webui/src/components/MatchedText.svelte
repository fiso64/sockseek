<script module lang="ts">
  export { matchedText, matchedPath };

  export function filterPattern(value?: string | null): RegExp | undefined {
    const literal = value?.trim().replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    return literal ? new RegExp(literal, 'giu') : undefined;
  }

  function textParts(text: string, pattern?: RegExp) {
    if (!pattern) return;
    const parts: { start: number; end: number; matched: boolean }[] = [];
    let end = 0;
    for (const match of text.matchAll(pattern)) {
      if (end < match.index) parts.push({ start: end, end: match.index, matched: false });
      end = match.index + match[0].length;
      parts.push({ start: match.index, end, matched: true });
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
