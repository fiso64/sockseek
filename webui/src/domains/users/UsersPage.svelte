<script lang="ts">
  import ShareBrowser from './ShareBrowser.svelte';
  import { peerShareSource } from './share-source';
  import { userPresence } from './presence.svelte';
  import LoadingIndicator from '../../components/LoadingIndicator.svelte';
  import { onDestroy, untrack } from 'svelte';
  import type { UserBrowseDto, UserProfileDto } from '../../api/types';
  import type { DaemonLiveClient, LiveConnectionStatus } from '../../api/live';
  import { daemonApi } from '../../api/client';
  import { formatBytes, formatCount, formatSpeed, userInitials } from '../../app/format';
  import Icon from '../../components/Icon.svelte';
  import LinkifiedText from '../../components/LinkifiedText.svelte';

  import type { UserBrowserSession, UserBrowseView } from './session';

  interface Props {
    session: UserBrowserSession;
    view: UserBrowseView;
    liveStatus: LiveConnectionStatus;
    liveClient: DaemonLiveClient;
    onviewchange: (view: UserBrowseView) => void;
    onmessageuser: (username: string) => void;
  }

  let { session, view, liveStatus, liveClient, onviewchange, onmessageuser }: Props = $props();
  // App keys this component by session. A new user starts a fresh lifetime;
  // ordinary navigation restores the saved view without keeping its DOM alive.
  const username = untrack(() => session.username);
  const saved = untrack(() => session.snapshot);
  let profile = $state<UserProfileDto | null>(saved?.profile ?? null);
  let profileLoading = $state(false);
  let profileError = $state<Error | null>(null);
  let profilePictureOpen = $state(false);
  let browse = $state<UserBrowseDto | null>(saved?.browse ?? null);
  let browseError = $state<Error | null>(null);
  let lifetime = new AbortController();
  let browseTimer: ReturnType<typeof setTimeout> | null = null;
  let browsePollAttempts = 0;
  let wasOffline = false;
  let browseStreamFailed = false;
  const activeBrowseId = $derived(view === 'shares' && browse
    && (browse.state === 'queued' || browse.state === 'running') ? browse.browseId : null);

  let pictureUrl = $derived(profile?.picture?.url ? absoluteApiPath(profile.picture.url) : null);

  $effect(() => {
    const currentView = view;
    untrack(() => {
      if (currentView === 'user' && !profile && !profileLoading) void loadProfile(username);
    });
  });

  $effect(() => {
    const currentView = view;
    const currentUsername = username;
    if (currentView === 'shares' && (!browse || browse.username !== currentUsername)) void acquireBrowse(currentUsername);
  });

  $effect(() => {
    if (liveStatus === 'offline') {
      wasOffline = true;
      return;
    }
    if (liveStatus !== 'connected' || !wasOffline) return;
    wasOffline = false;
    if (view === 'user' && profileError && !profile && !profileLoading) void loadProfile(username);
    if (view === 'shares' && browseError) {
      if (!browse) void acquireBrowse(username);
      else if (browse.state === 'queued' || browse.state === 'running') {
        browseError = null;
        void advanceBrowse();
      }
    }
  });

  $effect(() => {
    const browseId = activeBrowseId;
    const connected = liveStatus === 'connected';
    if (!browseId || !connected) {
      untrack(() => { void advanceBrowse(); });
      return;
    }
    let active = true;
    let unsubscribe: (() => Promise<void>) | null = null;
    browseStreamFailed = false;
    untrack(() => { void advanceBrowse(); });
    const failed = () => {
      if (!active) return;
      active = false;
      browseStreamFailed = true;
      void unsubscribe?.();
      void advanceBrowse();
    };
    void liveClient.subscribeUserBrowse(browseId, value => {
      if (!active || lifetime.signal.aborted) return;
      browse = value;
      browseError = null;
      void advanceBrowse();
    }, failed).then(release => {
      if (!active) void release();
      else unsubscribe = release;
    }).catch(failed);
    return () => { active = false; void unsubscribe?.(); };
  });

  onDestroy(() => {
    lifetime.abort();
    if (browseTimer) clearTimeout(browseTimer);
    session.snapshot = { profile, browse };
  });

  const presence = userPresence(() => view === 'user' ? username : null, () => liveClient, () => liveStatus);

  async function loadProfile(value: string, refresh = false): Promise<void> {
    const signal = lifetime.signal;
    profileLoading = true;
    profileError = null;
    try {
      const result = await daemonApi.getUserProfile(value, refresh, signal);
      if (!signal.aborted) profile = result;
    }
    catch (cause) { if (!signal.aborted) profileError = asError(cause); }
    finally { if (!signal.aborted) profileLoading = false; }
  }

  async function acquireBrowse(value: string, refresh = false): Promise<void> {
    if (browse?.username === value && (browse.state === 'running' || browse.state === 'queued' || browse.state === 'complete') && !refresh) return;
    browseError = null;
    const signal = lifetime.signal;
    try {
      const result = await daemonApi.startUserBrowse(value, refresh, signal);
      if (signal.aborted) return;
      browse = result;
      browsePollAttempts = 0;
      await advanceBrowse();
    } catch (cause) {
      if (!signal.aborted) browseError = asError(cause);
    }
  }

  async function advanceBrowse(): Promise<void> {
    if (browseTimer) clearTimeout(browseTimer);
    browseTimer = null;
    if (!browse || view !== 'shares' || lifetime.signal.aborted) return;
    if (browse.state === 'complete') return;
    if (browse.state === 'failed' || browse.state === 'cancelled') {
      browseError = new Error(browse.failure?.error ?? `Browse ${browse.state}.`);
      return;
    }
    if (liveStatus === 'connected' && !browseStreamFailed) return;
    const delay = browsePollAttempts++ === 0 ? 500 : 1_000;
    browseTimer = setTimeout(async () => {
      browseTimer = null;
      if (!browse || lifetime.signal.aborted) return;
      const signal = lifetime.signal;
      try {
        const result = await daemonApi.getUserBrowse(browse.browseId, signal);
        if (signal.aborted) return;
        browse = result;
        await advanceBrowse();
      } catch (cause) {
        if (!signal.aborted) browseError = asError(cause);
      }
    }, delay);
  }

  function absoluteApiPath(value: string): string { return value.startsWith('/') ? value : `/${value}`; }
  function asError(cause: unknown): Error { return cause instanceof Error ? cause : new Error(String(cause)); }
