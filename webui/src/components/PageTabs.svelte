<script lang="ts" generics="T extends string">
  import { tick } from 'svelte';

  let { id, label, items, value, onchange }: {
    id: string;
    label: string;
    items: readonly { id: T; label: string }[];
    value: T;
    onchange: (value: T) => void;
  } = $props();

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

<div class="page-tabs" role="tablist" aria-label={label}>
  {#each items as item (item.id)}
    <button type="button" role="tab" id={`${id}-${item.id}`} aria-selected={value === item.id}
      aria-controls={`${id}-panel`} tabindex={value === item.id ? 0 : -1} class:active={value === item.id}
      onkeydown={keydown} onclick={() => onchange(item.id)}>{item.label}</button>
  {/each}
</div>

<style>
  .page-tabs { position: sticky; top: 0; z-index: 2; background: var(--bg); display: flex; gap: 20px; margin-bottom: 16px; border-bottom: 1px solid var(--border); overflow-x: auto; }
  button { flex-shrink: 0; padding: 10px 0; border: 0; border-bottom: 2px solid transparent; background: transparent; color: var(--muted); font-size: .85rem; }
  button:hover { color: var(--text); }
  button.active { color: var(--text); border-bottom-color: var(--accent); }
  @media (max-width: 600px) { .page-tabs { gap: 12px; } }
</style>
