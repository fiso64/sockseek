<script lang="ts">
  import { keyboardShortcutsBlocked } from '../lib/keyboard';
  interface Props {
    selectedCount: number;
    visible?: boolean;
    floatingLabel?: string;
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
  function downloadByKey(event: KeyboardEvent): void {
    if (event.key !== 'Enter' || !event.ctrlKey || event.repeat || !visible || actionDisabled
      || keyboardShortcutsBlocked(event, true)) return;
    event.preventDefault();
    onaction();
  }
</script>

<svelte:window onkeydown={downloadByKey} />

{#if visible}
  <div class="floating-selection-actions" aria-label="Selection actions">
    {#if detail}<span class="floating-selection-detail">{detail}</span>{/if}
    {#if onselectall}<button type="button" class="floating-select-action" onclick={onselectall}>Select all</button>{/if}
    <button type="button" class="floating-deselect-action" onclick={onclear}>Deselect all</button>
    {#if onoptions}
      <button
        type="button"
        class:active={optionsOpen}
        class:customized={optionsCustomized}
        class="floating-options-action"
        aria-label={optionsLabel}
        title={optionsLabel}
        aria-expanded={optionsOpen}
        onclick={onoptions}
      >
        <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M4 5h12M4 10h12M4 15h12"/><circle cx="8" cy="5" r="1.6"/><circle cx="13" cy="10" r="1.6"/><circle cx="7" cy="15" r="1.6"/></svg>
        {#if optionsCustomized}<i aria-hidden="true"></i>{/if}
      </button>
    {/if}
    <button type="button" class="floating-download-action" aria-keyshortcuts="Control+Enter" aria-label={floatingLabel} disabled={actionDisabled} onclick={onaction}>
      <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M10 3v9M6.5 8.5L10 12l3.5-3.5M4 15.5h12" /></svg>
      <span>{floatingLabel}</span><kbd aria-hidden="true">Ctrl+↵</kbd>
    </button>
  </div>
{/if}
