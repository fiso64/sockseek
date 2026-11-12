<script lang="ts">
  import type { Schema } from '../../api/types';
  import type { DaemonLiveClient, LiveConnectionStatus } from '../../api/live';
  import { DaemonHttpClient } from '../../api/http';
  import { browserSession } from '../../api/session.svelte';
  import { formatBytes, formatCount, humanizeStateValue, soulseekStatusLabel } from '../../app/format';
  import Notice from '../../components/Notice.svelte';
  import LoadingIndicator from '../../components/LoadingIndicator.svelte';

  let { daemon, liveStatus, liveClient }: { liveClient: DaemonLiveClient; daemon: Schema<'DaemonStateDto'> | null; liveStatus: LiveConnectionStatus } = $props();
  const http = new DaemonHttpClient();
  let info = $state<Schema<'ServerInfoDto'> | null>(null);
  let status = $state<Schema<'ServerStatusDto'> | null>(null);
  let error = $state<string | null>(null);
  let retry = $state(0);
  const connectionState = $derived(daemon?.soulseekClient.state);
  const sharing = $derived(daemon?.sharing);
  const uploads = $derived(daemon?.uploads);
  const persistence = $derived(status?.persistence);
  const connection = $derived(status?.connection);
  const client = $derived(daemon?.soulseekClient ?? status?.soulseekClient);

  // Live counters/status already arrive through App's stream. Fetch storage and
  // connection details on entry, reconnect or retry, without polling.
  $effect(() => {
    const state = liveStatus;
    retry;
    if (state !== 'connected' || !connectionState) return;
    const request = new AbortController();
    error = null;
    info = liveClient.serverInfo;
    void http.get<Schema<'ServerStatusDto'>>('/server/status', request.signal).then(current => {
      if (!request.signal.aborted) status = current;
    }).catch(cause => {
      if (!request.signal.aborted) error = cause instanceof Error ? cause.message : 'Status unavailable.';
    });
    return () => request.abort();
  });

  function date(value: string | null | undefined): string { return value ? new Date(value).toLocaleString() : '—'; }
</script>

