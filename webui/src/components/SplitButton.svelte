<script lang="ts">
  import type { AppIconName } from '../app/icons';
  import { anchoredMenu } from '../lib/anchored-menu';
  import Icon from './Icon.svelte';

  /** A default action with further ways to do the same thing behind an arrow.
   * Without alternatives it is an ordinary button. */
  let { label, icon, disabled = false, menuDisabled = disabled, onclick, menuLabel, items = [], testId, class: extraClass = '' }: {
    label: string;
    icon?: AppIconName;
    disabled?: boolean;
    /** The alternatives can stay reachable while the default action is unavailable. */
    menuDisabled?: boolean;
    onclick: () => void;
    menuLabel: string;
    /** Items that carry `checked` form a single choice; the current one is ticked. */
    items?: readonly { id: string; label: string; checked?: boolean; onselect: () => void }[];
    testId?: string;
    class?: string;
  } = $props();
  let root = $state<HTMLSpanElement | null>(null);
  let open = $state(false);

  function choose(select: () => void): void {
    open = false;
    select();
  }
</script>

<svelte:window
  onpointerdown={event => { if (open && !root?.contains(event.target as Node)) open = false; }}
  onkeydown={event => { if (event.key === 'Escape' && open) open = false; }} />

<span class="split-button" bind:this={root}>
  <button type="button" class={`button action-button ${items.length ? 'split-button-main' : ''} ${extraClass}`} data-testid={testId}
    aria-label={label} title={label} {disabled} {onclick}>
    {#if icon}<Icon name={icon} />{/if}<span>{label}</span>
  </button>
  {#if items.length}
    <button type="button" class="button split-button-toggle" data-testid={testId ? `${testId}-menu` : undefined}
      aria-label={menuLabel} aria-haspopup="menu" aria-expanded={open} disabled={menuDisabled} onclick={() => (open = !open)}>
      <svg viewBox="0 0 16 16" aria-hidden="true"><path d="m4 6 4 4 4-4" /></svg>
    </button>
    {#if open}
      <div class="popover-panel split-button-menu" role="menu" aria-label={menuLabel} use:anchoredMenu={{ anchor: root, align: 'end' }}>
        {#each items as item (item.id)}
          <button class="menu-item" class:active={item.checked} type="button" role={item.checked === undefined ? 'menuitem' : 'menuitemradio'}
            aria-checked={item.checked} data-testid={testId ? `${testId}-${item.id}` : undefined} onclick={() => choose(item.onselect)}>
            {#if item.checked !== undefined}<span class="split-button-check">{item.checked ? '✓' : ''}</span>{/if}
            <span>{item.label}</span>
          </button>
        {/each}
      </div>
    {/if}
  {/if}
</span>
