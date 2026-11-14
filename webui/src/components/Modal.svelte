<script lang="ts">
  import { onMount, type Snippet } from 'svelte';
  import Icon from './Icon.svelte';

  let { title, label = title, closeLabel = 'Close dialog', wide = false, compact = false, locked = false, onclose, children }: {
    title: string;
    label?: string;
    closeLabel?: string;
    wide?: boolean;
    compact?: boolean;
    locked?: boolean;
    onclose: () => void;
    children: Snippet;
  } = $props();
  let dialog: HTMLDialogElement;
  let pressedOutside = false;
  onMount(() => dialog.showModal());

  function outside(event: MouseEvent): boolean {
    const bounds = dialog.getBoundingClientRect();
    return event.clientX < bounds.left || event.clientX > bounds.right
      || event.clientY < bounds.top || event.clientY > bounds.bottom;
  }
  // Opt in through the primary button's shortcut so its existing click handler
  // and disabled state remain the single acceptance path, including inside inputs.
  function acceptByKey(event: KeyboardEvent): void {
    if (event.defaultPrevented || event.repeat || !event.ctrlKey || event.altKey || event.metaKey
      || event.key !== 'Enter' || locked) return;
    if (!(event.target instanceof Element) || event.target.closest('dialog') !== dialog) return;
    const primary = dialog.querySelector<HTMLButtonElement>('button[aria-keyshortcuts="Control+Enter"]');
    if (!primary) return;
    event.preventDefault();
    if (!primary.disabled) primary.click();
  }

  function dismiss(): void { if (!locked) dialog.close(); }
</script>

<dialog bind:this={dialog} class:wide class:compact aria-label={label} onclose={onclose} onkeydown={acceptByKey}
  oncancel={(event) => { event.preventDefault(); dismiss(); }}
  onpointerdown={(event) => { pressedOutside = event.target === dialog && outside(event); }}
  onclick={(event) => { if (pressedOutside && event.target === dialog && outside(event)) dismiss(); }}>
  <header>
    <h2>{title}</h2>
    <button type="button" class="modal-close" aria-label={closeLabel} disabled={locked} onclick={dismiss}><Icon name="x" /></button>
  </header>
  <div class="modal-body">{@render children()}</div>
</dialog>

<style>
  /* Dialogs can live inside compact rows; their body type must not inherit the row's sizing. */
  dialog { width: min(40rem, calc(100vw - 48px)); max-height: calc(100dvh - 48px); padding: 0; border: 1px solid var(--border-strong); border-radius: 13px; background: var(--surface); color: var(--text); font-size: .85rem; font-weight: 400; line-height: 1.5; box-shadow: 0 22px 60px #0005; overflow: hidden; }
  dialog[open] { display: flex; flex-direction: column; }
  dialog.wide { width: min(1000px, calc(100vw - 48px)); height: min(920px, calc(100dvh - 48px)); }
  dialog.compact { width: min(28rem, calc(100vw - 32px)); }
  dialog::backdrop { background: color-mix(in srgb, var(--bg) 74%, transparent); backdrop-filter: blur(3px); }
  header { display: flex; flex-shrink: 0; align-items: center; justify-content: space-between; gap: 16px; padding: 16px 18px; border-bottom: 1px solid var(--border); }
  h2 { margin: 0; font-size: 1.08rem; font-weight: 650; }
  .modal-close { width: 32px; height: 32px; display: grid; place-items: center; padding: 0; border: 1px solid var(--border); border-radius: 8px; background: var(--surface-subtle); color: var(--muted); }
  .modal-close :global(.app-icon) { --app-icon-size: 16px; }
  .modal-close:hover:not(:disabled) { background: var(--surface-raised); color: var(--text); }
  .modal-body { min-height: 0; overflow: auto; overscroll-behavior: contain; padding: 18px; }
  .wide .modal-body { flex: 1; padding: 0; overflow: hidden; }
  @media (max-width: 980px) { dialog, dialog.wide { width: calc(100vw - 32px); max-height: calc(100dvh - 32px); } dialog.wide { height: min(920px, calc(100dvh - 32px)); } }
</style>
