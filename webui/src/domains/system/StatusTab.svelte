<script lang="ts">
  import ManualLink from '../../components/ManualLink.svelte';
  import HelpPopover from '../../components/HelpPopover.svelte';
  import type { Schema } from '../../api/types';
  import type { DaemonLiveClient, LiveConnectionStatus } from '../../api/live';
  import { DaemonHttpClient } from '../../api/http';
  import { browserSession } from '../../api/session.svelte';
  import { formatBytes, formatCount, humanizeStateValue, soulseekStatusLabel } from '../../app/format';
  import { appPath } from '../../app/paths';
  import Notice from '../../components/Notice.svelte';
  import LinkifiedText from '../../components/LinkifiedText.svelte';
  import ProfilePicture from '../../components/ProfilePicture.svelte';
  import { dismissedUpdate } from './update.svelte';

  let { daemon, liveStatus, liveClient, onloadingchange }: {
    liveClient: DaemonLiveClient;
    daemon: Schema<'DaemonStateDto'> | null;
    liveStatus: LiveConnectionStatus;
    onloadingchange: (loading: boolean) => void;
  } = $props();
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
  const profile = $derived(status?.profile);
  // Where the daemon's resident memory comes from, largest concern first.
  // Mapped files are reclaimable, so they are painted as the quiet part.
  const memory = $derived(status?.memory);
  const memoryParts = $derived(memory?.heapBytes == null ? [] : [
    { id: 'heap', label: 'Heap', bytes: Number(memory.heapBytes) },
    { id: 'runtime', label: 'Runtime', bytes: Number(memory.runtimeBytes ?? 0) },
    { id: 'files', label: 'Mapped files', bytes: Number(memory.mappedFileBytes ?? 0) },
  ]);
  // Status is operational context: show the first lines of a long description
  // and let the reader expand it, rather than pushing the other cards away.
  let descriptionElement = $state<HTMLParagraphElement | null>(null);
  let descriptionExpanded = $state(false);
  let descriptionClamped = $state(false);
  $effect(() => {
    const element = descriptionElement;
    void profile?.description;
    const expanded = descriptionExpanded;
    if (!element) return;
    const measure = () => { descriptionClamped = expanded || element.scrollHeight > element.clientHeight + 1; };
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  });

  $effect(() => onloadingchange(!status && !error));
  const update = $derived(daemon?.update && daemon.update.version !== dismissedUpdate.version ? daemon.update : null);

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

