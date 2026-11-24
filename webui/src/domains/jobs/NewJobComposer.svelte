<script lang="ts">
  // TODO [ARCHITECTURE]: This dialog keeps five separate baseline/value pairs
  // (download, import, CSV and one per condition family), each rebased by hand
  // when settings resolve, and then reassembles a settings patch from them in
  // plan(). Settings it is opened with (`carriedSettings`) need a client-side
  // merge that repeats the daemon's patch rules (mergeSettingsPatch). Hold one
  // settings patch plus the resolved settings instead, and have every panel
  // read and write that patch; do this before adding more options here.
  import ProfilePicker from '../../components/ProfilePicker.svelte';
  import LoadingIndicator from '../../components/LoadingIndicator.svelte';
  import { onMount, tick, untrack } from 'svelte';
  import Icon from '../../components/Icon.svelte';
  import Modal from '../../components/Modal.svelte';
  import PageTabs from '../../components/PageTabs.svelte';
  import DownloadOptionsPanel from '../../components/DownloadOptionsPanel.svelte';
  import SearchConfigPanel from '../../components/SearchConfigPanel.svelte';
  import type { SearchConditions } from '../search/config-state';
  import { readSearchConditions, rebaseSearchConditions, searchConditionsPatchForFamily, searchExpectedLength, mergeSearchSettings } from '../search/config-mapping';
  import { buildDownloadOptionsPatch, downloadOptionsFromResolved, mergeSettingsPatch, profileSettingsSections, rebaseOptions, changedOptions, type DownloadOptions } from './download-options';
  import JobTypeNav, { jobChoiceMetadata } from './JobTypeNav.svelte';
  import JobInputs from './JobInputs.svelte';
  import CsvMapping from './CsvMapping.svelte';
  import ImportOptionsPanel from './ImportOptions.svelte';
  import { buildImportPatch, createImportOptions, importOptionsCustomized, importOptionsFromResolved, type ImportOptions } from './import-options';
  import type { Schema } from '../../api/types';
  import { daemonApi } from '../../api/client';
  import { PreviewController, previewNeedsReview, type PreviewState } from './preview-controller';
  import { projectPreviewSelection, toggleSubtree, type SubtreeRule } from './preview-selection';
  import { applyProfileInput, profileInputDefaults, buildJobPreviewRequest, buildJobSettingsRequest, createNewJobDraft, type NewJobChoice, type NewJobDraft, type NewJobStart } from './submission';
  import { jobKindIcon, jobKindLabel } from './kind';
  import { basename } from '../presentation/items';

  let { connected, start = null, onclose, onstarted, oncommittingchange }: { connected: boolean; onclose: () => void; start?: NewJobStart | null; onstarted: (rootJobId: string) => void; oncommittingchange: (committing: boolean) => void } = $props();
  const initial = untrack(() => start);
  let draft = $state(initial ? { ...initial.draft } : createNewJobDraft());
  const initialChoice = untrack(() => draft.choice);
  // Settings the dialog was opened with belong to that kind of job. They are
  // its starting point: the panels show them, and edits are made on top.
  let carriedSettings = $derived(draft.choice === initialChoice ? initial?.settings : undefined);
  let choiceMetadata = $derived(jobChoiceMetadata(draft.choice));
  let profileNames = $state<string[]>([...(initial?.profileNames ?? [])]);
  let appliedProfiles = $state<Schema<'AppliedProfileDto'>[]>([]);
  let profileSections = $state(profileSettingsSections({}));
  let importBaseline = $state<ImportOptions | null>(null);
  let csvBaseline = $state<Schema<'CsvSettingsPatchDto'> | null>(null);
  type OptionsTab = 'import' | 'filtering' | 'ranking' | 'download' | 'review';
  let optionsTab = $state<OptionsTab>('download');
  let preview = $state<PreviewState>({ summary: null, loading: false, branches: new Map(), error: null });
  let conditionPanel = $state<ReturnType<typeof SearchConfigPanel>>();
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
  let reviewedDownloadOptions: DownloadOptions | null = null;
  let settingsLoading = $state(false);
  let settingsReady: Promise<void> = Promise.resolve();
  let preparing = $state(false);
  let stopped = false;
  let settingsError = $state<Error | null>(null);
  let settingsRetry = $state(0);
  let downloadCapabilities = $derived({ albumFolderEnrichment: draft.choice !== 'song' && draft.choice !== 'soulseek-link', playlistOutput: ['spotify', 'youtube', 'soundcloud', 'bandcamp', 'musicbrainz', 'csv', 'list'].includes(draft.choice), nameFormat: true });
  let downloadPatch = $derived(downloadValues && downloadBaseline ? buildDownloadOptionsPatch(downloadValues, downloadBaseline, downloadCapabilities) : {});
  let downloadCustomized = $derived(profileSections.download || Object.keys(downloadPatch).length > 0);
  let csvMapping = $state<Schema<'CsvSettingsPatchDto'>>({});
  let importOptions = $state<ImportOptions>(createImportOptions());
  let hasImportOptions = $derived(['spotify', 'youtube', 'soundcloud', 'bandcamp', 'musicbrainz', 'csv', 'list'].includes(draft.choice));
  let optionTabs = $derived([
    ...(hasImportOptions ? [{ id: 'import' as const, label: 'Import options',
      customized: profileSections.import || importOptionsCustomized(importOptions, importBaseline ?? createImportOptions())
        || draft.choice === 'csv' && (profileSections.csv || Object.keys(changedOptions(csvMapping, csvBaseline ?? {})).length > 0) }] : []),
    ...(draft.choice !== 'soulseek-link' ? [
      { id: 'filtering' as const, label: 'Filtering', customized: filteringCustomized },
      { id: 'ranking' as const, label: 'Ranking', customized: rankingCustomized },
    ] : []),
    { id: 'download' as const, label: 'Download', customized: downloadCustomized },
    ...(preview.summary ? [{ id: 'review' as const, label: 'Review' }] : []),
  ]);
  let fileName = $state('');
  let uploading = $state(false);
  let error = $state<Error | null>(null);
  let automaticStart = false;
  let all = $state(true);
  let selectionRules = $state<SubtreeRule[]>([]);
  let upload: AbortController | null = null;
  const controller = new PreviewController();
  let valid = $derived(draft.choice === 'song' ? Boolean(draft.title.trim())
    : draft.choice === 'album' ? Boolean(draft.album.trim())
    : draft.choice === 'csv' || draft.choice === 'list' ? (draft.fileInput === 'path' ? Boolean(draft.source.trim()) : Boolean(draft.artifactId))
    : draft.choice === 'spotify' && draft.spotifyInput !== 'url' || Boolean(draft.source.trim()));
  let needsReview = $derived(previewNeedsReview(preview));
  let busy = $derived(uploading || !downloadBaseline || preparing || Boolean(settingsError) || preview.loading || (preview.summary?.state === 'Planning' && !needsReview) || Boolean(preview.committing));
  $effect(() => oncommittingchange(Boolean(preview.committing)));
  let subtreeCounts = $derived(new Map([...preview.branches.values()].flatMap((page) =>
    page.items.map((node) => [node.ref, Number(node.selectableSubtreeCount)] as const))));
  let selection = $derived(projectPreviewSelection(Number(preview.summary?.selectableNodeCount ?? 0), subtreeCounts, all, selectionRules));
  let selectedCount = $derived(selection.total);
  let committable = $derived(connected && !needsReview && (preview.summary?.state === 'Ready' || preview.summary?.state === 'PartiallyReady'));
  $effect(() => {
    if (connected) untrack(() => void controller.recheckReady());
  });
  const expiry = $derived(committable ? preview.summary?.expiresAtUtc : null);
  $effect(() => {
    if (!expiry) return;
    const delay = Date.parse(expiry) - Date.now();
    if (delay > 0) {
      const timer = setTimeout(() => void controller.recheckReady(), delay);
      return () => clearTimeout(timer);
    }
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
    const title = node.queryText ?? node.itemName;
    if (node.kind === 'extract' && title && (node.inputType === 'CSV' || node.inputType === 'List')) return basename(title);
    return title ?? previewNodeType(node);
  }

  function previewNodeType(node: Schema<'JobPreviewNodeDto'>): string {
    if (node.kind !== 'extract') return jobKindLabel(node.kind);
    switch (node.inputType) {
      case 'String': return 'Text';
      case 'soulseek-link': return 'Soulseek link';
      default: return node.inputType ?? 'Source';
    }
  }

  $effect(() => {
    settingsRetry;
    const requestDraft = { ...draft };
    const requestedProfiles = [...profileNames];
    const carried = carriedSettings;
    const requestFamily: ConditionFamily = requestDraft.choice === 'song' ? 'track' : requestDraft.choice === 'album' ? 'album' : 'mixed';
    const abort = new AbortController();
    let settled!: () => void;
    settingsReady = new Promise<void>((resolve) => { settled = resolve; });
    const timer = untrack(() => {
      settingsLoading = true;
      settingsError = null;
      // Refresh in the background; explicit actions await this request before planning.
      return setTimeout(async () => {
        try {
          const resolved = await daemonApi.resolveEffectiveSettings({ ...buildJobSettingsRequest(requestDraft),
            options: { profileNames: requestedProfiles, ...(carried ? { downloadSettings: carried } : {}) } }, abort.signal);
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
        } finally { if (!abort.signal.aborted) settingsLoading = false; settled(); }
      }, downloadBaseline ? 200 : 0);
    });
    return () => { clearTimeout(timer); abort.abort(); settled(); };
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
      if (state.summary && state.summary.previewId !== preview.summary?.previewId) optionsTab = 'review';
      else if (!state.summary && optionsTab === 'review') optionsTab = 'download';
      preview = state;
      if (automaticStart && (state.summary?.state === 'Ready' || state.summary?.state === 'PartiallyReady')) {
        automaticStart = false;
        void controller.commit({ mode: 'AllExcept', refs: [] });
      }
      if (state.receipt?.rootJobId) onstarted(state.receipt.rootJobId);
    });
    return () => { stopped = true; unsubscribe(); controller.stop(); upload?.abort(); };
  });

  function change(next: NewJobDraft): void {
    if (preview.committing) return;
    invalidatePreview();
    if (next.choice !== draft.choice || next.fileInput !== draft.fileInput) {
      upload?.abort(); upload = null; uploading = false; fileName = '';
      next = { ...next, artifactId: null };
    }
    draft = next;
  }

  let beforeProfile: { draft: NewJobDraft; fileName: string } | null = null;
  function selectProfiles(names: string[], catalog: Schema<'ProfileSummaryDto'>[]): void {
    if (preview.committing) return;
    const previous = profileInputDefaults(profileNames, catalog);
    const next = profileInputDefaults(names, catalog);
    profileNames = names;
    if (next.input === previous.input && next.type === previous.type) {
      invalidatePreview();
      return;
    }
    if (!next.input && !next.type) {
      invalidatePreview();
      if (beforeProfile) {
        upload?.abort();
        upload = null;
        uploading = false;
        draft = beforeProfile.draft;
        fileName = beforeProfile.fileName;
        beforeProfile = null;
      }
      return;
    }
    beforeProfile ??= { draft: { ...draft }, fileName };
    const base = !next.input && previous.input ? beforeProfile.draft : draft;
    const input = next.resolvedType && next.input === previous.input ? undefined : next.input?.input ?? undefined;
    const updated = applyProfileInput(base, input, next.resolvedType);
    if (updated) change(updated);
    else {
      change(createNewJobDraft());
      error = new Error('Unrecognized profile input. Select a job type and enter its source.');
    }
  }

  function choose(choice: NewJobChoice): void {
    if (preview.committing) return;
    upload?.abort();
    upload = null;
    uploading = false;
    fileName = '';
    optionsTab = 'download';
    const next = { ...draft, choice, artifactId: null, fileInput: 'upload' as const };
    if (choice === 'song' || choice === 'album') {
      next.artist = '';
      next.title = '';
      next.album = '';
      next.expectedLength = undefined;
    } else {
      next.source = '';
      if (choice === 'spotify') next.spotifyInput = 'url';
    }
    change(next);
  }

  function invalidatePreview(): void {
    if (preview.committing) return;
    if (optionsTab === 'review') optionsTab = 'download';
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

  async function plan(start: boolean): Promise<void> {
    if (busy) return;
    preparing = true;
    try {
      // Flush the latest input and freeze editing before awaiting its settings.
      await tick();
      await settingsReady;
      if (stopped || settingsError || !valid) return;
      const edits: Schema<'DownloadSettingsPatchDto'> = { ...downloadPatch.downloadSettings };
      if (conditionSession && draft.choice !== 'soulseek-link') {
        const searchEdits = searchConditionsPatchForFamily(family, conditionSession.baseline, conditionSession.values);
        if (Object.keys(searchEdits).length) edits.search = mergeSearchSettings(edits.search ?? undefined, searchEdits);
        if (family === 'mixed' && conditionSession.values.track.expectedLength !== conditionSession.baseline.track.expectedLength) {
          throw new Error('Expected length belongs to each extracted song; it cannot be applied to every item in a mixed source.');
        }
      }
      if (draft.choice === 'csv') edits.csv = changedOptions(csvMapping, csvBaseline ?? {});
      if (hasImportOptions) edits.extraction = buildImportPatch(importOptions, importBaseline ?? createImportOptions());
      const settings = mergeSettingsPatch(carriedSettings, edits);
      error = null;
      const request = buildJobPreviewRequest(draft, { ...downloadPatch, profileNames: [...profileNames], downloadSettings: settings });
      if (request.job.kind === 'song' && conditionSession) {
        request.job.songQuery.length = searchExpectedLength(conditionSession.values.track.expectedLength) ?? null;
      }
      // A rebuilt source can contain different songs at the same positions.
      // Keep a shortlist unchecked until the user chooses from the new rows.
      all = !needsReview || (all && selectionRules.length === 0);
      selectionRules = [];
      reviewedDownloadOptions = downloadValues && { ...downloadValues };
      automaticStart = start;
      void controller.create(request);
    } catch (cause) { error = cause instanceof Error ? cause : new Error(String(cause)); }
    finally { preparing = false; }
  }

  function startReviewed(): void {
    if (!downloadValues || !reviewedDownloadOptions) return;
    const changes = buildDownloadOptionsPatch(downloadValues, reviewedDownloadOptions, downloadCapabilities);
    const settings = changes.downloadSettings ?? {};
    if ('outputParentDir' in changes)
      settings.output = { ...settings.output, parentDir: changes.outputParentDir || downloadBaseline?.outputParentDir };
    void controller.commit({ mode: all ? 'AllExcept' : 'Only', refs: [] }, selectionRules,
      Object.keys(settings).length ? settings : undefined);
  }

  function toggle(ref: string, selected: boolean): void {
    if (preview.committing) return;
    selectionRules = toggleSubtree(ref, selected, all, selectionRules);
  }

</script>

<Modal title="Create new job" label="New job" closeLabel="Close new job" wide locked={Boolean(preview.committing)} {onclose}>
<section class="new-job-composer" aria-label="New job">
  <div class="new-job-layout" inert={preparing || preview.committing ? true : undefined}>
    <JobTypeNav value={draft.choice} onchange={choose} />
    <div class="new-job-workspace">
      <div class="new-job-scroll-area">
        <header class="new-job-current-choice"><div><strong>{choiceMetadata.label}</strong><small>{choiceMetadata.detail}</small></div>
          <ProfilePicker value={profileNames} applied={appliedProfiles} disabled={Boolean(preview.committing)} onchange={selectProfiles} />
        </header>
        <div class="new-job-config">
          <JobInputs {draft} {fileName} onchange={change} onfile={(file) => void chooseFile(file)} />
          <section class="new-job-job-settings" aria-label="Job settings">
            <div class="new-job-options-header">
              <PageTabs variant="segmented" id="new-job-options" label="Job options" items={optionTabs} value={optionsTab} onchange={(tab) => (optionsTab = tab)} />
              <div class="search-config-actions">
                {#if optionsTab === 'filtering' || optionsTab === 'ranking'}
                  <button class="button" type="button" data-testid="search-config-clear" disabled={Boolean(settingsError) || !conditionPanel} onclick={() => conditionPanel?.clearActiveTab()}>Clear</button>
                  <button class="button" type="button" data-testid="search-config-reset" disabled={Boolean(settingsError) || !conditionPanel} onclick={() => conditionPanel?.resetActiveTab()}>Reset</button>
                {:else if optionsTab === 'review'}
                  <button class="button" type="button" data-testid="new-job-selection-toggle" onclick={() => { all = !all; selectionRules = []; }}>{all ? 'Deselect all' : 'Select all'}</button>
                {:else}
                  <button class="button" type="button" data-testid="new-job-options-reset" disabled={!downloadBaseline || Boolean(settingsError)} onclick={() => {
                    if (optionsTab === 'import') {
                      importOptions = { ...(importBaseline ?? createImportOptions()) };
                      if (draft.choice === 'csv') csvMapping = { ...(csvBaseline ?? {}) };
                      invalidatePreview();
                    } else if (downloadBaseline) {
                      downloadValues = { ...downloadBaseline };
                    }
                  }}>Reset</button>
                {/if}
              </div>
            </div>
            <div id="new-job-options-panel" class="new-job-options-panel" role="tabpanel" aria-labelledby={`new-job-options-${optionsTab}`}>
              {#if optionsTab === 'review' && preview.summary}
                <section class="new-job-preview">
                  {@render branch(null, 0)}
                </section>
              {:else if settingsLoading && !downloadBaseline}<LoadingIndicator label="Loading effective settings" />
              {:else if settingsError}<p role="alert">{settingsError.message}</p><button type="button" data-testid="new-job-settings-retry" onclick={() => settingsRetry++}>Retry settings</button>
              {:else if optionsTab === 'import'}
                <ImportOptionsPanel value={importOptions} onchange={(value) => { invalidatePreview(); importOptions = value; }} />
                {#if draft.choice === 'csv'}
                  <CsvMapping value={csvMapping} onchange={(value) => { invalidatePreview(); csvMapping = value; }} />
                {/if}
              {:else if (optionsTab === 'filtering' || optionsTab === 'ranking') && conditionSession}
                <SearchConfigPanel bind:this={conditionPanel} showActions={false} mode={family === 'album' ? 'album' : 'track'} configurationFamily={family}
                  bind:conditions={conditionSession.values} loadDefaults={() => conditionSession!.baseline}
                  section={optionsTab === 'filtering' ? 'conditions' : 'ranking'} embedded automatic singleItem={family !== 'mixed'} />
              {:else if optionsTab === 'download' && downloadValues}
                <DownloadOptionsPanel bind:value={downloadValues} capabilities={downloadCapabilities} separators={false} />
              {/if}
            </div>
          </section>
        </div>
        {#if error || preview.error || preview.summary?.state === 'Expired'}<p class="mutation-status rejected" role="alert">{(error ?? preview.error)?.message ?? 'This preview has expired. Review it again before starting.'}</p>{/if}
        {#if uploading}<p role="status">Uploading source file…</p>{/if}
      </div>
      <footer class="new-job-footer">
        {#if preview.summary}
          <span class="new-job-footer-status">{selectedCount} selected</span>
          <div class="new-job-footer-actions">
            <button type="button" class="button new-job-review-button" disabled={!valid || busy} onclick={() => plan(false)}>Review again</button>
            <button type="button" class="button primary new-job-start-button" aria-keyshortcuts="Control+Enter" title="Start (Ctrl+Enter)" disabled={!committable || !selectedCount || busy} onclick={startReviewed}>{preview.committing ? 'Starting…' : `Start ${selectedCount} ${selectedCount === 1 ? 'job' : 'jobs'}`}</button>
          </div>
        {:else}
          <span class="new-job-footer-status" role="status">{preview.loading ? 'Planning…' : ''}</span>
          <div class="new-job-footer-actions">
            <button type="button" class="button new-job-review-button" disabled={!valid || busy} onclick={() => plan(false)}>Review</button>
            <button type="button" class="button primary new-job-start-button" aria-keyshortcuts="Control+Enter" title="Start (Ctrl+Enter)" disabled={!valid || busy} onclick={() => plan(true)}>Start</button>
          </div>
        {/if}
      </footer>
    </div>
  </div>
</section>
</Modal>

{#snippet branch(parent: string | null, depth: number)}
  {@const page = preview.branches.get(parent)}
  <div class:job-preview-tree={depth === 0}>
    {#each page?.items ?? [] as node (node.ref)}
      {@const count = subtreeCounts.get(node.ref) ?? 0}
      {@const selected = selection.subtreeCount(node.ref)}
      <div class="job-preview-node" style={`--job-preview-depth:${depth}`}>
        <label class="job-preview-row">
          <input type="checkbox" disabled={!count} checked={selected === count && count > 0} indeterminate={selected > 0 && selected < count} onchange={(event) => toggle(node.ref, event.currentTarget.checked)} />
          <span class="job-preview-icon"><Icon name={jobKindIcon(node.kind)} /></span>
          <span class="job-preview-name">
            <strong class="job-preview-title" title={previewNodeTitle(node)}>{previewNodeTitle(node)}</strong>
            {#if (node.kind === 'song' || node.kind === 'album') && node.queryText != null}
              <small>{node.queryText ? `Searches for ${node.queryText}` : 'Search text is empty after cleanup'}</small>
            {/if}
          </span>
          <span class="job-preview-details" title={node.failureMessage ?? undefined}>
            {#if node.failureMessage}<span class="job-preview-error">{node.failureMessage}</span><span>·</span>{/if}
            {#if Number(node.directChildCount) > 0}<span>{count} {count === 1 ? 'job' : 'jobs'}</span><span>·</span>{/if}
            <span>{previewNodeType(node)}</span>
          </span>
        </label>
        {#if Number(node.directChildCount) > 0}
          <div class="job-preview-children">{@render branch(node.ref, depth + 1)}</div>
        {/if}
      </div>
    {/each}
    {#if !needsReview}
      {#if page?.error}<p role="alert">{page.error.message}</p>{/if}
      {#if !page || page.nextCursor || page.error || page.loading}
        <button type="button" disabled={page?.loading || preview.summary?.state === 'Planning'} onclick={() => void controller.loadBranch(parent)}>{page?.loading ? 'Loading…' : page?.error ? 'Retry' : 'Load jobs'}</button>
      {/if}
    {/if}
  </div>
{/snippet}
