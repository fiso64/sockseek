<script lang="ts">
  import type { Snippet } from 'svelte';
  import Icon from './Icon.svelte';

  let { variant = '', ondismiss, children }: {
    variant?: '' | 'info' | 'degraded' | 'unavailable' | 'loading';
    ondismiss?: () => void;
    children: Snippet;
  } = $props();
</script>

<div class={`resource-state-notice ${variant}`} role={variant === 'unavailable' ? 'alert' : 'status'}>
  <span class="resource-state-dot" aria-hidden="true"></span>
  <div class="notice-content">{@render children()}</div>
  {#if ondismiss}
    <button type="button" class="button quiet notice-dismiss" aria-label="Dismiss notice" onclick={ondismiss}><Icon name="x" /></button>
  {/if}
</div>

<style>
  .notice-content { flex: 1; min-width: 0; overflow-wrap: anywhere; }
  .notice-dismiss { display: grid; place-items: center; flex: 0 0 28px; width: 28px; height: 28px; padding: 0; --app-icon-size: 15px; }
</style>
