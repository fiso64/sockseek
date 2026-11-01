<script lang="ts">
  import { navigationItems, type PageId } from '../app/navigation';
  import Icon from './Icon.svelte';

  interface Props {
    activePage: PageId;
    downloadCount: number;
    uploadCount: number;
    unreadChats: number;
    placement?: 'primary' | 'secondary';
    onnavigate: (page: PageId) => void;
  }

  let { activePage, downloadCount, uploadCount, unreadChats, placement = 'primary', onnavigate }: Props = $props();
  let items = $derived(navigationItems.filter((item) => item.placement === placement));

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
    <button
      type="button"
      aria-label={item.label}
      class:active={item.id === activePage}
      aria-current={item.id === activePage ? 'page' : undefined}
      onclick={() => onnavigate(item.id)}
    >
      <span class="nav-icon" aria-hidden="true"><Icon name={item.icon} /></span>
      <span class="nav-label">{item.label}</span>
      {#if count !== null && count > 0}
        <span class:unread={item.id === 'chat'} class="nav-count" title={countTitle(item.id)}>{count}</span>
      {/if}
    </button>
  {/each}
</nav>
