<script lang="ts">
  import { keyboardShortcutsBlocked } from '../lib/keyboard';
  interface Props {
    selectedCount: number;
    visible?: boolean;
    floatingLabel?: string;
    countPending?: boolean;
    detail?: string;
    actionDisabled?: boolean;
    optionsLabel?: string;
    optionsOpen?: boolean;
    optionsCustomized?: boolean;
    onclear: () => void;
    onselectall?: () => void;
    onoptions?: () => void;
    onaction?: () => void;
  }

  let {
    selectedCount,
    visible = selectedCount > 0,
    floatingLabel = `Download ${selectedCount}`,
    countPending = false,
    detail,
    actionDisabled = false,
    optionsLabel = 'Download options',
    optionsOpen = false,
    optionsCustomized = false,
    onclear,
    onselectall,
    onoptions,
    onaction = () => {},
  }: Props = $props();

  let canSelectAll = $derived(Boolean(onselectall));
  let display = $state.raw({ visible: false, label: '', selectAll: false, counting: false });
  $effect(() => {
    const next = { visible, label: floatingLabel, selectAll: canSelectAll, counting: countPending };
    // Keep the last settled layout during a quick recount. Actions use the live state.
    if (visible && countPending) {
      const timer = setTimeout(() => display = next, 300);
      return () => clearTimeout(timer);
    }
    display = next;
  });

  let downloadDisabled = $derived(actionDisabled || countPending);
  function downloadByKey(event: KeyboardEvent): void {
    if (event.key !== 'Enter' || !event.ctrlKey || event.repeat || !visible || downloadDisabled
      || keyboardShortcutsBlocked(event, true)) return;
    event.preventDefault();
    onaction();
  }
</script>

<svelte:window onkeydown={downloadByKey} />

{#if display.visible}
  <div class="floating-selection-actions" aria-label="Selection actions">
    {#if detail}<span class="floating-selection-detail">{detail}</span>{/if}
    {#if display.selectAll}<button type="button" class="button floating-select-action" onclick={onselectall}>Select all</button>{/if}
    <button type="button" class="button floating-deselect-action" onclick={onclear}>Deselect all</button>
    {#if onoptions}
      <button
        type="button"
        class:customized={optionsCustomized}
        class="button floating-options-action"
        aria-label={optionsLabel}
        title={optionsLabel}
        aria-expanded={optionsOpen}
        onclick={onoptions}
      >
        <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M4 5h12M4 10h12M4 15h12"/><circle cx="8" cy="5" r="1.6"/><circle cx="13" cy="10" r="1.6"/><circle cx="7" cy="15" r="1.6"/></svg>
        {#if optionsCustomized}<i aria-hidden="true"></i>{/if}
      </button>
    {/if}
    <button type="button" class="button primary floating-download-action" class:recounting={countPending && !display.counting && !actionDisabled}
      aria-keyshortcuts="Control+Enter" aria-busy={countPending || undefined} disabled={downloadDisabled} onclick={onaction}>
      <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M10 3v9M6.5 8.5L10 12l3.5-3.5M4 15.5h12" /></svg>
      <span>{display.label}</span><kbd aria-hidden="true">Ctrl+↵</kbd>
    </button>
  </div>
{/if}

<style>
  .floating-download-action { font-variant-numeric: tabular-nums; }
  .floating-download-action.recounting:disabled { opacity: 1; }
</style>
