<script lang="ts">
  import Icon from './Icon.svelte';
  import { anchoredMenu } from '../lib/anchored-menu';

  export type BulkCancelMode = 'all' | 'queued' | 'active';

  interface Props {
    mode: BulkCancelMode;
    canCancel: boolean;
    canRemoveCompleted: boolean;
    busy?: boolean;
    onmodechange: (mode: BulkCancelMode) => void;
    oncancel: () => void;
    onremovecompleted: () => void;
  }

  let { mode, canCancel, canRemoveCompleted, busy = false, onmodechange, oncancel, onremovecompleted }: Props = $props();
  let splitRoot = $state<HTMLDivElement | null>(null);
  let menuOpen = $state(false);
  let root: HTMLDivElement;

  const options: { value: BulkCancelMode; label: string }[] = [
    { value: 'all', label: 'All' },
    { value: 'queued', label: 'Queued' },
    { value: 'active', label: 'In Progress' },
  ];

  let currentLabel = $derived(options.find((option) => option.value === mode)?.label ?? 'All');
  let buttonLabel = $derived(mode === 'all' ? 'Cancel all' : mode === 'queued' ? 'Cancel queued' : 'Cancel in progress');

  function handleWindowPointerDown(event: PointerEvent): void {
    if (!menuOpen || root?.contains(event.target as Node)) return;
    menuOpen = false;
  }

  function handleWindowKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape' && menuOpen) menuOpen = false;
  }

  function selectMode(nextMode: BulkCancelMode): void {
    onmodechange(nextMode);
    menuOpen = false;
  }
</script>

<svelte:window onpointerdown={handleWindowPointerDown} onkeydown={handleWindowKeydown} />

<div class="transfer-bulk-actions" bind:this={root}>
  <button type="button" class="transfer-remove-completed-button" disabled={!canRemoveCompleted || busy} title="Remove succeeded, failed, and cancelled items" onclick={onremovecompleted}>
    <Icon name="trash" />
    <span>Remove all completed</span>
  </button>

  <div class="bulk-cancel-split" bind:this={splitRoot}>
    <button type="button" class="bulk-cancel-main" disabled={!canCancel || busy} onclick={oncancel}>
      <Icon name="x" />
      <span>{buttonLabel}</span>
    </button>
    <button type="button" class="bulk-cancel-menu-button" aria-label={`Choose cancel scope. Current: ${currentLabel}`} aria-haspopup="menu" aria-expanded={menuOpen} disabled={busy} onclick={() => (menuOpen = !menuOpen)}>
      <svg viewBox="0 0 16 16" aria-hidden="true"><path d="m4 6 4 4 4-4" /></svg>
    </button>

    {#if menuOpen}
      <div class="bulk-cancel-menu" role="menu" aria-label="Cancel scope" use:anchoredMenu={{ anchor: splitRoot, align: 'end' }}>
        {#each options as option}
          <button type="button" role="menuitemradio" aria-checked={mode === option.value} class:active={mode === option.value} onclick={() => selectMode(option.value)}>
            <span class="bulk-cancel-check">{mode === option.value ? '✓' : ''}</span>
            <span>{option.label}</span>
          </button>
        {/each}
      </div>
    {/if}
  </div>
</div>
