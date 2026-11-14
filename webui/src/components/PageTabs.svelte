<script lang="ts" generics="T extends string">
  import { tick } from 'svelte';

  let { id, label, items, value, variant = 'underline', onchange }: {
    id: string;
    label: string;
    items: readonly { id: T; label: string; customized?: boolean }[];
    value: T;
    variant?: 'underline' | 'segmented';
    onchange: (value: T) => void;
  } = $props();

  let tabList: HTMLDivElement;
  $effect(() => {
    const selected = `${id}-${value}`;
    items;
    void tick().then(() => {
      const active = document.getElementById(selected);
      if (!active || !tabList) return;
      // Keep programmatically selected tabs visible without scrolling the page.
      const left = active.getBoundingClientRect().left - tabList.getBoundingClientRect().left;
      const right = left + active.offsetWidth;
      if (left < 0) tabList.scrollLeft += left;
      else if (right > tabList.clientWidth) tabList.scrollLeft += right - tabList.clientWidth;
    });
  });

  function keydown(event: KeyboardEvent): void {
    if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
    event.preventDefault();
    const current = items.findIndex(item => item.id === value);
    const index = event.key === 'Home' ? 0 : event.key === 'End' ? items.length - 1
      : (current + (event.key === 'ArrowRight' ? 1 : -1) + items.length) % items.length;
    const next = items[index];
    if (!next) return;
    onchange(next.id);
    // A page may remount its contents on navigation; focus the replacement tab.
    void tick().then(() => document.getElementById(`${id}-${next.id}`)?.focus());
  }
</script>

<div bind:this={tabList} class="page-tabs" class:segmented-tabs={variant === 'segmented'} role="tablist" aria-label={label}>
  {#each items as item (item.id)}
    <button type="button" role="tab" id={`${id}-${item.id}`} aria-selected={value === item.id}
      aria-controls={`${id}-panel`} tabindex={value === item.id ? 0 : -1} class:active={value === item.id}
      onkeydown={keydown} onclick={() => onchange(item.id)}>
      {item.label}
      {#if item.customized}<i class="settings-disclosure-customized" aria-hidden="true"></i>{/if}
    </button>
  {/each}
</div>

<style>
  .page-tabs { position: sticky; top: 0; z-index: 2; margin-bottom: 16px; max-width: 100%; overflow-x: auto; }
  .page-tabs:not(.segmented-tabs) { background: var(--page-tabs-background, var(--bg)); display: flex; gap: 20px; border-bottom: 1px solid var(--border); }
  button { display: inline-flex; align-items: center; gap: 7px; flex-shrink: 0; }
  .page-tabs:not(.segmented-tabs) button { padding: 10px 0; border: 0; border-bottom: 2px solid transparent; background: transparent; color: var(--muted); font-size: .85rem; }
  .page-tabs:not(.segmented-tabs) button:hover { color: var(--text); }
  .page-tabs:not(.segmented-tabs) button.active { color: var(--text); border-bottom-color: var(--accent); }
  @media (max-width: 600px) { .page-tabs:not(.segmented-tabs) { gap: 12px; } }
</style>
