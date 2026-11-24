<script lang="ts">
  import type { Schema } from '../../api/types';
  import { formatCount, formatSpeed } from '../../app/format';

  let { peer }: { peer: Schema<'PeerInfoDto'> } = $props();
  let speed = $derived(formatSpeed(peer.uploadSpeed));
</script>

<!-- Natural-width facts in a fixed order, right after the peer name. -->
<span class="peer-stats">
  {#if peer.hasFreeUploadSlot != null}
    <span title="Slot availability when the peer answered the search" class="peer-slot" class:available={peer.hasFreeUploadSlot} class:unavailable={!peer.hasFreeUploadSlot}>
      <i aria-hidden="true"></i>{peer.hasFreeUploadSlot ? 'Free slot' : 'No free slot'}
    </span>
  {/if}
  {#if speed}
    <span class="peer-stat peer-upload-stat" title="Upload speed reported in this search"><b>{speed}</b></span>
  {/if}
  {#if peer.queueLength != null}
    <span class="peer-stat peer-queue-stat" title="Queue length reported in this search"><b>{formatCount(peer.queueLength)}</b> queued</span>
  {/if}
</span>
