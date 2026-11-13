<script lang="ts">
  import { onMount } from 'svelte';
  import { daemonApi } from '../api/client';
  import type { Schema } from '../api/types';
  import Icon from './Icon.svelte';
  let { value = [], applied = [], onchange, disabled = false }: {
    value?: string[]; applied?: Schema<'AppliedProfileDto'>[]; disabled?: boolean;
    onchange: (names: string[]) => void;
  } = $props();
  let catalog = $state<Schema<'ProfileSummaryDto'>[]>([]);
  let error = $state('');
  let loading = $state(true);
  const lifetime = new AbortController();
  async function load() {
    loading = true;
    error = '';
    try { catalog = (await daemonApi.getProfiles(lifetime.signal)).filter(profile => profile.hasDownloadSettings); }
    catch (cause) { if (!lifetime.signal.aborted) error = cause instanceof Error ? cause.message : String(cause); }
    finally { if (!lifetime.signal.aborted) loading = false; }
  }
  onMount(() => { void load(); return () => lifetime.abort(); });
  const id = $props.id();
  let popout = $state<HTMLDivElement>();
  let open = $state(false);
  let position = $state('');
  let inherited = $derived(applied.filter(profile => profile.source === 'automatic' || profile.source === 'startup'));
  let names = $derived([...new Set([...catalog.map(profile => profile.name), ...value, ...inherited.map(profile => profile.name)])]);

  function positionPopout(event: MouseEvent) {
    const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
    const below = innerHeight - rect.bottom - 12;
    const above = rect.top - 12;
    const upwards = below < 240 && above > below;
    position = `left:${Math.max(12, Math.min(rect.right - 250, innerWidth - 262))}px;`
      + (upwards ? `bottom:${innerHeight - rect.top + 6}px;` : `top:${rect.bottom + 6}px;`)
      + `max-height:${Math.max(80, upwards ? above : below)}px;`;
  }
</script>

<svelte:window onresize={() => popout?.hidePopover()} />

{#if names.length || error}
  <div class="profile-picker">
    <button type="button" class="profile-trigger" {disabled} popovertarget={id}
      aria-expanded={open} onclick={positionPopout}>
      <Icon name="sliders" />Profiles
    </button>
    <div bind:this={popout} {id} popover="auto" class="profile-popout" style={position}
      aria-label="Profiles" ontoggle={(event) => open = event.newState === 'open'}>
      {#each names as name (name)}
        {@const automatic = inherited.find(profile => profile.name === name)}
        <label class="profile-option" title={automatic ? `Applied by ${automatic.source === 'automatic' ? 'auto-profile' : 'daemon startup'}` : undefined}>
          <input type="checkbox" aria-label={name} checked={value.includes(name) || Boolean(automatic)}
            disabled={disabled || Boolean(automatic)}
            onchange={(event) => onchange(event.currentTarget.checked ? [...value, name] : value.filter(item => item !== name))} />
          <span class="profile-check"><Icon name="check" /></span>
          <span class="profile-name">{name}</span>
        </label>
      {/each}
      {#if error}<p role="alert">{error} <button type="button" disabled={loading} onclick={() => void load()}>Retry</button></p>{/if}
    </div>
  </div>
{/if}

<style>
  .profile-picker { flex-shrink: 0; }
  .profile-trigger { display: inline-flex; align-items: center; gap: 6px; min-height: 30px; padding: 5px 8px; border: 0; border-radius: 7px; background: transparent; color: var(--muted); font-size: .72rem; cursor: pointer; }
  .profile-trigger:hover, .profile-trigger[aria-expanded="true"] { background: var(--surface-subtle); color: var(--text); }
  .profile-trigger :global(svg) { width: 14px; height: 14px; }
  .profile-popout { position: fixed; inset: auto; width: min(250px, calc(100vw - 24px)); box-sizing: border-box; margin: 0; padding: 5px; overflow-y: auto; border: 1px solid var(--border); border-radius: 9px; background: var(--surface-raised); color: var(--text); box-shadow: 0 10px 30px rgb(0 0 0 / .22); }
  .profile-option { position: relative; display: flex; align-items: center; gap: 8px; padding: 8px; border-radius: 5px; cursor: pointer; font-size: .76rem; }
  .profile-option:hover, .profile-option:has(input:focus-visible) { background: var(--surface-subtle); }
  .profile-option:has(input:focus-visible) { outline: 1px solid var(--accent); }
  .profile-option input { position: absolute; opacity: 0; width: 1px; height: 1px; }
  .profile-check { display: flex; width: 15px; flex-shrink: 0; color: var(--accent); visibility: hidden; }
  input:checked + .profile-check { visibility: visible; }
  .profile-check :global(svg) { width: 15px; height: 15px; }
  .profile-name { overflow-wrap: anywhere; }
  p { padding: 8px; margin: 0; font-size: .76rem; }
</style>
