<script lang="ts">
  import ExpandableSettingsPanel from '../../components/ExpandableSettingsPanel.svelte';
  import type { Schema } from '../../api/types';
  let { value, baseline = {}, disabled = false, profileApplied = false, open = $bindable(false), onchange }: { value: Schema<'CsvSettingsPatchDto'>; baseline?: Schema<'CsvSettingsPatchDto'>; disabled?: boolean; profileApplied?: boolean; open?: boolean; onchange: (value: Schema<'CsvSettingsPatchDto'>) => void } = $props();
  let customized = $derived(profileApplied || Object.keys(value).some(key => value[key as keyof typeof value] !== baseline[key as keyof typeof baseline]));
  const fields = [
    ['artistCol', 'Artist'], ['titleCol', 'Title'], ['albumCol', 'Album'], ['lengthCol', 'Length'],
    ['trackCountCol', 'Track count'], ['ytIdCol', 'YouTube URL / ID'], ['descCol', 'Description'],
  ] as const;
</script>

<section class="new-job-csv-options">
  <ExpandableSettingsPanel title="Column mapping" summary={customized ? 'Custom' : Object.values(baseline).some(value => typeof value === 'string' && value) ? 'Defaults' : 'Auto-detect'} {customized} bind:open bodyId="new-job-csv-mapping">
    <div class="new-job-csv-mapping">
      <p>Override header names only when the CSV does not use common column names.</p>
      <div class="new-job-csv-grid">
        {#each fields as [field, label] (field)}
          <label><span>{label}</span><input {disabled} placeholder="Auto" value={value[field] ?? ''} oninput={(event) => onchange({ ...value, [field]: event.currentTarget.value || null })} /></label>
        {/each}
      </div>
    </div>
  </ExpandableSettingsPanel>
</section>
