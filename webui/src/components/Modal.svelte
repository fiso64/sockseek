<script lang="ts">
  import { onMount, tick, type Snippet } from 'svelte';
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
  let layer: HTMLDivElement;
  let dialog: HTMLDivElement;
  let heading: HTMLHeadingElement;
  let closeButton: HTMLButtonElement;
  let pressedOutside = false;
  let restoreFocus: HTMLElement | null = null;

  // A popover in the top layer, not showModal(): a modal <dialog> makes the rest
  // of the page inert, which browsers apply as a style recalculation of every
  // element on open and again on close, so long lists made dialogs sluggish.
  // aria-modal tells assistive technology the page behind is unavailable; the
  // full-screen layer blocks pointers and the handlers below keep the keyboard inside.
  onMount(() => {
    restoreFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    openModals.push(dialog);
    layer.showPopover();
    void tick().then(() => {
      // Respect explicit content focus; prefer a visible field over dialog chrome.
      if (!dialog.isConnected || dialog.contains(document.activeElement) && document.activeElement !== closeButton && document.activeElement !== heading) return;
      const field = [...dialog.querySelectorAll<HTMLElement>(
        '.modal-body :is(input:not([type="hidden"], [type="checkbox"], [type="radio"]), textarea, select)',
      )].find(element => !element.matches(':disabled') && !element.closest('[inert]')
        && element.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true }));
      (field ?? heading).focus({ preventScroll: true });
    });
    return () => openModals.splice(openModals.indexOf(dialog), 1);
  });

  function keydown(event: KeyboardEvent): void {
    if (event.defaultPrevented || openModals.at(-1) !== dialog) return;
    if (event.key === 'Escape') {
      event.preventDefault();
      dismiss();
    } else if (event.key === 'Tab') {
      trapTab(event);
    } else if (event.key === 'Enter' && event.ctrlKey && !event.repeat && !event.altKey && !event.metaKey && !locked) {
      // Opt in through the primary button's shortcut so its existing click handler
      // and disabled state remain the single acceptance path, including inside inputs.
      const primary = dialog.querySelector<HTMLButtonElement>('button[aria-keyshortcuts="Control+Enter"]');
      if (!primary) return;
      event.preventDefault();
      if (!primary.disabled) primary.click();
    }
  }

  function trapTab(event: KeyboardEvent): void {
    const focusable = [...dialog.querySelectorAll<HTMLElement>(
      'a[href], button, input, select, textarea, summary, [tabindex]:not([tabindex="-1"])',
    )].filter(element => !element.matches(':disabled') && !element.closest('[inert]')
      && element.checkVisibility({ checkVisibilityCSS: true }));
    if (!focusable.length) return;
    const first = focusable[0]!, last = focusable.at(-1)!;
    const current = document.activeElement;
    if (event.shiftKey ? current === first || !dialog.contains(current) : current === last || !dialog.contains(current)) {
      event.preventDefault();
      (event.shiftKey ? last : first).focus();
    }
  }

  // Focus moved behind the modal by other means (a script, assistive technology) returns to it.
  function focusin(event: FocusEvent): void {
    if (openModals.at(-1) === dialog && event.target instanceof Node && !dialog.contains(event.target)
      && !(event.target instanceof Element && event.target.closest('[popover]:popover-open')))
      heading.focus({ preventScroll: true });
  }

  function dismiss(): void {
    if (locked) return;
    layer.hidePopover();
    onclose();
    restoreFocus?.focus({ preventScroll: true });
  }
</script>

<script module lang="ts">
  /** Open modals, top-most last: only it handles keys and keeps focus. */
  const openModals: HTMLElement[] = [];
</script>

<svelte:document onkeydown={keydown} onfocusin={focusin} />

<!-- The layer covers the page: a press that starts and ends on it, outside the
     dialog, dismisses. Escape is handled for the document above. Its events
     still bubble (window listeners close nested popovers); row handlers skip
     them with insideModal(). -->
<!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
<div popover="manual" class="modal-layer" bind:this={layer}
  onpointerdown={(event) => { pressedOutside = event.target === layer; }}
  onclick={(event) => { if (pressedOutside && event.target === layer) dismiss(); }}>
  <div role="dialog" aria-modal="true" tabindex="-1" class="popover-panel modal" bind:this={dialog} class:wide class:compact aria-label={label}>
    <header class="panel-header">
      <h2 bind:this={heading} tabindex="-1">{title}</h2>
      <button bind:this={closeButton} type="button" class="button modal-close" aria-label={closeLabel} disabled={locked} onclick={dismiss}><Icon name="x" /></button>
    </header>
    <div class="modal-body">{@render children()}</div>
  </div>
</div>

<style>
  /* Dialogs can live inside rows: nothing of the row (type, cursor) may carry over. */
  .modal-layer { position: fixed; inset: 0; width: 100%; height: 100%; max-width: none; max-height: none; margin: 0; padding: 0;
    border: 0; overflow: hidden; background: color-mix(in srgb, var(--bg) 74%, transparent); backdrop-filter: blur(3px);
    color: var(--text); cursor: auto; }
  .modal-layer:popover-open { display: grid; place-items: center; }
  .modal { display: flex; flex-direction: column; width: min(40rem, calc(100vw - 48px)); max-height: calc(100dvh - 48px); padding: 0; border-radius: 13px; font-size: .85rem; font-weight: 400; line-height: 1.5; overflow: hidden; }
  .modal.wide { width: min(1000px, calc(100vw - 48px)); height: min(920px, calc(100dvh - 48px)); }
  .modal.compact { width: min(28rem, calc(100vw - 32px)); }
  header { display: flex; flex-shrink: 0; align-items: center; justify-content: space-between; gap: 16px; padding: 16px 18px; }
  h2 { margin: 0; font-size: 1.08rem; font-weight: 650; }
  h2:focus { outline: none; }
  .modal-close { width: 32px; height: 32px; display: grid; place-items: center; padding: 0; border-radius: 8px; }
  .modal-close :global(.app-icon) { --app-icon-size: 16px; }
  .modal-body { min-height: 0; overflow: auto; overscroll-behavior: contain; padding: 18px; }
  .wide .modal-body { flex: 1; padding: 0; overflow: hidden; }
  @media (max-width: 980px) { .modal, .modal.wide { width: calc(100vw - 32px); max-height: calc(100dvh - 32px); } .modal.wide { height: min(920px, calc(100dvh - 32px)); } }
  /* Phones: wide dialogs fill the screen; short ones stay centred cards. */
  @media (max-width: 640px) { .modal.wide { width: 100vw; max-width: 100vw; height: 100dvh; max-height: 100dvh; border: 0; border-radius: 0; } }
</style>
