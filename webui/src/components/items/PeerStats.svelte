<script lang="ts">
  import type { Schema } from '../../api/types';

  let { peer }: { peer: Schema<'PeerInfoDto'> } = $props();
</script>

{#if peer.uploadSpeed != null}
  <span class="peer-stat peer-upload-stat" title="Upload speed reported in this search"><b>{(Number(peer.uploadSpeed) / 1_000_000).toFixed(1)} MB/s</b></span>
{/if}
{#if peer.hasFreeUploadSlot != null}
  <span title="Slot availability when the peer answered the search" class="peer-slot" class:available={peer.hasFreeUploadSlot} class:unavailable={!peer.hasFreeUploadSlot}>
    <i aria-hidden="true"></i>{peer.hasFreeUploadSlot ? 'Free slot' : 'No free slot'}
  </span>
{/if}
{#if peer.queueLength != null}
  <span class="peer-stat peer-queue-stat" title={`Queue reported in this search: ${peer.queueLength}`}>Queue: <b>{peer.queueLength}</b></span>
{/if}
