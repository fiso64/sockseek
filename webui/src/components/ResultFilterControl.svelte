<script lang="ts">
  import Icon from './Icon.svelte';
  import { tick, type Snippet } from 'svelte';
  interface Props { value: string; placeholder?: string; ariaLabel?: string; collapsible?: boolean; children?: Snippet; }
  let { value = $bindable(), placeholder = 'Filter…', ariaLabel = 'Filter items', collapsible = false, children }: Props = $props();
  let expanded = $state(false);
  let collapsed = $derived(collapsible && !expanded);
  let trigger = $state<HTMLButtonElement>();
  let input = $state<HTMLInputElement>();

  async function setExpanded(open: boolean) {
    expanded = open;
    if (!open) value = '';
    await tick();
    (open ? input : trigger)?.focus();
  }

  function keydown(event: KeyboardEvent) {
    if (expanded && event.key === 'Escape') {
      event.stopPropagation();
      event.preventDefault();
      void setExpanded(false);
    }
  }
</script>

<div class="result-filter-control" class:field-group={!collapsed} class:button={collapsed} class:collapsible class:expanded>
  {#if collapsible}
    <button type="button" class="filter-toggle" data-testid="expand-result-filter" bind:this={trigger}
      title={expanded ? 'Collapse filter' : ariaLabel} aria-label={ariaLabel} aria-expanded={expanded}
      onkeydown={keydown} onclick={() => setExpanded(!expanded)}>
      <Icon name="search" class="result-filter-icon" />
    </button>
  {:else}
    <Icon name="search" class="result-filter-icon" />
  {/if}
  {#if !collapsed}
    <input bind:this={input} bind:value {placeholder} aria-label={ariaLabel} onkeydown={keydown} />
    {#if value}
      <button type="button" aria-label="Clear filter" onkeydown={keydown} onclick={() => { value = ''; input?.focus(); }}>×</button>
    {:else if collapsible}
      <button type="button" aria-label="Collapse filter" onkeydown={keydown} onclick={() => setExpanded(false)}>×</button>
    {/if}
    {@render children?.()}
  {/if}
</div>
