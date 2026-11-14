<script lang="ts" generics="T">
  import type { Snippet } from 'svelte';
  import { untrack } from 'svelte';
  import type { ViewLoader } from '../app/view-loader.svelte';
  import Notice from './Notice.svelte';

  let { view, children }: { view: ViewLoader<T>; children: Snippet<[T]> } = $props();

  $effect(() => {
    const current = view;
    untrack(() => current.load());
  });
</script>

{#if view.component}
  {@render children(view.component)}
{:else if view.failed}
  <div class="view-load-error">
    <Notice variant="unavailable">
      <span class="resource-state-copy">
        <strong>This view could not be loaded.</strong>
        <small>Check your connection and reload the page. Reloading clears unsaved changes.</small>
      </span>
      <div class="view-load-actions">
        <button type="button" onclick={() => window.location.reload()}>Reload page</button>
      </div>
    </Notice>
  </div>
{/if}

<style>
  .view-load-error { padding: 24px; }
  .view-load-actions { display: flex; gap: 8px; margin-top: 12px; }
  .view-load-actions :global(button) { margin-left: 0; }
</style>
