import type { DaemonLiveClient, LiveConnectionStatus } from '../../api/live';
import type { Schema } from '../../api/types';

/** A view owns its stream; Svelte cleanup releases it on navigation or disconnect. */
export function userPresence(username: () => string | null, client: () => DaemonLiveClient,
  connection: () => LiveConnectionStatus) {
  let state = $state<Schema<'UserPresenceState'>>('checking');
  $effect(() => {
    const peer = username();
    const live = client();
    const status = connection();
    const connected = status === 'connected';
    // Initial connection setup is pending, not a lost connection.
    state = connected || status === 'connecting' ? 'checking' : 'disconnected';
    if (!peer || !connected) return;
    let disposed = false;
    const unavailable = () => { if (!disposed) state = 'unavailable'; };
    let release: (() => void) | undefined;
    try {
      release = live.observeUserPresence(peer, update => {
        if (!disposed) state = update.state;
      }, unavailable);
    } catch { unavailable(); }
    return () => { disposed = true; release?.(); };
  });
  return {
    get state() { return state; },
    get label() {
      switch (state) {
        case 'online': return 'Online';
        case 'away': return 'Away';
        case 'offline': return 'Offline';
        case 'checking': return 'Checking status…';
        case 'disconnected': return 'Disconnected';
        default: return 'Status unavailable';
      }
    },
  };
}
