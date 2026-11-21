<script module lang="ts">
  import { readable } from 'svelte/store';

  // One timer for all visible age labels; stopped when the last label unmounts.
  const relativeTimeClock = readable(Date.now(), (set) => {
    set(Date.now());
    const timer = setInterval(() => set(Date.now()), 30_000);
    return () => clearInterval(timer);
  });
</script>

<script lang="ts">
  import { relativeTime } from '../app/relative-time';
  let { timestamp, lowercase = false }: { timestamp: string | null | undefined; lowercase?: boolean } = $props();
  let label = $derived(relativeTime(timestamp, $relativeTimeClock));
  let displayedLabel = $derived(label && lowercase ? label.toLowerCase() : label);
</script>

{#if timestamp && label}
  <time datetime={timestamp} title={new Date(timestamp).toLocaleString()}>{displayedLabel}</time>
{/if}
