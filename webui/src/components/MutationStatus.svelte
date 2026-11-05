<script lang="ts">
  export interface MutationState {
    phase: 'idle' | 'pending' | 'succeeded' | 'partially-succeeded' | 'rejected' | 'failed';
    label?: string;
    detail?: string;
  }

  interface Props { state: MutationState; }
  let { state }: Props = $props();
</script>

{#if state.phase !== 'idle'}
  <span class={`mutation-status ${state.phase}`} role="status">
    {state.label ?? state.phase}
    {#if state.detail}<small>{state.detail}</small>{/if}
  </span>
{/if}
