import { readable } from 'svelte/store';

// One timer for all visible age labels; stopped when the last label unmounts.
export const relativeTimeClock = readable(Date.now(), (set) => {
  set(Date.now());
  const timer = setInterval(() => set(Date.now()), 30_000);
  return () => clearInterval(timer);
});
