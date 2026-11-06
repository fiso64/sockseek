<script lang="ts">
  interface Props {
    label?: string;
    loadingLabel?: string;
    loading?: boolean;
    disabled?: boolean;
    onclick: () => void;
  }

  let { label = 'Load more', loadingLabel = 'Loading…', loading = false, disabled = false, onclick }: Props = $props();
</script>

<div class="load-more-row">
  <button type="button" disabled={disabled || loading} {onclick}>{loading ? loadingLabel : label}</button>
</div>
