<script lang="ts">
  import type { AppIconName } from '../app/icons';
  import Icon from './Icon.svelte';

  let { icon, title, detail, class: layout = 'resource-state-notice empty blocking' }: {
    icon: AppIconName;
    title: string;
    detail?: string;
    class?: string;
  } = $props();
</script>

<div class={layout} role="status">
  <span class="empty-state-icon" aria-hidden="true"><Icon name={icon} /></span>
  <span class="empty-state-copy"><strong>{title}</strong>{#if detail}<small>{detail}</small>{/if}</span>
</div>

<style>
  .empty-state-icon {
    width: 42px;
    height: 42px;
    display: grid;
    place-items: center;
    justify-self: center;
    border: 1px solid var(--border-strong);
    border-radius: 12px;
    background: var(--surface-raised);
    color: var(--accent-text);
    box-shadow: var(--surface-shadow);
    --app-icon-size: 20px;
  }
  .empty-state-copy { display: grid; gap: 5px; }
  strong { color: var(--text); font-size: .86rem; font-weight: 600; }
  small { color: var(--muted); font-size: .78rem; line-height: 1.55; }
</style>
