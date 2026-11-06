<script lang="ts">
  import { tick } from 'svelte';
  import type { SearchDraft, SearchResultMode } from '../domains/search/model';
  import { editableSearchDraft, searchModeFamily as family } from '../domains/search/model';
  import ModeIconToggle from './ModeIconToggle.svelte';
  import Icon from './Icon.svelte';
  import Modal from './Modal.svelte';
  import SearchConfiguration from '../domains/search/SearchConfiguration.svelte';

  export interface UserBrowseDraft {
    query: string;
    mode: 'user' | 'shares';
  }

  interface Props {
    variant?: 'content' | 'user';
    value: SearchDraft;
    userValue: UserBrowseDraft;
    busy?: boolean;
    onchange: (value: SearchDraft) => void;
    onsubmit: (value: SearchDraft) => void;
    ondownloadrequest: (value: SearchDraft) => void;
    onuserchange: (value: UserBrowseDraft) => void;
    onusersubmit: (value: UserBrowseDraft) => void;
  }

  let { variant = 'content', value, userValue, busy = false, onchange, onsubmit, ondownloadrequest, onuserchange, onusersubmit }: Props = $props();
  let suppressAutoSplit = $state(false);
  let settingsOpen = $state(false);
  let modeMenuOpen = $state(false);
  let modeHelpOpen = $state(false);
  let settingsMounted = $state(false);
  let configurationError = $state<string | null>(null);
  let configurationPending = $state(false);
  let queuedDownload = false;
  let queuedSubmitKey = $state<string | null>(null);
  let searchRoot: HTMLDivElement;
  let searchEngaged = $state(false);
  let conditionsResolved = $state(false);
  $effect(() => { variant; modeMenuOpen = false; modeHelpOpen = false; });

  function submissionKey(): string {
    return JSON.stringify([value.resultMode, value.mode, value.query, value.artist, value.title]);
  }

  $effect(() => {
    const queued = queuedSubmitKey;
    if (!queued) return;
    if (queued !== submissionKey() || (configurationError && !configurationPending)) {
      queuedSubmitKey = null;
      return;
    }
    if (configurationPending || !conditionsResolved || configurationError) return;
    // SearchConfiguration publishes its sparse settings patch in the same
    // update cycle as readiness. Submit the settled draft, not stale defaults.
    void tick().then(() => {
      if (queuedSubmitKey !== queued || queued !== submissionKey()
        || configurationPending || configurationError || busy) return;
      queuedSubmitKey = null;
      submitDraft(queuedDownload);
    });
  });

  function dismissSearch(): void {
    queuedSubmitKey = null;
    if (configurationPending) configurationError = null;
    configurationPending = false;
    searchEngaged = false;
    settingsOpen = false;
    if (document.activeElement instanceof HTMLElement && searchRoot?.contains(document.activeElement)) document.activeElement.blur();
  }

  function outsidePointerDown(event: PointerEvent): void {
    if ((searchEngaged || settingsOpen) && event.target instanceof Node && !searchRoot?.contains(event.target)) dismissSearch();
  }

  const modes = [
    { value: 'generic', label: 'File Search', icon: 'generic' as const, description: 'Search for any file type, organized by peer and folder. No music filters are applied by default.' },
    { value: 'track', label: 'Track Search', icon: 'track' as const, description: 'Search for individual songs by artist and title. Uses your music format and quality conditions; each peer’s copy appears separately.' },
    { value: 'album', label: 'Album Search', icon: 'album' as const, description: 'Search by artist and album, with each peer’s album folder shown separately. Applies music and album conditions, such as track count. Select whole folders or individual files.' },
    { value: 'song-aggregate', label: 'Song Aggregate', icon: 'song-aggregate' as const, description: 'Like Track Search, but combines matching copies from different peers into one result per song. Choose a copy within each result. By default, only songs shared by at least two peers are shown.' },
    { value: 'album-aggregate', label: 'Album Aggregate', icon: 'album-aggregate' as const, description: 'Like Album Search, but combines matching album folders from different peers into one result per album. Choose a folder within each result. By default, only albums shared by at least two peers are shown.' },
  ];
  const userModes = [
    { value: 'user', label: 'User', icon: 'user' as const },
    { value: 'shares', label: 'Shares', icon: 'folder' as const },
  ];

  function findDelimiter(input: string): { index: number; length: number } | null {
    return [' — ', ' - ']
      .map((delimiter) => ({ index: input.indexOf(delimiter), length: delimiter.length }))
      .filter((candidate) => candidate.index >= 0)
      .sort((left, right) => left.index - right.index)[0] ?? null;
  }

  function setSimple(query: string): void {
    onchange({ ...editableSearchDraft(value), mode: 'simple', query, artist: '', title: '' });
  }

  function setSplit(artist: string, title: string): void {
    onchange({ ...editableSearchDraft(value), mode: 'split', query: '', artist, title });
  }

  function setMode(next: string): void {
    if (!modes.some((mode) => mode.value === next)) return;
    const resultMode = next as SearchResultMode;
    if (resultMode === value.resultMode) return;
    // The prototype starts a fresh condition set across the generic/music
    // boundary. The daemon then resolves defaults for the new concrete intent.
    const crossingGeneric = resultMode === 'generic' || value.resultMode === 'generic';
    const base = family(resultMode) === family(value.resultMode) ? value : editableSearchDraft(value);
    if (crossingGeneric) {
      base.searchSettings = undefined;
      base.expectedLength = undefined;
    }
    if (resultMode === 'generic' && value.mode === 'split') {
      onchange({ ...base, mode: 'simple', resultMode, query: [value.artist.trim(), value.title.trim()].filter(Boolean).join(' '), artist: '', title: '' });
    } else {
      onchange({ ...base, resultMode });
    }
  }

  function placeholder(): string {
    return {
      generic: 'Search files…',
      track: 'Search songs…',
      album: 'Search albums…',
      'song-aggregate': 'Search song aggregates…',
      'album-aggregate': 'Search album aggregates…',
    }[value.resultMode];
  }

  function handleInput(event: Event): void {
    const query = (event.currentTarget as HTMLInputElement).value;
    if (value.resultMode === 'generic') return setSimple(query);
    const delimiter = findDelimiter(query);
    if (!delimiter || suppressAutoSplit) {
      if (!delimiter) suppressAutoSplit = false;
      return setSimple(query);
    }
    setSplit(query.slice(0, delimiter.index), query.slice(delimiter.index + delimiter.length));
    focusTitleAfterUpdate();
  }

  function focusTitleAfterUpdate(): void {
    // The inputs only need the DOM update, not a later paint. A queued frame
    // could steal focus after the user has already chosen the artist field.
    void tick().then(() => searchRoot?.querySelector<HTMLInputElement>('#global-search-title')?.focus());
  }

  function split(): void {
    const delimiter = findDelimiter(value.query);
    setSplit(delimiter ? value.query.slice(0, delimiter.index) : value.query, delimiter ? value.query.slice(delimiter.index + delimiter.length) : '');
    focusTitleAfterUpdate();
  }

  function merge(): void {
    const combined = value.artist && value.title ? `${value.artist} - ${value.title}` : value.artist || value.title;
    suppressAutoSplit = true;
    setSimple(combined);
    void tick().then(() => {
      const input = searchRoot?.querySelector<HTMLInputElement>('#global-search-simple');
      input?.focus();
      input?.setSelectionRange(combined.length, combined.length);
    });
  }

  function submitDraft(download: boolean): void {
    if (download) ondownloadrequest(value);
    else onsubmit(value);
    dismissSearch();
  }

  function keydown(event: KeyboardEvent): void {
    if ((event.key === 'ArrowUp' || event.key === 'ArrowDown') && !event.altKey && !event.ctrlKey && !event.metaKey && !event.shiftKey) {
      event.preventDefault();
      const index = modes.findIndex((mode) => mode.value === value.resultMode);
      const next = (index + (event.key === 'ArrowDown' ? 1 : -1) + modes.length) % modes.length;
      const wasSplit = value.mode === 'split';
      setMode(modes[next]!.value);
      if (wasSplit && modes[next]!.value === 'generic') {
        void tick().then(() => searchRoot?.querySelector<HTMLInputElement>('#global-search-simple')?.focus());
      }
      return;
    }
    if (event.key === 'Enter') {
      event.preventDefault();
      if (busy) return;
      const download = event.ctrlKey && family(value.resultMode) !== 'generic';
      if (configurationPending) {
        queuedDownload = download;
        queuedSubmitKey = submissionKey();
        return;
      }
      if (queuedSubmitKey) return;
      if (!configurationError) submitDraft(download);
    }
  }

  function splitKeydown(event: KeyboardEvent, field: 'artist' | 'title'): void {
    if (event.key === 'Backspace' && !value[field]) {
      event.preventDefault();
      if (field === 'artist' || family(value.resultMode) === 'album') merge();
      else searchRoot?.querySelector<HTMLInputElement>('#global-search-artist')?.focus();
      return;
    }
    keydown(event);
  }

  function userKeydown(event: KeyboardEvent): void {
    if ((event.key === 'ArrowUp' || event.key === 'ArrowDown') && !event.altKey && !event.ctrlKey && !event.metaKey && !event.shiftKey) {
      event.preventDefault();
      onuserchange({ ...userValue, mode: userValue.mode === 'user' ? 'shares' : 'user' });
      return;
    }
    if (event.key !== 'Enter') return;
    event.preventDefault();
    onusersubmit(userValue);
    dismissSearch();
  }

  function windowKeydown(event: KeyboardEvent): void {
    const target = event.target as HTMLElement | null;
    if (event.key === 'Escape' && searchRoot?.contains(target)) {
      event.preventDefault();
      dismissSearch();
      return;
    }
    if (event.key === '/' && !target?.matches('input, textarea, select, [contenteditable="true"]')) {
      event.preventDefault();
      document.querySelector<HTMLInputElement>(variant === 'user' ? '#global-user-search' : value.mode === 'split' ? '#global-search-artist' : '#global-search-simple')?.focus();
    }
  }
