<script lang="ts">
  import ProfilePicker from '../../components/ProfilePicker.svelte';
  import LoadingIndicator from '../../components/LoadingIndicator.svelte';
  import { onMount, untrack } from 'svelte';
  import Icon from '../../components/Icon.svelte';
  import ExpandableSettingsPanel from '../../components/ExpandableSettingsPanel.svelte';
  import DownloadOptionsPanel from '../../components/DownloadOptionsPanel.svelte';
  import SearchConfigPanel from '../../components/SearchConfigPanel.svelte';
  import type { SearchDraft } from '../search/model';
  import type { SearchConditions } from '../search/config-state';
  import { readSearchConditions, rebaseSearchConditions, searchConditionsPatchForFamily, searchExpectedLength, mergeSearchSettings } from '../search/config-mapping';
  import { buildDownloadOptionsPatch, downloadOptionsFromResolved, profileSettingsSections, rebaseOptions, changedOptions, type DownloadOptions } from './download-options';
  import JobTypeNav, { jobChoiceMetadata } from './JobTypeNav.svelte';
  import JobInputs from './JobInputs.svelte';
  import CsvMapping from './CsvMapping.svelte';
  import ImportOptionsPanel from './ImportOptions.svelte';
  import { buildImportPatch, createImportOptions, importOptionsFromResolved, type ImportOptions } from './import-options';
  import type { Schema } from '../../api/types';
  import { daemonApi } from '../../api/client';
  import { DaemonApiError } from '../../api/http';
  import { PreviewController, type PreviewState } from './preview-controller';
  import { selectedPreviewCount, selectedSubtreeCount, toggleSubtree, type SubtreeRule } from './preview-selection';
  import { buildJobPreviewRequest, buildJobSettingsRequest, createNewJobDraft, newJobDraftFromSearch, type NewJobChoice, type NewJobDraft } from './submission';
  import { jobKindIcon, jobKindLabel } from './kind';

  let { initialSearch = null, onstarted, oncommittingchange }: { initialSearch?: SearchDraft | null; onstarted: () => void; oncommittingchange: (committing: boolean) => void } = $props();
  const initial = untrack(() => initialSearch);
  let draft = $state(initial ? newJobDraftFromSearch(initial) : createNewJobDraft());
  const initialChoice = untrack(() => draft.choice);
  let carriedSearchSettings = $derived(draft.choice === initialChoice ? initial?.searchSettings : undefined);
  let choiceMetadata = $derived(jobChoiceMetadata(draft.choice));
  let profileNames = $state<string[]>([]);
  let appliedProfiles = $state<Schema<'AppliedProfileDto'>[]>([]);
  let profileSections = $state(profileSettingsSections({}));
  let importBaseline = $state<ImportOptions | null>(null);
  let csvBaseline = $state<Schema<'CsvSettingsPatchDto'> | null>(null);
  let downloadOpen = $state(false);
  let filteringOpen = $state(false);
  let rankingOpen = $state(false);
  let csvMappingOpen = $state(false);
  let importOptionsOpen = $state(false);
  type ConditionFamily = 'track' | 'album' | 'mixed';
  type ConditionSession = { baseline: SearchConditions; values: SearchConditions };
  let conditionSessions = $state<Record<ConditionFamily, ConditionSession | null>>({ track: null, album: null, mixed: null });
  const conditionFingerprints = new Map<ConditionFamily, string>();
  let family = $derived<ConditionFamily>(draft.choice === 'song' ? 'track' : draft.choice === 'album' ? 'album' : 'mixed');
  let conditionSession = $derived(conditionSessions[family]);
  let filteringCustomized = $derived(profileSections.filtering || Boolean(conditionSession && JSON.stringify({ common: conditionSession.values.common, track: conditionSession.values.track, album: conditionSession.values.album })
    !== JSON.stringify({ common: conditionSession.baseline.common, track: conditionSession.baseline.track, album: conditionSession.baseline.album })));
  let rankingCustomized = $derived(profileSections.ranking || Boolean(conditionSession && JSON.stringify(conditionSession.values.ranking) !== JSON.stringify(conditionSession.baseline.ranking)));
  let downloadValues = $state<DownloadOptions | null>(null);
  let downloadBaseline = $state<DownloadOptions | null>(null);
  let settingsLoading = $state(false);
  let settingsError = $state<Error | null>(null);
  let settingsRetry = $state(0);
  let downloadCapabilities = $derived({ albumFolderEnrichment: draft.choice !== 'song' && draft.choice !== 'soulseek-link', playlistOutput: ['spotify', 'youtube', 'soundcloud', 'bandcamp', 'musicbrainz', 'csv', 'list'].includes(draft.choice), nameFormat: true });
  let downloadPatch = $derived(downloadValues && downloadBaseline ? buildDownloadOptionsPatch(downloadValues, downloadBaseline, downloadCapabilities) : {});
  let downloadCustomized = $derived(profileSections.download || Object.keys(downloadPatch).length > 0);
  let csvMapping = $state<Schema<'CsvSettingsPatchDto'>>({});
  let importOptions = $state<ImportOptions>(createImportOptions());
  let hasImportOptions = $derived(['spotify', 'youtube', 'soundcloud', 'bandcamp', 'musicbrainz', 'csv', 'list'].includes(draft.choice));
  let fileName = $state('');
  let uploading = $state(false);
  let error = $state<Error | null>(null);
  let preview = $state<PreviewState>({ summary: null, loading: false, branches: new Map(), error: null });
  let automaticStart = false;
  let all = $state(true);
  let selectionRules = $state<SubtreeRule[]>([]);
  let upload: AbortController | null = null;
  const controller = new PreviewController();
  let valid = $derived(draft.choice === 'song' ? Boolean(draft.title.trim())
    : draft.choice === 'album' ? Boolean(draft.album.trim())
    : draft.choice === 'csv' || draft.choice === 'list' ? Boolean(draft.artifactId)
    : draft.choice === 'spotify' && draft.spotifyInput !== 'url' || Boolean(draft.source.trim()));
  let busy = $derived(uploading || settingsLoading || Boolean(settingsError) || preview.loading || preview.summary?.state === 'Planning' || Boolean(preview.committing));
  $effect(() => oncommittingchange(Boolean(preview.committing)));
  let subtreeCounts = $derived(new Map([...preview.branches.values()].flatMap((page) =>
    page.items.map((node) => [node.ref, Number(node.selectableSubtreeCount)] as const))));
  let selectedCount = $derived(selectedPreviewCount(Number(preview.summary?.selectableNodeCount ?? 0), subtreeCounts, all, selectionRules));
  let needsReview = $derived(preview.summary?.state === 'Expired' || preview.error instanceof DaemonApiError
    && (preview.error.code === 'preview-not-found' || preview.error.code === 'preview-conflict'));
  let committable = $derived(!needsReview && (preview.summary?.state === 'Ready' || preview.summary?.state === 'PartiallyReady'));
  let previewTitle = $derived.by(() => {
    if (draft.choice === 'song' || draft.choice === 'album') {
      const subject = (draft.choice === 'song' ? draft.title : draft.album).trim();
      return [draft.artist.trim(), subject].filter(Boolean).join(' — ');
    }
    return fileName || preview.branches.get(null)?.items[0]?.itemName || choiceMetadata.label;
  });
  let previewSourceLabel = $derived.by(() => {
    const count = Number(preview.summary?.selectableNodeCount ?? 0);
    if (draft.choice === 'song' || draft.choice === 'album') return 'Direct job';
    if (draft.choice === 'csv') return `CSV · ${count} ${count === 1 ? 'job' : 'jobs'}`;
    if (draft.choice === 'list') return 'List file · nested sources';
    const sourceLabel = preview.branches.get(null)?.items[0]?.sourceLabel;
    if (sourceLabel) return sourceLabel;
    return choiceMetadata.label;
  });

  function previewNodeTitle(node: Schema<'JobPreviewNodeDto'>): string {
    if (node.kind === 'song' && node.songQuery) {
      const title = [node.songQuery.artist, node.songQuery.title].filter(Boolean).join(' — ');
      if (title) return title;
    }
    if (node.kind === 'album' && node.albumQuery) {
      const title = [node.albumQuery.artist, node.albumQuery.album].filter(Boolean).join(' — ');
      if (title) return title;
    }
    return node.queryText ?? node.itemName ?? jobKindLabel(node.kind);
  }

  function previewNodeDetail(node: Schema<'JobPreviewNodeDto'>, depth: number): string {
    if (node.failureMessage) return node.failureMessage;
    if (depth === 0 && node.kind === 'extract' && (draft.choice === 'csv' || draft.choice === 'list'))
      return 'Import · Uploaded artifact';
    const kind = jobKindLabel(node.kind);
    return node.sourceDetail ? `${kind} · ${node.sourceDetail}` : kind;
  }

  let needsSettings = $derived(downloadOpen || filteringOpen || rankingOpen || importOptionsOpen
    || csvMappingOpen || downloadBaseline !== null || profileNames.length > 0);
  $effect(() => {
    settingsRetry;
    const requestDraft = { ...draft };
    const requestedProfiles = [...profileNames];
    const carriedSettings = carriedSearchSettings;
    const requestFamily: ConditionFamily = requestDraft.choice === 'song' ? 'track' : requestDraft.choice === 'album' ? 'album' : 'mixed';
    if (!needsSettings) return;
    const abort = new AbortController();
    untrack(() => {
      settingsLoading = true;
      settingsError = null;
      void (async () => {
        try {
          const resolved = await daemonApi.resolveEffectiveSettings({ ...buildJobSettingsRequest(requestDraft),
            options: { profileNames: requestedProfiles, ...(carriedSettings ? { downloadSettings: { search: carriedSettings } } : {}) } }, abort.signal);
          if (abort.signal.aborted) return;
          appliedProfiles = resolved.appliedProfiles;
          profileSections = profileSettingsSections(resolved.provenance);
          const nextImport = importOptionsFromResolved(resolved.settings);
          importOptions = rebaseOptions(importBaseline, importOptions, nextImport);
          importBaseline = nextImport;
          const nextCsv = resolved.settings.values.csv ?? {};
          csvMapping = rebaseOptions(csvBaseline, csvMapping, nextCsv);
          csvBaseline = nextCsv;
          const baseline = downloadOptionsFromResolved(resolved.settings);
          if (!resolved.settings.values.search) throw new Error('The daemon did not return resolved search settings.');
          const searchBaseline = readSearchConditions(resolved.settings.values.search, requestDraft.expectedLength);
          const resolvedFamily = requestFamily;
          const previousConditions = conditionSessions[resolvedFamily];
          const nextConditions: ConditionSession = {
            baseline: searchBaseline,
            values: previousConditions
              ? rebaseSearchConditions(previousConditions.baseline, previousConditions.values, searchBaseline)
              : structuredClone(searchBaseline),
          };
          conditionFingerprints.set(resolvedFamily, JSON.stringify(nextConditions.values));
          conditionSessions = { ...conditionSessions, [resolvedFamily]: nextConditions };
          downloadValues = rebaseOptions(downloadBaseline, downloadValues ?? baseline, baseline);
          downloadBaseline = baseline;
        } catch (cause) {
          if (!abort.signal.aborted) settingsError = cause instanceof Error ? cause : new Error(String(cause));
        } finally { if (!abort.signal.aborted) settingsLoading = false; }
      })();
    });
    return () => abort.abort();
  });

  $effect(() => {
    const currentFamily = family;
    const values = conditionSessions[currentFamily]?.values;
    if (!values) return;
    const fingerprint = JSON.stringify(values);
    const previous = conditionFingerprints.get(currentFamily);
    if (previous !== undefined && previous !== fingerprint) untrack(invalidatePreview);
    conditionFingerprints.set(currentFamily, fingerprint);
  });

  onMount(() => {
    const unsubscribe = controller.subscribe((state) => {
      preview = state;
      if (automaticStart && (state.summary?.state === 'Ready' || state.summary?.state === 'PartiallyReady')) {
        automaticStart = false;
        void controller.commit({ mode: 'AllExcept', refs: [] });
      }
      if (state.receipt?.submissionId) onstarted();
    });
    return () => { unsubscribe(); controller.stop(); upload?.abort(); };
  });

  function change(next: NewJobDraft): void {
    if (preview.committing) return;
    controller.stop();
    automaticStart = false;
    preview = { summary: null, loading: false, branches: new Map(), error: null };
    if (next.choice !== draft.choice) { upload?.abort(); uploading = false; fileName = ''; next = { ...next, artifactId: null }; }
    draft = next;
    all = true;
    selectionRules = [];
    error = null;
  }

  function choose(choice: NewJobChoice): void {
    if (preview.committing) return;
    upload?.abort();
    upload = null;
    uploading = false;
    fileName = '';
    filteringOpen = false;
    rankingOpen = false;
    downloadOpen = false;
    csvMappingOpen = false;
    importOptionsOpen = false;
    const next = { ...draft, choice, artifactId: null };
    if (choice === 'song' || choice === 'album') {
      next.artist = '';
      next.title = '';
      next.album = '';
      next.expectedLength = undefined;
    } else if (choice !== 'csv' && choice !== 'list') {
      next.source = '';
      if (choice === 'spotify') next.spotifyInput = 'url';
    }
    change(next);
  }

  function invalidatePreview(): void {
    if (preview.committing) return;
    controller.stop();
    automaticStart = false;
    preview = { summary: null, loading: false, branches: new Map(), error: null };
    all = true;
    selectionRules = [];
    error = null;
  }

  async function chooseFile(file: File): Promise<void> {
    if (preview.committing) return;
    change({ ...draft, artifactId: null });
    upload?.abort();
    const request = new AbortController();
    upload = request;
    fileName = file.name;
    uploading = true;
    try {
      const artifact = await daemonApi.uploadInputArtifact(file, request.signal);
      if (!request.signal.aborted) draft = { ...draft, artifactId: artifact.artifactId };
    } catch (cause) {
      if (!request.signal.aborted) error = cause instanceof Error ? cause : new Error(String(cause));
    } finally { if (upload === request) uploading = false; }
  }

  function plan(start: boolean): void {
    if (busy) return;
    try {
      const settings: Schema<'DownloadSettingsPatchDto'> = { ...downloadPatch.downloadSettings, search: carriedSearchSettings };
      if (conditionSession && draft.choice !== 'soulseek-link') {
        const searchEdits = searchConditionsPatchForFamily(family, conditionSession.baseline, conditionSession.values);
        if (Object.keys(searchEdits).length) settings.search = mergeSearchSettings(settings.search ?? undefined, searchEdits);
        if (family === 'mixed' && conditionSession.values.track.expectedLength !== conditionSession.baseline.track.expectedLength) {
          throw new Error('Expected length belongs to each extracted song; it cannot be applied to every item in a mixed source.');
        }
      }
      if (draft.choice === 'csv') settings.csv = changedOptions(csvMapping, csvBaseline ?? {});
      if (hasImportOptions) settings.extraction = buildImportPatch(importOptions, importBaseline ?? createImportOptions());
      error = null;
      const request = buildJobPreviewRequest(draft, { ...downloadPatch, profileNames: [...profileNames], downloadSettings: settings });
      if (request.job.kind === 'song' && conditionSession) {
        request.job.songQuery.length = searchExpectedLength(conditionSession.values.track.expectedLength) ?? null;
      }
      // Re-extraction may return different nodes. Review the new tree before
      // applying any choices; selections belong only to their original preview.
      all = true;
      selectionRules = [];
      automaticStart = start;
      void controller.create(request);
    } catch (cause) { error = cause instanceof Error ? cause : new Error(String(cause)); }
  }

  function toggle(ref: string, selected: boolean): void {
    if (preview.committing) return;
    selectionRules = toggleSubtree(ref, selected, all, selectionRules);
  }

  function indeterminate(node: HTMLInputElement, value: boolean) {
    node.indeterminate = value;
    return { update(next: boolean) { node.indeterminate = next; } };
  }
