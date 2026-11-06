<script lang="ts">
  import ExpandableSettingsPanel from '../../components/ExpandableSettingsPanel.svelte';
  import type { Schema } from '../../api/types';
  let { value, open = $bindable(false), onchange }: { value: Schema<'CsvSettingsPatchDto'>; open?: boolean; onchange: (value: Schema<'CsvSettingsPatchDto'>) => void } = $props();
  let customized = $derived(Object.values(value).some((entry) => typeof entry === 'string' && entry.length > 0));
  const fields = [
    ['artistCol', 'Artist'], ['titleCol', 'Title'], ['albumCol', 'Album'], ['lengthCol', 'Length'],
    ['trackCountCol', 'Track count'], ['ytIdCol', 'YouTube URL / ID'], ['descCol', 'Description'],
  ] as const;
</script>

<section class="new-job-csv-options">
  <ExpandableSettingsPanel title="Column mapping" summary={customized ? 'Custom' : 'Auto-detect'} {customized} bind:open bodyId="new-job-csv-mapping">
    <div class="new-job-csv-mapping">
      <p>Override header names only when the CSV does not use common column names.</p>
      <div class="new-job-csv-grid">
        {#each fields as [field, label] (field)}
          <label><span>{label}</span><input placeholder="Auto" value={value[field] ?? ''} oninput={(event) => onchange({ ...value, [field]: event.currentTarget.value || null })} /></label>
        {/each}
      </div>
    </div>
  </ExpandableSettingsPanel>
</section>
