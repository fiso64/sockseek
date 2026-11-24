<script lang="ts">
  import { matchedText, matchedPath } from '../MatchedText.svelte';
  import type { Snippet } from 'svelte';
  import Icon from '../Icon.svelte';
  import LockedIndicator from '../LockedIndicator.svelte';
  import LoadMoreButton from '../LoadMoreButton.svelte';
  import TransferStateLabel from './TransferStateLabel.svelte';
  import TransferItemActionButton from './TransferItemActionButton.svelte';
  import RelativeTime from '../RelativeTime.svelte';
  import { folderItemIconPaths, type AppIconName } from '../../app/icons';
  import type { FolderItemFile, TransferPresentation } from '../../domains/presentation/items';
  import { audioSummary, basename, formatBytes, formatLength, isBusyTransfer, isTerminalTransfer, parentPath } from '../../domains/presentation/items';
  import { buildFolderTreeRows, type FolderTreeNode } from '../../domains/presentation/folder-tree';
  import { fileTypeIcon } from '../../domains/presentation/file-types';

  interface Props {
    highlight?: RegExp;
    path: string;
    sizeBytes: number | null;
    files: FolderItemFile[];
    totalFileCount?: number | null;
    filesComplete?: boolean;
    fileLayout?: 'sections' | 'tree';
    showFileIcons?: boolean;
    dataStateLabel?: string;
    contentsState?: 'partial' | 'retrieving' | 'complete' | 'failed';
    locked?: boolean;
    selected?: boolean;
    partial?: boolean;
    preferred?: boolean;
    selectable?: boolean;
    keyboardKey?: string;
    keyboardChildren?: boolean;
    transfer?: TransferPresentation;
    selectedFileIds?: Set<string>;
    /** Shown left of the folder state label. */
    retry?: Snippet;
    actions?: Snippet;
    fileActions?: Snippet<[FolderItemFile]>;
    /** Retries a failed file; its button sits left of the file state label. */
    onretryfile?: (file: FolderItemFile) => void;
    actionsDisabled?: boolean;
    onselectall?: (selected: boolean) => void;
    onselectfiles?: (files: FolderItemFile[], selected: boolean) => void;
    onselectfolder?: (relativePath: string, selected: boolean, partial: boolean) => void;
    isfolderselected?: (relativePath: string) => boolean;
    onloadfullcontents?: () => void;
    onloadmorefiles?: () => void;
    loadingMoreFiles?: boolean;
  }

  let {
    highlight,
    path,
    sizeBytes,
    files,
    totalFileCount = files.length,
    filesComplete = true,
    fileLayout = 'sections',
    showFileIcons = true,
    dataStateLabel,
    contentsState,
    locked = false,
    selected = false,
    partial = false,
    preferred = false,
    selectable = false,
    keyboardKey,
    keyboardChildren = Boolean(keyboardKey),
    transfer,
    selectedFileIds = new Set<string>(),
    retry,
    actions,
    fileActions,
    onretryfile,
    actionsDisabled = false,
    onselectall,
    onselectfiles,
    onselectfolder,
    isfolderselected,
    onloadfullcontents,
    onloadmorefiles,
    loadingMoreFiles = false,
  }: Props = $props();

  let transferDirectionClass = $derived(transfer?.direction ? `transfer-${transfer.direction}` : '');

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
    // A file re-queued after an interrupted attempt keeps its bytes: show them.
    return value.progressPercent !== undefined && !isTerminalTransfer(value)
      && (value.tone !== 'queued' || value.progressPercent > 0);
  }

  function fileTransferProgressText(value: TransferPresentation): string {
    return value.progressPercent === undefined ? '' : `${Math.round(value.progressPercent)}%`;
  }

  function fileTransferSecondary(value: TransferPresentation): string {
    // Explanations belong to the state label's details dialog, not beside it.
    const moving = [value.speed, value.eta].filter(Boolean).join(' · ');
    // The slot is free while nothing moves: say which attempt is waiting.
    return moving || (value.attempt ? `attempt ${value.attempt}` : '');
  }

  // Without path-level selection, a subfolder selects its loaded files at once.
  function subfolderFiles(node: FolderTreeNode, selected: boolean, into: FolderItemFile[] = []): FolderItemFile[] {
    for (const { file } of node.files)
      if (!file.locked && selectedFileIds.has(file.id) !== selected) into.push(file);
    for (const child of node.folders.values()) subfolderFiles(child, selected, into);
    return into;
  }

  function subfolderSizeTitle(node: FolderTreeNode): string | undefined {
    if (!filesComplete) return node.unknownSizeCount > 0
      ? 'Known size of loaded files; some sizes are unreported and more files may exist.'
      : 'Known size of loaded files; more files may exist.';
    return node.unknownSizeCount > 0 ? 'Total known file size; unreported sizes are excluded.' : undefined;
  }