</script>

<svelte:window onkeydown={(event) => {
  if (event.key === 'Escape') profilePictureOpen = false;
}} />

<section class="page user-browser-page">
  <header class="user-browser-heading">
    <div><h1>User browser</h1></div>
    <nav class="user-view-tabs" aria-label="User browser views">
      <button type="button" class:active={view === 'user'}
        aria-current={view === 'user' ? 'page' : undefined}
        onclick={() => onviewchange('user')}>User</button>
      <button type="button" class:active={view === 'shares'}
        aria-current={view === 'shares' ? 'page' : undefined}
        onclick={() => onviewchange('shares')}>Shares</button>
    </nav>
  </header>

  {#if view === 'user'}
    {#if profileError}
      <div class={`resource-state-notice ${liveStatus === 'offline' ? 'offline' : 'unavailable'}`}
        class:blocking={!profile} role="alert">
        <span class="resource-state-dot"></span>
        <span class="resource-state-copy">
          <strong>{liveStatus === 'offline' ? 'Daemon unavailable' : 'Profile unavailable'}</strong>
          <small>{liveStatus === 'offline' ? 'Profile data cannot be loaded while the daemon is offline.' : profileError.message}</small>
        </span>
        {#if liveStatus !== 'offline'}
          <button type="button" disabled={profileLoading}
            onclick={() => void loadProfile(username)}>Retry</button>
        {/if}
      </div>
    {/if}
    {#if profileLoading && !profile && !profileError}
      <LoadingIndicator label="Loading profile" blocking />
    {/if}
    {#if profile}
      <article class="user-profile-card">
        <div class="user-profile-primary">
          {#if pictureUrl}
            <button type="button" class="user-profile-picture-button"
              aria-label={`View ${profile.username}'s profile picture`}
              onclick={() => (profilePictureOpen = true)}>
              <img class="user-profile-picture" src={pictureUrl} alt="" />
            </button>
          {:else}
            <div class="user-profile-picture user-profile-placeholder" aria-label="No profile picture">
              <Icon name="user"/><span>{userInitials(profile.username)}</span>
            </div>
          {/if}
          <div class="user-profile-copy">
            <div class="user-profile-name-line">
              <h2>{profile.username}</h2>
              <span class={`user-presence ${presence.state}`} data-presence={presence.state}><i></i>{presence.label}</span>
              <span class="user-capacity-inline" aria-label="Upload capacity">
                <span class:available={profile.hasFreeUploadSlot === true}
                  class:unknown={profile.hasFreeUploadSlot == null} class="slot-state"><i></i>{profile.hasFreeUploadSlot == null ? 'Slot unknown' : profile.hasFreeUploadSlot ? 'Free slot' : 'No free slot'}</span>
                <span><strong>{profile.uploadSlots ?? '—'}</strong> slots</span>
                <span><strong>{profile.queueLength ?? '—'}</strong> queued</span>
              </span>
            </div>
            {#if profile.description}
              <p class="user-profile-description"><LinkifiedText text={profile.description}/></p>
            {/if}
            <div class="user-profile-actions">
              <button type="button" class="user-message-button" disabled={profileLoading}
                onclick={() => loadProfile(username, true)}>
                <Icon name="refresh"/><span>{profileLoading ? 'Refreshing…' : 'Refresh'}</span>
              </button>
              <button type="button" class="user-message-button" disabled={profile.privateMessagesBlocked}
                onclick={() => onmessageuser(profile!.username)}>
                <Icon name="chat"/>
                <span>{profile.privateMessagesBlocked ? 'Messages blocked' : 'Message'}</span>
              </button>
            </div>
          </div>
        </div>
        {#if profile.info.state !== 'available' || profile.statistics.state !== 'available' || profile.pictureSection.state !== 'available'}
          <div class="profile-section-states" aria-label="Profile section availability">
            {#if profile.info.state !== 'available'}
              <span>Info {profile.info.state}{profile.info.reason ? ` · ${profile.info.reason}` : ''}</span>
            {/if}
            {#if profile.statistics.state !== 'available'}
              <span>Statistics {profile.statistics.state}{profile.statistics.reason ? ` · ${profile.statistics.reason}` : ''}</span>
            {/if}
            {#if profile.pictureSection.state !== 'available'}
              <span>Picture {profile.pictureSection.state}{profile.pictureSection.reason ? ` · ${profile.pictureSection.reason}` : ''}</span>
            {/if}
          </div>
        {/if}
        <div class="user-profile-stats">
          <div><span>Shared files</span><strong>{profile.sharedFileCount == null ? '—' : formatCount(profile.sharedFileCount)}</strong></div>
          <div><span>Shared folders</span><strong>{profile.sharedDirectoryCount == null ? '—' : formatCount(profile.sharedDirectoryCount)}</strong></div>
          <div><span>Average upload speed</span><strong>{formatSpeed(profile.averageUploadSpeed) ?? '—'}</strong></div>
          <div><span>Uploads</span><strong>{profile.uploadCount == null ? '—' : formatCount(profile.uploadCount)}</strong></div>
        </div>
      </article>
    {/if}
  {:else}
    {#if browseError}
      <div class={`resource-state-notice ${liveStatus === 'offline' ? 'offline' : 'unavailable'}`}
        class:blocking={!browse || browse.state !== 'complete'} role="alert">
        <span class="resource-state-dot"></span>
        <span class="resource-state-copy">
          <strong>{liveStatus === 'offline' ? 'Daemon unavailable' : 'Shares unavailable'}</strong>
          <small>{liveStatus === 'offline' ? 'Shared files cannot be loaded while the daemon is offline.' : browseError.message}</small>
        </span>
      </div>
    {/if}
    {#if !browseError && (!browse || browse.state === 'queued' || browse.state === 'running')}
      {#if browse && Number(browse.compressedBytesReceived) > 0}
        <div class="resource-state-notice loading blocking">
          <span class="resource-state-dot"></span>
          <span class="resource-state-copy">
            <strong>{browse?.phase === 'receiving' ? 'Receiving shares' : browse?.phase === 'indexing' ? 'Indexing shares' : 'Browsing user'}</strong>
            <small>{browse ? `${formatBytes(browse.compressedBytesReceived)} received${browse.compressedBytesExpected ? ` of ${formatBytes(browse.compressedBytesExpected)}` : ''}` : `Requesting ${username}'s shares.`}</small>
          </span>
        </div>
      {:else}
        <LoadingIndicator label="Loading shares" blocking />
      {/if}
    {/if}
    {#if browse?.state === 'complete'}
      {#key browse.browseId}
        <ShareBrowser source={peerShareSource(browse)} session={session.shares} />
      {/key}
    {/if}
  {/if}
</section>

{#if profilePictureOpen && pictureUrl}
  <div class="profile-picture-lightbox">
    <button type="button" class="profile-picture-lightbox-backdrop"
      aria-label="Close profile picture" onclick={() => (profilePictureOpen = false)}></button>
    <div class="profile-picture-lightbox-dialog" role="dialog" aria-modal="true"
      aria-label={`${profile?.username} profile picture`}>
      <img src={pictureUrl} alt={`${profile?.username} profile picture`}/>
      <button type="button" class="profile-picture-lightbox-close"
        aria-label="Close profile picture" onclick={() => (profilePictureOpen = false)}>×</button>
    </div>
  </div>
{/if}
