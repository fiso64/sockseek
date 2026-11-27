<script lang="ts">
  let { speed, eta }: { speed?: string; eta?: string } = $props();
</script>

<!-- "left" moves to a clipped second line instead of being cut off when the row is too narrow. -->
<span class="transfer-rate"><span class="transfer-rate-text">{[speed, eta].filter(Boolean).join(' · ')}</span>{#if eta}<span class="transfer-rate-unit">&nbsp;left</span>{/if}</span>