{#if error}<Notice variant="unavailable">{error}<button class="auth-button" onclick={() => retry++}>Retry</button></Notice>{/if}
{#if !status && !error}<LoadingIndicator />{/if}
<div class="status-grid">
  <section class="status-card" aria-labelledby="daemon-heading">
    <h2 id="daemon-heading">Daemon</h2>
    <dl>
      <div><dt>Version</dt><dd>{info?.version ?? '—'}</dd></div>
      <div><dt>Started</dt><dd>{date(info?.startedAtUtc)}</dd></div>
      <div><dt>Browser connection</dt><dd class:healthy={liveStatus === 'connected'}>{humanizeStateValue(liveStatus)}</dd></div>
      <div><dt>Authentication</dt><dd>{browserSession.data?.authenticationEnabled ? 'Enabled' : 'Disabled'}</dd></div>
      <div><dt>Configuration</dt><dd>{!daemon?.configuration ? '—' : daemon.configuration.reloadFailed ? 'Reload failed' : daemon?.configuration?.restartRequired ? 'Restart required' : daemon?.configuration?.watching ? 'Watching for changes' : 'Automatic reload disabled'}</dd></div>
    </dl>
  </section>
  <section class="status-card" aria-labelledby="soulseek-heading">
    <h2 id="soulseek-heading">Soulseek</h2>
    <dl>
      <div><dt>Connection</dt><dd class:healthy={client?.isReady}>{soulseekStatusLabel(client, liveStatus === 'connected')}</dd></div>
      <div><dt>Username</dt><dd>{connection?.username ?? '—'}</dd></div>
      <div><dt>Server</dt><dd>{connection?.serverAddress ? `${connection.serverAddress}:${connection.serverPort}` : '—'}</dd></div>
      <div><dt>Server address</dt><dd>{connection?.serverEndpoint ?? '—'}</dd></div>
      <div><dt>Incoming port</dt><dd>{connection ? connection.listenPort ?? 'Disabled' : '—'}</dd></div>
    </dl>
  </section>
  <section class="status-card" aria-labelledby="sharing-heading">
    <h2 id="sharing-heading">Sharing</h2>
    <dl>
      <div><dt>Status</dt><dd>{sharing ? humanizeStateValue(sharing.state) : '—'}{sharing?.reason ? ` · ${humanizeStateValue(sharing.reason)}` : ''}</dd></div>
      <div><dt>Shared files</dt><dd>{sharing ? formatCount(sharing.catalog.fileCount) : '—'}</dd></div>
      <div><dt>Folders</dt><dd>{sharing ? formatCount(sharing.catalog.directoryCount) : '—'}</dd></div>
      <div><dt>Total size</dt><dd>{sharing ? formatBytes(sharing.catalog.totalBytes) : '—'}</dd></div>
      <div><dt>Shared roots</dt><dd>{sharing ? sharing.aliases.join(', ') || 'None' : '—'}</dd></div>
      <div><dt>Published</dt><dd>{date(sharing?.catalog.publishedAtUtc)}</dd></div>
      {#if sharing?.lastScan}<div><dt>Last scan</dt><dd>{humanizeStateValue(sharing.lastScan.phase)}{sharing.lastScan.errorCount ? ` · ${formatCount(sharing.lastScan.errorCount)} errors` : ''}</dd></div>{/if}
    </dl>
  </section>
  <section class="status-card" aria-labelledby="uploads-heading">
    <h2 id="uploads-heading">Uploads</h2>
    <dl>
      <div><dt>Accepting uploads</dt><dd>{uploads ? uploads.acceptingUploads ? 'Yes' : 'No' : '—'}</dd></div>
      {#if uploads?.reason}<div><dt>Status</dt><dd>{humanizeStateValue(uploads.reason)}</dd></div>{/if}
      <div><dt>Slots in use</dt><dd>{uploads ? `${uploads.activeSlots} / ${uploads.slots}` : '—'}</dd></div>
      <div><dt>Queued files</dt><dd>{uploads ? formatCount(uploads.queuedFiles) : '—'}</dd></div>
      <div><dt>Queued size</dt><dd>{uploads ? formatBytes(uploads.queuedBytes) : '—'}</dd></div>
      <div><dt>Speed limit</dt><dd>{!uploads ? '—' : uploads.speedLimitKiBPerSecond ? `${formatCount(uploads.speedLimitKiBPerSecond)} KiB/s` : 'Unlimited'}</dd></div>
      <div><dt>Blocked users / addresses</dt><dd>{sharing ? `${sharing.uploadBlockedUsernameCount} / ${sharing.uploadBlockedIpAddressCount}` : '—'}</dd></div>
    </dl>
  </section>
  <section class="status-card storage-card" aria-labelledby="storage-heading">
    <h2 id="storage-heading">History &amp; storage</h2>
    <dl>
      <div><dt>Status</dt><dd>{persistence ? humanizeStateValue(persistence.state) : '—'}</dd></div>
      <div><dt>Database size</dt><dd>{persistence?.databaseSizeBytes == null ? '—' : formatBytes(persistence.databaseSizeBytes)}</dd></div>
      <div><dt>Write-ahead log</dt><dd>{persistence?.walSizeBytes == null ? '—' : formatBytes(persistence.walSizeBytes)}</dd></div>
      <div><dt>Last saved</dt><dd>{date(persistence?.lastSuccessfulCommitAtUtc)}</dd></div>
      <div><dt>Last retention cleanup</dt><dd>{date(persistence?.lastRetentionAtUtc)}</dd></div>
    </dl>
    {#if persistence?.lastFailure}<p class="storage-error">Last storage error: {persistence.lastFailure}</p>{/if}
  </section>
</div>

<style>
  .status-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; }
  .status-card { padding: 18px 20px; background: var(--surface); border: 1px solid var(--border); border-radius: 14px; min-width: 0; }
  h2 { margin: 0; font-size: .9rem; font-weight: 600; }
  dl { margin: 14px 0 0; display: grid; gap: 12px; font-size: .8125rem; }
  dl > div { display: grid; grid-template-columns: minmax(100px, .85fr) minmax(0, 1.15fr); gap: 14px; }
  dt { color: var(--muted); }
  dd { margin: 0; overflow-wrap: anywhere; }
  .healthy { color: var(--success); }
  .storage-card { grid-column: 1 / -1; }
  .storage-card dl { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px 30px; }
  .storage-error { color: var(--danger); font-size: .8125rem; overflow-wrap: anywhere; }
  @media (max-width: 800px) { .status-grid, .storage-card dl { grid-template-columns: minmax(0, 1fr); } }
</style>
