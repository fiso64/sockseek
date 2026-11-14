<script lang="ts">
  import type { ImportOptions } from './import-options';
  let { value, onchange }: { value: ImportOptions; onchange: (value: ImportOptions) => void } = $props();
</script>

<div class="new-job-import-options-body">
  <div class="config-grid">
    <label><span>Limit items</span><input type="number" min="1" step="1" value={value.maxTracks} placeholder="All" oninput={(event) => onchange({ ...value, maxTracks: event.currentTarget.value })} /></label>
    <label><span>Offset</span><input type="number" min="0" step="1" value={value.offset} oninput={(event) => onchange({ ...value, offset: event.currentTarget.value })} /></label>
  </div>
  <label class="config-check"><input type="checkbox" checked={value.upgradeToAlbum} onchange={(event) => onchange({ ...value, upgradeToAlbum: event.currentTarget.checked })} /> Upgrade song items to albums</label>
</div>