</script>


{#snippet rowIcon(name: keyof typeof folderItemIconPaths)}
  <svg
    class="app-icon folder-row-icon" class:audio={name === 'track'} aria-hidden="true"
    viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"
  >
    <path d={folderItemIconPaths[name]} />
  </svg>
{/snippet}

{#snippet folderIdentity()}
  <div class="folder-heading-copy">
    <div class="folder-heading-identity">
      <strong class="folder-heading-path" class:folder-transfer-path={Boolean(transfer)} title={path}>
        <bdi dir="ltr">{@render matchedPath(path, highlight, parentPath(path).length, Boolean(transfer))}</bdi>
      </strong>
    </div>
    <!-- A one-file folder's row already shows its size; keep only the age. -->
    {#if totalFileCount !== 1 || transfer?.createdAt}
      <div class="folder-heading-meta">
        {#if totalFileCount !== 1}
          <span class="folder-file-count" class:folder-transfer-file-count={Boolean(transfer)}><strong>{transfer && !filesComplete ? `${files.length}/${totalFileCount ?? '?'}` : totalFileCount ?? '—'}</strong> files</span>
          <span class="folder-heading-size" title="Total known file size; unreported sizes are excluded.">{sizeBytes == null ? '—' : formatBytes(sizeBytes)}</span>
        {/if}
        {#if transfer?.createdAt}<span class="folder-transfer-age"><RelativeTime timestamp={transfer.createdAt} lowercase /></span>{/if}
      </div>
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
          class="button folder-load-contents-button"
          aria-label={contentsState === 'failed' ? 'Retry loading all files in this folder' : 'Load all files in this folder'}
          title={contentsState === 'failed'
            ? 'Folder retrieval failed. Try again.'
            : 'Load all files in this folder in case the search omitted any.'}
          onclick={() => onloadfullcontents?.()}
        ><Icon name="search" /></button>
      {:else if contentsState === 'retrieving'}
        <span class="folder-contents-state retrieving" aria-label="Loading all files in this folder" title="Loading all files in this folder…"><i></i></span>
      {:else}
        <span class="folder-contents-state complete" aria-label="Full folder loaded" title="Full folder loaded"><Icon name="check" /></span>
      {/if}
    </div>
  {/if}
{/snippet}

<article
  class="surface-card item-card result-card folder-result-block folder-item-card"
  class:locked
  class:selected={selected && !locked}
  class:partial={partial && !locked}
  class:preferred
  data-keyboard-item={keyboardKey}
  data-range-disabled={locked || undefined}
  tabindex="-1"
  class:selectable={selectable && !locked}
  class:tree-layout={fileLayout === 'tree'}
  class:transfer-card={Boolean(transfer)}
>
  <div class="folder-item-summary folder-result-summary" class:folder-transfer-summary={Boolean(transfer)}>
    <div class="folder-heading-primary">
      {#if selectable && !locked}
        <label class="folder-summary-select-target clickable" data-range-toggle>
          <input
            type="checkbox"
            checked={selected}
            aria-label={`Select all files in ${basename(path)}`}
            indeterminate={partial}
            onchange={(event) => {
              event.currentTarget.checked = selected;
              event.currentTarget.indeterminate = partial;
              onselectall?.(!selected);
            }}
          />
          {@render folderIdentity()}
        </label>
      {:else}
        <div class="folder-summary-select-target" class:nonselectable={!locked}>
          {#if locked}<LockedIndicator />{/if}
          {@render folderIdentity()}
        </div>
      {/if}
      <div class="folder-heading-actions">
        {@render contentsControl()}
        {#if retry}<span class="item-card-action">{@render retry()}</span>{/if}
        {#if transfer?.job}<TransferStateLabel {transfer} {path} {sizeBytes} {files} {filesComplete} />{/if}
        {#if actions}<span class="item-card-action">{@render actions()}</span>{/if}
      </div>
    </div>
    {#if dataStateLabel}
      <div class="item-data-state">{dataStateLabel}</div>
    {/if}
  </div>

  {#if transfer && !isTerminalTransfer(transfer)}
    <div class={`transfer-card-footer folder-transfer-footer ${transfer.tone ?? ''} ${transferDirectionClass}`}>
      {#if transfer.progressPercent !== undefined}
        <div class="progress-track transfer-progress-track" aria-label={`${transfer.progressPercent.toFixed(0)}% complete`}>
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

  <div class="folder-files-table" class:transfer-files={Boolean(transfer)}>
    {#each buildFolderTreeRows(files, selectedFileIds, fileLayout) as row (row.key)}
      {#if row.kind === 'folder'}
        {@const rowSelected = isfolderselected
          ? isfolderselected(row.relativePath) && row.node.selectedCount === row.node.selectableFileCount
          : row.node.selectableFileCount > 0 && row.node.selectedCount === row.node.selectableFileCount}
        {@const rowPartial = row.node.selectedCount > 0 && (row.node.selectedCount < row.node.selectableFileCount || !rowSelected)}
        {@const selectableFolder = selectable && Boolean(onselectfolder || onselectfiles) && row.node.selectableFileCount > 0}
        <svelte:element
          this={selectableFolder ? 'label' : 'div'}
          data-keyboard-item={keyboardChildren && fileLayout === 'tree' ? row.relativePath : undefined}
          data-range-toggle={selectableFolder || undefined}
          data-selection-folder={keyboardChildren && fileLayout === 'tree' || undefined}
          data-range-disabled={row.node.selectableFileCount === 0 || undefined}
          tabindex={keyboardChildren && fileLayout === 'tree' ? -1 : undefined}
          class="folder-subfolder-row"
          class:clickable={selectableFolder}
        >
          {#if row.node.selectableFileCount === 0}
            <LockedIndicator />
          {:else if selectableFolder}
            <input
              type="checkbox"
              checked={rowSelected}
              aria-label={`Select all matching files in ${row.name}`}
              indeterminate={rowPartial}
              onchange={(event) => {
                event.currentTarget.checked = rowSelected;
                event.currentTarget.indeterminate = rowPartial;
                if (onselectfolder) onselectfolder(row.relativePath, !rowSelected, rowPartial);
                else onselectfiles?.(subfolderFiles(row.node, !rowSelected), !rowSelected);
              }}
            />
          {:else}
            {@render rowIcon('folder')}
          {/if}
          <strong class="folder-subfolder-path" title={row.relativePath} style:--folder-tree-depth={row.depth || undefined}>
            {#if selectableFolder || row.node.selectableFileCount === 0}{@render rowIcon('folder')}{/if}
            <span>{#if highlight}{@render matchedText(row.relativePath, highlight, row.relativePath.length - row.name.length)}{:else}{row.name}{/if}</span>
          </strong>
          <span class="folder-subfolder-count">{row.node.fileCount} {row.node.fileCount === 1 ? 'file' : 'files'}{filesComplete ? '' : ' loaded'}</span>
          <span class="folder-subfolder-size" title={subfolderSizeTitle(row.node)}>{row.node.sizeBytes === 0 && row.node.unknownSizeCount > 0 ? '—' : formatBytes(row.node.sizeBytes)}</span>
        </svelte:element>
      {:else}
        {@const file = row.file}
        {@const matchPath = highlight && file.sourcePath?.endsWith(row.displayName) ? file.sourcePath : row.displayName}
        {@const fileDirectionClass = file.transfer?.direction ? `transfer-${file.transfer.direction}` : ''}
        {@const icon = showFileIcons && fileTypeIcon({ filename: row.displayName, hasAudioMetadata: Boolean(file.audio) })}
        <svelte:element
          this={selectable && !file.locked ? 'label' : 'div'}
          class:locked={file.locked}
          class:has-transfer={Boolean(file.transfer)}
          class:has-audio={Boolean(file.audio)}
          data-keyboard-item={keyboardChildren ? file.id : undefined}
          data-range-toggle={selectable && !file.locked || undefined}
          data-range-disabled={file.locked || undefined}
          tabindex={keyboardChildren ? -1 : undefined}
          class="folder-file-row"
        >
          {#if file.locked}
            <LockedIndicator />
          {:else if selectable}
            <input
              type="checkbox"
              checked={selectedFileIds.has(file.id)}
              onchange={(event) => onselectfiles?.([file], (event.currentTarget as HTMLInputElement).checked)}
            />
          {:else if icon}
            {@render rowIcon(icon)}
          {/if}
          <span class="folder-file-path" title={file.sourcePath ?? file.relativePath} style:--folder-tree-depth={row.depth || undefined}>
            {#if icon && (selectable || file.locked)}{@render rowIcon(icon)}{/if}
            <strong><bdi dir="ltr">{#if highlight}{@render matchedText(matchPath, highlight, matchPath.length - row.displayName.length)}{:else}{row.displayName}{/if}</bdi></strong>
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
                <span class="folder-file-progress-group">
                  <strong class="folder-file-transfer-percent">{fileTransferProgressText(file.transfer)}</strong>
                  <span
                    class={`progress-track folder-file-progress ${file.transfer.tone ?? ''} ${fileDirectionClass}`}
                    aria-label={`${file.transfer.progressPercent?.toFixed(0)}% complete`}
                  >
                    <i style={`width:${file.transfer.progressPercent}%`}></i>
                  </span>
                </span>
              {/if}
              {#if secondaryTransferText}
                <small class="folder-file-transfer-detail" class:attempt={!file.transfer.speed && !file.transfer.eta} title={secondaryTransferText}>{secondaryTransferText}</small>
              {/if}
              {#if usesCircularTransferState(file.transfer)}
                <TransferStateLabel transfer={file.transfer} path={file.sourcePath ?? file.relativePath}
                  sizeBytes={file.sizeUnknown ? null : file.sizeBytes} circular>
                  {#if !isBusyTransfer(file.transfer)}<Icon name={transferIcon(file.transfer)} />{/if}
                </TransferStateLabel>
              {:else}
                <span class="folder-file-transfer-label">
                  {#if onretryfile && file.transfer.record?.status.availableActions?.some(action => action.kind === 'retry')}
                    <TransferItemActionButton kind="retry" label={`Retry ${file.relativePath}`} disabled={actionsDisabled} onclick={() => onretryfile(file)} />
                  {/if}
                  <TransferStateLabel transfer={file.transfer} path={file.sourcePath ?? file.relativePath}
                    sizeBytes={file.sizeUnknown ? null : file.sizeBytes} />
                </span>
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
  {#if onloadmorefiles && !filesComplete}
    <LoadMoreButton label="Load more" loading={loadingMoreFiles} onclick={onloadmorefiles} />
  {/if}
</article>