</script>

<section class="new-job-composer" aria-label="New job">
  <div class="new-job-layout" inert={preview.committing ? true : undefined}>
    <JobTypeNav value={draft.choice} onchange={choose} />
    <div class="new-job-workspace">
      <div class="new-job-scroll-area">
        <header class="new-job-current-choice"><div><strong>{choiceMetadata.label}</strong><small>{choiceMetadata.detail}</small></div>
          <ProfilePicker value={profileNames} applied={appliedProfiles} onchange={(names) => { profileNames = names; invalidatePreview(); }} />
        </header>
        <div class="new-job-config">
          <JobInputs {draft} {fileName} onchange={change} onfile={(file) => void chooseFile(file)} />
          {#if draft.choice === 'csv'}<CsvMapping profileApplied={profileSections.csv} value={csvMapping} baseline={csvBaseline ?? {}} disabled={settingsLoading || Boolean(settingsError)} bind:open={csvMappingOpen} onchange={(value) => { invalidatePreview(); csvMapping = value; }} />{/if}
          {#if hasImportOptions}<ImportOptionsPanel profileApplied={profileSections.import} value={importOptions} baseline={importBaseline ?? createImportOptions()} disabled={settingsLoading || Boolean(settingsError)} bind:open={importOptionsOpen} onchange={(value) => { invalidatePreview(); importOptions = value; }} />{/if}
          {#if settingsError && !filteringOpen && !rankingOpen && !downloadOpen}
            <p role="alert">{settingsError.message}</p><button type="button" onclick={() => settingsRetry++}>Retry settings</button>
          {/if}
          <section class="new-job-job-settings" aria-label="Job settings">
            {#if draft.choice !== 'soulseek-link'}
            <ExpandableSettingsPanel title="Filtering" summary={filteringCustomized ? 'Custom' : 'Defaults'} customized={filteringCustomized} bind:open={filteringOpen} bodyId="new-job-filtering-options">
              {#if settingsLoading}<LoadingIndicator label="Loading effective settings" />
              {:else if settingsError}<p role="alert">{settingsError.message}</p><button type="button" onclick={() => settingsRetry++}>Retry settings</button>
              {:else if conditionSession}
                <SearchConfigPanel mode={family === 'album' ? 'album' : 'track'} configurationFamily={family} bind:conditions={conditionSession.values} section="conditions" embedded singleItem={family !== 'mixed'} />
              {/if}
            </ExpandableSettingsPanel>
            <ExpandableSettingsPanel title="Ranking" summary={rankingCustomized ? 'Custom' : 'Defaults'} customized={rankingCustomized} bind:open={rankingOpen} bodyId="new-job-ranking-options">
              {#if settingsLoading}<LoadingIndicator label="Loading effective settings" />
              {:else if settingsError}<p role="alert">{settingsError.message}</p><button type="button" onclick={() => settingsRetry++}>Retry settings</button>
              {:else if conditionSession}
                <SearchConfigPanel mode={family === 'album' ? 'album' : 'track'} configurationFamily={family} bind:conditions={conditionSession.values} section="ranking" embedded />
              {/if}
            </ExpandableSettingsPanel>
            {/if}
            <ExpandableSettingsPanel title="Download" summary={downloadCustomized ? 'Custom' : 'Defaults'} customized={downloadCustomized} bind:open={downloadOpen} bodyId="new-job-download-options">
              {#if settingsLoading}<LoadingIndicator label="Loading effective settings" />
              {:else if settingsError}<p role="alert">{settingsError.message}</p><button type="button" onclick={() => settingsRetry++}>Retry settings</button>
              {:else if downloadValues}
                <div oninput={invalidatePreview}>
                  <DownloadOptionsPanel bind:value={downloadValues} capabilities={downloadCapabilities} />
                </div>
                <footer class="search-config-footer embedded">
                  <button type="button" onclick={() => {
                    if (!downloadBaseline) return;
                    downloadValues = { ...downloadBaseline };
                    invalidatePreview();
                  }}>Reset download options</button>
                </footer>
              {/if}
            </ExpandableSettingsPanel>
          </section>
        </div>
        {#if error || preview.error || preview.summary?.state === 'Expired'}<p class="mutation-status rejected" role="alert">{(error ?? preview.error)?.message ?? 'This preview has expired. Review it again before starting.'}</p>{/if}
        {#if uploading}<p role="status">Uploading source file…</p>{/if}
        {#if preview.summary}
          <section class="new-job-preview">
            <header>
              <div><p class="eyebrow">Review</p><h3>{previewTitle}</h3><small>{preview.summary.state === 'Planning' ? 'Planning…' : previewSourceLabel}{#if Number(preview.summary.failedNodeCount) > 0}{' · '}{preview.summary.failedNodeCount} failed{/if}</small></div>
              <button type="button" class="new-job-select-toggle" onclick={() => { all = !all; selectionRules = []; }}>{all ? 'Deselect all' : 'Select all'}</button>
            </header>
            <p class="new-job-preview-note">Review resolves extraction and preprocessing only. Soulseek candidate discovery starts after submission.</p>
            {@render branch(null, 0)}
          </section>
        {/if}
      </div>
      <footer class="new-job-footer">
        {#if preview.summary}
          <span class="new-job-footer-status">{selectedCount} selected</span>
          <div class="new-job-footer-actions">
            {#if needsReview}<button type="button" class="new-job-review-button" disabled={!valid || busy} onclick={() => plan(false)}>Review again</button>{/if}
            <button type="button" class="new-job-start-button" aria-keyshortcuts="Control+Enter" title="Start (Ctrl+Enter)" disabled={!committable || !selectedCount || busy} onclick={() => void controller.commit({ mode: all ? 'AllExcept' : 'Only', refs: [] }, selectionRules)}>{preview.committing ? 'Starting…' : `Start ${selectedCount || ''} ${selectedCount === 1 ? 'job' : 'jobs'}`}</button>
          </div>
        {:else}
          <span class="new-job-footer-status" role="status">{preview.loading ? 'Planning…' : ''}</span>
          <div class="new-job-footer-actions">
            <button type="button" class="new-job-review-button" disabled={!valid || busy} onclick={() => plan(false)}>Review</button>
            <button type="button" class="new-job-start-button" aria-keyshortcuts="Control+Enter" title="Start (Ctrl+Enter)" disabled={!valid || busy} onclick={() => plan(true)}>Start</button>
          </div>
        {/if}
      </footer>
    </div>
  </div>
</section>

{#snippet branch(parent: string | null, depth: number)}
  {@const page = preview.branches.get(parent)}
  <div class:job-preview-tree={depth === 0}>
    {#each page?.items ?? [] as node (node.ref)}
      {@const count = subtreeCounts.get(node.ref) ?? 0}
      {@const selected = selectedSubtreeCount(node.ref, subtreeCounts, all, selectionRules)}
      <div class="job-preview-node" style={`--job-preview-depth:${depth}`}>
        <label class="job-preview-row">
          <input type="checkbox" disabled={!count} checked={selected === count && count > 0} use:indeterminate={selected > 0 && selected < count} onchange={(event) => toggle(node.ref, event.currentTarget.checked)} />
          <span class="job-preview-icon"><Icon name={jobKindIcon(node.kind)} /></span>
          <span class="job-preview-copy"><strong>{previewNodeTitle(node)}</strong><small>{previewNodeDetail(node, depth)}</small></span>
          {#if Number(node.directChildCount) > 0}<span class="job-preview-count">{count} {count === 1 ? 'job' : 'jobs'}</span>{/if}
        </label>
        {#if Number(node.directChildCount) > 0}
          <div class="job-preview-children">{@render branch(node.ref, depth + 1)}</div>
        {/if}
      </div>
    {/each}
    {#if page?.error}<p role="alert">{page.error.message}</p>{/if}
    {#if !page || page.nextCursor || page.error || page.loading}
      <button type="button" disabled={page?.loading || preview.summary?.state === 'Planning'} onclick={() => void controller.loadBranch(parent)}>{page?.loading ? 'Loading…' : page?.error ? 'Retry' : 'Load jobs'}</button>
    {/if}
  </div>
{/snippet}
