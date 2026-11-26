<script module lang="ts">
  /** For a page that is only rendering, not fetching: most renders finish sooner. */
  export const RENDER_SPINNER_DELAY_MS = 150;
</script>

<script lang="ts">
  let { label = 'Loading', blocking = false, compact = false, delayMs = 0 }:
    { label?: string; blocking?: boolean; compact?: boolean; delayMs?: number } = $props();
</script>

<!-- The delay is a CSS animation rather than a timer, so the spinner still
     appears while the page that replaces it is busy mounting. -->
<div class="loading-indicator" class:blocking class:compact class:delayed={delayMs > 0}
  style:--loading-delay={delayMs ? `${delayMs}ms` : undefined} role="status" aria-label={label}>
  <span class="resource-state-dot" aria-hidden="true"></span>
</div>
