<script lang="ts">
  import { flushSync, type Snippet } from 'svelte';
  import { navigationItems, type PageId } from '../app/navigation';
  import Notifications from './Notifications.svelte';
  import type { LiveConnectionStatus, ReplicatedDaemonState } from '../api/live';
  import type { SearchDraft } from '../domains/search/model';
  import { humanizeStateValue, soulseekStatusLabel } from '../app/format';
  import GlobalSearch from './GlobalSearch.svelte';
  import type { UserBrowseDraft } from './GlobalSearch.svelte';
  import { keyboardShortcutsBlocked } from '../lib/keyboard';
  import Sidebar from './Sidebar.svelte';
  import { dismissedUpdate } from '../domains/system/update.svelte';

  interface Props {
    activePage: PageId;
    liveStatus: LiveConnectionStatus;
    liveState: ReplicatedDaemonState;
    search: SearchDraft;
    userBrowse: UserBrowseDraft;
    searchBusy?: boolean;
    onnavigate: (page: PageId) => void;
    onsearchchange: (search: SearchDraft) => void;
    onsearchsubmit: (search: SearchDraft) => void;
    ondownloadrequest: (search: SearchDraft) => void;
    onuserbrowsechange: (browse: UserBrowseDraft) => void;
    onuserbrowsesubmit: (browse: UserBrowseDraft) => void;
    children: Snippet;
  }

  let {
    activePage,
    liveStatus,
    liveState,
    search,
    userBrowse,
    searchBusy = false,
    onnavigate,
    onsearchchange,
    onsearchsubmit,
    ondownloadrequest,
    onuserbrowsechange,
    onuserbrowsesubmit,
    children,
  }: Props = $props();

  let daemonReachable = $derived(liveStatus === 'connected');
  let downloadCount = $derived(liveState.activitySummary.downloads);
  let uploadCount = $derived(liveState.activitySummary.uploads);
  let unreadChats = $derived(Number(liveState.daemon?.chat.unreadPrivateMessageCount ?? 0) + Number(liveState.daemon?.chat.unreadRoomMessageCount ?? 0));
  let updateAvailable = $derived(Boolean(liveState.daemon?.update) && liveState.daemon?.update?.version !== dismissedUpdate.version);
  let soulseek = $derived(soulseekStatusLabel(liveState.daemon?.soulseekClient, liveStatus));
  let connectionLabel = $derived(daemonReachable ? soulseek : `Daemon ${liveStatus === 'offline' ? 'disconnected' : humanizeStateValue(liveStatus).toLowerCase()}`);
  // On phones the Search tab opens straight into typing. iOS only raises the
  // keyboard for focus inside the tap, so the bar is rendered synchronously first.
  function navigateFromTab(page: PageId): void {
    onnavigate(page);
    if (page !== 'searches' || !matchMedia('(max-width: 640px)').matches) return;
    flushSync();
    document.querySelector<HTMLInputElement>('.global-search input:not(:disabled)')?.focus();
  }

  function navigateByKey(event: KeyboardEvent): void {
    if (event.repeat || keyboardShortcutsBlocked(event) || !/^[1-9]$/.test(event.key)) return;
    const item = navigationItems[Number(event.key) - 1];
    if (item) { event.preventDefault(); onnavigate(item.id); }
  }
</script>

<svelte:window onkeydown={navigateByKey} />

<!-- Phones show the search bar only where searching happens. -->
<div class="app-shell" class:search-context={activePage === 'searches' || activePage === 'users' || activePage === 'shares'}>
  <aside class="sidebar">
    <button type="button" class="brand" aria-label="sockseek dashboard" onclick={() => onnavigate('dashboard')}>
      <img class="brand-mark" src="sockseek-logo.svg" alt="" />
      <span class="brand-copy"><strong>sockseek</strong></span>
    </button>

    <Sidebar {activePage} {downloadCount} {uploadCount} {unreadChats} {updateAvailable} onnavigate={navigateFromTab} />
    <div class="sidebar-spacer"></div>

    <div class="connection-status">
      <div aria-label="Connection status">
        <span class:warning={daemonReachable && !liveState.daemon?.soulseekClient.isReady} class:offline={!daemonReachable} class="status-dot"></span>
        <strong>{connectionLabel}</strong>
      </div>
    </div>

    <Sidebar {activePage} {downloadCount} {uploadCount} {unreadChats} {updateAvailable} placement="secondary" onnavigate={navigateFromTab} />
  </aside>

  <div class="workspace">
    <header class="topbar">
      <div class="topbar-inner">
        <GlobalSearch variant={activePage === 'users' || activePage === 'shares' ? 'user' : 'content'} value={search} userValue={userBrowse} busy={searchBusy} onchange={onsearchchange} onsubmit={onsearchsubmit} {ondownloadrequest} onuserchange={onuserbrowsechange} onusersubmit={onuserbrowsesubmit} />
      </div>
    </header>
    <main class:chat-content={activePage === 'chat'} class="page-content">
      {@render children()}
    </main>
  </div>
</div>

<Notifications />
