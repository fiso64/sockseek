<script lang="ts">
  import {
    createEmptySearchConditions,
    createEmptySearchRanking,
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
  import SearchFormatControl from './SearchFormatControl.svelte';


  type PanelTab = ConfigTab;

  interface Props {
    mode: SearchResultMode;
    conditions: SearchConditions;
    title?: string;
    initialTab?: PanelTab;
    section?: PanelTab;
    embedded?: boolean;
    onclose?: () => void;
    configurationFamily?: SearchModeFamily | 'mixed';
    singleItem?: boolean;
  }

  let {
    mode,
    conditions = $bindable(),
    title = 'Search configuration',
    initialTab = 'conditions',
    section,
    embedded = false,
    onclose,
    configurationFamily,
    singleItem = true,
  }: Props = $props();

  let family = $derived(configurationFamily ?? searchModeFamily(mode));
  let genericMode = $derived(family === 'generic');
  let groupMode = $derived(isGroupSearchMode(mode));
  let activeTab = $state<PanelTab>('conditions');
  $effect(() => { activeTab = section ?? initialTab; });
  let requiredTrackTitle = $state('');

  function clearActiveTab(): void {
    if (activeTab === 'conditions') {
      const ranking = conditions.ranking;
      const requiredTrackTitles = conditions.album.requiredTrackTitles;
      const expectedLength = conditions.track.expectedLength;
      Object.assign(conditions, createEmptySearchConditions());
      conditions.ranking = ranking;
      if (!singleItem) {
        conditions.album.requiredTrackTitles = requiredTrackTitles;
        conditions.track.expectedLength = expectedLength;
      }
      requiredTrackTitle = '';
    } else if (activeTab === 'ranking') {
      conditions.ranking = createEmptySearchRanking();
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

{#if !embedded}
  <header class="search-config-header">
    <strong>{title}</strong>
    <nav class="search-config-tabs" aria-label="Search configuration sections">
      <button type="button" class:active={activeTab === 'conditions'} aria-current={activeTab === 'conditions' ? 'page' : undefined} onclick={() => (activeTab = 'conditions')}>Filtering</button>
      <button type="button" class:active={activeTab === 'ranking'} aria-current={activeTab === 'ranking' ? 'page' : undefined} onclick={() => (activeTab = 'ranking')}>Ranking</button>
    </nav>
    {#if onclose}
      <button type="button" class="search-config-close" aria-label="Close search configuration" onclick={onclose}>×</button>
    {/if}
  </header>
{/if}

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
        <label class="config-check"><input type="checkbox" bind:checked={conditions.common.rejectUnknownMetadata} /> {searchConfigLabel('rejectUnknownMetadata', 'conditions')}</label>
      {/if}
      {#if family === 'album' || family === 'mixed'}
        <label class="config-check"><input type="checkbox" bind:checked={conditions.album.strictAlbumQuality} /> {searchConfigLabel('strictAlbumQuality', 'conditions')}</label>
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

        {#if groupMode}
          <section class="search-config-section">
            <h3>Grouping</h3>
            <div class="config-grid">
              <label><span>Minimum sharers</span><input type="number" min="1" step="1" bind:value={conditions.group.minShares} /></label>
              <label><span>Grouping tolerance <small>sec</small></span><input type="number" min="0" step="1" bind:value={conditions.group.lengthTolerance} /></label>
            </div>
            <p class="download-option-note">Groups must have at least this many distinct sharers; tolerance controls near-length grouping.</p>
          </section>
        {/if}
      </div>

      <div class="search-config-column search-config-column-secondary">
        <section class="search-config-section peer-config-section">
          <h3>Peers</h3>
          <div class="config-grid peer-config-grid">
            <label><span>{searchConfigLabel('allowedUsers', 'conditions')}</span><input type="text" bind:value={conditions.common.allowedUsers} placeholder="e.g. user1, user2" /></label>
            <label><span>{searchConfigLabel('bannedUsers', 'conditions')}</span><input type="text" bind:value={conditions.common.bannedUsers} placeholder="e.g. user1, user2" /></label>
          </div>
          <label class="config-check"><input type="checkbox" bind:checked={conditions.common.excludeLocked} /> {searchConfigLabel('excludeLocked', 'conditions')}</label>
        </section>
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
          <label class="config-check" title="After quality preferences, before upload slots and speed. A folder with any locked file counts as locked."><input type="checkbox" bind:checked={conditions.ranking.common.excludeLocked} /> {searchConfigLabel('excludeLocked', 'ranking')}</label>
        </section>
      </div>
    </div>
  </div>
{/if}

<footer class:embedded class="search-config-footer">
  <button type="button" onclick={clearActiveTab}>{activeTab === 'conditions' ? 'Clear filtering' : 'Clear ranking'}</button>
</footer>
