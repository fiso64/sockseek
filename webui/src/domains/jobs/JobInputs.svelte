<script lang="ts">
  import HelpPopover from '../../components/HelpPopover.svelte';
  import Icon from '../../components/Icon.svelte';
  import type { NewJobDraft } from './submission';

  let { draft, fileName = '', onchange, onfile }: {
    draft: NewJobDraft;
    fileName?: string;
    onchange: (draft: NewJobDraft) => void;
    onfile: (file: File) => void;
  } = $props();
  let dragging = $state(false);
  const placeholders = {
    spotify: 'Paste a Spotify URL…', youtube: 'Paste a YouTube URL…',
    soundcloud: 'Paste a SoundCloud URL…', bandcamp: 'Paste a Bandcamp URL…', musicbrainz: 'Paste a MusicBrainz URL…', 'soulseek-link': 'Paste a slsk:// link…',
  };
  function setField(field: 'artist' | 'title' | 'album' | 'source', event: Event): void {
    onchange({ ...draft, [field]: (event.currentTarget as HTMLInputElement).value });
  }
  function chooseFile(event: Event): void {
    const input = event.currentTarget as HTMLInputElement;
    const file = input.files?.[0];
    if (file) onfile(file);
    input.value = '';
  }
  function drop(event: DragEvent): void {
    event.preventDefault();
    dragging = false;
    const file = event.dataTransfer?.files[0];
    if (file) onfile(file);
  }
</script>

{#if draft.choice === 'song'}
  <label><span>Artist <small>(optional)</small></span><input value={draft.artist} placeholder="Artist…" oninput={(event) => setField('artist', event)} /></label>
  <label><span>Title</span><input value={draft.title} placeholder="Song title…" oninput={(event) => setField('title', event)} /></label>
{:else if draft.choice === 'album'}
  <label><span>Artist <small>(optional)</small></span><input value={draft.artist} placeholder="Artist…" oninput={(event) => setField('artist', event)} /></label>
  <label><span>Album</span><input value={draft.album} placeholder="Album title…" oninput={(event) => setField('album', event)} /></label>
{:else if draft.choice === 'csv' || draft.choice === 'list'}
  <div class="new-job-source-mode">
    <div class="new-job-segmented" role="group" aria-label="File source">
      <button type="button" data-testid="job-file-upload" aria-pressed={draft.fileInput === 'upload'} class:active={draft.fileInput === 'upload'} onclick={() => onchange({ ...draft, fileInput: 'upload' })}>Upload file</button>
      <button type="button" data-testid="job-file-path" aria-pressed={draft.fileInput === 'path'} class:active={draft.fileInput === 'path'} onclick={() => onchange({ ...draft, fileInput: 'path' })}>Server path</button>
    </div>
  </div>
  {#if draft.fileInput === 'path'}
    <label class="new-job-source-field"><span>File path on the server</span><input data-testid="job-file-path-input" value={draft.source} placeholder={draft.choice === 'csv' ? '/path/to/music.csv' : '/path/to/list.txt'} oninput={(event) => setField('source', event)} /></label>
  {:else}
    <label class:dragging class="new-job-drop-zone" ondragover={(event) => { event.preventDefault(); dragging = true; }} ondragleave={() => (dragging = false)} ondrop={drop}>
      <input type="file" accept={draft.choice === 'csv' ? '.csv,text/csv' : '.txt,.list,text/plain'} onchange={chooseFile} />
      <Icon name="upload-file" />
      <span>
        <strong>{fileName || `Drop a ${draft.choice === 'csv' ? 'CSV' : 'list'} file here`}</strong>
        {#if !fileName}<small>or click to choose a local file</small>{/if}
      </span>
    </label>
  {/if}
{:else}
  {#if draft.choice === 'spotify'}
    <div class="new-job-source-mode">
      <span>Input <HelpPopover title="Spotify input" testId="spotify-input-help">Liked songs and albums use the Spotify account configured for the daemon.</HelpPopover></span>
      <div class="new-job-segmented" role="group" aria-label="Spotify input">
        <button type="button" class:active={draft.spotifyInput === 'url'} onclick={() => onchange({ ...draft, spotifyInput: 'url' })}>Playlist / album URL</button>
        <button type="button" class:active={draft.spotifyInput === 'likes'} onclick={() => onchange({ ...draft, spotifyInput: 'likes' })}>Liked songs</button>
        <button type="button" class:active={draft.spotifyInput === 'albums'} onclick={() => onchange({ ...draft, spotifyInput: 'albums' })}>Liked albums</button>
      </div>
    </div>
  {/if}
  {#if draft.choice !== 'spotify' || draft.spotifyInput === 'url'}
    <label class="new-job-source-field"><span>URL</span><input value={draft.source} placeholder={placeholders[draft.choice]} oninput={(event) => setField('source', event)} /></label>
  {/if}
{/if}
