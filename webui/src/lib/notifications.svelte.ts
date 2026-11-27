/**
 * App-wide notifications for outcomes the user may not be looking at, such as a
 * download requested from far down a long list. Successes fade on their own;
 * warnings and failures stay until dismissed.
 */
export interface AppNotification {
  id: number;
  /** A later notification with the same key replaces this one, so a retry's success clears its failure. */
  key?: string;
  tone: 'success' | 'warning' | 'error';
  title: string;
  details?: string[];
  action?: { label: string; testId?: string; run: () => void };
}

const SUCCESS_MS = 6_000;
const MAX_SHOWN = 3;
let nextId = 0;

export const notifications = $state<AppNotification[]>([]);

export function notify(notification: Omit<AppNotification, 'id'>): void {
  const id = ++nextId;
  const replaced = notification.key ? notifications.findIndex(shown => shown.key === notification.key) : -1;
  if (replaced >= 0) notifications.splice(replaced, 1);
  notifications.push({ ...notification, id });
  if (notifications.length > MAX_SHOWN) notifications.shift();
  if (notification.tone === 'success') setTimeout(() => dismiss(id), SUCCESS_MS);
}

export function dismiss(id: number): void {
  const index = notifications.findIndex(notification => notification.id === id);
  if (index >= 0) notifications.splice(index, 1);
}
