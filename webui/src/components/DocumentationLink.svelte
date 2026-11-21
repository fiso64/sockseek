<script lang="ts">
  import Icon from './Icon.svelte';

  let { quiet = false }: { quiet?: boolean } = $props();
</script>

{#if document.documentElement.hasAttribute('data-docs')}
  <a class="button auth-button docs-link" class:quiet href="docs/" aria-label="Documentation" title="Documentation">
    <Icon name="book" />
    <span class="full-label">Documentation</span><span class="short-label">Help</span>
  </a>
{/if}

<style>
  .docs-link {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: 6px;
    --app-icon-size: 16px;
    text-decoration: none;
  }
  .short-label { display: none; }
  @media (max-width: 600px) {
    .full-label { display: none; }
    .short-label { display: inline; }
    .docs-link.quiet { min-height: 44px; padding-inline: 6px; }
  }
  .docs-link:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
</style>
