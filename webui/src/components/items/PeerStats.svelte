<script lang="ts">
  import type { Schema } from '../../api/types';

  let { peer }: { peer: Schema<'PeerInfoDto'> } = $props();
</script>

{#if peer.uploadSpeed != null}
  <span class="peer-stat peer-upload-stat" title="Upload speed"><b>{(Number(peer.uploadSpeed) / 1_000_000).toFixed(1)} MB/s</b></span>
{/if}
{#if peer.hasFreeUploadSlot != null}
  <span class="peer-slot" class:available={peer.hasFreeUploadSlot} class:unavailable={!peer.hasFreeUploadSlot}>
    <i aria-hidden="true"></i>{peer.hasFreeUploadSlot ? 'Free slot' : 'No slot'}
  </span>
{/if}
{#if peer.queueLength != null}
  <span class="peer-stat peer-queue-stat" title={`Queue: ${peer.queueLength}`}>Queue: <b>{peer.queueLength}</b></span>
{/if}
