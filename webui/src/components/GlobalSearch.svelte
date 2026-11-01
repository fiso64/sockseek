<script lang="ts">
  import { tick } from 'svelte';
  import type { SearchDraft, SearchResultMode } from '../domains/search/model';
  import { searchModeFamily as family } from '../domains/search/model';
  import ModeIconToggle from './ModeIconToggle.svelte';
  import Icon from './Icon.svelte';
  import SearchConfiguration from '../domains/search/SearchConfiguration.svelte';
  import SearchConditionPills from './SearchConditionPills.svelte';
  import { createEmptySearchConditions } from '../domains/search/config-state';
  import { hasAppliedConditions } from '../domains/search/applied-conditions';

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
    onuserchange: (value: UserBrowseDraft) => void;
    onusersubmit: (value: UserBrowseDraft) => void;
  }

  let { variant = 'content', value, userValue, busy = false, onchange, onsubmit, onuserchange, onusersubmit }: Props = $props();
  let suppressAutoSplit = $state(false);
  let settingsOpen = $state(false);
  let modeMenuOpen = $state(false);
  let settingsMounted = $state(false);
  let configurationError = $state<string | null>(null);
  let configurationPending = $state(false);
  let queuedSubmitKey = $state<string | null>(null);
  let searchRoot: HTMLDivElement;
  let searchEngaged = $state(false);
  let conditions = $state(createEmptySearchConditions());
  let conditionsResolved = $state(false);
  let conditionOverlayHeight = $state(0);
  let overlayVisible = $derived(conditionsResolved && (searchEngaged || settingsOpen) && hasAppliedConditions(value.resultMode, conditions));
  $effect(() => { if (searchEngaged && variant === 'content') settingsMounted = true; });
  $effect(() => { variant; modeMenuOpen = false; });

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
      onsubmit(value);
      dismissSearch();
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
    { value: 'generic', label: 'File Search', icon: 'generic' as const },
    { value: 'track', label: 'Track Search', icon: 'track' as const },
    { value: 'album', label: 'Album Search', icon: 'album' as const },
    { value: 'song-aggregate', label: 'Song Aggregate', icon: 'song-aggregate' as const },
    { value: 'album-aggregate', label: 'Album Aggregate', icon: 'album-aggregate' as const },
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
    onchange({ ...value, mode: 'simple', query, artist: '', title: '' });
  }

  function setSplit(artist: string, title: string): void {
    onchange({ ...value, mode: 'split', query: '', artist, title });
  }

  function setMode(next: string): void {
    if (!modes.some((mode) => mode.value === next)) return;
    const resultMode = next as SearchResultMode;
    if (resultMode === value.resultMode) return;
    // The prototype starts a fresh condition set across the generic/music
    // boundary. The daemon then resolves defaults for the new concrete intent.
    const crossingGeneric = resultMode === 'generic' || value.resultMode === 'generic';
    const base = crossingGeneric
      ? { ...value, searchSettings: undefined, expectedLength: undefined }
      : value;
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

  function keydown(event: KeyboardEvent): void {
    if (event.key === 'Enter') {
      event.preventDefault();
      if (busy) return;
      if (configurationPending) {
        queuedSubmitKey = submissionKey();
        return;
      }
      if (queuedSubmitKey) return;
      if (!configurationError) { onsubmit(value); dismissSearch(); }
    }
  }

  function userKeydown(event: KeyboardEvent): void {
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
      <ModeIconToggle value={value.resultMode} options={modes} ariaLabel="Search result mode" onchange={setMode} onopenchange={(open) => modeMenuOpen = open} />
      {#if value.mode === 'simple'}
        <div class="search-bar simple has-mode-picker">
          <input id="global-search-simple" value={value.query} disabled={busy} placeholder={placeholder()} autocomplete="off" spellcheck="false" aria-label={placeholder().replace('…', '')} oninput={handleInput} onkeydown={keydown} />
          <span class="search-shortcut" aria-hidden="true">/</span>
          {#if value.resultMode !== 'generic'}<button type="button" class="search-mode-button" onclick={split}>split</button>{/if}
        </div>
      {:else}
        <div class="search-bar split has-mode-picker">
          <label class="search-field"><span>artist</span><input id="global-search-artist" value={value.artist} placeholder="artist…" autocomplete="off" spellcheck="false" oninput={(event) => setSplit((event.currentTarget as HTMLInputElement).value, value.title)} onkeydown={(event) => { if (event.key === 'Backspace' && !value.artist) { event.preventDefault(); merge(); } else keydown(event); }} /></label>
          <span class="search-divider" aria-hidden="true"></span>
          <label class="search-field"><span>{family(value.resultMode) === 'album' ? 'album' : 'track'}</span><input id="global-search-title" value={value.title} placeholder={family(value.resultMode) === 'album' ? 'album…' : 'track…'} autocomplete="off" spellcheck="false" oninput={(event) => setSplit(value.artist, (event.currentTarget as HTMLInputElement).value)} onkeydown={(event) => { if (event.key === 'Backspace' && !value.title) { event.preventDefault(); document.querySelector<HTMLInputElement>('#global-search-artist')?.focus(); } else keydown(event); }} /></label>
          <button type="button" class="search-mode-button" onclick={merge}>merge</button>
        </div>
      {/if}
      {#if overlayVisible}
        <section class="search-focus-overlay" aria-label="Applied search conditions" bind:clientHeight={conditionOverlayHeight}>
          <div class="search-condition-pills"><SearchConditionPills mode={value.resultMode} bind:conditions /></div>
        </section>
      {/if}
    </div>
    <button type="button" class:active={settingsOpen} class="search-settings-button" aria-label="Search configuration" aria-expanded={settingsOpen} onclick={() => { settingsMounted = true; settingsOpen = !settingsOpen; }}><Icon name="sliders" /></button>
    {/if}
  </div>
  {#if variant === 'content' && settingsMounted}
    {@const configurationTop = 62 + (overlayVisible ? conditionOverlayHeight + 8 : 0)}
    <section class="search-config-popover" aria-label="Search configuration" style={`top: ${configurationTop}px; max-height: calc(100dvh - 25px - ${configurationTop}px)`} style:display={settingsOpen ? undefined : 'none'}>
      <SearchConfiguration draft={value} active={settingsOpen} prefetch={searchEngaged} bind:conditions bind:resolved={conditionsResolved} {onchange} onvalidation={(message, pending) => { configurationError = message; configurationPending = pending; }} onclose={() => settingsOpen = false} />
    </section>
  {/if}
  {#if variant === 'content' && (overlayVisible || settingsOpen)}
    <button class="search-focus-backdrop" type="button" tabindex="-1" aria-label="Close focused search" onclick={dismissSearch}></button>
  {/if}
  {#if variant === 'content' && !settingsOpen && configurationError}<p role="alert">{configurationError} Open Search configuration to resolve it.</p>{/if}
</div>
