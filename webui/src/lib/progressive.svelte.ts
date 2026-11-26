/**
 * Renders a long list a slice per frame, so building it never blocks input for
 * long. Only the first slice is rendered on mount and after `restart()`; later
 * growth (appended pages, live additions) keeps what is already shown.
 */
export function progressiveCount(total: () => number, slice = 6) {
  let count = $state(slice);
  $effect(() => {
    const target = total();
    if (count >= target) return;
    const frame = requestAnimationFrame(() => (count = Math.min(target, count + slice)));
    return () => cancelAnimationFrame(frame);
  });
  return {
    get count() { return count; },
    /** Call when the user replaces the list (new sort or filter), before it arrives. */
    restart() { count = slice; },
  };
}
