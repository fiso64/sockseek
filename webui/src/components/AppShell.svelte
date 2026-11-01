<script lang="ts">
  import type { Snippet } from 'svelte';
  import type { PageId } from '../app/navigation';
  import type { LiveConnectionStatus, ReplicatedDaemonState } from '../api/live';
  import type { SearchDraft } from '../domains/search/model';
  import { humanizeStateValue, soulseekStatusLabel } from '../app/format';
  import GlobalSearch from './GlobalSearch.svelte';
  import type { UserBrowseDraft } from './GlobalSearch.svelte';
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
    onuserbrowsechange,
    onuserbrowsesubmit,
    children,
  }: Props = $props();

  let daemonReachable = $derived(liveStatus === 'connected');
  let downloadCount = $derived(liveState.transferSummary.downloads);
  let uploadCount = $derived(liveState.transferSummary.uploads);
  let unreadChats = $derived(Number(liveState.daemon?.chat.unreadPrivateMessageCount ?? 0) + Number(liveState.daemon?.chat.unreadRoomMessageCount ?? 0));
  let soulseek = $derived(soulseekStatusLabel(liveState.daemon?.soulseekClient, daemonReachable));
  let daemonLabel = $derived(humanizeStateValue(liveStatus));
</script>

<div class="app-shell">
  <aside class="sidebar">
    <div class="brand">
      <div class="brand-mark" aria-hidden="true">S</div>
      <div class="brand-copy"><strong>sockseek</strong><span>daemon</span></div>
    </div>

    <Sidebar {activePage} {downloadCount} {uploadCount} {unreadChats} {onnavigate} />
    <div class="sidebar-spacer"></div>

    <div class="connection-status" aria-label="Connection status">
      <div>
        <span class:warning={daemonReachable && !liveState.daemon?.soulseekClient.isReady} class:offline={!daemonReachable} class="status-dot"></span>
        <span>Soulseek</span>
        <strong>{soulseek}</strong>
      </div>
      <div>
        <span class:offline={!daemonReachable} class="status-dot"></span>
        <span>Daemon</span>
        <strong>{daemonLabel}</strong>
      </div>
    </div>

    <Sidebar {activePage} {downloadCount} {uploadCount} {unreadChats} placement="secondary" {onnavigate} />
  </aside>

  <div class="workspace">
    <header class="topbar">
      <div class="topbar-inner">
        <GlobalSearch variant={activePage === 'users' ? 'user' : 'content'} value={search} userValue={userBrowse} busy={searchBusy} onchange={onsearchchange} onsubmit={onsearchsubmit} onuserchange={onuserbrowsechange} onusersubmit={onuserbrowsesubmit} />
      </div>
    </header>
    <main class:chat-content={activePage === 'chat'} class="page-content">
      {@render children()}
    </main>
  </div>
</div>
