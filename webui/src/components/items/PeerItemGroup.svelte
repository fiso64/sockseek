<script lang="ts">
  import type { Snippet } from 'svelte';
  import type { ItemPeerInfo } from '../../domains/presentation/items';
  import type { UserLinkActions } from '../../app/navigation';
  import UsernameLink from '../UsernameLink.svelte';
  import PeerSlot from './PeerSlot.svelte';

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
      <PeerSlot available={peer.freeUploadSlot} class="peer-stat" />
      {#if peer.uploadSpeedMbps !== undefined}<span class="peer-stat peer-upload-stat" title="Upload speed"><b>{peer.uploadSpeedMbps.toFixed(1)} MB/s</b></span>{/if}
      {#if peer.queueLength !== undefined}<span class="peer-stat peer-queue-stat" title={`Queue: ${peer.queueLength}`}>Queue: <b>{peer.queueLength}</b></span>{/if}
    </span>
    {/if}
  </div>

  {#if !collapsed}
    <div class="result-peer-items">
      {@render children()}
    </div>
  {/if}
</section>

<style>
  .peer-item-group { container-type: inline-size; }
  .result-peer-header,
  .result-peer-header.compact {
    width: max-content;
    max-width: none;
    min-height: 44px;
    grid-template-columns: 18px minmax(20ch, max-content) auto;
    gap: 8px;
    padding: 5px 12px 5px 0;
    font-size: .86rem;
  }
  .result-peer-header.compact { grid-template-columns: 18px minmax(20ch, max-content); }
  .peer-identity :global(.username-link) { overflow: visible; text-overflow: clip; }
  .peer-metadata {
    display: grid;
    grid-template-columns: 8ch 10ch max-content;
    gap: 12px;
    margin-left: 8px;
    font-size: .76rem;
  }
  .peer-stat { display: inline-flex; }
  .peer-upload-stat { grid-area: 1 / 2; min-width: 0; }
  .peer-queue-stat { grid-area: 1 / 3; font-variant-numeric: tabular-nums; }
  .peer-stat b { min-width: 0; overflow: hidden; text-overflow: ellipsis; }
  .peer-metadata :global(.peer-slot) { grid-area: 1 / 1; display: inline-flex; font-weight: 600; }
  .result-peer-items { padding-left: 26px; }

  @container (max-width: 30rem) {
    .result-peer-header,
    .result-peer-header.compact { width: 100%; max-width: 100%; grid-template-columns: 18px minmax(0, 1fr); row-gap: 2px; }
    .peer-identity :global(.username-link) { white-space: normal; overflow-wrap: anywhere; }
    .peer-metadata { grid-area: 2 / 2; display: flex; flex-wrap: wrap; gap: 6px 12px; margin-left: 0; }
    .peer-metadata :global(.peer-slot) { width: 8ch; }
    .peer-upload-stat { width: 10ch; }
    .result-peer-items { padding-left: 0; }
  }
</style>
