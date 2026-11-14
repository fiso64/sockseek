<script lang="ts">
  let { available, class: className = '' }: { available: boolean | null | undefined; class?: string } = $props();
</script>

{#if available != null}
  <span class={`peer-slot ${className}`} class:available class:unavailable={!available}>
    <i aria-hidden="true"></i>{available ? 'Free slot' : 'No slot'}
  </span>
{/if}
