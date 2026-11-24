<script lang="ts">
  import Icon from './Icon.svelte';
  import SplitButton from './SplitButton.svelte';

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

  const options: { value: BulkCancelMode; label: string }[] = [
    { value: 'all', label: 'All' },
    { value: 'queued', label: 'Queued' },
    { value: 'active', label: 'In Progress' },
  ];

  let currentLabel = $derived(options.find((option) => option.value === mode)?.label ?? 'All');
  let buttonLabel = $derived(mode === 'all' ? 'Cancel all' : mode === 'queued' ? 'Cancel queued' : 'Cancel in progress');
</script>

<div class="transfer-bulk-actions">
  <button type="button" class="button action-button" disabled={!canRemoveCompleted || busy} title="Remove successful transfers from history" onclick={onremovecompleted}>
    <Icon name="trash" />
    <span>Remove all completed</span>
  </button>

  <SplitButton label={buttonLabel} icon="x" disabled={!canCancel || busy} menuDisabled={busy} onclick={oncancel}
    menuLabel={`Choose cancel scope. Current: ${currentLabel}`}
    items={options.map(option => ({ id: option.value, label: option.label, checked: mode === option.value, onselect: () => onmodechange(option.value) }))} />
</div>
