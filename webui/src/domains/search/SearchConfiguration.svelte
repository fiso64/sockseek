<script lang="ts">
  import { untrack } from 'svelte';
  import { daemonApi } from '../../api/client';
  import SearchConfigPanel from '../../components/SearchConfigPanel.svelte';
  import LoadingIndicator from '../../components/LoadingIndicator.svelte';
  import { searchJobDraft, type SearchDraft } from './model';
  import { createEmptySearchConditions, type SearchConditions } from './config-state';
  import { readSearchConditions, searchConditionsPatch, searchExpectedLength, mergeSearchSettings } from './config-mapping';

  let { draft, active, conditions = $bindable(createEmptySearchConditions()), resolved = $bindable(false), customized = $bindable(false), onchange, onvalidation, onclose }: {
    draft: SearchDraft;
    active: boolean;
    conditions?: SearchConditions;
    resolved?: boolean;
    customized?: boolean;
    onchange: (value: SearchDraft) => void;
    onvalidation: (message: string | null, pending: boolean) => void;
    onclose: () => void;
  } = $props();
  let baseline = $state<SearchConditions | null>(null);
  let defaults = $state<SearchConditions | null>(null);
  let initial = $state<SearchDraft | null>(null);
  let loading = $state(false);
  let displayedMode = $state<SearchDraft['resultMode'] | null>(null);
  let error = $state<string | null>(null);
  let retry = $state(0);
  let loadedKey: string | null = null;
  let validationKey: string | null = null;
  // Split and merged fields share a key; edits to the text or search mode still
  // resolve again because auto-profiles can depend on the search intent.
  let queryKey = $derived(JSON.stringify([draft.resultMode, draft.mode === 'split'
    ? (draft.artist && draft.title ? `${draft.artist} - ${draft.title}` : draft.artist || draft.title)
    : draft.query.replace(' — ', ' - ')]));

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
    const timer = setTimeout(() => { void (async () => {
      const request = { job: searchJobDraft(snapshot) };
      const [response, defaultResponse] = await Promise.all([
        daemonApi.resolveEffectiveSettings({
          ...request,
          options: snapshot.searchSettings ? { downloadSettings: { search: snapshot.searchSettings } } : undefined,
        }, controller.signal),
        snapshot.searchSettings ? daemonApi.resolveEffectiveSettings(request, controller.signal).catch(() => null) : Promise.resolve(null),
      ]);
      if (controller.signal.aborted) return;
      const search = response.settings.values.search;
      if (!search) throw new Error('The daemon did not return resolved search settings.');
      const defaultSearch = snapshot.searchSettings ? defaultResponse?.settings.values.search : search;
      defaults = defaultSearch ? readSearchConditions(defaultSearch) : null;
      conditions = readSearchConditions(search, snapshot.expectedLength);
      baseline = readSearchConditions(search, snapshot.expectedLength);
      initial = snapshot;
      displayedMode = snapshot.resultMode;
      resolved = true;
      loadedKey = key;
    })().catch((cause: unknown) => {
      if (controller.signal.aborted) return;
      error = cause instanceof Error ? cause.message : 'Search configuration unavailable.';
      onvalidation(error, false);
    }).finally(() => { if (!controller.signal.aborted) loading = false; }); }, 0);
    return () => { clearTimeout(timer); controller.abort(); };
  });

  $effect(() => {
    if (baseline && defaults) customized = JSON.stringify(conditions) !== JSON.stringify(defaults);
    else if (baseline) customized = Boolean(draft.expectedLength || (draft.searchSettings && Object.keys(draft.searchSettings).length));
    else if (!draft.searchSettings && !draft.expectedLength) customized = false;
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
{#if loading || baseline}
  <div class="configuration-content">
    {#if displayedMode}
      <div class:waiting={loading} inert={loading}>
        <SearchConfigPanel mode={displayedMode} bind:conditions {onclose} loadDefaults={async signal => {
          const response = await daemonApi.resolveEffectiveSettings({ job: searchJobDraft(draft) }, signal);
          const search = response.settings.values.search;
          if (!search) throw new Error('The daemon did not return resolved search settings.');
          return readSearchConditions(search, draft.expectedLength);
        }} />
      </div>
    {/if}
    {#if loading}
      <div class:overlay={displayedMode !== null}><LoadingIndicator label="Resolving search configuration" /></div>
    {/if}
  </div>
{/if}

<style>
  .configuration-content { position: relative; }
  .waiting { visibility: hidden; }
  .overlay { position: absolute; inset: 0; display: grid; place-items: center; }
</style>
