<script lang="ts">
  import HelpPopover from '../../components/HelpPopover.svelte';
  import type { Schema } from '../../api/types';
  let { value, onchange }: { value: Schema<'CsvSettingsPatchDto'>; onchange: (value: Schema<'CsvSettingsPatchDto'>) => void } = $props();
  const fields = [
    ['artistCol', 'Artist'], ['titleCol', 'Title'], ['albumCol', 'Album'], ['lengthCol', 'Length'],
    ['trackCountCol', 'Track count'], ['ytIdCol', 'YouTube URL / ID'], ['descCol', 'Description'],
  ] as const;
</script>

<div class="new-job-csv-mapping">
  <h3>Column mapping <HelpPopover title="Column mapping" testId="csv-mapping-help">You only need to override column names when the CSV does not use common ones.</HelpPopover></h3>
  <div class="new-job-csv-grid">
    {#each fields as [field, label] (field)}
      <label><span>{label}</span><input placeholder="Auto" value={value[field] ?? ''} oninput={(event) => onchange({ ...value, [field]: event.currentTarget.value })} /></label>
    {/each}
  </div>
</div>
