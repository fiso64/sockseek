<script lang="ts">
  import type { Snippet } from 'svelte';
  import { navigationItems, type PageId } from '../app/navigation';
  import type { LiveConnectionStatus, ReplicatedDaemonState } from '../api/live';
  import type { SearchDraft } from '../domains/search/model';
  import { humanizeStateValue, soulseekStatusLabel } from '../app/format';
  import GlobalSearch from './GlobalSearch.svelte';
  import type { UserBrowseDraft } from './GlobalSearch.svelte';
  import { keyboardShortcutsBlocked } from '../lib/keyboard';
  import Sidebar from './Sidebar.svelte';

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
  let downloadCount = $derived(liveState.transferSummary.downloads);
  let uploadCount = $derived(liveState.transferSummary.uploads);
  let unreadChats = $derived(Number(liveState.daemon?.chat.unreadPrivateMessageCount ?? 0) + Number(liveState.daemon?.chat.unreadRoomMessageCount ?? 0));
  let soulseek = $derived(soulseekStatusLabel(liveState.daemon?.soulseekClient, daemonReachable));
  let connectionLabel = $derived(daemonReachable ? soulseek : `Daemon ${liveStatus === 'offline' ? 'disconnected' : humanizeStateValue(liveStatus).toLowerCase()}`);
  function navigateByKey(event: KeyboardEvent): void {
    if (event.repeat || keyboardShortcutsBlocked(event) || !/^[1-9]$/.test(event.key)) return;
    const item = navigationItems[Number(event.key) - 1];
    if (item) { event.preventDefault(); onnavigate(item.id); }
  }
</script>

<svelte:window onkeydown={navigateByKey} />

<div class="app-shell">
  <aside class="sidebar">
    <div class="brand">
      <div class="brand-mark" aria-hidden="true">S</div>
      <div class="brand-copy"><strong>sockseek</strong></div>
    </div>

    <Sidebar {activePage} {downloadCount} {uploadCount} {unreadChats} {onnavigate} />
    <div class="sidebar-spacer"></div>

    <div class="connection-status" aria-label="Connection status">
      <div>
        <span class:warning={daemonReachable && !liveState.daemon?.soulseekClient.isReady} class:offline={!daemonReachable} class="status-dot"></span>
        <strong>{connectionLabel}</strong>
      </div>
    </div>

    <Sidebar {activePage} {downloadCount} {uploadCount} {unreadChats} placement="secondary" {onnavigate} />
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
