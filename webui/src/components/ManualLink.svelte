<script lang="ts" module>
  /** Where the manual explains what the UI can only hint at. Paths are relative
   * to the manual's root. */
  export const manualPages = {
    rankingAndFiltering: 'downloading/ranking-and-filtering.html',
    downloadModes: 'downloading/download-modes.html',
    nameFormat: 'downloading/name-format.html',
    csvInput: 'downloading/inputs.html#csv-file',
    spotifyInput: 'downloading/inputs.html#spotify',
    soundcloudInput: 'downloading/inputs.html#soundcloud',
    listInput: 'downloading/inputs.html#list-file',
    profiles: 'using-sockseek/configuration.html#configuration-profiles',
    sharing: 'using-sockseek/daemon.html#share-a-folder',
    connectivity: 'using-sockseek/daemon.html#ports-and-connectivity',
    remoteCli: 'using-sockseek/daemon.html#remote-cli',
  } as const;

  /** The manual's address: the copy bundled with the daemon, or the published
   * one when the daemon does not serve it (--no-docs). Both share one layout,
   * but the published one follows the newest version: `from=app` lets it
   * explain a page or section this daemon still links to and it has moved. */
  export const manualUrl = (page = ''): string => {
    if (document.documentElement.hasAttribute('data-docs')) return `docs/${page}`;
    const [path, anchor] = page.split('#');
    return `https://fiso64.github.io/sockseek/${path}?from=app${anchor ? `#${anchor}` : ''}`;
  };
</script>

<script lang="ts">
  import type { Snippet } from 'svelte';

  /** A link to one topic in the manual. */
  let { topic, children }: { topic: keyof typeof manualPages; children: Snippet } = $props();
</script>

<a class="text-link" href={manualUrl(manualPages[topic])} target="_blank" rel="noopener" data-testid={`manual-${topic}`}>{@render children()}</a>
