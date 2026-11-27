<script lang="ts">
  import { conditionPills, rankingPills, type ConditionPill, type PreferenceCondition } from '../domains/search/applied-conditions';
  import type { SearchConditions } from '../domains/search/config-state';
  import type { SearchResultMode } from '../domains/search/model';

  interface Props {
    mode: SearchResultMode;
    conditions: SearchConditions;
    /** Preferred conditions that currently rank these results. */
    ranking?: readonly PreferenceCondition[];
    /** Opens the pill's setting for editing. */
    onedit?: (pill: ConditionPill) => void;
  }

  let { mode, conditions = $bindable(), ranking = [], onedit }: Props = $props();
  let pills = $derived([
    ...conditionPills(mode, conditions).map(pill => ({ ...pill, ranking: false })),
    ...rankingPills(ranking, conditions).map(pill => ({ ...pill, ranking: true })),
  ]);
</script>

{#if pills.length}
  <div class="result-condition-pills">
    {#each pills as pill, index}
      {#if pill.ranking && pills[index - 1]?.ranking}<span class="ranking-order" aria-hidden="true" title="Ranks above the next">›</span>{/if}
      <span class="badge search-condition-pill" class:ranking={pill.ranking}>{#if onedit}<button type="button" class="search-condition-pill-edit" data-action="edit-condition" onclick={() => onedit(pill)}>{pill.label}</button>{:else}{pill.label}{/if}<button type="button" class="button quiet" data-action="remove-condition" aria-label={pill.removeLabel} onclick={pill.remove}>×</button></span>
    {/each}
  </div>
{/if}
