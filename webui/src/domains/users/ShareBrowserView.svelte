<script lang="ts">
  import { untrack, type ComponentProps } from 'svelte';
  import type { ShareBrowserPosition } from './session';
  import ShareBrowser from './ShareBrowser.svelte';

  let { source, ...props }: ComponentProps<typeof ShareBrowser> = $props();
  let displayed = $state.raw(untrack(() => source));
  let browser = $state<ReturnType<typeof ShareBrowser>>();
  let position = $state.raw<ShareBrowserPosition>();

  $effect.pre(() => {
    const next = source;
    if (next.browseId === displayed.browseId) return;
    position = next.username === displayed.username ? untrack(() => browser?.capturePosition()) : undefined;
    displayed = next;
  });
</script>

{#key displayed.browseId}
  <ShareBrowser {...props} source={displayed} {position} bind:this={browser}/>
{/key}
