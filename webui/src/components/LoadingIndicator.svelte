<script lang="ts">
  let { label = 'Loading', blocking = false }: { label?: string; blocking?: boolean } = $props();
</script>

<div class="loading-indicator" class:blocking role="status" aria-label={label}>
  <span class="resource-state-dot" aria-hidden="true"></span>
</div>
