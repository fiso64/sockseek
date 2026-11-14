<script lang="ts">
  import {
    createEmptySearchConditions,
    type SearchConditions,
  } from '../domains/search/config-state';
  import {
    SEARCH_BIT_DEPTHS as bitDepths,
    SEARCH_GENERIC_FORMATS as genericFormats,
    SEARCH_SAMPLE_RATES as sampleRates,
    searchConfigLabel,
    type SearchConfigTab as ConfigTab,
  } from '../domains/search/config-schema';
  import type { SearchModeFamily, SearchResultMode } from '../domains/search/model';
  import { isGroupSearchMode, searchModeFamily } from '../domains/search/model';
  import { onDestroy } from 'svelte';
  import HelpPopover from './HelpPopover.svelte';
  import Notice from './Notice.svelte';
  import SearchFormatControl from './SearchFormatControl.svelte';


  type PanelTab = ConfigTab;

  interface Props {
    mode: SearchResultMode;
    conditions: SearchConditions;
    title?: string;
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

{#snippet actions()}
  <div class="search-config-actions">
    <button type="button" data-testid="search-config-clear" disabled={resetting} onclick={clearActiveTab}>Clear</button>
    <button type="button" data-testid="search-config-reset" disabled={resetting} onclick={resetActiveTab}>Reset</button>
  </div>
{/snippet}

<!-- Keep placement stable while editing: only album-group filtering puts Grouping below Peers. -->
{#snippet grouping()}
  <section class="search-config-section">
    <h3>Grouping <HelpPopover title="Grouping" testId="search-grouping-help">Groups must have at least this many distinct sharers; tolerance controls near-length grouping.</HelpPopover></h3>
    <div class="config-grid">
      <label><span>Minimum sharers</span><input type="number" min="1" step="1" bind:value={conditions.group.minShares} /></label>
      <label><span>Grouping tolerance <small>sec</small></span><input type="number" min="0" step="1" bind:value={conditions.group.lengthTolerance} /></label>
    </div>
  </section>
{/snippet}

{#if !embedded}
  <header class="search-config-header">
    <strong>{title}</strong>
    <nav class="search-config-tabs" aria-label="Search configuration sections">
      <button type="button" class:active={activeTab === 'conditions'} aria-current={activeTab === 'conditions' ? 'page' : undefined} onclick={() => (activeTab = 'conditions')}>Filtering</button>
      <button type="button" class:active={activeTab === 'ranking'} aria-current={activeTab === 'ranking' ? 'page' : undefined} onclick={() => (activeTab = 'ranking')}>Ranking</button>
    </nav>
    {#if showActions}{@render actions()}{/if}
    {#if onclose}
      <button type="button" class="search-config-close" aria-label="Close search configuration" onclick={onclose}>×</button>
    {/if}
  </header>
{/if}
{#if resetError}<Notice variant="unavailable">{resetError}</Notice>{/if}

{#if activeTab === 'conditions'}
  <div class="search-config-flow">
    <section class="search-config-section">
      <h3>{genericMode ? 'File conditions' : 'Audio quality'}</h3>
      <SearchFormatControl
        bind:values={conditions.common.formats}
        label={searchConfigLabel('formats', 'conditions')}
        ariaLabel="Comma-separated required formats"
        idPrefix="search-config-conditions"
        suggestions={genericMode ? genericFormats : undefined}
        customPlaceholder={genericMode ? 'pdf, epub, zip, txt…' : undefined}
      />

      <div class="config-grid">
        <label>
          <span>{searchConfigLabel('minBitrate', activeTab)} <small>kbps</small></span>
          <input type="number" min="0" step="1" bind:value={conditions.common.minBitrate} placeholder="Any" />
        </label>
        <label>
          <span>{searchConfigLabel('maxBitrate', activeTab)} <small>kbps</small></span>
          <input type="number" min="0" step="1" bind:value={conditions.common.maxBitrate} placeholder="Any" />
        </label>
      </div>

      <div class="config-grid">
        <label>
          <span>Min {searchConfigLabel('sampleRate', 'conditions').toLowerCase()}</span>
          <select bind:value={conditions.common.minSampleRate}>
            <option value="">Any</option>
            {#each sampleRates as rate}<option value={rate.value}>{rate.label}</option>{/each}
          </select>
        </label>
        <label>
          <span>Max {searchConfigLabel('sampleRate', 'conditions').toLowerCase()}</span>
          <select bind:value={conditions.common.maxSampleRate}>
            <option value="">Any</option>
            {#each sampleRates as rate}<option value={rate.value}>{rate.label}</option>{/each}
          </select>
        </label>
      </div>

      <div class="config-grid">
        <label>
          <span>Min {searchConfigLabel('bitDepth', 'conditions').toLowerCase()}</span>
          <select bind:value={conditions.common.minBitDepth}>
            <option value="">Any</option>
            {#each bitDepths as depth}<option value={depth}>{depth}-bit</option>{/each}
          </select>
        </label>
        <label>
          <span>Max {searchConfigLabel('bitDepth', 'conditions').toLowerCase()}</span>
          <select bind:value={conditions.common.maxBitDepth}>
            <option value="">Any</option>
            {#each bitDepths as depth}<option value={depth}>{depth}-bit</option>{/each}
          </select>
        </label>
      </div>

      {#if !genericMode}
        <div class="config-check-with-help">
          <label class="config-check"><input type="checkbox" bind:checked={conditions.common.rejectUnknownMetadata} /> {searchConfigLabel('rejectUnknownMetadata', 'conditions')}</label>
          <HelpPopover title="Reject unknown metadata" testId="unknown-metadata-help">
            <span>Some peers' clients do not report properties such as bitrate. By default, a file with an unknown bitrate is still accepted even when you set a minimum bitrate.</span>
            <span>Enable this to reject files when a property being checked is unknown. Use with care: this can exclude otherwise valid files simply because of the particular client sharing them.</span>
          </HelpPopover>
        </div>
      {/if}
      {#if family === 'album' || family === 'mixed'}
        <div class="config-check-with-help">
          <label class="config-check"><input type="checkbox" bind:checked={conditions.album.strictAlbumQuality} /> {searchConfigLabel('strictAlbumQuality', 'conditions')}</label>
          <HelpPopover title="Every album track must satisfy quality" testId="album-quality-help">
            <span>By default, mixed-quality folders are ranked by coverage. For example, if you prefer FLAC, a folder with 9 FLAC files and 1 MP3 is still accepted (ranking above a mostly-MP3 folder).</span>
            <span>Enable this to require every audio file in the folder to meet your audio quality conditions.</span>
          </HelpPopover>
        </div>
      {/if}
    </section>

    <div class="search-config-columns">
      <div class="search-config-column search-config-column-primary">
        {#if !genericMode}
          <section class="search-config-section">
            <h3>Matching</h3>
            <label class="config-check"><input type="checkbox" bind:checked={conditions.common.strictArtist} /> {searchConfigLabel('strictArtist', 'conditions')}</label>
            {#if family === 'track' || family === 'mixed'}
              {#if family === 'mixed'}<div class="config-subgroup-label">Songs</div>{/if}
              <label class="config-check"><input type="checkbox" bind:checked={conditions.track.strictTitle} /> {searchConfigLabel('strictTitle', 'conditions')}</label>
              <div class:single-config-field={!singleItem} class="config-grid config-grid-spaced">
                {#if singleItem}
                  <label>
                    <span>Expected length <small>sec</small></span>
                    <input type="number" min="0" bind:value={conditions.track.expectedLength} placeholder="Any" />
                  </label>
                {/if}
                <label>
                  <span>{searchConfigLabel('lengthTolerance', 'conditions')} <small>sec</small></span>
                  <input type="number" min="0" bind:value={conditions.track.lengthTolerance} />
                </label>
              </div>
              <label class="config-check"><input type="checkbox" bind:checked={conditions.track.acceptNoLength} /> Accept unknown length</label>
            {/if}
            {#if family === 'album' || family === 'mixed'}
              {#if family === 'mixed'}<div class="config-subgroup-label">Albums</div>{/if}
              <label class="config-check"><input type="checkbox" bind:checked={conditions.album.strictAlbum} /> {searchConfigLabel('strictAlbum', 'conditions')}</label>
            {/if}
          </section>
        {/if}

        {#if family === 'album' || family === 'mixed'}
          <section class="search-config-section">
            <h3>Album structure</h3>
            <div class="config-grid">
              <label><span>{searchConfigLabel('minTrackCount', 'conditions')}</span><input type="number" min="0" bind:value={conditions.album.minTrackCount} placeholder="Any" /></label>
              <label><span>{searchConfigLabel('maxTrackCount', 'conditions')}</span><input type="number" min="0" bind:value={conditions.album.maxTrackCount} placeholder="Any" /></label>
            </div>

            {#if singleItem}
            <div class="config-label config-label-spaced">{searchConfigLabel('requiredTrackTitle', 'conditions')}</div>
            <div class="required-track-row">
              <input
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
              <button type="button" onclick={addRequiredTrackTitle}>Add</button>
            </div>
            {#if conditions.album.requiredTrackTitles.length}
              <div class="required-track-pills">
                {#each conditions.album.requiredTrackTitles as trackTitle}
                  <button type="button" onclick={() => removeRequiredTrackTitle(trackTitle)}>{trackTitle} ×</button>
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
        <section class="search-config-section peer-config-section">
          <h3>Peers</h3>
          <div class="config-grid peer-config-grid">
            <label><span>{searchConfigLabel('allowedUsers', 'conditions')}</span><input type="text" bind:value={conditions.common.allowedUsers} placeholder="e.g. user1, user2" /></label>
            <label><span>{searchConfigLabel('bannedUsers', 'conditions')}</span><input type="text" bind:value={conditions.common.bannedUsers} placeholder="e.g. user1, user2" /></label>
          </div>
          {#if !automatic}
            <label class="config-check"><input type="checkbox" bind:checked={conditions.common.excludeLocked} /> {searchConfigLabel('excludeLocked', 'conditions')}</label>
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
    <section class="search-config-section">
      <h3>{genericMode ? 'File ranking' : 'Audio ranking'}</h3>
      <SearchFormatControl
        bind:values={conditions.ranking.common.formats}
        label={searchConfigLabel('formats', 'ranking')}
        ariaLabel="Comma-separated preferred formats"
        idPrefix="search-config-ranking"
        suggestions={genericMode ? genericFormats : undefined}
        customPlaceholder={genericMode ? 'pdf, epub, zip, txt…' : undefined}
      />

      <div class="config-grid">
        <label><span>{searchConfigLabel('minBitrate', activeTab)} <small>kbps</small></span><input type="number" min="0" bind:value={conditions.ranking.common.minBitrate} placeholder="Any" /></label>
        <label><span>{searchConfigLabel('maxBitrate', activeTab)} <small>kbps</small></span><input type="number" min="0" bind:value={conditions.ranking.common.maxBitrate} placeholder="Any" /></label>
      </div>

      <div class="config-grid">
        <label>
          <span>Min {searchConfigLabel('sampleRate', 'ranking').toLowerCase()}</span>
          <select bind:value={conditions.ranking.common.minSampleRate}>
            <option value="">Any</option>
            {#each sampleRates as rate}<option value={rate.value}>{rate.label}</option>{/each}
          </select>
        </label>
        <label>
          <span>Max {searchConfigLabel('sampleRate', 'ranking').toLowerCase()}</span>
          <select bind:value={conditions.ranking.common.maxSampleRate}>
            <option value="">Any</option>
            {#each sampleRates as rate}<option value={rate.value}>{rate.label}</option>{/each}
          </select>
        </label>
      </div>

      <div class="config-grid">
        <label>
          <span>Min {searchConfigLabel('bitDepth', 'ranking').toLowerCase()}</span>
          <select bind:value={conditions.ranking.common.minBitDepth}>
            <option value="">Any</option>
            {#each bitDepths as depth}<option value={depth}>{depth}-bit</option>{/each}
          </select>
        </label>
        <label>
          <span>Max {searchConfigLabel('bitDepth', 'ranking').toLowerCase()}</span>
          <select bind:value={conditions.ranking.common.maxBitDepth}>
            <option value="">Any</option>
            {#each bitDepths as depth}<option value={depth}>{depth}-bit</option>{/each}
          </select>
        </label>
      </div>
    </section>

    <div class="search-config-columns">
      <div class="search-config-column search-config-column-primary">
        {#if !genericMode}
          <section class="search-config-section">
            <h3>Matching</h3>
            <label class="config-check"><input type="checkbox" bind:checked={conditions.ranking.common.strictArtist} /> {searchConfigLabel('strictArtist', 'ranking')}</label>
            {#if family === 'track' || family === 'mixed'}
              {#if family === 'mixed'}<div class="config-subgroup-label">Songs</div>{/if}
              <label class="config-check"><input type="checkbox" bind:checked={conditions.ranking.track.strictTitle} /> {searchConfigLabel('strictTitle', 'ranking')}</label>
              <div class="config-grid config-grid-spaced single-config-field">
                <label><span>{searchConfigLabel('lengthTolerance', 'ranking')} <small>sec</small></span><input type="number" min="0" bind:value={conditions.ranking.track.lengthTolerance} placeholder="Disabled" /></label>
              </div>
            {/if}
            {#if family === 'album' || family === 'mixed'}
              {#if family === 'mixed'}<div class="config-subgroup-label">Albums</div>{/if}
              <label class="config-check"><input type="checkbox" bind:checked={conditions.ranking.album.strictAlbum} /> {searchConfigLabel('strictAlbum', 'ranking')}</label>
            {/if}
          </section>
        {/if}
      </div>

      <div class="search-config-column search-config-column-secondary">
        <section class="search-config-section peer-config-section">
          <h3>Peers</h3>
          <div class="config-grid peer-config-grid">
            <label><span>{searchConfigLabel('allowedUsers', 'ranking')}</span><input type="text" bind:value={conditions.ranking.common.allowedUsers} placeholder="e.g. user1, user2" /></label>
            <label><span>{searchConfigLabel('bannedUsers', 'ranking')}</span><input type="text" bind:value={conditions.ranking.common.bannedUsers} placeholder="e.g. user1, user2" /></label>
          </div>
          {#if !automatic}
            <label class="config-check"><input type="checkbox" bind:checked={conditions.ranking.common.excludeLocked} /> {searchConfigLabel('excludeLocked', 'ranking')}</label>
          {/if}
        </section>
      </div>
    </div>
  </div>
{/if}
