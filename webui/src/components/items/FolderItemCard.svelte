<script lang="ts">
  import type { Snippet } from 'svelte';
  import Icon from '../Icon.svelte';
  import LoadMoreButton from '../LoadMoreButton.svelte';
  import RelativeTime from '../RelativeTime.svelte';
  import type { AppIconName } from '../../app/icons';
  import type { FolderItemFile, TransferPresentation } from '../../domains/presentation/items';
  import { audioSummary, basename, formatBytes, formatLength } from '../../domains/presentation/items';
  import { buildFolderTreeRows, type FolderTreeNode } from '../../domains/presentation/folder-tree';
  import { fileTypeIcon } from '../../domains/presentation/file-types';

  interface Props {
    path: string;
    sizeBytes: number | null;
    files: FolderItemFile[];
    totalFileCount?: number | null;
    filesComplete?: boolean;
    fileLayout?: 'flat' | 'tree';
    dataStateLabel?: string;
    contentsState?: 'partial' | 'retrieving' | 'complete' | 'failed';
    locked?: boolean;
    selected?: boolean;
    partial?: boolean;
    preferred?: boolean;
    selectable?: boolean;
    transfer?: TransferPresentation;
    selectedFileIds?: Set<string>;
    actions?: Snippet;
    fileActions?: Snippet<[FolderItemFile]>;
    onselectall?: (selected: boolean) => void;
    onselectfile?: (file: FolderItemFile, selected: boolean) => void;
    onselectfiles?: (files: FolderItemFile[], selected: boolean) => void;
    onselectfolder?: (relativePath: string, selected: boolean) => void;
    isfolderselected?: (relativePath: string) => boolean;
    onloadfullcontents?: () => void;
    onloadmorefiles?: () => void;
    loadingMoreFiles?: boolean;
    fileLoadError?: string | null;
  }

  let {
    path,
    sizeBytes,
    files,
    totalFileCount = files.length,
    filesComplete = true,
    fileLayout = 'flat',
    dataStateLabel,
    contentsState,
    locked = false,
    selected = false,
    partial = false,
    preferred = false,
    selectable = false,
    transfer,
    selectedFileIds = new Set<string>(),
    actions,
    fileActions,
    onselectall,
    onselectfile,
    onselectfiles,
    onselectfolder,
    isfolderselected,
    onloadfullcontents,
    onloadmorefiles,
    loadingMoreFiles = false,
    fileLoadError = null,
  }: Props = $props();

  function selectFolderFiles(node: FolderTreeNode, checked: boolean): void {
    const folderFiles: FolderItemFile[] = [];
    const collect = (current: FolderTreeNode): void => {
      for (const entry of current.files) folderFiles.push(entry.file);
      for (const child of current.folders.values()) collect(child);
    };
    collect(node);
    if (onselectfiles) {
      onselectfiles(folderFiles, checked);
      return;
    }
    for (const file of folderFiles) onselectfile?.(file, checked);
  }

  let transferDirectionClass = $derived(transfer?.direction ? `transfer-${transfer.direction}` : '');

  function indeterminate(node: HTMLInputElement, value: boolean) {
    node.indeterminate = value;
    return { update(next: boolean) { node.indeterminate = next; } };
  }

  function transferIcon(value?: TransferPresentation): AppIconName {
    if (value?.tone === 'complete') return 'check';
    if (value?.tone === 'active') return value.direction === 'upload' ? 'upload' : 'download';
    if (value?.tone === 'failed' || value?.tone === 'cancelled') return 'x';
    return value ? 'clock' : 'file';
  }

  function usesCircularTransferState(value: TransferPresentation): boolean {
    return value.tone === 'complete' || value.tone === 'active' || value.tone === 'queued';
  }

  function showsInlineTransferProgress(value: TransferPresentation): boolean {
    return value.progressPercent !== undefined && value.tone !== 'complete' && value.tone !== 'queued';
  }

  function fileTransferProgressText(value: TransferPresentation): string {
    return value.progressPercent === undefined ? '' : `${Math.round(value.progressPercent)}%`;
  }

  function fileTransferSecondary(value: TransferPresentation): string {
    return [value.detail, value.speed, value.eta].filter(Boolean).join(' · ');
  }

  function needsTransferDetail(value?: TransferPresentation): boolean {
    return Boolean(value && (showsInlineTransferProgress(value)
      || fileTransferSecondary(value) || !usesCircularTransferState(value)));
  }

  function parentPath(value: string): string {
    const normalized = value.replace(/\\/g, '/');
    const splitAt = normalized.lastIndexOf('/');
    return splitAt >= 0 ? normalized.slice(0, splitAt + 1) : '';
  }

  function subfolderSizeTitle(node: FolderTreeNode): string | undefined {
    if (!filesComplete) return node.unknownSizeCount > 0
      ? 'Known size of loaded files; some sizes are unreported and more files may exist.'
      : 'Known size of loaded files; more files may exist.';
    return node.unknownSizeCount > 0 ? 'Total known file size; unreported sizes are excluded.' : undefined;
  }