</script>

<svelte:window onkeydown={windowKeydown} onpointerdown={outsidePointerDown} />

<div class:mode-menu-open={modeMenuOpen} class="global-search" bind:this={searchRoot} onfocusin={() => searchEngaged = true} aria-label={variant === 'user' ? 'Browse Soulseek user' : 'Global Sockseek search'}>
  <div class="search-controls-row">
    {#if variant === 'user'}
      <div class="search-entry">
        <ModeIconToggle value={userValue.mode} options={userModes} ariaLabel="User browse mode" onchange={(mode) => { if (mode === 'user' || mode === 'shares') onuserchange({ ...userValue, mode }); }} onopenchange={(open) => modeMenuOpen = open} />
        <div class="search-bar simple has-mode-picker user-browser-global-search">
          <input id="global-user-search" value={userValue.query} placeholder={userValue.mode === 'shares' ? 'Search user shares…' : 'Search users…'} autocomplete="off" spellcheck="false" aria-label={userValue.mode === 'shares' ? 'Search user shares' : 'Search users'} oninput={(event) => onuserchange({ ...userValue, query: (event.currentTarget as HTMLInputElement).value })} onkeydown={userKeydown} />
          <span class="search-shortcut" aria-hidden="true">/</span>
        </div>
      </div>
      <span class="search-settings-spacer" aria-hidden="true"></span>
    {:else}
    <div class="search-entry">
      <ModeIconToggle value={value.resultMode} options={modes} ariaLabel="Search result mode" onchange={setMode} onhelp={() => { settingsOpen = false; modeHelpOpen = true; }} onopenchange={(open) => modeMenuOpen = open} />
      {#if value.mode === 'simple'}
        <div class="search-bar simple has-mode-picker">
          <input id="global-search-simple" value={value.query} disabled={busy} placeholder={placeholder()} autocomplete="off" spellcheck="false" aria-label={placeholder().replace('…', '')} oninput={handleInput} onkeydown={keydown} />
          <span class="search-shortcut" aria-hidden="true">/</span>
          {#if value.resultMode !== 'generic'}<button type="button" class="search-mode-button" onclick={split}>split</button>{/if}
        </div>
      {:else}
        <div class="search-bar split has-mode-picker">
          <label class="search-field"><span>artist</span><input id="global-search-artist" value={value.artist} placeholder="artist…" autocomplete="off" spellcheck="false" oninput={(event) => setSplit((event.currentTarget as HTMLInputElement).value, value.title)} onkeydown={(event) => splitKeydown(event, 'artist')} /></label>
          <span class="search-divider" aria-hidden="true"></span>
          <label class="search-field"><span>{family(value.resultMode) === 'album' ? 'album' : 'track'}</span><input id="global-search-title" value={value.title} placeholder={family(value.resultMode) === 'album' ? 'album…' : 'track…'} autocomplete="off" spellcheck="false" oninput={(event) => setSplit(value.artist, (event.currentTarget as HTMLInputElement).value)} onkeydown={(event) => splitKeydown(event, 'title')} /></label>
          <button type="button" class="search-mode-button" onclick={merge}>merge</button>
        </div>
      {/if}

    </div>
    <button type="button" class:active={settingsOpen} class="search-settings-button" aria-label="Search configuration" aria-expanded={settingsOpen} onclick={() => { settingsMounted = true; settingsOpen = !settingsOpen; }}><Icon name="sliders" /></button>
    {/if}
  </div>
  {#if variant === 'content' && settingsMounted}
    <section data-shortcuts-blocked={settingsOpen} class="search-config-popover" aria-label="Search configuration" style="top: 62px; max-height: calc(100dvh - 87px)" style:display={settingsOpen ? undefined : 'none'}>
      <SearchConfiguration draft={value} active={settingsOpen} bind:resolved={conditionsResolved} {onchange} onvalidation={(message, pending) => { configurationError = message; configurationPending = pending; }} onclose={() => settingsOpen = false} />
    </section>
  {/if}
  {#if variant === 'content' && settingsOpen}
    <button class="search-focus-backdrop" type="button" tabindex="-1" aria-label="Close focused search" onclick={dismissSearch}></button>
  {/if}
  {#if variant === 'content' && !settingsOpen && configurationError}<p role="alert">{configurationError} Open Search configuration to resolve it.</p>{/if}
</div>


{#if modeHelpOpen}
  <Modal title="Search modes" closeLabel="Close search modes" onclose={() => modeHelpOpen = false}>
    <div class="search-mode-help-cards">
      {#each modes as mode}
        <button type="button" class="search-mode-help-card" aria-label={`Use ${mode.label}`} aria-describedby={`search-mode-help-${mode.value}`} aria-pressed={value.resultMode === mode.value}
          onclick={() => { setMode(mode.value); modeHelpOpen = false; }}>
          <span class="search-mode-help-icon"><Icon name={mode.icon} /></span>
          <span class="search-mode-help-copy"><strong>{mode.label}</strong><span id={`search-mode-help-${mode.value}`}>{mode.description}</span></span>
          <span class="search-mode-help-check">{#if value.resultMode === mode.value}<Icon name="check" />{/if}</span>
        </button>
      {/each}
    </div>
  </Modal>
{/if}
