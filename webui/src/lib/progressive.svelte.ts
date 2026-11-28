/**
 * Renders a long list a slice per frame when it first appears, so building it
 * never blocks input for long. Later growth (appended pages, live additions)
 * keeps what is already shown.
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
    slice,
  };
}
