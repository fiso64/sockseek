<script module lang="ts">
  import type { AppIconName } from '../../app/icons';
  import type { NewJobChoice } from './submission';

  const groups: { title: string; choices: { value: NewJobChoice; label: string; detail: string; icon: AppIconName }[] }[] = [
    { title: 'Download automatically', choices: [
      { value: 'song', label: 'Song', detail: 'Find and download one song', icon: 'track' },
      { value: 'album', label: 'Album', detail: 'Find and download one album', icon: 'album' },
    ] },
    { title: 'From source', choices: [
      { value: 'spotify', label: 'Spotify', detail: 'Playlist, album, or saved library', icon: 'spotify' },
      { value: 'youtube', label: 'YouTube', detail: 'Playlist URL', icon: 'youtube' },
      { value: 'soundcloud', label: 'SoundCloud', detail: 'Track or playlist URL', icon: 'link' },
      { value: 'bandcamp', label: 'Bandcamp', detail: 'Track, album, wishlist, or artist URL', icon: 'bandcamp' },
      { value: 'musicbrainz', label: 'MusicBrainz', detail: 'Release, release group, or collection URL', icon: 'musicbrainz' },
      { value: 'soulseek', label: 'Soulseek', detail: 'File or directory slsk:// link', icon: 'soulseek' },
    ] },
    { title: 'From file', choices: [
      { value: 'csv', label: 'CSV file', detail: 'CSV of songs and albums', icon: 'upload-file' },
      { value: 'list', label: 'List file', detail: 'List of nested sources', icon: 'upload-file' },
    ] },
  ];
  export function jobChoiceMetadata(value: NewJobChoice) {
    return groups.flatMap((group) => group.choices).find((choice) => choice.value === value)!;
  }
</script>

<script lang="ts">
  import Icon from '../../components/Icon.svelte';
  let { value, onchange }: { value: NewJobChoice; onchange: (value: NewJobChoice) => void } = $props();
</script>

<aside class="new-job-type-nav" aria-label="Job type">
  {#each groups as group (group.title)}
    <section>
      <h3>{group.title}</h3>
      {#each group.choices as choice (choice.value)}
        <button type="button" class:active={value === choice.value} class="new-job-type-choice" onclick={() => onchange(choice.value)}>
          <Icon name={choice.icon} />
          <span><strong>{choice.label}</strong><small>{choice.detail}</small></span>
        </button>
      {/each}
    </section>
  {/each}
</aside>
