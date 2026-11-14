<script lang="ts">
  import Icon from '../Icon.svelte';

  interface Props {
    kind?: 'cancel' | 'remove';
    label: string;
    disabled?: boolean;
    onclick: () => void;
  }

  let { kind = 'cancel', label, disabled = false, onclick }: Props = $props();
</script>

<button type="button" class:remove={kind === 'remove'} class="transfer-item-action-button" aria-label={label} title={label} {disabled} {onclick}>
  <Icon name={kind === 'remove' ? 'trash' : 'x'} />
</button>
