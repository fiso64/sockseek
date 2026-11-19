<script lang="ts">
  import type { Snippet } from 'svelte';
  import type { Schema } from '../../api/types';
  import type { UserLinkActions } from '../../app/navigation';
  import UsernameLink from '../UsernameLink.svelte';
  import PeerStats from './PeerStats.svelte';

  interface Props {
    highlight?: RegExp;
    peer: Schema<'PeerInfoDto'>;
    itemNoun?: string;
    itemNounPlural?: string;
    userActions: UserLinkActions;
    children: Snippet;
  }

  let {
    highlight,
    peer,
    itemNoun = 'result',
    itemNounPlural = `${itemNoun}s`,
    userActions,
    children,
  }: Props = $props();

  let collapsed = $state(false);
  let hasPeerStats = $derived(
    peer.hasFreeUploadSlot != null || peer.uploadSpeed != null || peer.queueLength != null,
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
      <UsernameLink {highlight} username={peer.username} actions={userActions} />
    </span>
    {#if hasPeerStats}
    <span class="peer-metadata" aria-label={`${peer.username} result metadata`}>
      <PeerStats {peer} />
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
  .peer-metadata :global(.peer-upload-stat) { grid-area: 1 / 2; justify-content: flex-end; }
  .peer-metadata :global(.peer-queue-stat) { grid-area: 1 / 3; }
  .peer-metadata :global(.peer-slot) { grid-area: 1 / 1; display: inline-flex; font-weight: 600; }
  .result-peer-items { padding-left: 26px; }

  @container (max-width: 30rem) {
    .result-peer-header,
    .result-peer-header.compact { width: 100%; max-width: 100%; grid-template-columns: 18px minmax(0, 1fr); row-gap: 2px; }
    .peer-identity :global(.username-link) { white-space: normal; overflow-wrap: anywhere; }
    .peer-metadata { grid-area: 2 / 2; display: flex; flex-wrap: wrap; gap: 6px 12px; margin-left: 0; }
    .peer-metadata :global(.peer-slot) { width: 8ch; order: -1; }
    .peer-metadata :global(.peer-upload-stat) { width: 10ch; }
    .result-peer-items { padding-left: 0; }
  }
</style>
