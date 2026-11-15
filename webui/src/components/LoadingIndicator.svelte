<script lang="ts">
  let { label = 'Loading', blocking = false, compact = false }: { label?: string; blocking?: boolean; compact?: boolean } = $props();
</script>

<div class="loading-indicator" class:blocking class:compact role="status" aria-label={label}>
  <span class="resource-state-dot" aria-hidden="true"></span>
</div>
