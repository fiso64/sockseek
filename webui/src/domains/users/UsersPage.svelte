<script lang="ts">
  import { appPath } from '../../app/paths';
  import ShareBrowser from './ShareBrowserView.svelte';
  import { peerShareSource } from './share-source';
  import { userPresence } from './presence.svelte';
  import LoadingIndicator from '../../components/LoadingIndicator.svelte';
  import { onDestroy, untrack } from 'svelte';
  import type { UserBrowseDto, UserProfileDto } from '../../api/types';
  import type { DaemonLiveClient, LiveConnectionStatus } from '../../api/live';
  import { daemonApi } from '../../api/client';
  import { formatBytes, formatCount, formatSpeed } from '../../app/format';
  import Icon from '../../components/Icon.svelte';
  import RelativeTime from '../../components/RelativeTime.svelte';
  import LinkifiedText from '../../components/LinkifiedText.svelte';
  import { copySoulseekLink, formatSoulseekLink } from './soulseek-link';
  import ProfilePicture from '../../components/ProfilePicture.svelte';

  import type { UserBrowserSession, UserBrowseView } from './session';
  import type { UserLinkActions } from '../../app/navigation';

  interface Props {
    session: UserBrowserSession;
    view: UserBrowseView;
    liveStatus: LiveConnectionStatus;
    documentVisible: boolean;
    configurationRevision?: number;
    liveClient: DaemonLiveClient;
    onviewchange: (view: UserBrowseView) => void;
    onmessageuser: (username: string) => void;
    onopenjob: (jobId: string) => void;
    userActions: UserLinkActions;
    /** The shares filter left the folder or file its address links to. */
    onleavelink?: () => void;
  }

  let { session, view, liveStatus, documentVisible, configurationRevision, liveClient, onviewchange, onmessageuser, onopenjob, userActions, onleavelink }: Props = $props();
  // App keys this component by session. A new user starts a fresh lifetime;
  // ordinary navigation restores the saved view without keeping its DOM alive.
  const username = untrack(() => session.username);
  const saved = untrack(() => session.snapshot);
  let profile = $state<UserProfileDto | null>(saved?.profile ?? null);
  let profileLoading = $state(false);
  let profileError = $state<Error | null>(null);
  let profileRequest: AbortController | null = null;
  // Cached profiles return at once; offer cancellation only for a request
  // that is evidently waiting on the peer.
  let profileWaiting = $state(false);
  let browse = $state<UserBrowseDto | null>(saved?.browse ?? null);
  let displayedBrowse = $state<UserBrowseDto | null>(saved?.browse?.state === 'complete' ? saved.browse : null);
  let browseStarting = $state(false);
  let browseError = $state<Error | null>(null);
  let lifetime = new AbortController();
  let browseTimer: ReturnType<typeof setTimeout> | null = null;
  let browsePollAttempts = 0;
  let wasOffline = false;
  let browseStreamFailed = false;
  const activeBrowseId = $derived(view === 'shares' && browse
    && (browse.state === 'queued' || browse.state === 'running') ? browse.browseId : null);
  const browsePending = $derived(browseStarting || (!browseError && (browse?.state === 'queued' || browse?.state === 'running')));
  // A cached or completed browse is returned complete by its start request.
  const cancellable = $derived(view === 'user' ? profileWaiting
    : !browseError && (browse?.state === 'queued' || browse?.state === 'running'));

  let pictureUrl = $derived(profile?.picture?.url ? appPath(profile.picture.url) : null);

  $effect(() => {
    const currentView = view;
    untrack(() => {
      if (currentView === 'user' && !profile && !profileLoading) void loadProfile(username);
    });
  });

  $effect(() => {
    const currentView = view;
    untrack(() => {
      if (currentView === 'shares' && (!browse || browse.username !== username)) void acquireBrowse(username);
    });
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
      updateBrowse(value);
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
    session.snapshot = { profile, browse: displayedBrowse ?? browse };
  });

  const presence = userPresence(() => view === 'user' ? username : null, () => liveClient, () => liveStatus);
  // Only an explicit offline result can fill in a failed status lookup. Cached
  // profile details must not override live presence or a lost connection.
  const displayedPresence = $derived(
    (presence.state === 'unavailable' || presence.state === 'unknown')
      && (profile?.info.reason === 'offline' || profile?.statistics.reason === 'offline')
      ? 'offline' : presence.state);
  const unavailableSections = $derived(profile ? [
    { label: 'Info', ...profile.info },
    { label: 'Statistics', ...profile.statistics },
    { label: 'Picture', ...profile.pictureSection },
  ].filter(section => section.state !== 'available' && section.reason !== 'offline') : []);

  async function loadProfile(value: string, refresh = false): Promise<void> {
    profileRequest?.abort();
    const request = new AbortController();
    profileRequest = request;
    const signal = AbortSignal.any([lifetime.signal, request.signal]);
    profileLoading = true;
    profileError = null;
    const waiting = setTimeout(() => { if (!signal.aborted) profileWaiting = true; }, 400);
    try {
      const result = await daemonApi.getUserProfile(value, refresh, signal);
      if (!signal.aborted) profile = result;
    }
    catch (cause) { if (!signal.aborted) profileError = asError(cause); }
    finally {
      clearTimeout(waiting);
      if (!signal.aborted) {
        profileLoading = false;
        profileWaiting = false;
      }
    }
  }

  async function cancelPeerRequest(): Promise<void> {
    if (view === 'user') {
      if (!profileRequest || lifetime.signal.aborted) return;
      profileRequest.abort();
      profileRequest = null;
      profileLoading = false;
      profileWaiting = false;
      profileError = new Error('Profile request cancelled.');
      return;
    }
    if (!browse) return;
    const signal = lifetime.signal;
    try {
      const result = await daemonApi.cancelUserBrowse(browse.browseId);
      if (signal.aborted) return;
      updateBrowse(result);
      await advanceBrowse();
    } catch (cause) {
      if (!signal.aborted) browseError = asError(cause);
    }
  }

  async function acquireBrowse(value: string, refresh = false): Promise<void> {
    if (browsePending) return;
    if (browse?.username === value && (browse.state === 'running' || browse.state === 'queued' || browse.state === 'complete') && !refresh) return;
    browseStarting = true;
    browseError = null;
    const signal = lifetime.signal;
    try {
      const result = await daemonApi.startUserBrowse(value, refresh, signal);
      if (signal.aborted) return;
      updateBrowse(result);
      browsePollAttempts = 0;
      await advanceBrowse();
    } catch (cause) {
      if (!signal.aborted) browseError = asError(cause);
    } finally {
      if (!signal.aborted) browseStarting = false;
    }
  }

  function updateBrowse(value: UserBrowseDto): void {
    browse = value;
    if (value.state === 'complete') displayedBrowse = value;
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
        updateBrowse(result);
        await advanceBrowse();
      } catch (cause) {
        if (!signal.aborted) browseError = asError(cause);
      }
    }, delay);
  }

  function asError(cause: unknown): Error { return cause instanceof Error ? cause : new Error(String(cause)); }
