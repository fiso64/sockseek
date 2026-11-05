import { tick } from 'svelte';

function keyboardTargetIsEditing(target: EventTarget | null): boolean {
  return target instanceof Element && Boolean(target.closest(
    'textarea, select, [contenteditable]:not([contenteditable="false"]), [role="textbox"], '
    + 'input:not([type="checkbox"]):not([type="radio"]):not([type="button"]):not([type="submit"]):not([type="reset"])',
  ));
}

function keyboardTargetUsesNativeActivation(target: EventTarget | null): boolean {
  if (!(target instanceof Element)) return false;
  return Boolean(target.closest('button, a[href], input, select, textarea, [role="button"], [role="tab"], [role^="menuitem"]'));
}

export function keyboardShortcutsBlocked(event: KeyboardEvent, allowControl = false): boolean {
  return event.defaultPrevented || (event.ctrlKey && !allowControl) || event.metaKey || event.altKey
    || keyboardTargetIsEditing(event.target)
    || Boolean(document.querySelector('[role="dialog"][aria-modal="true"], dialog[open], [data-shortcuts-blocked="true"], [popover]:popover-open'));
}

interface KeyboardListOptions {
  /** Changing scope cancels pending navigation; arriving pages do not. */
  scope: string;
  enabled?: boolean;
  /** Rows/loading state: triggers reconciliation after Svelte renders a page. */
  revision: unknown;
  toggle?: (key: string) => void;
  retrieve?: () => void;
  horizontal?: (direction: -1 | 1, key: string | null) => void;
  loadPage?: (key: string) => void;
}

/** Navigate rendered rows in display order, including branch-local page boundaries.
 * Selection and requests stay with the feature owners. No polling or shadow row list. */
export function keyboardList(root: HTMLElement, initial: KeyboardListOptions) {
  let options = initial;
  let current: HTMLElement | null = null;
  let pending: { item: HTMLElement; page: string; known: Set<string> } | null = null;
  let disposed = false;

  function mark(item: HTMLElement | null): void {
    if (current === item) return;
    current?.classList.remove('keyboard-current');
    current?.removeAttribute('aria-current');
    current = item;
    current?.classList.add('keyboard-current');
    current?.setAttribute('aria-current', 'true');
  }

  function focus(item: HTMLElement, first = false): void {
    mark(item);
    const target = item.querySelector<HTMLElement>('[data-keyboard-focus]') ?? item;
    target.focus({ preventScroll: true });
    if (first) root.closest('.page-content')?.scrollTo({ top: 0 });
    else item.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  }

  function entries(): HTMLElement[] {
    return [...root.querySelectorAll<HTMLElement>('[data-keyboard-item], [data-keyboard-page]')]
      .filter(item => item.getClientRects().length > 0);
  }

  function focusIn(event: FocusEvent): void {
    const item = event.target instanceof Element ? event.target.closest<HTMLElement>('[data-keyboard-item]') : null;
    // Moving focus elsewhere while a page loads must not let its response steal focus.
    if (pending && item !== pending.item) pending = null;
    if (item && root.contains(item)) mark(item);
  }

  function keydown(event: KeyboardEvent): void {
    if (options.enabled === false || keyboardShortcutsBlocked(event)) return;
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      const rows = entries();
      const direction = event.key === 'ArrowDown' ? 1 : -1;
      const index = current ? rows.indexOf(current) : -1;
      let nextIndex = index < 0 ? (direction > 0 ? 0 : rows.length - 1) : index + direction;
      // Up walks loaded rows; only Down requests more at a page boundary.
      if (direction < 0) while (nextIndex >= 0 && rows[nextIndex]?.dataset.keyboardPage !== undefined) nextIndex--;
      const next = rows[nextIndex];
      if (!next) return;
      event.preventDefault();
      if (next.dataset.keyboardPage !== undefined) {
        if (pending || next.dataset.loading === 'true' || next.dataset.failed === 'true' || !current) return;
        pending = { item: current, page: next.dataset.keyboardPage,
          known: new Set(rows.flatMap(row => row.dataset.keyboardItem === undefined ? [] : [row.dataset.keyboardItem])) };
        options.loadPage?.(pending.page);
      } else {
        pending = null;
        focus(next, next === rows[0]);
      }
    } else if ((event.key === 'ArrowLeft' || event.key === 'ArrowRight') && options.horizontal) {
      pending = null;
      event.preventDefault();
      options.horizontal(event.key === 'ArrowRight' ? 1 : -1, current?.dataset.keyboardItem ?? null);
    } else if (event.key === 'Tab' && !event.shiftKey && event.target === current) {
      const control = current?.querySelector<HTMLElement>('button:not([disabled]), input:not([disabled]), a[href], [tabindex="0"]');
      if (control) { event.preventDefault(); control.focus(); }
    } else if (!event.repeat && event.key === ' ' && current && options.toggle
      && !keyboardTargetUsesNativeActivation(event.target)) {
      event.preventDefault();
      options.toggle(current.dataset.keyboardItem!);
    } else if (!event.repeat && event.key.toLowerCase() === 'r' && options.retrieve) {
      event.preventDefault();
      options.retrieve();
    }
  }

  window.addEventListener('keydown', keydown);
  window.addEventListener('focusin', focusIn);
  return {
    update(next: KeyboardListOptions) {
      if (next.scope !== options.scope || next.enabled === false) { pending = null; mark(null); }
      options = next;
      // A page can arrive through HTTP or a replacement live snapshot. Reconcile
      // after rendering, and advance only if the user is still on its prior row.
      if (!pending && (!current || root.contains(current))) return;
      void tick().then(() => {
        if (disposed) return;
        if (current && !root.contains(current)) { mark(null); pending = null; }
        if (!pending) return;
        const rows = entries();
        const page = rows.find(row => row.dataset.keyboardPage === pending!.page);
        if (page?.dataset.loading === 'true') return;
        if (page?.dataset.failed === 'true') { pending = null; return; }
        const isNew = (row: HTMLElement) => row.dataset.keyboardItem !== undefined
          && !pending!.known.has(row.dataset.keyboardItem);
        // Shares place files before child folders, so a branch's next file page
        // may insert before the old boundary row. Prefer continuation after the
        // cursor, otherwise focus the first newly loaded row in that branch.
        const nextRow = rows.slice(rows.indexOf(pending.item) + 1).find(isNew) ?? rows.find(isNew);
        pending = null;
        if (nextRow) focus(nextRow);
      });
    },
    destroy() {
      disposed = true;
      window.removeEventListener('keydown', keydown);
      window.removeEventListener('focusin', focusIn);
    },
  };
}
