<script lang="ts">
  import ExpandableSettingsPanel from '../../components/ExpandableSettingsPanel.svelte';
  import { importOptionsCustomized, type ImportOptions } from './import-options';
  let { value, open = $bindable(false), onchange }: { value: ImportOptions; open?: boolean; onchange: (value: ImportOptions) => void } = $props();
  let customized = $derived(importOptionsCustomized(value));
</script>

<section class="new-job-import-options">
  <ExpandableSettingsPanel title="Import options" summary={customized ? 'Custom' : 'Defaults'} {customized} bind:open bodyId="new-job-import-options">
    <div class="new-job-import-options-body">
      <div class="config-grid">
        <label><span>Limit items</span><input type="number" min="1" step="1" value={value.maxTracks} placeholder="All" oninput={(event) => onchange({ ...value, maxTracks: event.currentTarget.value })} /></label>
        <label><span>Offset</span><input type="number" min="0" step="1" value={value.offset} oninput={(event) => onchange({ ...value, offset: event.currentTarget.value })} /></label>
      </div>
      <label class="config-check"><input type="checkbox" checked={value.upgradeToAlbum} onchange={(event) => onchange({ ...value, upgradeToAlbum: event.currentTarget.checked })} /> Upgrade song items to albums</label>
    </div>
  </ExpandableSettingsPanel>
</section>
