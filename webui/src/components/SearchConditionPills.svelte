<script lang="ts">
  import { conditionPills } from '../domains/search/applied-conditions';
  import type { SearchConditions } from '../domains/search/config-state';
  import type { SearchResultMode } from '../domains/search/model';

  interface Props {
    mode: SearchResultMode;
    conditions: SearchConditions;
  }

  let { mode, conditions = $bindable() }: Props = $props();
  let pills = $derived(conditionPills(mode, conditions));
</script>

{#if pills.length}
  <div class="result-condition-pills">
    {#each pills as pill}
      <span class="search-condition-pill">{pill.label}<button type="button" aria-label={pill.removeLabel} onclick={pill.remove}>×</button></span>
    {/each}
  </div>
{/if}
