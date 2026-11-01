<script lang="ts">
  import type { Snippet } from 'svelte';
  import type { ItemPeerInfo } from '../../domains/presentation/items';
  import type { UserLinkActions } from '../../app/navigation';
  import UsernameLink from '../UsernameLink.svelte';

  interface Props {
    peer: ItemPeerInfo;
    itemCount: number;
    itemNoun?: string;
    itemNounPlural?: string;
    userActions: UserLinkActions;
    children: Snippet;
  }

  let {
    peer,
    itemCount,
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
      {#if peer.freeUploadSlot !== undefined}
        <span class:available={peer.freeUploadSlot} class="peer-slot"><i></i>{peer.freeUploadSlot ? 'Free slot' : 'No free slot'}</span>
      {/if}
    </span>
    <span class="peer-metadata" aria-label={`${peer.username} result metadata`}>
      <span class="peer-stat peer-upload-stat">
        {#if peer.uploadSpeedMbps !== undefined}<b>{peer.uploadSpeedMbps.toFixed(1)} MB/s</b><small>upload</small>{/if}
      </span>
      <span class="peer-stat peer-queue-stat">
        {#if peer.queueLength !== undefined}<b>{peer.queueLength}</b><small>queued</small>{/if}
      </span>
      <span class="peer-result-count">{itemCount} {itemCount === 1 ? itemNoun : itemNounPlural}</span>
    </span>
  </div>

  {#if !collapsed}
    <div class="result-peer-items">
      {@render children()}
    </div>
  {/if}
</section>
