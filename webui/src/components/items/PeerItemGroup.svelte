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
  /* Columns come from the enclosing .peer-group-list (app.css). */
  .result-peer-header { min-height: 44px; padding: 5px 12px 5px 0; font-size: .86rem; }
</style>
