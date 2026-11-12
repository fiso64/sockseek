<script lang="ts">
  import type { Schema } from '../../api/types';
  import type { DaemonLiveClient, LiveConnectionStatus } from '../../api/live';
  import { DaemonHttpClient } from '../../api/http';
  import { browserSession } from '../../api/session.svelte';
  import { formatCountWithNoun } from '../../app/format';
  import Icon from '../../components/Icon.svelte';
  import Notice from '../../components/Notice.svelte';
  import LoadingIndicator from '../../components/LoadingIndicator.svelte';
  import ShareBrowser from '../users/ShareBrowser.svelte';
  import type { ShareBrowserSession } from '../users/session';
  import { localShareSource } from '../users/share-source';
  import StatusTab from './StatusTab.svelte';
  import PageTabs from '../../components/PageTabs.svelte';
  import ApiTab from './ApiTab.svelte';
  import ManagementTab from './ManagementTab.svelte';
  import type { SystemTab } from './tabs';

  let { daemon, liveStatus, liveClient, tab, ontabchange }: {
    daemon: Schema<'DaemonStateDto'> | null;
    liveStatus: LiveConnectionStatus;
    liveClient: DaemonLiveClient;
    tab: SystemTab;
    ontabchange: (tab: SystemTab) => void;
  } = $props();
  const sharing = $derived(daemon?.sharing);
  const configuration = $derived(daemon?.configuration);
  const http = new DaemonHttpClient();
  const sharesSession: ShareBrowserSession = {};
  let pending = $state(false);
  let error = $state<string | null>(null);
  let scanPending = $state(false);
  let scanError = $state<string | null>(null);
  let requestedScanId = $state<string | null>(null);
  const scanning = $derived(scanPending || !!sharing?.activeScan);
  const scanDisabled = $derived(scanning || !sharing || sharing.state === 'Disabled' || liveStatus !== 'connected');
  const completedScan = $derived(sharing?.lastScan?.scanId === requestedScanId ? sharing.lastScan : null);

  async function rescan(): Promise<void> {
    if (scanDisabled) return;
    scanPending = true;
    scanError = null;
    requestedScanId = null;
    try {
      const result = await http.post<Schema<'StartShareScanResponseDto'>>('/sharing/scans');
      requestedScanId = result.scan.scanId;
    } catch (cause) { scanError = message(cause); }
    finally { scanPending = false; }
  }

  async function logout(): Promise<void> {
    pending = true;
    try { await browserSession.logout(); }
    catch (cause) { error = message(cause); }
    finally { pending = false; }
  }
  function message(cause: unknown): string { return cause instanceof Error ? cause.message : 'Request failed.'; }
</script>

<section class="page system-page">
  <header class="page-heading settings-heading">
    <h1>System</h1>
    <div class="settings-actions">
      <button class="auth-button" onclick={() => void rescan()} disabled={scanDisabled}
        title={sharing?.state === 'Disabled' ? 'Configure shared folders before rescanning.' : undefined}>
        <Icon name="refresh" spin={scanning} />{scanning ? 'Scanning…' : 'Rescan shares'}
      </button>
      {#if browserSession.data?.authenticationEnabled}
        <button class="auth-button" onclick={() => void logout()} disabled={pending}><Icon name="log-out" />Sign out</button>
      {/if}
    </div>
  </header>
  <PageTabs id="system-tab" label="System sections" value={tab} onchange={ontabchange}
    items={[{ id: 'status', label: 'Status' }, { id: 'shares', label: 'Shares' }, { id: 'management', label: 'Management' }, { id: 'api', label: 'API' }]} />
  <div role="tabpanel" id="system-tab-panel" aria-labelledby={`system-tab-${tab}`}>
  {#if error}<Notice variant="unavailable" ondismiss={() => error = null}>{error}</Notice>{/if}
  {#if configuration?.reloadFailed}
    <Notice variant="unavailable">
      <span>{configuration.watching
        ? 'Configuration could not be reloaded. Check the daemon log and configuration file.'
        : 'Configuration watching stopped. Check the daemon log and restart to resume automatic reloading.'}</span>
    </Notice>
  {/if}
  {#if configuration?.restartRequired}
    <Notice variant="degraded">
      <span>Some configuration changes require a daemon restart.</span>
    </Notice>
  {/if}
  {#if scanError}
    <Notice variant="unavailable" ondismiss={() => scanError = null}>{scanError}</Notice>
  {:else if sharing?.activeScan}
    <Notice variant="loading">
      <span>Scanning shares: {formatCountWithNoun(sharing.activeScan.filesDiscovered, 'file')} found.</span>
    </Notice>
  {:else if completedScan}
    <Notice variant={completedScan.phase === 'Failed' ? 'unavailable'
      : Number(completedScan.errorCount) > 0 || completedScan.phase === 'Cancelled' ? 'degraded' : ''}
      ondismiss={() => requestedScanId = null}>
      <span>{completedScan.phase === 'Failed' ? 'Share scan failed. Check the daemon log.'
        : completedScan.phase === 'Cancelled' ? 'Share scan cancelled.'
        : `Share scan complete: ${formatCountWithNoun(completedScan.filesDiscovered, 'file')}${Number(completedScan.errorCount) > 0 ? `, ${formatCountWithNoun(completedScan.errorCount, 'error')} (see daemon log)` : ''}.`}</span>
    </Notice>
  {/if}
  {#if tab === 'status'}
    <StatusTab {daemon} {liveStatus} {liveClient} />
  {:else if tab === 'shares'}
    {#if sharing?.catalog.generationId}
      {#key sharing.catalog.generationId}
        <ShareBrowser source={localShareSource(sharing.catalog.generationId, sharing.catalog.totalBytes)} session={sharesSession} />
      {/key}
    {:else if !sharing}
      <LoadingIndicator />
    {:else}
      <Notice>{sharing.state === 'Disabled' ? 'Configure shared folders in the daemon configuration to start sharing.' : 'No share catalog has been published yet. Scan your shares to build one.'}</Notice>
    {/if}
  {:else if tab === 'management'}
    <ManagementTab />
  {:else}
    <ApiTab />
  {/if}
  </div>
</section>

<style>
  .page-heading { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 16px; }
  .settings-actions { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; }
  .settings-actions button { display: inline-flex; align-items: center; gap: 7px; --app-icon-size: 16px; }
  @media (max-width: 600px) {
    .system-page { --content-gutter: 18px; }
    .settings-actions { gap: 6px; }
    .settings-actions button { padding-inline: 6px; gap: 5px; }
  }
</style>
