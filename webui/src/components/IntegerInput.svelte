<script lang="ts">
  import type { HTMLInputAttributes } from 'svelte/elements';

  /** A non-negative integer or null when empty; other characters are never accepted. */
  let { value = $bindable(), ...rest }: { value: number | null } & Omit<HTMLInputAttributes, 'value' | 'type'> = $props();
</script>

<input {...rest} class="field" type="text" inputmode="numeric" autocomplete="off"
  value={value ?? ''}
  oninput={(event) => {
    const input = event.currentTarget;
    const digits = input.value.replace(/\D/g, '');
    if (digits !== input.value) input.value = digits;
    value = digits ? Number(digits) : null;
  }} />
