<script lang="ts">
  import { onDestroy, type Snippet } from 'svelte';
  import Icon from './Icon.svelte';
  import { anchoredMenu } from '../lib/anchored-menu';

  let { title, testId, children }: {
    title: string;
    testId: string;
    children: Snippet<[close: () => void, anchor: HTMLButtonElement]>;
  } = $props();
  const id = $props.id();
  let root: HTMLSpanElement;
  let button = $state<HTMLButtonElement>();
  let open = $state(false);
  let pinned = false;
  let hovered = false;
  let hoverTimer: ReturnType<typeof setTimeout> | undefined;

  function cancelHover(): void { clearTimeout(hoverTimer); hoverTimer = undefined; }
  function close(): void { cancelHover(); open = false; pinned = false; }
  onDestroy(cancelHover);

  function hover(event: PointerEvent): void {
    if (event.pointerType === 'touch') return;
    hovered = true;
    if (!open) {
      cancelHover();
      hoverTimer = setTimeout(() => { hoverTimer = undefined; open = true; }, 200);
    }
  }

  $effect(() => {
    if (!open) return;
    const outside = (event: PointerEvent) => {
      if (event.target instanceof Node && !root.contains(event.target)) close();
    };
    const escape = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      // Dismiss help before an enclosing modal or configuration popover.
      event.preventDefault();
      event.stopImmediatePropagation();
      button?.focus({ preventScroll: true });
      close();
    };
    window.addEventListener('pointerdown', outside);
    window.addEventListener('keydown', escape, true);
    return () => {
      window.removeEventListener('pointerdown', outside);
      window.removeEventListener('keydown', escape, true);
    };
  });
</script>

<span bind:this={root} class="help-popover" role="group" aria-label={`${title} information`}
  onpointerenter={hover}
  onpointerleave={() => { cancelHover(); hovered = false; if (!pinned && !root.contains(document.activeElement)) close(); }}
  onfocusout={(event) => { if (!pinned && !hovered && !(event.relatedTarget instanceof Node && root.contains(event.relatedTarget))) close(); }}>
  <button bind:this={button} type="button" class="help-button" data-testid={testId}
    aria-label={`About ${title.toLowerCase()}`} aria-haspopup="dialog" aria-expanded={open}
    aria-controls={open ? id : undefined} onfocus={() => { cancelHover(); open = true; }}
    onclick={() => { cancelHover(); pinned = !pinned; open = pinned; }}><Icon name="help" /></button>
  {#if open}
    <span {id} role="dialog" aria-label={title} class="help-panel" data-testid={`${testId}-content`}
      use:anchoredMenu={{ anchor: button, gap: 0, viewportMargin: 12 }}>
      {@render children(close, button!)}
    </span>
  {/if}
</span>

<style>
  .help-popover { display: inline-flex; vertical-align: middle; margin-left: 5px; }
  .help-button { display: grid; place-items: center; padding: 0; border: 0; background: transparent; color: var(--muted); cursor: pointer; }
  .help-button :global(.app-icon) { --app-icon-size: 15px; }
  .help-button:hover, .help-button:focus-visible { color: var(--text); }
  .help-button:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; border-radius: 50%; }
  .help-panel { position: fixed; inset: auto; margin: 0; z-index: 50; display: grid; gap: 6px; width: min(320px, calc(100vw - 24px)); max-height: calc(100dvh - 24px); overflow: auto; padding: 12px 14px; border: 1px solid var(--border-strong); border-radius: 9px; background: var(--surface-raised); color: var(--muted); box-shadow: 0 8px 24px #0004; font-size: .79rem; line-height: 1.45; font-weight: 400; letter-spacing: normal; text-transform: none; text-align: left; white-space: normal; }
</style>