{#if error}<Notice variant="unavailable">{error}<button class="button auth-button" onclick={() => retry++}>Retry</button></Notice>{/if}
{#if update}
  <div data-testid="update-notice"><Notice variant="info" ondismiss={() => dismissedUpdate.dismiss(update.version)}>Sockseek {update.version} is available. <a class="text-link" href={update.url} target="_blank" rel="noopener">Release notes</a></Notice></div>
{/if}
<div class="status-grid">
  <!-- Keep the card's place while status loads, like the other cards' placeholders. -->
  {#if !status || profile}
    <section class="panel status-card profile-card" aria-labelledby="profile-heading">
      <h2 id="profile-heading">Your profile</h2>
      <div class="profile-body">
        <ProfilePicture src={profile?.pictureUrl ? appPath(profile.pictureUrl) : null} username={client?.username}
          emptyLabel={profile ? 'No picture' : ''} emptyTitle={profile ? 'Set user-picture to show a picture to peers' : undefined} />
        <div class="profile-copy">
          <strong>{client?.username ?? '—'}</strong>
          {#if profile?.description}
            <p class="user-profile-description" class:clamped={!descriptionExpanded} bind:this={descriptionElement}><LinkifiedText text={profile.description} /></p>
            {#if descriptionClamped}
              <button type="button" class="text-button profile-more" aria-expanded={descriptionExpanded}
                onclick={() => (descriptionExpanded = !descriptionExpanded)}>{descriptionExpanded ? 'Show less' : 'Show more'}</button>
            {/if}
          {:else if profile}
            <p class="profile-empty">
              {#if profile.pictureUrl}No description. Peers see it when they view your profile; set it with <code>user-description</code>.
              {:else}No description or picture. Peers see these when they view your profile; set them with <code>user-description</code> and <code>user-picture</code>.{/if}
            </p>
          {/if}
        </div>
      </div>
    </section>
  {/if}
  <section class="panel status-card" aria-labelledby="soulseek-heading">
    <h2 id="soulseek-heading">Soulseek</h2>
    <dl>
      <div><dt>Connection</dt><dd class:healthy={liveStatus === 'connected' && client?.isReady}>{soulseekStatusLabel(client, liveStatus)}</dd></div>
      <div><dt>Server</dt><dd>{connection?.serverAddress ? `${connection.serverAddress}:${connection.serverPort}` : '—'}</dd></div>
      <div><dt>Server address</dt><dd>{connection?.serverEndpoint ?? '—'}</dd></div>
      <div><dt>Incoming port <HelpPopover title="Incoming port" testId="incoming-port-help">Other Soulseek users connect to this port to download from you and to send you files. If it cannot be reached from the internet, transfers with peers who are also unreachable fail. <ManualLink topic="connectivity">Ports and connectivity</ManualLink></HelpPopover></dt><dd>{connection ? connection.listenPort ?? 'Disabled' : '—'}</dd></div>
    </dl>
  </section>
  <section class="panel status-card" aria-labelledby="sharing-heading">
    <h2 id="sharing-heading">Sharing</h2>
    <dl>
      <div><dt>Status</dt><dd>{sharing ? humanizeStateValue(sharing.state) : '—'}{sharing?.reason ? ` · ${humanizeStateValue(sharing.reason)}` : ''}</dd></div>
      <div><dt>Shared files</dt><dd>{sharing ? formatCount(sharing.catalog.fileCount) : '—'}</dd></div>
      <div><dt>Folders</dt><dd>{sharing ? formatCount(sharing.catalog.directoryCount) : '—'}</dd></div>
      <div><dt>Total size</dt><dd>{sharing ? formatBytes(sharing.catalog.totalBytes) : '—'}</dd></div>
      <div><dt>Shared roots <HelpPopover title="Shared roots" testId="shared-roots-help">The folders other users can browse and download from. Add one with <code>share = /path/to/Music</code> in the config, then rescan. <ManualLink topic="sharing">Sharing a folder</ManualLink></HelpPopover></dt><dd>{sharing ? sharing.aliases.join(', ') || 'None' : '—'}</dd></div>
      <div><dt>Published</dt><dd>{date(sharing?.catalog.publishedAtUtc)}</dd></div>
      {#if sharing?.lastScan}<div><dt>Last scan</dt><dd>{humanizeStateValue(sharing.lastScan.phase)}{sharing.lastScan.errorCount ? ` · ${formatCount(sharing.lastScan.errorCount)} errors` : ''}</dd></div>{/if}
    </dl>
  </section>
  <section class="panel status-card" aria-labelledby="uploads-heading">
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
  <section class="panel status-card" aria-labelledby="daemon-heading">
    <h2 id="daemon-heading">Daemon</h2>
    <dl>
      <div><dt>Version</dt><dd>{info?.version ?? '—'}{#if daemon?.update}{' · '}<a class="text-link" href={daemon.update.url} target="_blank" rel="noopener" data-testid="update-available">{daemon.update.version} available</a>{/if}</dd></div>
      <div><dt>Started</dt><dd>{date(info?.startedAtUtc)}</dd></div>
      <div><dt>Browser connection</dt><dd class:healthy={liveStatus === 'connected'}>{humanizeStateValue(liveStatus)}</dd></div>
      <div><dt>Authentication</dt><dd>{browserSession.data?.authenticationEnabled ? 'Enabled' : 'Disabled'}</dd></div>
      <div><dt>Configuration</dt><dd>{!daemon?.configuration ? '—' : daemon.configuration.reloadFailed ? 'Reload failed' : daemon?.configuration?.restartRequired ? 'Restart required' : daemon?.configuration?.watching ? 'Watching for changes' : 'Automatic reload disabled'}</dd></div>
      <div data-testid="daemon-memory"><dt>Memory</dt><dd>
        {#if memory}
          {formatBytes(memory.residentBytes)}<span class="memory-note">{memory.peakResidentBytes == null ? '' : ` · peak ${formatBytes(memory.peakResidentBytes)}`}{Number(memory.swappedBytes) > 0 ? ` · ${formatBytes(memory.swappedBytes)} swapped` : ''}</span>
          {#if memoryParts.length}
            <span class="progress-track memory-line" role="img" aria-label={memoryParts.map(part => `${part.label} ${formatBytes(part.bytes)}`).join(', ')}>
              {#each memoryParts as part (part.id)}<i class={part.id} style={`width:${part.bytes / Number(memory.residentBytes) * 100}%`} title={`${part.label}: ${formatBytes(part.bytes)}`}></i>{/each}
            </span>
            <span class="memory-legend">{#each memoryParts as part (part.id)}<span class={part.id}>{part.label} {formatBytes(part.bytes)}</span>{/each}</span>
          {/if}
        {:else}—{/if}
      </dd></div>
    </dl>
  </section>
  <section class="panel status-card" aria-labelledby="storage-heading">
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
  .status-card { padding: 20px 22px; border-radius: 12px; min-width: 0; }
  h2 { margin: 0; padding-bottom: 12px; border-bottom: 1px solid var(--border); font-size: .95rem; font-weight: 650; }
  dl { margin: 14px 0 0; display: grid; gap: 12px; font-size: .8125rem; line-height: 1.4; }
  dl > div { display: grid; grid-template-columns: minmax(100px, .85fr) minmax(0, 1.15fr); gap: 14px; }
  dt { color: var(--muted); }
  dd { margin: 0; overflow-wrap: anywhere; }
  .healthy { color: var(--success); }
  .memory-note { color: var(--muted); }
  .memory-line { display: flex; height: 5px; margin-top: 7px; }
  .memory-line > i { border-radius: 0; transition: none; background-color: var(--part); }
  .memory-legend { display: flex; flex-wrap: wrap; gap: 2px 12px; margin-top: 6px; color: var(--muted); font-size: .72rem; }
  .memory-legend > span::before { content: ""; display: inline-block; width: 7px; height: 7px; margin-right: 5px; border-radius: 2px; background-color: var(--part); }
  .heap { --part: var(--status-active); }
  .runtime { --part: var(--status-partial); }
  .files { --part: var(--faint); }
  .profile-body { --profile-picture-size: 112px; display: grid; grid-template-columns: 112px minmax(0, 1fr); gap: 20px; align-items: start; margin-top: 16px; }
  .profile-copy { min-width: 0; }
  .profile-copy strong { font-size: 1.05rem; font-weight: 650; overflow-wrap: anywhere; }
  .profile-copy p { margin: 8px 0 0; font-size: .8125rem; line-height: 1.6; }
  .clamped { display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 4; line-clamp: 4; overflow: hidden; }
  .profile-more { margin: 6px 0 0 -8px; font-size: .78rem; }
  .profile-empty { color: var(--muted); }
  .storage-error { color: var(--danger); font-size: .8125rem; overflow-wrap: anywhere; }
  @media (max-width: 800px) { .status-grid { grid-template-columns: minmax(0, 1fr); } }
  @media (max-width: 640px) {
    .profile-body { --profile-picture-size: 72px; grid-template-columns: 72px minmax(0, 1fr); gap: 14px; }
  }
</style>
