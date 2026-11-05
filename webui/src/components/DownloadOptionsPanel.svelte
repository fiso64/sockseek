<script lang="ts">
  import type { DownloadOptionCapabilities, DownloadOptions } from '../domains/jobs/download-options';

  interface Props {
    value: DownloadOptions;
    capabilities: DownloadOptionCapabilities;
    context?: 'automatic' | 'selection';
    compact?: boolean;
    separators?: boolean;
    mixedFields?: ReadonlySet<keyof DownloadOptions>;
    onfieldchange?: (field: keyof DownloadOptions) => void;
  }

  let { value = $bindable(), capabilities, context = 'automatic', compact = false, separators = true, mixedFields = new Set(), onfieldchange }: Props = $props();
</script>

<div class:compact class:seamless={!separators} class="download-options-panel">
  <section class="download-options-section">
    <h3>Behavior</h3>
    <label class="config-check"><input type="checkbox" bind:checked={value.skipExisting} indeterminate={mixedFields.has('skipExisting')} onchange={() => onfieldchange?.('skipExisting')} /> Skip existing downloads{#if mixedFields.has('skipExisting')} <small>(mixed defaults)</small>{/if}</label>
    {#if capabilities.albumFolderEnrichment}
      <label class="config-check"><input type="checkbox" bind:checked={value.loadFullAlbumFolder} indeterminate={mixedFields.has('loadFullAlbumFolder')} onchange={() => onfieldchange?.('loadFullAlbumFolder')} /> Load full album folder before downloading{#if mixedFields.has('loadFullAlbumFolder')} <small>(mixed defaults)</small>{/if}</label>
      <p class="download-option-note">Loads files the search result may have omitted.{#if context === 'automatic'} Even when disabled, automatic album downloads may retrieve folder contents to verify track-count or strict quality requirements.{/if}</p>
    {/if}
  </section>

  <section class="download-options-section">
    <h3>Output</h3>
    <div class="download-options-fields">
      <label>
        <span>Output directory</span>
        <input type="text" bind:value={value.outputParentDir} placeholder={mixedFields.has('outputParentDir') ? 'Mixed defaults' : 'Inherit daemon default'} oninput={() => onfieldchange?.('outputParentDir')} />
      </label>
      {#if capabilities.nameFormat ?? true}
        <label>
          <span>Name format</span>
          <input type="text" bind:value={value.nameFormat} placeholder={mixedFields.has('nameFormat') ? 'Mixed defaults' : 'Inherit daemon default'} oninput={() => onfieldchange?.('nameFormat')} />
        </label>
      {/if}
    </div>
  </section>

  {#if capabilities.playlistOutput}
    <section class="download-options-section collection-output">
      <h3>Collection output</h3>
      <label class="config-check"><input type="checkbox" bind:checked={value.writePlaylist} indeterminate={mixedFields.has('writePlaylist')} onchange={() => onfieldchange?.('writePlaylist')} /> Create .m3u playlist{#if mixedFields.has('writePlaylist')} <small>(mixed defaults)</small>{/if}</label>
    </section>
  {/if}
</div>
