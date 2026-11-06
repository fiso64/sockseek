<script lang="ts">
  import type { Snippet } from 'svelte';
  import Icon from './Icon.svelte';

  interface Props {
    title: string;
    summary: string;
    open: boolean;
    bodyId?: string;
    customized?: boolean;
    children: Snippet;
  }

  let { title, summary, open = $bindable(), bodyId, customized = false, children }: Props = $props();
</script>

<div class="settings-disclosure">
  <button
    type="button"
    class="settings-disclosure-toggle"
    aria-expanded={open}
    aria-controls={bodyId}
    onclick={() => (open = !open)}
  >
    <span class="settings-disclosure-label"><Icon name="settings" /><strong>{title}</strong></span>
    <span class="settings-disclosure-summary">
      <small>{summary}</small>
      {#if customized}<i class="settings-disclosure-customized" aria-hidden="true"></i>{/if}
    </span>
  </button>
  {#if open}
    <div class="settings-disclosure-body" id={bodyId}>
      {@render children()}
    </div>
  {/if}
</div>
