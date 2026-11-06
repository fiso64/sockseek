<script lang="ts">
  import Icon from './Icon.svelte';
  import type { Snippet } from 'svelte';
  interface Props { value: string; placeholder?: string; ariaLabel?: string; children?: Snippet; }
  let { value = $bindable(), placeholder = 'Filter…', ariaLabel = 'Filter items', children }: Props = $props();
</script>

<div class="result-filter-control">
  <Icon name="search" class="result-filter-icon" />
  <input bind:value {placeholder} aria-label={ariaLabel} />
  {#if value}<button type="button" aria-label="Clear filter" onclick={() => (value = '')}>×</button>{/if}
  {@render children?.()}
</div>
