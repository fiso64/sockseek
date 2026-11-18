<script lang="ts">
  import {
    createEmptySearchConditions,
    type SearchConditions,
  } from '../domains/search/config-state';
  import {
    SEARCH_BIT_DEPTHS as bitDepths,
    SEARCH_GENERIC_FORMATS as genericFormats,
    SEARCH_SAMPLE_RATES as sampleRates,
    SEARCH_CONFIG_FIELDS as fields,
    type SearchConfigTab as ConfigTab,
  } from '../domains/search/config-schema';
  import type { SearchModeFamily, SearchResultMode } from '../domains/search/model';
  import { isGroupSearchMode, searchModeFamily } from '../domains/search/model';
  import { onDestroy, type ComponentProps } from 'svelte';
  import ProfilePicker from './ProfilePicker.svelte';
  import HelpPopover from './HelpPopover.svelte';
  import Notice from './Notice.svelte';
  import SearchFormatControl from './SearchFormatControl.svelte';


  type PanelTab = ConfigTab;

  interface Props {
    mode: SearchResultMode;
    conditions: SearchConditions;
    title?: string;
    profiles?: ComponentProps<typeof ProfilePicker>;
    initialTab?: PanelTab;
    section?: PanelTab;
    embedded?: boolean;
    showActions?: boolean;
    onclose?: () => void;
    configurationFamily?: SearchModeFamily | 'mixed';
    singleItem?: boolean;
    automatic?: boolean;
    loadDefaults: (signal: AbortSignal) => SearchConditions | Promise<SearchConditions>;
  }

  let {
    mode,
    conditions = $bindable(),
    title = 'Search configuration',
    profiles,
    initialTab = 'conditions',
    section,
    embedded = false,
    showActions = true,
    onclose,
    configurationFamily,
    singleItem = true,
    automatic = false,
    loadDefaults,
  }: Props = $props();

  let family = $derived(configurationFamily ?? searchModeFamily(mode));
  let genericMode = $derived(family === 'generic');
  let groupMode = $derived(isGroupSearchMode(mode));
  let activeTab = $state<PanelTab>('conditions');
  $effect(() => { activeTab = section ?? initialTab; });
  let requiredTrackTitle = $state('');

  let resetting = $state(false);
  let resetError = $state<string | null>(null);
  let resetRequest: AbortController | undefined;
  onDestroy(() => resetRequest?.abort());

  function replaceTab(values: SearchConditions, tab: PanelTab): void {
    // Copy resolved defaults: editing this panel must never mutate its baseline.
    const next = JSON.parse(JSON.stringify(values)) as SearchConditions;
    if (tab === 'conditions') {
      next.ranking = conditions.ranking;
      if (!singleItem) {
        next.album.requiredTrackTitles = conditions.album.requiredTrackTitles;
        next.track.expectedLength = conditions.track.expectedLength;
      }
      conditions = next;
      requiredTrackTitle = '';
    } else {
      conditions.ranking = next.ranking;
    }
  }

  export function clearActiveTab(): void {
    resetError = null;
    replaceTab(createEmptySearchConditions(), activeTab);
  }

  export async function resetActiveTab(): Promise<void> {
    const tab = activeTab;
    const request = new AbortController();
    resetRequest = request;
    resetting = true;
    resetError = null;
    try {
      const defaults = await loadDefaults(request.signal);
      if (!request.signal.aborted) replaceTab(defaults, tab);
    } catch (error) {
      if (!request.signal.aborted)
        resetError = error instanceof Error ? error.message : 'Could not load default settings.';
    } finally {
      if (!request.signal.aborted) resetting = false;
    }
  }

  function addRequiredTrackTitle(): void {
    const trackTitle = requiredTrackTitle.trim();
    if (!trackTitle || conditions.album.requiredTrackTitles.includes(trackTitle)) return;
    conditions.album.requiredTrackTitles = [...conditions.album.requiredTrackTitles, trackTitle];
    requiredTrackTitle = '';
  }

  function removeRequiredTrackTitle(trackTitle: string): void {
    conditions.album.requiredTrackTitles = conditions.album.requiredTrackTitles.filter((item) => item !== trackTitle);
  }
</script>

