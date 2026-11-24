<script lang="ts" module>
  /** Where the manual explains what the UI can only hint at. Paths are those
   * of the manual bundled with the daemon at docs/. */
  export const manualPages = {
    fileConditions: 'downloading/file-conditions.html',
    downloadModes: 'downloading/download-modes.html',
    nameFormat: 'downloading/name-format.html',
    csvInput: 'downloading/inputs.html#csv-file',
    spotifyInput: 'downloading/inputs.html#spotify',
    profiles: 'using-sockseek/configuration.html#configuration-profiles',
    sharing: 'using-sockseek/daemon.html#share-a-folder',
    connectivity: 'using-sockseek/daemon.html#ports-and-connectivity',
    remoteCli: 'using-sockseek/daemon.html#remote-cli',
  } as const;

  /** False when the daemon does not serve the manual (--no-docs). */
  export const manualAvailable = (): boolean => document.documentElement.hasAttribute('data-docs');
</script>

<script lang="ts">
  import type { Snippet } from 'svelte';

  /** A link to one topic in the manual. Renders nothing when the daemon does
   * not serve the manual (--no-docs). */
  let { topic, children }: { topic: keyof typeof manualPages; children: Snippet } = $props();
</script>

{#if manualAvailable()}
  <a class="text-link" href={`docs/${manualPages[topic]}`} target="_blank" rel="noopener" data-testid={`manual-${topic}`}>{@render children()}</a>
{/if}
