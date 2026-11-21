<script lang="ts">
  import type { Schema } from '../api/types';
  let { profiles = [] }: { profiles?: Schema<'AppliedProfileDto'>[] } = $props();
  const labels: Record<string, string> = { default: 'Defaults', automatic: 'Automatic', startup: 'Startup', selected: 'Selected' };
</script>

{#if profiles.length}
  <ol class="applied-profiles" aria-label="Applied profiles, in application order">
    {#each profiles as profile, index (index)}
      <li><span>{profile.name}</span><small>{labels[profile.source] ?? profile.source}</small></li>
    {/each}
  </ol>
{/if}

<style>
  ol { margin: 0; padding: 0; list-style: none; display: flex; flex-wrap: wrap; gap: 0.4rem 1rem; }
  li { display: inline-flex; align-items: baseline; gap: 0.4rem; font-size: 0.85rem; }
  small { color: var(--muted); font-size: 0.75rem; }
</style>
