<script lang="ts">
  import { relativeTime } from '../app/relative-time';
  import { relativeTimeClock } from '../app/relative-time-clock';
  let { timestamp, lowercase = false }: { timestamp: string | null | undefined; lowercase?: boolean } = $props();
  let label = $derived(relativeTime(timestamp, $relativeTimeClock));
  let displayedLabel = $derived(label && lowercase ? label.toLowerCase() : label);
</script>

{#if timestamp && label}
  <time datetime={timestamp} title={new Date(timestamp).toLocaleString()}>{displayedLabel}</time>
{/if}
