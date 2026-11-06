<script lang="ts">
  import type { Snippet } from 'svelte';
  import TransferStateLabel from './TransferStateLabel.svelte';
  import RelativeTime from '../RelativeTime.svelte';
  import Icon from '../Icon.svelte';
  import type { AudioAttributes, TransferPresentation } from '../../domains/presentation/items';
  import { audioSummary, basename, formatBytes, formatLength, parentPath } from '../../domains/presentation/items';

  interface Props {
    path: string;
    sizeBytes: number | null;
    audio?: AudioAttributes;
    locked?: boolean;
    selected?: boolean;
    preferred?: boolean;
    selectable?: boolean;
    keyboardKey?: string;
    transfer?: TransferPresentation;
    actions?: Snippet;
    onselect?: (selected: boolean) => void;
  }

  let {
    path,
    sizeBytes,
    audio,
    locked = false,
    selected = false,
    preferred = false,
    selectable = false,
    keyboardKey,
    transfer,
    actions,
    onselect,
  }: Props = $props();

  let transferDirectionClass = $derived(transfer?.direction ? `transfer-${transfer.direction}` : '');

  function transferFooterDetail(value: TransferPresentation): string {
    return [value.tone === 'failed' ? undefined : value.detail, value.speed, value.eta].filter(Boolean).join(' · ');
  }
</script>

<article
  class="item-card result-card file-item-card"
  class:locked
  class:selected
  class:preferred
  data-keyboard-item={keyboardKey}
  tabindex="-1"
  class:selectable
  class:transfer-card={Boolean(transfer)}
  class:has-audio={Boolean(audio)}
>
  {#if transfer}
    <div class="file-item-main folder-result-summary file-transfer-summary">
      <div class="folder-heading-primary">
        <div class="folder-summary-select-target nonselectable">
          <div class="folder-heading-copy">
            <div class="folder-heading-identity">
              <strong class="folder-heading-path file-transfer-path" title={path}><bdi dir="ltr"><span class="folder-path-ancestors">{parentPath(path)}</span><b class="file-transfer-name">{basename(path)}</b></bdi></strong>
              {#if locked}<span class="locked-badge">Locked</span>{/if}
            </div>
            <div class="folder-heading-meta file-transfer-meta">
              {#if audio}
                <span class="file-transfer-audio">{audioSummary(audio)}</span>
                <span class="file-transfer-length">{formatLength(audio.lengthSeconds)}</span>
              {/if}
              <span class="file-transfer-size">{sizeBytes == null ? '—' : formatBytes(sizeBytes)}</span>
              {#if transfer.createdAt}<span class="file-transfer-age"><RelativeTime timestamp={transfer.createdAt} lowercase /></span>{/if}
            </div>
          </div>
        </div>
        <div class="folder-heading-actions">
          <TransferStateLabel {transfer} {path} {sizeBytes} circular={transfer.tone === 'complete'}>
            {#if transfer.tone === 'complete'}<Icon name="check" />{:else}{transfer.state}{/if}
          </TransferStateLabel>
          {#if actions}<span class="item-card-action">{@render actions()}</span>{/if}
        </div>
      </div>
    </div>
  {:else}
    <svelte:element this={selectable ? 'label' : 'div'} class:clickable={selectable} class:nonselectable={!selectable} class="file-item-main file-result-row">
      {#if selectable}
        <input
          type="checkbox"
          checked={selected}
          aria-label={`Select ${basename(path)}`}
          onchange={(event) => onselect?.((event.currentTarget as HTMLInputElement).checked)}
        />
      {/if}

      <div class="item-path-block result-path-block">
        <div class="item-name-line result-name-line">
          <strong class="folder-heading-path" title={path}><bdi dir="ltr"><span class="folder-path-ancestors">{parentPath(path)}</span><b>{basename(path)}</b></bdi></strong>
          {#if locked}<span class="locked-badge">Locked</span>{/if}
        </div>
      </div>

      <div class="item-detail result-detail item-size-detail"><strong>{sizeBytes == null ? '—' : formatBytes(sizeBytes)}</strong></div>
      {#if audio}
        <div class="item-detail result-detail audio-detail"><strong>{audioSummary(audio)}</strong></div>
        <div class="item-detail result-detail item-length-detail"><strong>{formatLength(audio.lengthSeconds)}</strong></div>
      {/if}
      {#if actions}
        <div class="item-card-action">{@render actions()}</div>
      {/if}
    </svelte:element>
  {/if}

  {#if transfer && transfer.tone !== 'complete'}
    {@const footerDetail = transferFooterDetail(transfer)}
    <div class={`transfer-card-footer file-transfer-footer ${transfer.tone ?? ''} ${transferDirectionClass}`}>
      {#if transfer.progressPercent !== undefined}
        <div class="transfer-progress-track" aria-label={`${transfer.progressPercent.toFixed(0)}% complete`}>
          <span style={`width:${transfer.progressPercent}%`}></span>
        </div>
      {/if}
      {#if transfer.progressText || footerDetail || transfer.completedAt}
        <div class="transfer-progress-meta">
          <span>{transfer.progressText ?? ''}</span>
          <span>{#if transfer.completedAt}<RelativeTime timestamp={transfer.completedAt} lowercase />{#if footerDetail} · {/if}{/if}{footerDetail}</span>
        </div>
      {/if}
    </div>
  {/if}
</article>
