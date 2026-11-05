<script lang="ts">
  import { untrack } from 'svelte';
  import { daemonApi } from '../../api/client';
  import SearchConfigPanel from '../../components/SearchConfigPanel.svelte';
  import { searchJobDraft, type SearchDraft } from './model';
  import { createEmptySearchConditions, type SearchConditions } from './config-state';
  import { readSearchConditions, searchConditionsPatch, searchExpectedLength, mergeSearchSettings } from './config-mapping';

  let { draft, active, conditions = $bindable(createEmptySearchConditions()), resolved = $bindable(false), onchange, onvalidation, onclose }: {
    draft: SearchDraft;
    active: boolean;
    conditions?: SearchConditions;
    resolved?: boolean;
    onchange: (value: SearchDraft) => void;
    onvalidation: (message: string | null, pending: boolean) => void;
    onclose: () => void;
  } = $props();
  let baseline = $state<SearchConditions | null>(null);
  let initial = $state<SearchDraft | null>(null);
  let loading = $state(false);
  let error = $state<string | null>(null);
  let retry = $state(0);
  let loadedKey: string | null = null;
  let validationKey: string | null = null;
  // Settings edits do not restart resolution. Query/mode changes do, because
  // auto-profiles and the baseline can depend on the actual search intent.
  let queryKey = $derived(JSON.stringify([draft.resultMode, draft.mode, draft.query, draft.artist, draft.title]));

  $effect(() => {
    const key = queryKey;
    if (validationKey !== key) {
      validationKey = key;
      onvalidation(null, false);
    }
    // Never apply edits using a different query's condition mapping.
    if (loadedKey !== key) {
      baseline = null;
      initial = null;
      resolved = false;
    }
    if (!active) return;
    retry;
    if (untrack(() => loadedKey === key && baseline !== null)) return;
    const snapshot = untrack(() => JSON.parse(JSON.stringify(draft)) as SearchDraft);
    const controller = new AbortController();
    baseline = null;
    resolved = false;
    initial = null;
    error = null;
    loading = true;
    untrack(() => onvalidation('Search configuration is loading.', true));
    const timer = setTimeout(() => { void daemonApi.resolveEffectiveSettings({
      job: searchJobDraft(snapshot),
      options: snapshot.searchSettings ? { downloadSettings: { search: snapshot.searchSettings } } : undefined,
    }, controller.signal).then((response) => {
      if (controller.signal.aborted) return;
      const search = response.settings.values.search;
      if (!search) throw new Error('The daemon did not return resolved search settings.');
      conditions = readSearchConditions(search, snapshot.expectedLength);
      baseline = readSearchConditions(search, snapshot.expectedLength);
      initial = snapshot;
      resolved = true;
      loadedKey = key;
    }).catch((cause: unknown) => {
      if (controller.signal.aborted) return;
      error = cause instanceof Error ? cause.message : 'Search configuration unavailable.';
      onvalidation(error, false);
    }).finally(() => { if (!controller.signal.aborted) loading = false; }); }, 0);
    return () => { clearTimeout(timer); controller.abort(); };
  });

  $effect(() => {
    const current = JSON.stringify(conditions);
    const original = baseline;
    const source = initial;
    if (!original || !source) return;
    untrack(() => {
      try {
        const edited = JSON.parse(current) as SearchConditions;
        const patch = searchConditionsPatch(source.resultMode, original, edited);
        const expectedLength = searchExpectedLength(edited.track.expectedLength);
        error = null;
        onvalidation(null, false);
        onchange({ ...draft, searchSettings: mergeSearchSettings(source.searchSettings, patch), expectedLength });
      } catch (cause) {
        error = cause instanceof Error ? cause.message : 'Invalid search configuration.';
        onvalidation(error, false);
      }
    });
  });
</script>

{#if error}
  <div class="resource-state-notice unavailable" role="alert">
    <span>{error}</span>
    {#if !baseline && !loading}<button type="button" onclick={() => retry++}>Retry</button>{/if}
  </div>
{/if}
{#if loading}
  <div class="search-results-empty" role="status">Resolving search configuration…</div>
{:else if baseline}
  <SearchConfigPanel mode={draft.resultMode} bind:conditions {onclose} />
{/if}
