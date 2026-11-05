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
    onhelp?: () => void;
    onopenchange?: (open: boolean) => void;
  }

  let { value, options, ariaLabel, onchange, onhelp, onopenchange }: Props = $props();
  let menuOpen = $state(false);
  let root = $state<HTMLDivElement | null>(null);

  let current = $derived(options.find((option) => option.value === value) ?? options[0]);

  function setMenuOpen(open: boolean): void {
    menuOpen = open;
    onopenchange?.(open);
  }

  function choose(nextValue: string): void {
    onchange(nextValue);
    setMenuOpen(false);
  }

  function toggleMenu(): void {
    setMenuOpen(!menuOpen);
  }

  function handleWindowPointerDown(event: PointerEvent): void {
    if (!menuOpen) return;
    const target = event.target as Node | null;
    if (target && root?.contains(target)) return;
    setMenuOpen(false);
  }

  function handleWindowKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape' && menuOpen) setMenuOpen(false);
  }
</script>

<svelte:window onpointerdown={handleWindowPointerDown} onkeydown={handleWindowKeydown} />

<div class="mode-icon-toggle" bind:this={root}>
  <button
    type="button"
    class="search-result-mode-button mode-icon-button"
    aria-label={`${ariaLabel}: ${current?.label ?? value}. Choose mode.`}
    aria-haspopup="menu"
    aria-expanded={menuOpen}
    title={`${current?.label ?? value} · choose mode`}
    onpointerdown={(event) => event.preventDefault()}
    onclick={toggleMenu}
  >
    {#if current}
      <Icon name={current.icon} />
    {/if}
  </button>

  {#if menuOpen}
    <div class="mode-icon-menu" role="menu" aria-label={ariaLabel} use:anchoredMenu={{ anchor: root, align: 'start', gap: 7 }}>
      {#each options as option}
        <button
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
      {#if onhelp}
        <button type="button" role="menuitem" class="mode-menu-help" onclick={() => { setMenuOpen(false); onhelp?.(); }}>
          <span class="mode-menu-check" aria-hidden="true"></span>
          <span class="mode-menu-icon" aria-hidden="true"><Icon name="help" /></span>
          <span>About search modes</span>
        </button>
      {/if}
    </div>
  {/if}
</div>
