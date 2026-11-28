export function relativeTime(timestamp: string | null | undefined, now: number): string | null {
  if (!timestamp) return null;
  const instant = Date.parse(timestamp);
  if (!Number.isFinite(instant)) return null;
  const elapsed = now - instant;
  const minutes = Math.floor(Math.abs(elapsed) / 60_000);
  if (minutes < 1) return 'Just now';
  const amount = minutes < 60 ? minutes : minutes < 1440 ? Math.floor(minutes / 60) : Math.floor(minutes / 1440);
  const unit = minutes < 60 ? 'min' : minutes < 1440 ? 'h' : 'd';
  return elapsed < 0 ? `in ${amount} ${unit}` : `${amount} ${unit} ago`;
}