{#snippet fieldHelp(field: { label: string; help: readonly string[] }, testId: string)}
  <HelpPopover title={field.label} {testId}>
    {#each field.help as paragraph}<span>{paragraph}</span>{/each}
  </HelpPopover>
{/snippet}

<!-- Keep placement stable while editing: only album-group filtering puts Grouping below Peers. -->
{#snippet grouping()}
  <section class="panel subtle search-config-section">
    <h3>Grouping</h3>
    <div class="config-grid">
      <label><span>{fields.minShares.conditions.label}</span><input class="field" type="number" min="1" step="1" bind:value={conditions.group.minShares} /></label>
      <label><span>{fields.groupLengthTolerance.conditions.label} <small>sec</small></span><input class="field" type="number" min="0" step="1" bind:value={conditions.group.lengthTolerance} /></label>
    </div>
  </section>
{/snippet}

{#if !embedded}
  <header class="search-config-header">
    <strong>{title}</strong>
    <nav class="segmented-control search-config-tabs" aria-label="Search configuration sections">
      <button type="button" class:active={activeTab === 'conditions'} aria-current={activeTab === 'conditions' ? 'page' : undefined} onclick={() => (activeTab = 'conditions')}>Filtering</button>
      <button type="button" class:active={activeTab === 'ranking'} aria-current={activeTab === 'ranking' ? 'page' : undefined} onclick={() => (activeTab = 'ranking')}>Ranking</button>
    </nav>
    {#if showActions || profiles}
      <div class="search-config-header-tools">
        {#if profiles}<ProfilePicker {...profiles} quiet />{/if}
        {#if showActions}
          <div class="search-config-actions">
            <button class="button secondary" type="button" data-testid="search-config-clear" disabled={resetting} onclick={clearActiveTab}>Clear</button>
            <button class="button secondary" type="button" data-testid="search-config-reset" disabled={resetting} onclick={resetActiveTab}>Reset</button>
          </div>
        {/if}
      </div>
    {/if}
    {#if onclose}
      <button type="button" class="button quiet search-config-close" aria-label="Close search configuration" onclick={onclose}>×</button>
    {/if}
  </header>
{/if}
{#if resetError}<Notice variant="unavailable">{resetError}</Notice>{/if}

{#if activeTab === 'conditions'}
  <div class="search-config-flow">
    <section class="panel subtle search-config-section">
      <h3>{genericMode ? 'File conditions' : 'Audio quality'}</h3>
      <SearchFormatControl
        bind:values={conditions.common.formats}
        label={fields.formats.conditions.label}
        ariaLabel="Comma-separated required formats"
        idPrefix="search-config-conditions"
        suggestions={genericMode ? genericFormats : undefined}
        customPlaceholder={genericMode ? 'pdf, epub, zip, txt…' : undefined}
      />

      <div class="config-grid">
        <label>
          <span>{fields.minBitrate.conditions.label} <small>kbps</small></span>
          <input class="field" type="number" min="0" step="1" bind:value={conditions.common.minBitrate} placeholder="Any" />
        </label>
        <label>
          <span>{fields.maxBitrate.conditions.label} <small>kbps</small></span>
          <input class="field" type="number" min="0" step="1" bind:value={conditions.common.maxBitrate} placeholder="Any" />
        </label>
      </div>

      <div class="config-grid">
        <label>
          <span>{fields.minSampleRate.conditions.label}</span>
          <select class="field" bind:value={conditions.common.minSampleRate}>
            <option value="">Any</option>
            {#each sampleRates as rate}<option value={rate.value}>{rate.label}</option>{/each}
          </select>
        </label>
        <label>
          <span>{fields.maxSampleRate.conditions.label}</span>
          <select class="field" bind:value={conditions.common.maxSampleRate}>
            <option value="">Any</option>
            {#each sampleRates as rate}<option value={rate.value}>{rate.label}</option>{/each}
          </select>
        </label>
      </div>

      <div class="config-grid">
        <label>
          <span>{fields.minBitDepth.conditions.label}</span>
          <select class="field" bind:value={conditions.common.minBitDepth}>
            <option value="">Any</option>
            {#each bitDepths as depth}<option value={depth}>{depth}-bit</option>{/each}
          </select>
        </label>
        <label>
          <span>{fields.maxBitDepth.conditions.label}</span>
          <select class="field" bind:value={conditions.common.maxBitDepth}>
            <option value="">Any</option>
            {#each bitDepths as depth}<option value={depth}>{depth}-bit</option>{/each}
          </select>
        </label>
      </div>

      {#if !genericMode}
        <div class="config-check-with-help">
          <label class="config-check"><input type="checkbox" bind:checked={conditions.common.rejectUnknownMetadata} /> {fields.rejectUnknownMetadata.conditions.label}</label>
          {@render fieldHelp(fields.rejectUnknownMetadata.conditions, 'unknown-metadata-help')}
        </div>
      {/if}
      {#if family === 'album' || family === 'mixed'}
        <div class="config-check-with-help">
          <label class="config-check"><input type="checkbox" bind:checked={conditions.album.strictAlbumQuality} /> {fields.strictAlbumQuality.conditions.label}</label>
          {@render fieldHelp(fields.strictAlbumQuality.conditions, 'album-quality-help')}
        </div>
      {/if}
    </section>

    <div class="search-config-columns">
      <div class="search-config-column search-config-column-primary">
        {#if !genericMode}
          <section class="panel subtle search-config-section">
            <h3>Matching</h3>
            <div class="config-check-with-help">
              <label class="config-check"><input type="checkbox" bind:checked={conditions.common.strictArtist} /> {fields.strictArtist.conditions.label}</label>
              {@render fieldHelp(fields.strictArtist.conditions, 'strict-artist-conditions-help')}
            </div>
            {#if family === 'track' || family === 'mixed'}
              {#if family === 'mixed'}<div class="config-subgroup-label">Songs</div>{/if}
              <div class="config-check-with-help">
                <label class="config-check"><input type="checkbox" data-testid="strict-title-conditions" bind:checked={conditions.track.strictTitle} /> {fields.strictTitle.conditions.label}</label>
                {@render fieldHelp(fields.strictTitle.conditions, 'strict-title-conditions-help')}
              </div>
              <div class:single-config-field={!singleItem} class="config-grid config-grid-spaced">
                {#if singleItem}
                  <label>
                    <span>{fields.expectedLength.conditions.label} <small>sec</small></span>
                    <input class="field" type="number" min="0" bind:value={conditions.track.expectedLength} placeholder="Any" />
                  </label>
                {/if}
                <label>
                  <span>{fields.lengthTolerance.conditions.label} <small>sec</small></span>
                  <input class="field" type="number" min="0" bind:value={conditions.track.lengthTolerance} />
                </label>
              </div>
              <div class="config-check-with-help">
                <label class="config-check"><input type="checkbox" bind:checked={conditions.track.acceptNoLength} /> {fields.acceptNoLength.conditions.label}</label>
                {@render fieldHelp(fields.acceptNoLength.conditions, 'unknown-length-help')}
              </div>
            {/if}
            {#if family === 'album' || family === 'mixed'}
              {#if family === 'mixed'}<div class="config-subgroup-label">Albums</div>{/if}
              <div class="config-check-with-help">
                <label class="config-check"><input type="checkbox" bind:checked={conditions.album.strictAlbum} /> {fields.strictAlbum.conditions.label}</label>
                {@render fieldHelp(fields.strictAlbum.conditions, 'strict-album-conditions-help')}
              </div>
            {/if}
          </section>
        {/if}

        {#if family === 'album' || family === 'mixed'}
          <section class="panel subtle search-config-section">
            <h3>Album structure</h3>
            <div class="config-grid">
              <label><span>{fields.minTrackCount.conditions.label}</span><input class="field" type="number" min="0" bind:value={conditions.album.minTrackCount} placeholder="Any" /></label>
              <label><span>{fields.maxTrackCount.conditions.label}</span><input class="field" type="number" min="0" bind:value={conditions.album.maxTrackCount} placeholder="Any" /></label>
            </div>

            {#if singleItem}
            <div class="config-label config-label-spaced">{fields.requiredTrackTitle.conditions.label}</div>
            <div class="required-track-row">
              <input class="field"
                value={requiredTrackTitle}
                placeholder="e.g. Music Is Math"
                oninput={(event) => (requiredTrackTitle = (event.currentTarget as HTMLInputElement).value)}
                onkeydown={(event) => {
                  if (event.key === 'Enter') {
                    event.preventDefault();
                    addRequiredTrackTitle();
                  }
                }}
              />
              <button class="button" type="button" onclick={addRequiredTrackTitle}>Add</button>
            </div>
            {#if conditions.album.requiredTrackTitles.length}
              <div class="required-track-pills">
                {#each conditions.album.requiredTrackTitles as trackTitle}
                  <button class="button" type="button" onclick={() => removeRequiredTrackTitle(trackTitle)}>{trackTitle} ×</button>
                {/each}
              </div>
            {/if}
            {/if}
          </section>
        {/if}

        {#if groupMode && family !== 'album'}
          {@render grouping()}
        {/if}
      </div>

      <div class="search-config-column search-config-column-secondary">
        <section class="panel subtle search-config-section peer-config-section">
          <h3>Peers</h3>
          <div class="config-grid peer-config-grid">
            <label><span>{fields.allowedUsers.conditions.label}</span><input class="field" type="text" bind:value={conditions.common.allowedUsers} placeholder="e.g. user1, user2" /></label>
            <label><span>{fields.bannedUsers.conditions.label}</span><input class="field" type="text" bind:value={conditions.common.bannedUsers} placeholder="e.g. user1, user2" /></label>
          </div>
          {#if !automatic}
            <label class="config-check"><input type="checkbox" bind:checked={conditions.common.excludeLocked} /> {fields.excludeLocked.conditions.label}</label>
          {/if}
        </section>
        {#if groupMode && family === 'album'}
          {@render grouping()}
        {/if}
      </div>
    </div>
  </div>
{:else if activeTab === 'ranking'}
  <div class="search-config-flow">
    <section class="panel subtle search-config-section">
      <h3>{genericMode ? 'File ranking' : 'Audio ranking'}</h3>
      <SearchFormatControl
        bind:values={conditions.ranking.common.formats}
        label={fields.formats.ranking.label}
        ariaLabel="Comma-separated preferred formats"
        idPrefix="search-config-ranking"
        suggestions={genericMode ? genericFormats : undefined}
        customPlaceholder={genericMode ? 'pdf, epub, zip, txt…' : undefined}
      />

      <div class="config-grid">
        <label><span>{fields.minBitrate.ranking.label} <small>kbps</small></span><input class="field" type="number" min="0" bind:value={conditions.ranking.common.minBitrate} placeholder="Any" /></label>
        <label><span>{fields.maxBitrate.ranking.label} <small>kbps</small></span><input class="field" type="number" min="0" bind:value={conditions.ranking.common.maxBitrate} placeholder="Any" /></label>
      </div>

      <div class="config-grid">
        <label>
          <span>{fields.minSampleRate.ranking.label}</span>
          <select class="field" bind:value={conditions.ranking.common.minSampleRate}>
            <option value="">Any</option>
            {#each sampleRates as rate}<option value={rate.value}>{rate.label}</option>{/each}
          </select>
        </label>
        <label>
          <span>{fields.maxSampleRate.ranking.label}</span>
          <select class="field" bind:value={conditions.ranking.common.maxSampleRate}>
            <option value="">Any</option>
            {#each sampleRates as rate}<option value={rate.value}>{rate.label}</option>{/each}
          </select>
        </label>
      </div>

      <div class="config-grid">
        <label>
          <span>{fields.minBitDepth.ranking.label}</span>
          <select class="field" bind:value={conditions.ranking.common.minBitDepth}>
            <option value="">Any</option>
            {#each bitDepths as depth}<option value={depth}>{depth}-bit</option>{/each}
          </select>
        </label>
        <label>
          <span>{fields.maxBitDepth.ranking.label}</span>
          <select class="field" bind:value={conditions.ranking.common.maxBitDepth}>
            <option value="">Any</option>
            {#each bitDepths as depth}<option value={depth}>{depth}-bit</option>{/each}
          </select>
        </label>
      </div>
    </section>

    <div class="search-config-columns">
      <div class="search-config-column search-config-column-primary">
        {#if !genericMode}
          <section class="panel subtle search-config-section">
            <h3>Matching</h3>
            <div class="config-check-with-help">
              <label class="config-check"><input type="checkbox" bind:checked={conditions.ranking.common.strictArtist} /> {fields.strictArtist.ranking.label}</label>
              {@render fieldHelp(fields.strictArtist.ranking, 'strict-artist-ranking-help')}
            </div>
            {#if family === 'track' || family === 'mixed'}
              {#if family === 'mixed'}<div class="config-subgroup-label">Songs</div>{/if}
              <div class="config-check-with-help">
                <label class="config-check"><input type="checkbox" bind:checked={conditions.ranking.track.strictTitle} /> {fields.strictTitle.ranking.label}</label>
                {@render fieldHelp(fields.strictTitle.ranking, 'strict-title-ranking-help')}
              </div>
              <div class="config-grid config-grid-spaced single-config-field">
                <label><span>{fields.lengthTolerance.ranking.label} <small>sec</small></span><input class="field" type="number" min="0" bind:value={conditions.ranking.track.lengthTolerance} placeholder="Disabled" /></label>
              </div>
            {/if}
            {#if family === 'album' || family === 'mixed'}
              {#if family === 'mixed'}<div class="config-subgroup-label">Albums</div>{/if}
              <div class="config-check-with-help">
                <label class="config-check"><input type="checkbox" bind:checked={conditions.ranking.album.strictAlbum} /> {fields.strictAlbum.ranking.label}</label>
                {@render fieldHelp(fields.strictAlbum.ranking, 'strict-album-ranking-help')}
              </div>
            {/if}
          </section>
        {/if}
      </div>

      <div class="search-config-column search-config-column-secondary">
        <section class="panel subtle search-config-section peer-config-section">
          <h3>Peers</h3>
          <div class="config-grid peer-config-grid">
            <label><span>{fields.allowedUsers.ranking.label}</span><input class="field" type="text" bind:value={conditions.ranking.common.allowedUsers} placeholder="e.g. user1, user2" /></label>
            <label><span>{fields.bannedUsers.ranking.label}</span><input class="field" type="text" bind:value={conditions.ranking.common.bannedUsers} placeholder="e.g. user1, user2" /></label>
          </div>
          {#if !automatic}
            <label class="config-check"><input type="checkbox" bind:checked={conditions.ranking.common.excludeLocked} /> {fields.excludeLocked.ranking.label}</label>
          {/if}
        </section>
      </div>
    </div>
  </div>
{/if}
