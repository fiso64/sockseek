<script lang="ts">
  let { label = 'Loading', blocking = false, compact = false, delayMs = 0 }:
    { label?: string; blocking?: boolean; compact?: boolean; delayMs?: number } = $props();
  let elapsed = $state(false);
  $effect(() => {
    if (!delayMs) return;
    elapsed = false;
    const timer = setTimeout(() => elapsed = true, delayMs);
    return () => clearTimeout(timer);
  });
</script>

{#if !delayMs || elapsed}
  <div class="loading-indicator" class:blocking class:compact role="status" aria-label={label}>
    <span class="resource-state-dot" aria-hidden="true"></span>
  </div>
{/if}
