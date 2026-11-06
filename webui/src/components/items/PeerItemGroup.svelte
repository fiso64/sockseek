<script lang="ts">
  import type { Snippet } from 'svelte';
  import type { ItemPeerInfo } from '../../domains/presentation/items';
  import type { UserLinkActions } from '../../app/navigation';
  import UsernameLink from '../UsernameLink.svelte';

  interface Props {
    peer: ItemPeerInfo;
    itemNoun?: string;
    itemNounPlural?: string;
    userActions: UserLinkActions;
    children: Snippet;
  }

  let {
    peer,
    itemNoun = 'result',
    itemNounPlural = `${itemNoun}s`,
    userActions,
    children,
  }: Props = $props();

  let collapsed = $state(false);
  let hasPeerStats = $derived(
    peer.freeUploadSlot !== undefined ||
    peer.uploadSpeedMbps !== undefined ||
    peer.queueLength !== undefined,
  );
</script>

<section class="result-peer-group peer-item-group">
  <div class:compact={!hasPeerStats} class="result-peer-header">
    <button
      type="button"
      class="peer-header-toggle"
      aria-label={`${collapsed ? 'Expand' : 'Collapse'} ${peer.username} ${itemNounPlural}`}
      aria-expanded={!collapsed}
      onclick={() => { collapsed = !collapsed; }}
    ></button>
    <svg class:collapsed class="peer-chevron" viewBox="0 0 20 20" aria-hidden="true"><path d="M7 5l6 5-6 5" /></svg>
    <span class="peer-identity">
      <UsernameLink username={peer.username} actions={userActions} />
    </span>
    {#if hasPeerStats}
    <span class="peer-metadata" aria-label={`${peer.username} result metadata`}>
      {#if peer.uploadSpeedMbps !== undefined}<span class="peer-stat peer-upload-stat" title="Upload speed"><b>{peer.uploadSpeedMbps.toFixed(1)} MB/s</b></span>{/if}
      {#if peer.queueLength !== undefined}<span class="peer-stat peer-queue-stat">Queue: <b>{peer.queueLength}</b></span>{/if}
      {#if peer.freeUploadSlot !== undefined}<span class="peer-stat peer-slot" class:available={peer.freeUploadSlot} class:unavailable={!peer.freeUploadSlot}><i aria-hidden="true"></i>{peer.freeUploadSlot ? 'Free slot' : 'No slot'}</span>{/if}
    </span>
    {/if}
  </div>

  {#if !collapsed}
    <div class="result-peer-items">
      {@render children()}
    </div>
  {/if}
</section>
