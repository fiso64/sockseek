<script lang="ts">
  import Icon from './Icon.svelte';
</script>

{#if document.documentElement.hasAttribute('data-docs')}
  <a class="button quiet auth-button docs-link" href="/docs/" title="Open documentation">
    <Icon name="help" />
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
    .docs-link { min-height: 44px; padding-inline: 6px; }
  }
  .docs-link:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
</style>
