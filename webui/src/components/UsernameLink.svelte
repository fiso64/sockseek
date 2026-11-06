<script lang="ts">
  import Icon from './Icon.svelte';
  import type { UserLinkActions } from '../app/navigation';
  import { anchoredMenu } from '../lib/anchored-menu';

  interface Props {
    username: string;
    actions: UserLinkActions;
    title?: string;
  }

  let { username, actions, title }: Props = $props();
  let root = $state<HTMLSpanElement | null>(null);
  let open = $state(false);

  function toggle(event: MouseEvent): void {
    event.stopPropagation();
    open = !open;
  }

  function choose(action: keyof UserLinkActions, event: MouseEvent): void {
    event.stopPropagation();
    open = false;
    actions[action](username);
  }

  function handleWindowPointerDown(event: PointerEvent): void {
    if (!open || root?.contains(event.target as Node)) return;
    open = false;
  }

  $effect(() => {
    if (!open) return;
    const keydown = (event: KeyboardEvent) => { if (event.key === 'Escape') open = false; };
    const resize = () => { open = false; };
    window.addEventListener('pointerdown', handleWindowPointerDown);
    window.addEventListener('keydown', keydown);
    window.addEventListener('resize', resize);
    return () => {
      window.removeEventListener('pointerdown', handleWindowPointerDown);
      window.removeEventListener('keydown', keydown);
      window.removeEventListener('resize', resize);
    };
  });
</script>

<span class="username-link-control" bind:this={root}>
  <button type="button" class="username-link" title={title ?? `Actions for ${username}`} aria-label={`Actions for ${username}`} aria-haspopup="menu" aria-expanded={open} onclick={toggle}>{username}</button>
  {#if open}
    <div class="username-action-menu" role="menu" use:anchoredMenu={{ anchor: root, align: 'start', gap: 5 }}>
      <button type="button" role="menuitem" onclick={(event) => choose('profile', event)}><Icon name="user" /><span>Profile</span></button>
      <button type="button" role="menuitem" onclick={(event) => choose('shares', event)}><Icon name="folder" /><span>Shares</span></button>
      <button type="button" role="menuitem" onclick={(event) => choose('message', event)}><Icon name="chat" /><span>Message</span></button>
    </div>
  {/if}
</span>
