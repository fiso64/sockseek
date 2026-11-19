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

export function keyboardShortcutsBlocked(event: KeyboardEvent, allowControl = false, allowMeta = false): boolean {
  return event.defaultPrevented || (event.ctrlKey && !allowControl) || (event.metaKey && !allowMeta) || event.altKey
    || keyboardTargetIsEditing(event.target)
    || Boolean(document.querySelector('[role="dialog"][aria-modal="true"], dialog[open], [data-shortcuts-blocked="true"], [popover]:popover-open'));
}

type ApplyRange = (rows: HTMLElement[], selected: boolean) => void;

interface KeyboardListOptions {
  /** Changing scope cancels pending navigation; arriving pages do not. */
  scope: string;
  enabled?: boolean;
  /** Rows/loading state: triggers reconciliation after Svelte renders a page. */
  revision: unknown;
  /** Capture the starting selection once so reversing direction can shrink a range. */
  beginRange?: (additive: boolean) => ApplyRange;
  retrieve?: () => void;
  horizontal?: (direction: -1 | 1) => void;
  loadPage?: (key: string) => void;
}

/** Navigate rendered rows in display order, including branch-local page boundaries.
 * Selection and requests stay with the feature owners. No polling or shadow row list. */
export function keyboardList(root: HTMLElement, initial: KeyboardListOptions) {
  let options = initial;
  let current: HTMLElement | null = null;
  let pending: { item: HTMLElement; page: string; known: Set<string>; extend: boolean } | null = null;
  let anchor: HTMLElement | null = null;
  let applyRange: ApplyRange | undefined;
  let rangeMode: 'select' | 'clear' | 'shrink' = 'select';
  let pointerDown = false;
  let disposed = false;

  function mark(item: HTMLElement | null, highlight = true): void {
    if (current !== item) {
      current?.classList.remove('keyboard-current');
      current?.removeAttribute('aria-current');
      current = item;
    }
    current?.classList.toggle('keyboard-current', highlight);
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

  function selectionControl(row: HTMLElement): HTMLElement | null {
    return row.hasAttribute('data-range-disabled') ? null
      : row.querySelector<HTMLElement>('input[type="checkbox"], button[data-range-toggle]');
  }

  function checked(row: HTMLElement): boolean {
    return Boolean(selectionControl(row)?.matches(':checked:not(:indeterminate), [aria-pressed="true"]'));
  }

  function startAt(item: HTMLElement | null): void {
    anchor = item;
    applyRange = undefined;
    rangeMode = 'select';
  }

  function extendTo(item: HTMLElement, rows: HTMLElement[], keyboard: boolean): void {
    if (!keyboard && rangeMode !== 'select') startAt(current);
    if (!anchor || !rows.includes(anchor)) startAt(current && rows.includes(current) ? current : item);
    const origin = anchor ?? item;
    const start = rows.indexOf(origin), end = rows.indexOf(item);
    // From a checked edge, leave the new cursor checked as the block shrinks.
    // Entering checked rows from an unchecked cursor clears the rows visited.
    if (!applyRange && keyboard && checked(item)) {
      if (start === end) return; // First key only places the cursor on an already checked row.
      rangeMode = checked(origin) ? 'shrink' : 'clear';
    }
    const omit = rangeMode === 'shrink' ? item : rangeMode === 'clear' ? anchor : null;
    const range = rows.slice(Math.min(start, end), Math.max(start, end) + 1)
      .filter(row => row.hasAttribute('data-keyboard-item') && !row.hasAttribute('data-range-disabled')
        && row !== omit);
    if (!range.length && !applyRange) return;
    applyRange ??= options.beginRange?.(true);
    applyRange?.(range, rangeMode === 'select');
    if (rangeMode !== 'select' && start === end) startAt(item);
  }

  function clickRow(event: MouseEvent): HTMLElement | null {
    if (!options.beginRange || options.enabled === false || !(event.target instanceof Element) || !root.contains(event.target)) return null;
    const control = event.target.closest('input, button, label, a, select, textarea, [role="button"]');
    if (control && !control.matches('input[type="checkbox"], label, [data-range-toggle], [data-keyboard-disclosure]')) return null;
    const row = event.target.closest<HTMLElement>('[data-keyboard-item]');
    return row?.hasAttribute('data-range-disabled') ? null : row;
  }

  function mousedown(event: MouseEvent): void {
    if (event.shiftKey && clickRow(event)) event.preventDefault(); // Avoid selecting row text.
  }

  function click(event: MouseEvent): void {
    pending = null;
    const row = clickRow(event);
    if (!row) { startAt(null); return; }
    const additive = event.ctrlKey || event.metaKey;
    if (event.shiftKey) {
      if (event.target instanceof HTMLInputElement) {
        // A cancelled checkbox click rolls back checked *after* our render.
        // Accept its activation and consume the single-row change instead.
        event.target.checked = true;
        event.target.addEventListener('change', change => change.stopImmediatePropagation(), { once: true });
      }
      extendTo(row, entries(), false);
    } else {
      startAt(row);
      const target = event.target as Element;
      // Keep explicit selection controls and plain folder disclosures native.
      if (event.detail === 0 || target.closest('input, [data-range-toggle]') || (!additive && target.closest('button[aria-expanded]'))) {
        mark(row, event.detail === 0);
        return;
      }
      if (additive) selectionControl(row)?.click();
      else options.beginRange?.(false)([row], true);
    }
    if (!(event.target instanceof HTMLInputElement)) event.preventDefault();
    event.stopPropagation();
    mark(row, false);
    row.focus({ preventScroll: true });
  }

  function focusIn(event: FocusEvent): void {
    const item = event.target instanceof Element ? event.target.closest<HTMLElement>('[data-keyboard-item]') : null;
    // Moving focus elsewhere while a page loads must not let its response steal focus.
    if (pending && item !== pending.item) pending = null;
    if (!pointerDown && item && root.contains(item)) {
      if (item !== current) { startAt(item); mark(item); }
    } else if (current && !current.contains(event.target as Node)) {
      if (!pointerDown) startAt(null);
      mark(null);
    }
  }

  function pointerdown(event: PointerEvent): void {
    pointerDown = true;
    if (!clickRow(event)) startAt(null);
    if (current && event.target instanceof Node && !current.contains(event.target)) {
      pending = null;
      if (document.activeElement instanceof HTMLElement && current.contains(document.activeElement)) document.activeElement.blur();
      mark(null);
    }
  }

  function pointerup(): void {
    pointerDown = false;
  }

  function keydown(event: KeyboardEvent): void {
    const arrow = event.key === 'ArrowDown' || event.key === 'ArrowUp';
    const extend = Boolean(options.beginRange && event.shiftKey && arrow);
    if (options.enabled === false || keyboardShortcutsBlocked(event, extend, extend)) return;
    if (event.key === 'Escape' && current && !document.querySelector('[role="menu"]')) {
      event.preventDefault();
      pending = null;
      startAt(null);
      if (document.activeElement instanceof HTMLElement && current.contains(document.activeElement)) document.activeElement.blur();
      mark(null);
    } else if (arrow) {
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
        pending = { item: current, page: next.dataset.keyboardPage, extend,
          known: new Set(rows.flatMap(row => row.dataset.keyboardItem === undefined ? [] : [row.dataset.keyboardItem])) };
        options.loadPage?.(pending.page);
      } else {
        pending = null;
        if (extend) extendTo(next, rows, true);
        else startAt(next);
        focus(next, next === rows[0]);
      }
    } else if ((event.key === 'ArrowLeft' || event.key === 'ArrowRight') && options.horizontal) {
      pending = null;
      event.preventDefault();
      options.horizontal(event.key === 'ArrowRight' ? 1 : -1);
    } else if (!event.shiftKey && (event.key === 'ArrowLeft' || event.key === 'ArrowRight' || event.key === 'Enter')) {
      const disclosure = current?.querySelector<HTMLButtonElement>('button[data-keyboard-disclosure]');
      if (!disclosure) return;
      event.preventDefault();
      pending = null;
      if (!event.repeat) disclosure.click();
    } else if (event.key === 'Tab' && !event.shiftKey && event.target === current) {
      const control = current?.querySelector<HTMLElement>('button:not([disabled]), input:not([disabled]), a[href], [tabindex="0"]');
      if (control) { event.preventDefault(); control.focus(); }
    } else if (!event.repeat && event.key === ' ' && current && options.beginRange
      && !keyboardTargetUsesNativeActivation(event.target)) {
      event.preventDefault();
      startAt(current);
      selectionControl(current)?.click();
    } else if (!event.repeat && event.key.toLowerCase() === 'r' && options.retrieve) {
      event.preventDefault();
      options.retrieve();
    }
  }

  root.addEventListener('click', click, true);
  root.addEventListener('mousedown', mousedown);
  window.addEventListener('keydown', keydown);
  window.addEventListener('focusin', focusIn);
  window.addEventListener('pointerdown', pointerdown);
  window.addEventListener('pointerup', pointerup);
  window.addEventListener('pointercancel', pointerup);
  return {
    update(next: KeyboardListOptions) {
      if (next.scope !== options.scope || next.enabled === false) { pending = null; mark(null); startAt(null); }
      if (!next.beginRange) startAt(null);
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
        const continuation = pending;
        pending = null;
        if (nextRow) {
          if (continuation.extend && options.beginRange) extendTo(nextRow, rows, true);
          else startAt(nextRow);
          focus(nextRow);
        }
      });
    },
    destroy() {
      disposed = true;
      root.removeEventListener('click', click, true);
      root.removeEventListener('mousedown', mousedown);
      window.removeEventListener('keydown', keydown);
      window.removeEventListener('focusin', focusIn);
      window.removeEventListener('pointerdown', pointerdown);
      window.removeEventListener('pointerup', pointerup);
      window.removeEventListener('pointercancel', pointerup);
    },
  };
}
