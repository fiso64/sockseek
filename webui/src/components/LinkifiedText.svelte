<script lang="ts">
  import { linkify } from '../lib/linkify';

  interface Props { text: string; }
  let { text }: Props = $props();

  let segments = $derived(linkify(text));
</script>

{#each segments as segment}
  {#if segment.kind === 'link' && /^slsk:/i.test(segment.href)}<a class="linkified" href={segment.href}>{segment.value}</a>
  {:else if segment.kind === 'link'}<a class="linkified" href={segment.href} target="_blank" rel="noopener noreferrer">{segment.value}</a>{:else}{segment.value}{/if}
{/each}
