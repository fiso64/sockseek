<script lang="ts">
  import Icon from './Icon.svelte';
  import type { AppIconName } from '../app/icons';
  import { anchoredMenu } from '../lib/anchored-menu';

  export type ModeIconOption = {
    value: string;
    label: string;
    icon: AppIconName;
  };

  interface Props {
    value: string;
    options: ModeIconOption[];
    ariaLabel: string;
    onchange: (value: string) => void;
    onfocussearch: () => void;
    onhelp?: () => void;
    open: boolean;
  }

  let { value, options, ariaLabel, onchange, onfocussearch, onhelp, open = $bindable() }: Props = $props();
  let root = $state<HTMLDivElement | null>(null);

  let current = $derived(options.find((option) => option.value === value) ?? options[0]);

  function choose(nextValue: string): void {
    onchange(nextValue);
    open = false;
    onfocussearch();
  }

  function toggleMenu(): void {
    open = !open;
    onfocussearch();
  }

  function handleWindowPointerDown(event: PointerEvent): void {
    if (!open) return;
    const target = event.target as Node | null;
    if (target && root?.contains(target)) return;
    open = false;
  }
</script>

<svelte:window onpointerdown={handleWindowPointerDown} />

<div class="mode-icon-toggle" bind:this={root}>
  <button
    type="button"
    class="button search-result-mode-button mode-icon-button"
    aria-label={`${ariaLabel}: ${current?.label ?? value}. Choose mode.`}
    aria-haspopup="menu"
    aria-expanded={open}
    title={`${current?.label ?? value} · choose mode`}
    onpointerdown={(event) => event.preventDefault()}
    onclick={toggleMenu}
  >
    {#if current}
      <Icon name={current.icon} />
    {/if}
  </button>

  {#if open}
    <div class="popover-panel mode-icon-menu" use:anchoredMenu={{ anchor: root, align: 'start', gap: 7 }}>
      <div class="mode-menu-choices" role="menu" aria-label={ariaLabel}>
        {#each options as option}
          <button class="menu-item"
            type="button"
            role="menuitemradio"
            aria-checked={option.value === value}
            class:active={option.value === value}
            onpointerdown={(event) => event.preventDefault()}
            onclick={() => choose(option.value)}
          >
            <span class="mode-menu-check" aria-hidden="true">{option.value === value ? '✓' : ''}</span>
            <span class="mode-menu-icon" aria-hidden="true"><Icon name={option.icon} /></span>
            <span>{option.label}</span>
          </button>
        {/each}
      </div>
      {#if onhelp}
        <div class="mode-menu-footer">
          <ul class="mode-menu-shortcuts" aria-label="Search bar shortcuts">
            <li><kbd>↑↓</kbd> Mode</li>
            <li><kbd>Ctrl+↵</kbd> New job</li>
          </ul>
          <button type="button" class="button quiet mode-menu-help" aria-label="About search modes" title="About search modes" onclick={() => { open = false; onhelp?.(); }}>
            <Icon name="help" />
          </button>
        </div>
      {/if}
    </div>
  {/if}
</div>
