<script lang="ts">
  import { SEARCH_AUDIO_FORMATS } from '../domains/search/config-schema';

  interface Props {
    values: string[];
    label: string;
    ariaLabel: string;
    idPrefix: string;
    suggestions?: readonly string[];
    customPlaceholder?: string;
  }

  let { values = $bindable(), label, ariaLabel, idPrefix, suggestions = SEARCH_AUDIO_FORMATS, customPlaceholder = 'flac, mp3, aac, ape…' }: Props = $props();
  let view = $state<'buttons' | 'custom'>('buttons');
  let customFormats = $state('');
  let buttons = $derived([...new Set([...suggestions, ...values])]);

  $effect(() => {
    if (view === 'buttons') customFormats = values.join(', ');
  });

  function toggleFormat(format: string): void {
    values = values.includes(format)
      ? values.filter((item) => item !== format)
      : [...values, format];
  }

  function clearFormats(): void {
    if (values.length === 0) return;
    values = [];
  }

  function parseCustomFormats(): void {
    values = [...new Set(
      customFormats
        .split(',')
        .map((format) => format.trim().toUpperCase())
        .filter(Boolean),
    )];
  }

  function showCustomFormats(): void {
    customFormats = values.join(', ');
    view = 'custom';
    requestAnimationFrame(() => document.querySelector<HTMLInputElement>(`#${idPrefix}-custom-formats`)?.focus());
  }

  function showButtons(): void {
    parseCustomFormats();
    view = 'buttons';
  }
</script>

<div class="config-label">{label}</div>
{#if view === 'buttons'}
  <div class="format-control-row">
    <div class="format-buttons">
      <button type="button" class:active={values.length === 0} disabled={values.length === 0} onclick={clearFormats}>Any</button>
      {#each buttons as format}
        <button type="button" class:active={values.includes(format)} onclick={() => toggleFormat(format)}>{format}</button>
      {/each}
    </div>
    <button type="button" class="format-view-button" onclick={showCustomFormats}>custom…</button>
  </div>
{:else}
  <div class="custom-format-row">
    <input
      id={`${idPrefix}-custom-formats`}
      value={customFormats}
      placeholder={customPlaceholder}
      aria-label={ariaLabel}
      oninput={(event) => {
        customFormats = (event.currentTarget as HTMLInputElement).value;
        parseCustomFormats();
      }}
    />
    <button type="button" onclick={showButtons}>buttons</button>
  </div>
{/if}
