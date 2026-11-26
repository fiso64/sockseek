<script lang="ts">
  import Icon from '../Icon.svelte';

  type Props = (
    | { kind?: 'cancel' | 'remove' | 'retry'; label: string }
    | { kind: 'retry-folder'; missingFiles: number }
  ) & {
    disabled?: boolean;
    onclick: () => void;
  };

  let props: Props = $props();
  let retry = $derived(props.kind === 'retry' || props.kind === 'retry-folder');
  let label = $derived(props.kind === 'retry-folder'
    ? `Retry ${props.missingFiles} missing ${props.missingFiles === 1 ? 'file' : 'files'}`
    : props.label);
</script>

<button type="button" class:retry class:remove={props.kind === 'remove'} class:danger={!retry}
  class="button quiet transfer-item-action-button" aria-label={label} title={label}
  data-testid={props.kind === 'retry-folder' ? 'retry-directory' : undefined}
  disabled={props.disabled} onclick={props.onclick}>
  <Icon name={retry ? 'refresh' : props.kind === 'remove' ? 'trash' : 'x'} />
  <!-- Rows show the icon alone; the transfer details dialog shows this text too. -->
  <span class="transfer-item-action-text">{retry ? 'Retry' : props.kind === 'remove' ? 'Remove from history' : 'Cancel'}</span>
</button>
