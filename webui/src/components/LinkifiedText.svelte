<script lang="ts">
  interface Props { text: string; }
  type Segment = { kind: 'text'; value: string } | { kind: 'link'; value: string; href: string };
  let { text }: Props = $props();

  function linkify(value: string): Segment[] {
    const segments: Segment[] = [];
    const matcher = /\b(?:https?:\/\/|www\.)[^\s<>"']+/gi;
    let cursor = 0;
    for (const match of value.matchAll(matcher)) {
      const index = match.index ?? 0;
      if (index > cursor) segments.push({ kind: 'text', value: value.slice(cursor, index) });
      const raw = match[0];
      const trimmed = raw.replace(/[.,!?;:]+$/, '');
      segments.push({ kind: 'link', value: trimmed, href: trimmed.startsWith('www.') ? `https://${trimmed}` : trimmed });
      if (raw.length > trimmed.length) segments.push({ kind: 'text', value: raw.slice(trimmed.length) });
      cursor = index + raw.length;
    }
    if (cursor < value.length) segments.push({ kind: 'text', value: value.slice(cursor) });
    return segments;
  }

  let segments = $derived(linkify(text));
</script>

{#each segments as segment}
  {#if segment.kind === 'link'}<a href={segment.href} target="_blank" rel="noopener noreferrer">{segment.value}</a>{:else}{segment.value}{/if}
{/each}
