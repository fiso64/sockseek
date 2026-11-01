<script lang="ts">
  import Icon from './Icon.svelte';
  interface Props { value: string; placeholder?: string; ariaLabel?: string; }
  let { value = $bindable(), placeholder = 'Filter…', ariaLabel = 'Filter items' }: Props = $props();
</script>

<label class="result-filter-control">
  <Icon name="search" class="result-filter-icon" />
  <input bind:value {placeholder} aria-label={ariaLabel} />
  {#if value}<button type="button" aria-label="Clear filter" onclick={() => (value = '')}>×</button>{/if}
</label>