</script>



<section class="page user-browser-page">
  <header class="page-heading user-browser-heading">
    <div class="page-heading-title">
      <!-- Both titles share one cell so the view toggle does not move between them. -->
      <h1 class="user-browser-title">
        <span aria-hidden={view === 'shares'}>Users</span><span aria-hidden={view !== 'shares'}>Shares</span>
      </h1>
      <div class="page-heading-tabs">
        <nav class="segmented-control user-view-tabs" aria-label="User browser views">
          <button type="button" class:active={view === 'user'}
            aria-current={view === 'user' ? 'page' : undefined}
            onclick={() => onviewchange('user')}>User</button>
          <button type="button" class:active={view === 'shares'}
            aria-current={view === 'shares' ? 'page' : undefined}
            onclick={() => onviewchange('shares')}>Shares</button>
        </nav>
      </div>
    </div>
    <div class="user-browser-actions">
      {#if cancellable}
        <button type="button" class="button danger action-button" data-testid="cancel-peer-request"
          onclick={() => void cancelPeerRequest()}><Icon name="x"/><span>Cancel</span></button>
      {/if}
      {#if view === 'user'}
        <button type="button" class="button action-button" data-testid="refresh-user-profile"
          disabled={profileLoading || !profile} onclick={() => loadProfile(username, true)}>
          <Icon name="refresh"/><span>{profileLoading ? 'Refreshing…' : 'Refresh'}</span>
        </button>
      {:else}
        <button type="button" class="button action-button" data-testid="refresh-user-shares"
          disabled={browsePending || !displayedBrowse} onclick={() => void acquireBrowse(username, true)}>
          <Icon name="refresh"/><span>{browsePending ? 'Refreshing…' : 'Refresh'}</span>
        </button>
      {/if}
    </div>
  </header>

  {#if view === 'user'}
    {#if profileError}
      <div class={`resource-state-notice ${liveStatus === 'offline' ? 'offline' : 'unavailable'}`}
        class:blocking={!profile} role="alert">
        <span class="resource-state-dot"></span>
        <span class="resource-state-copy">
          <strong>{liveStatus === 'offline' ? 'Daemon unavailable' : 'Profile unavailable'}</strong>
          <small>{liveStatus === 'offline' ? 'Profiles can’t be loaded while the daemon is offline.' : profileError.message}</small>
        </span>
        {#if liveStatus !== 'offline'}
          <button class="button" type="button" disabled={profileLoading}
            onclick={() => void loadProfile(username)}>Retry</button>
        {/if}
      </div>
    {/if}
    {#if profileLoading && !profile && !profileError}
      <LoadingIndicator label="Loading profile" blocking />
    {/if}
    {#if profile}
      <article class="panel user-profile-card">
        <div class="user-profile-primary">
          {#if pictureUrl}<ProfilePicture src={pictureUrl} username={profile.username} />{/if}
          <div class="user-profile-copy">
            <div class="user-profile-name-line">
              <h2>{profile.username}</h2>
              <span class={`badge user-presence ${displayedPresence}`} data-presence={displayedPresence}><i></i>{displayedPresence === 'offline' ? 'Offline' : presence.label}</span>
              <span class="user-capacity-inline" aria-label="Upload capacity">
                <span class:available={profile.hasFreeUploadSlot === true}
                  class:unknown={profile.hasFreeUploadSlot == null} class="slot-state"><i></i>{profile.hasFreeUploadSlot == null ? 'Slot unknown' : profile.hasFreeUploadSlot ? 'Free slot' : 'No free slot'}</span>
                <span><strong>{profile.uploadSlots ?? '—'}</strong> {profile.uploadSlots === 1 ? 'slot' : 'slots'}</span>
                <span><strong>{profile.queueLength ?? '—'}</strong> queued</span>
                {#if profile.info.state === 'available'}<span>Reported <RelativeTime timestamp={profile.observedAt} lowercase /></span>{/if}
              </span>
            </div>
            {#if profile.description}
              <p class="user-profile-description"><LinkifiedText text={profile.description}/></p>
            {/if}
            <div class="user-profile-actions">
              <button type="button" class="button user-message-button" disabled={profile.privateMessagesBlocked}
                onclick={() => onmessageuser(profile!.username)}>
                <Icon name="chat"/>
                <span>{profile.privateMessagesBlocked ? 'Messages blocked' : 'Message'}</span>
              </button>
              <button type="button" class="button user-message-button" data-action="copy-profile-link"
                onclick={() => void copySoulseekLink(formatSoulseekLink(profile!.username))}>
                <Icon name="link"/>
                <span>Copy link</span>
              </button>
            </div>
          </div>
        </div>
        {#if unavailableSections.length}
          <div class="profile-section-states" aria-label="Profile section availability">
            {#each unavailableSections as section (section.label)}
              <span>{section.label} {section.state}{section.reason ? ` · ${section.reason}` : ''}</span>
            {/each}
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
        class:blocking={!displayedBrowse} role="alert">
        <span class="resource-state-dot"></span>
        <span class="resource-state-copy">
          <strong>{liveStatus === 'offline' ? 'Daemon unavailable' : 'Shares unavailable'}</strong>
          <small>{liveStatus === 'offline' ? 'Shared files can’t be loaded while the daemon is offline.' : browseError.message}</small>
        </span>
        {#if !displayedBrowse}
          <button type="button" class="button" disabled={browsePending}
            onclick={() => void acquireBrowse(username, true)}>Retry</button>
        {/if}
      </div>
    {/if}
    {#if !browseError && !displayedBrowse && (!browse || browsePending)}
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
    {#if displayedBrowse}
      <ShareBrowser {configurationRevision} source={peerShareSource(displayedBrowse)} session={session.shares} connected={liveStatus === 'connected' && documentVisible} {userActions} {onopenjob} {onleavelink} />
    {/if}
  {/if}
</section>
