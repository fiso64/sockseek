<script lang="ts">
  import { navigationItems, type PageId } from '../app/navigation';
  import { MediaQuery } from 'svelte/reactivity';
  import Icon from './Icon.svelte';

  interface Props {
    activePage: PageId;
    downloadCount: number;
    uploadCount: number;
    unreadChats: number;
    updateAvailable: boolean;
    placement?: 'primary' | 'secondary';
    onnavigate: (page: PageId) => void;
  }

  let { activePage, downloadCount, uploadCount, unreadChats, updateAvailable, placement = 'primary', onnavigate }: Props = $props();
  // Phones fold Shares into the Users tab; the page's own switcher moves between them.
  const phone = new MediaQuery('max-width: 640px');
  let items = $derived(navigationItems.filter((item) => item.placement === placement && !(phone.current && item.id === 'shares')));
  const isActive = (id: PageId) => id === activePage || (phone.current && id === 'users' && activePage === 'shares');

  function countFor(id: PageId): number | null {
    if (id === 'downloads') return downloadCount;
    if (id === 'uploads') return uploadCount;
    if (id === 'chat') return unreadChats;
    return null;
  }

  function countTitle(id: PageId): string | undefined {
    if (id === 'downloads') return 'active download transfer rows';
    if (id === 'uploads') return 'active upload transfer rows';
    if (id === 'chat') return 'unread chat messages';
    return undefined;
  }
</script>

<nav class="sidebar-nav" aria-label={placement === 'primary' ? 'Primary navigation' : 'Secondary navigation'}>
  {#each items as item}
    {@const count = countFor(item.id)}
    <button class="choice"
      type="button"
      aria-label={item.label}
      aria-keyshortcuts={String(navigationItems.findIndex(candidate => candidate.id === item.id) + 1)}
      class:active={isActive(item.id)}
      aria-current={isActive(item.id) ? 'page' : undefined}
      onclick={() => onnavigate(item.id)}
    >
      <span class="nav-icon" aria-hidden="true"><Icon name={item.icon} /></span>
      <span class="nav-label">{item.label}</span>
      {#if count !== null && count > 0}
        <span class:unread={item.id === 'chat'} class="nav-count" title={countTitle(item.id)}>{count}</span>
      {/if}
      {#if item.id === 'system' && updateAvailable}
        <span class="nav-count unread" title="A newer version is available" data-testid="nav-update">↑</span>
      {/if}
    </button>
  {/each}
</nav>
