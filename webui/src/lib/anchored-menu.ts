export type AnchoredMenuAlign = 'start' | 'end';

export interface AnchoredMenuOptions {
  anchor: HTMLElement | null | undefined;
  align?: AnchoredMenuAlign;
  gap?: number;
  viewportMargin?: number;
}

function placeMenu(node: HTMLElement, options: AnchoredMenuOptions): void {
  if (typeof window === 'undefined' || !options.anchor) return;
  const gap = options.gap ?? 6;
  const margin = options.viewportMargin ?? 8;
  const anchorRect = options.anchor.getBoundingClientRect();
  const menuRect = node.getBoundingClientRect();
  const viewportWidth = document.documentElement.clientWidth;
  const viewportHeight = document.documentElement.clientHeight;

  const preferredLeft = options.align === 'end'
    ? anchorRect.right - menuRect.width
    : anchorRect.left;
  const left = Math.min(
    Math.max(preferredLeft, margin),
    Math.max(margin, viewportWidth - menuRect.width - margin),
  );

  const below = anchorRect.bottom + gap;
  const above = anchorRect.top - menuRect.height - gap;
  let top = below;
  if (below + menuRect.height > viewportHeight - margin && above >= margin) top = above;
  else top = Math.min(Math.max(below, margin), Math.max(margin, viewportHeight - menuRect.height - margin));

  node.style.left = `${Math.round(left)}px`;
  node.style.top = `${Math.round(top)}px`;
}

/**
 * Positions an anchored popup menu inside the viewport and flips it above the
 * trigger when there is not enough room below. The browser's popover layer keeps
 * it above row stacking contexts and outside ancestor clipping. Components still
 * own open/close state, outside clicks and Escape.
 */
export function anchoredMenu(node: HTMLElement, initial: AnchoredMenuOptions) {
  let options = initial;
  let frame = 0;
  node.popover = 'manual';
  node.showPopover();

  const schedule = () => {
    cancelAnimationFrame(frame);
    frame = requestAnimationFrame(() => placeMenu(node, options));
  };

  const observer = typeof ResizeObserver !== 'undefined' ? new ResizeObserver(schedule) : null;
  observer?.observe(node);
  window.addEventListener('resize', schedule);
  window.addEventListener('scroll', schedule, true);
  placeMenu(node, options);
  schedule();

  return {
    update(next: AnchoredMenuOptions) {
      options = next;
      schedule();
    },
    destroy() {
      cancelAnimationFrame(frame);
      observer?.disconnect();
      window.removeEventListener('resize', schedule);
      window.removeEventListener('scroll', schedule, true);
    },
  };
}

export interface AnchoredPopoverOptions extends AnchoredMenuOptions {
  /** Minimum useful height before preferring the opposite side of the anchor. */
  minHeight?: number;
}

function placePopover(node: HTMLElement, options: AnchoredPopoverOptions): void {
  if (typeof window === 'undefined' || !options.anchor) return;
  const gap = options.gap ?? 8;
  const margin = options.viewportMargin ?? 16;
  const minHeight = options.minHeight ?? 220;
  const anchorRect = options.anchor.getBoundingClientRect();
  const viewportWidth = document.documentElement.clientWidth;
  const viewportHeight = document.documentElement.clientHeight;

  const naturalRect = node.getBoundingClientRect();
  const preferredLeft = options.align === 'end'
    ? anchorRect.right - naturalRect.width
    : anchorRect.left;
  const left = Math.min(
    Math.max(preferredLeft, margin),
    Math.max(margin, viewportWidth - naturalRect.width - margin),
  );

  const belowTop = anchorRect.bottom + gap;
  const belowAvailable = Math.max(0, viewportHeight - margin - belowTop);
  const aboveBottom = anchorRect.top - gap;
  const aboveAvailable = Math.max(0, aboveBottom - margin);
  const placeAbove = belowAvailable < minHeight && aboveAvailable > belowAvailable;
  const availableHeight = Math.max(0, placeAbove ? aboveAvailable : belowAvailable);

  node.style.maxHeight = `${Math.floor(availableHeight)}px`;
  const constrainedRect = node.getBoundingClientRect();
  const top = placeAbove
    ? Math.max(margin, anchorRect.top - gap - constrainedRect.height)
    : Math.max(margin, belowTop);

  node.style.left = `${Math.round(left)}px`;
  node.style.top = `${Math.round(top)}px`;
}

/**
 * Positions a larger anchored surface (such as a configuration popover) inside
 * the viewport. Unlike compact menus, the surface keeps its preferred side of
 * the anchor when there is useful room and constrains its own height to the
 * remaining viewport so its contents scroll instead of crossing a screen edge.
 */
export function anchoredPopover(node: HTMLElement, initial: AnchoredPopoverOptions) {
  let options = initial;
  let frame = 0;

  const schedule = () => {
    cancelAnimationFrame(frame);
    frame = requestAnimationFrame(() => placePopover(node, options));
  };

  const observer = typeof ResizeObserver !== 'undefined' ? new ResizeObserver(schedule) : null;
  observer?.observe(node);
  window.addEventListener('resize', schedule);
  window.addEventListener('scroll', schedule, true);
  placePopover(node, options);
  schedule();

  return {
    update(next: AnchoredPopoverOptions) {
      options = next;
      schedule();
    },
    destroy() {
      cancelAnimationFrame(frame);
      observer?.disconnect();
      window.removeEventListener('resize', schedule);
      window.removeEventListener('scroll', schedule, true);
      node.style.removeProperty('left');
      node.style.removeProperty('top');
      node.style.removeProperty('max-height');
    },
  };
}