</script>


{#snippet folderIdentity()}
  <div class="item-path-block result-path-block" title={fileLayout === 'tree' ? path : undefined}>
    <div class="item-name-line result-name-line">
      {#if fileLayout === 'tree'}
        <strong class="tree-root-path"><span>{parentPath(path)}</span><b>{basename(path)}</b></strong>
      {:else}
        <strong>{basename(path)}</strong>
      {/if}
      {#if locked}<span class="locked-badge">Locked</span>{/if}
    </div>
    <small>{path}</small>
    {#if dataStateLabel}
      <div class="item-data-state"><span>{dataStateLabel}</span>{#if !filesComplete}<small>{totalFileCount == null ? `${files.length} ${files.length === 1 ? 'file' : 'files'} loaded; total unknown` : `${files.length} of ${totalFileCount} ${totalFileCount === 1 ? 'file' : 'files'} loaded`}</small>{/if}</div>
    {/if}
  </div>
{/snippet}

{#snippet contentsControl()}
  {#if contentsState}
    <div class="folder-contents-control-slot">
      {#if contentsState === 'partial' || contentsState === 'failed'}
        <button
          type="button"
          class:failed={contentsState === 'failed'}
          class="folder-load-contents-button"
          aria-label={contentsState === 'failed' ? 'Retry loading all files in this folder' : 'Load all files in this folder'}
          title={contentsState === 'failed'
            ? 'Folder retrieval failed. Try again.'
            : 'Load all files in this folder in case the search omitted any.'}
          onclick={() => onloadfullcontents?.()}
        >+</button>
      {:else if contentsState === 'retrieving'}
        <span class="folder-contents-state retrieving" aria-label="Loading all files in this folder" title="Loading all files in this folder…"><i></i></span>
      {:else}
        <span class="folder-contents-state complete" aria-label="Full folder loaded" title="Full folder loaded"><Icon name="check" /></span>
      {/if}
    </div>
  {/if}
{/snippet}

{#snippet transferSummary()}
  <div class="folder-transfer-summary">
    <div class="folder-transfer-summary-primary">
      <div class="folder-transfer-identity">
        <strong class="folder-transfer-name">{basename(path)}</strong>
        {#if locked}<span class="locked-badge">Locked</span>{/if}
      </div>
      <div class="folder-transfer-summary-meta">
        <span class="folder-transfer-file-count"><strong>{filesComplete ? totalFileCount ?? '—' : `${files.length}/${totalFileCount ?? '?'}`}</strong><span>{filesComplete && totalFileCount === 1 ? 'file' : 'files'}</span></span>
        {@render contentsControl()}
        <strong class="folder-transfer-size" title="Total known file size; unreported sizes are excluded.">{sizeBytes == null ? '—' : formatBytes(sizeBytes)}</strong>
        <span class={`transfer-state ${transfer?.tone ?? ''} ${transferDirectionClass}`}>{transfer?.state}</span>
        {#if actions}<span class="item-card-action">{@render actions()}</span>{/if}
      </div>
    </div>
    <div class="folder-transfer-summary-secondary">
      <small class="folder-transfer-path">{path}</small>
      {#if transfer?.createdAt}<span class="folder-transfer-age">{transfer.direction === 'upload' ? 'latest request' : 'created'} <RelativeTime timestamp={transfer.createdAt} lowercase /></span>{/if}
    </div>
  </div>
{/snippet}

<article
  class="item-card result-card folder-result-block folder-item-card"
  class:locked
  class:selected
  class:partial
  class:preferred
  class:selectable
  class:tree-layout={fileLayout === 'tree'}
  class:transfer-card={Boolean(transfer)}
>
  <div
    class="folder-item-summary folder-result-summary"
    class:transfer-summary={Boolean(transfer)}
    class:has-contents-control={Boolean(contentsState)}
    class:has-card-action={Boolean(actions)}
  >
    {#if transfer}
      {@render transferSummary()}
    {:else}
      {#if selectable}
        <label class="folder-summary-select-target clickable">
          <input
            type="checkbox"
            checked={selected}
            aria-label={`Select all files in ${basename(path)}`}
            use:indeterminate={partial}
            onchange={(event) => onselectall?.((event.currentTarget as HTMLInputElement).checked)}
          />
          {@render folderIdentity()}
        </label>
      {:else}
        <div class="folder-summary-select-target nonselectable">
          {@render folderIdentity()}
        </div>
      {/if}

      <div class="folder-summary-stat folder-file-count"><strong>{filesComplete ? totalFileCount ?? '—' : `${files.length}/${totalFileCount ?? '?'}`}</strong><small>{filesComplete && totalFileCount === 1 ? 'file' : 'files'}</small></div>
      {@render contentsControl()}
      <div class="item-detail result-detail item-size-detail"><strong>{sizeBytes == null ? '—' : formatBytes(sizeBytes)}</strong></div>
      {#if actions}
        <div class="item-card-action">{@render actions()}</div>
      {/if}
    {/if}
  </div>

  {#if transfer}
    {#if dataStateLabel}<div class="item-data-state"><span>{dataStateLabel}</span></div>{/if}
    <div class={`transfer-card-footer folder-transfer-footer ${transfer.tone ?? ''} ${transferDirectionClass}`}>
      {#if transfer.progressPercent !== undefined}
        <div class="transfer-progress-track" aria-label={`${transfer.progressPercent.toFixed(0)}% complete`}>
          <span style={`width:${transfer.progressPercent}%`}></span>
        </div>
      {/if}
      {#if transfer.progressText || transfer.speed || transfer.eta}
        <div class="transfer-progress-meta">
          <span>{transfer.progressText ?? ''}</span>
          <span>{[transfer.speed, transfer.eta].filter(Boolean).join(' · ')}</span>
        </div>
      {/if}
    </div>
  {/if}

  <div class="folder-files-table" class:tree-layout={fileLayout === 'tree'}>
    {#each buildFolderTreeRows(files, selectedFileIds, fileLayout) as row (row.key)}
      {#if row.kind === 'folder'}
        {@const rowSelected = isfolderselected
          ? isfolderselected(row.relativePath) && row.node.selectedCount === row.node.fileCount
          : row.node.fileCount > 0 && row.node.selectedCount === row.node.fileCount}
        {@const rowPartial = row.node.selectedCount > 0 && (row.node.selectedCount < row.node.fileCount || !rowSelected)}
        <svelte:element
          this={selectable ? 'label' : 'div'}
          class="folder-subfolder-row"
          class:clickable={selectable}
          style={`--folder-tree-depth:${row.depth}`}
        >
          {#if selectable}
            <input
              type="checkbox"
              checked={rowSelected}
              aria-label={`Select ${onselectfolder || filesComplete ? 'all matching' : 'loaded'} files in ${row.name}`}
              use:indeterminate={rowPartial}
              onchange={(event) => onselectfolder
                ? onselectfolder(row.relativePath, (event.currentTarget as HTMLInputElement).checked)
                : selectFolderFiles(row.node, (event.currentTarget as HTMLInputElement).checked)}
            />
          {:else}
            <span class="folder-subfolder-icon"><Icon name="folder" /></span>
          {/if}
          <span class="folder-subfolder-path">
            <strong>{row.name}</strong>
            <small>{row.relativePath}</small>
          </span>
          <span class="folder-subfolder-count">{row.node.fileCount} {row.node.fileCount === 1 ? 'file' : 'files'}{filesComplete ? '' : ' loaded'}</span>
          <span class="folder-subfolder-size" title={subfolderSizeTitle(row.node)}>{row.node.sizeBytes === 0 && row.node.unknownSizeCount > 0 ? '—' : formatBytes(row.node.sizeBytes)}</span>
        </svelte:element>
      {:else}
        {@const file = row.file}
        {@const fileDirectionClass = file.transfer?.direction ? `transfer-${file.transfer.direction}` : ''}
        {@const typeIcon = fileTypeIcon({ filename: file.relativePath, hasAudioMetadata: Boolean(file.audio) })}
        <svelte:element
          this={selectable ? 'label' : 'div'}
          class:locked={file.locked}
          class:has-transfer={Boolean(file.transfer)}
          class:has-transfer-detail={needsTransferDetail(file.transfer)}
          class:has-file-action={Boolean(fileActions)}
          class:has-audio={Boolean(file.audio)}
          class:tree-row={fileLayout === 'tree'}
          class="folder-file-row"
          style={`--folder-tree-depth:${row.depth}`}
        >
          {#if selectable}
            <input
              type="checkbox"
              checked={selectedFileIds.has(file.id)}
              onchange={(event) => onselectfile?.(file, (event.currentTarget as HTMLInputElement).checked)}
            />
          {:else}
            <span class:audio={typeIcon === 'track'} class="folder-file-type-icon" aria-hidden="true">
              <Icon name={typeIcon} />
            </span>
          {/if}
          <span class="folder-file-path" title={file.sourcePath ?? file.relativePath}>
            <strong>{row.displayName}</strong>
            {#if file.locked}<small>Locked</small>{/if}
          </span>
          {#if file.audio}
            <span class="folder-file-audio">{audioSummary(file.audio)}</span>
            <span class="folder-file-length">{formatLength(file.audio.lengthSeconds)}</span>
          {/if}
          <span class="folder-file-size" title={file.sizeUnknown ? 'File size is unknown' : undefined}>{file.sizeUnknown ? '—' : formatBytes(file.sizeBytes)}</span>
          {#if file.transfer}
            {@const secondaryTransferText = fileTransferSecondary(file.transfer)}
            {@const showProgress = showsInlineTransferProgress(file.transfer)}
            <span class="folder-file-transfer-strip">
              {#if showProgress}
                <strong class="folder-file-transfer-percent">{fileTransferProgressText(file.transfer)}</strong>
                <span
                  class={`folder-file-progress ${file.transfer.tone ?? ''} ${fileDirectionClass}`}
                  aria-label={`${file.transfer.progressPercent?.toFixed(0)}% complete`}
                >
                  <i style={`width:${file.transfer.progressPercent}%`}></i>
                </span>
              {/if}
              {#if secondaryTransferText}
                <small class="folder-file-transfer-detail" title={secondaryTransferText}>{secondaryTransferText}</small>
              {/if}
              {#if usesCircularTransferState(file.transfer)}
                <span
                  class={`folder-file-state ${fileDirectionClass}`}
                  class:active={file.transfer.tone === 'active'}
                  class:queued={file.transfer.tone === 'queued'}
                  class:complete={file.transfer.tone === 'complete'}
                  aria-label={file.transfer.state}
                  title={file.transfer.state}
                >
                  <Icon name={transferIcon(file.transfer)} />
                </span>
              {:else}
                <span class={`transfer-state folder-file-transfer-label ${file.transfer.tone ?? ''}`}>{file.transfer.state}</span>
              {/if}
            </span>
          {/if}
          {#if fileActions}
            <span class="folder-file-action">{@render fileActions(file)}</span>
          {/if}
        </svelte:element>
      {/if}
    {/each}
  </div>
  {#if fileLoadError}<div class="item-data-state" role="alert">{fileLoadError}</div>{/if}
  {#if onloadmorefiles && (!filesComplete || fileLoadError)}
    <LoadMoreButton label={fileLoadError ? 'Retry loading folder files' : 'Load more folder files'} loading={loadingMoreFiles} onclick={onloadmorefiles} />
  {/if}
</article>
