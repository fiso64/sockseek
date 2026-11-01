<script lang="ts">
  import type { SearchConditions } from '../domains/search/config-state';
  import type { SearchResultMode } from '../domains/search/model';
  import { isAggregateSearchMode, searchModeFamily } from '../domains/search/model';

  interface Props {
    mode: SearchResultMode;
    conditions: SearchConditions;
  }

  const sampleRates: Record<string, string> = {
    '44100': '44.1 kHz',
    '48000': '48 kHz',
    '88200': '88.2 kHz',
    '96000': '96 kHz',
    '176400': '176.4 kHz',
    '192000': '192 kHz',
  };

  let { mode, conditions = $bindable() }: Props = $props();
  let family = $derived(searchModeFamily(mode));
  let aggregateMode = $derived(isAggregateSearchMode(mode));

  function removeFormat(format: string): void {
    conditions.common.formats = conditions.common.formats.filter((item) => item !== format);
  }
</script>


{#if aggregateMode && conditions.aggregate.minShares}
  <span class="search-condition-pill">≥{conditions.aggregate.minShares} sharers<button type="button" aria-label="Remove minimum sharers filter" onclick={() => (conditions.aggregate.minShares = '')}>×</button></span>
{/if}
{#if aggregateMode}
  {#if conditions.aggregate.lengthTolerance && conditions.aggregate.lengthTolerance !== '3'}
    <span class="search-condition-pill">group ±{conditions.aggregate.lengthTolerance}s<button type="button" aria-label="Reset aggregate grouping tolerance" onclick={() => (conditions.aggregate.lengthTolerance = '3')}>×</button></span>
  {/if}
{/if}

{#each conditions.common.formats as format}
  <span class="search-condition-pill">format: {format}<button type="button" aria-label={`Remove ${format} format`} onclick={() => removeFormat(format)}>×</button></span>
{/each}
{#if conditions.common.minBitrate}
  <span class="search-condition-pill">bitrate ≥ {conditions.common.minBitrate} kbps<button type="button" onclick={() => (conditions.common.minBitrate = '')}>×</button></span>
{/if}
{#if conditions.common.maxBitrate}
  <span class="search-condition-pill">bitrate ≤ {conditions.common.maxBitrate} kbps<button type="button" onclick={() => (conditions.common.maxBitrate = '')}>×</button></span>
{/if}
{#if conditions.common.minSampleRate}
  <span class="search-condition-pill">sample rate ≥ {sampleRates[conditions.common.minSampleRate] ?? conditions.common.minSampleRate}<button type="button" onclick={() => (conditions.common.minSampleRate = '')}>×</button></span>
{/if}
{#if conditions.common.maxSampleRate}
  <span class="search-condition-pill">sample rate ≤ {sampleRates[conditions.common.maxSampleRate] ?? conditions.common.maxSampleRate}<button type="button" onclick={() => (conditions.common.maxSampleRate = '')}>×</button></span>
{/if}
{#if conditions.common.minBitDepth}
  <span class="search-condition-pill">bit depth ≥ {conditions.common.minBitDepth}-bit<button type="button" onclick={() => (conditions.common.minBitDepth = '')}>×</button></span>
{/if}
{#if conditions.common.maxBitDepth}
  <span class="search-condition-pill">bit depth ≤ {conditions.common.maxBitDepth}-bit<button type="button" onclick={() => (conditions.common.maxBitDepth = '')}>×</button></span>
{/if}
{#if family !== 'generic' && conditions.common.strictArtist}
  <span class="search-condition-pill">strict artist<button type="button" onclick={() => (conditions.common.strictArtist = false)}>×</button></span>
{/if}
{#if conditions.common.rejectUnknownMetadata}
  <span class="search-condition-pill">reject unknown metadata<button type="button" onclick={() => (conditions.common.rejectUnknownMetadata = false)}>×</button></span>
{/if}

{#if family === 'track'}
  {#if conditions.track.strictTitle}
    <span class="search-condition-pill">strict title<button type="button" onclick={() => (conditions.track.strictTitle = false)}>×</button></span>
  {/if}
  {#if conditions.track.expectedLength}
    <span class="search-condition-pill">length {conditions.track.expectedLength}s ±{conditions.track.lengthTolerance || '0'}s<button type="button" onclick={() => (conditions.track.expectedLength = '')}>×</button></span>
  {/if}
{:else if family === 'album'}
  {#if conditions.album.strictAlbum}
    <span class="search-condition-pill">strict album<button type="button" onclick={() => (conditions.album.strictAlbum = false)}>×</button></span>
  {/if}
  {#if conditions.album.minTrackCount}
    <span class="search-condition-pill">tracks ≥ {conditions.album.minTrackCount}<button type="button" onclick={() => (conditions.album.minTrackCount = '')}>×</button></span>
  {/if}
  {#if conditions.album.maxTrackCount}
    <span class="search-condition-pill">tracks ≤ {conditions.album.maxTrackCount}<button type="button" onclick={() => (conditions.album.maxTrackCount = '')}>×</button></span>
  {/if}
  {#each conditions.album.requiredTrackTitles as title}
    <span class="search-condition-pill">contains: {title}<button type="button" onclick={() => (conditions.album.requiredTrackTitles = conditions.album.requiredTrackTitles.filter((item) => item !== title))}>×</button></span>
  {/each}
  {#if conditions.album.strictAlbumQuality}
    <span class="search-condition-pill">all tracks meet quality<button type="button" onclick={() => (conditions.album.strictAlbumQuality = false)}>×</button></span>
  {/if}
{/if}

{#if conditions.common.allowedUsers.trim()}
  <span class="search-condition-pill">allow: {conditions.common.allowedUsers.trim()}<button type="button" onclick={() => (conditions.common.allowedUsers = '')}>×</button></span>
{/if}
{#if conditions.common.bannedUsers.trim()}
  <span class="search-condition-pill">ban: {conditions.common.bannedUsers.trim()}<button type="button" onclick={() => (conditions.common.bannedUsers = '')}>×</button></span>
{/if}
