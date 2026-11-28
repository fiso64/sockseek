<script lang="ts">
  import { onMount, untrack } from 'svelte';
  import type { DashboardAnalyticsDto } from '../../api/types';
  import type { LiveActivitySummary } from '../../api/state.svelte';
  import type { LiveConnectionStatus } from '../../api/live';
  import type { UserLinkActions } from '../../app/navigation';
  import { daemonApi } from '../../api/client';
  import { formatBytes as formatKnownBytes, formatCount as formatKnownCount, formatCountWithNoun, humanizeStateValue, soulseekStatusLabel } from '../../app/format';
  import UsernameLink from '../../components/UsernameLink.svelte';
  import Icon from '../../components/Icon.svelte';
  import LoadingIndicator from '../../components/LoadingIndicator.svelte';
  import { availableDashboardSummary, dashboardByteAxis, dashboardChartLabels, dashboardTransferRate, shareRatioDelta } from './presentation';

  const rangeIds = ['24h', '7d', '30d', '90d', '1y', 'all'] as const;
  type RangeId = (typeof rangeIds)[number];
  const rangeLabels: Record<RangeId, string> = { '24h': '24h', '7d': '7d', '30d': '30d', '90d': '90d', '1y': '1y', all: 'All' };
  const rangeContext: Record<RangeId, string> = {
    '24h': 'Last 24 hours', '7d': 'Last 7 days', '30d': 'Last 30 days',
    '90d': 'Last 90 days', '1y': 'Last 1 year', all: 'All history',
  };

  interface Props {
    liveStatus: LiveConnectionStatus;
    terminalTransferCommitRevision: number | string;
    soulseek?: import('../../api/types').Schema<'SoulseekClientStatusDto'> | null;
    activity: Readonly<LiveActivitySummary>;
    userActions: UserLinkActions;
  }

  let { liveStatus, terminalTransferCommitRevision, soulseek = null, activity, userActions }: Props = $props();
  const chartId = $props.id();
  let range = $state<RangeId>('30d');
  let rankingTab = $state<'transfers' | 'content' | 'errors'>('transfers');
  let data = $state<DashboardAnalyticsDto | null>(null);
  let loading = $state(true);
  let error = $state<Error | null>(null);
  let apiLatencyMs = $state<number | null>(null);
  let retryRevision = $state(0);
  let refreshRevision = $state(0);
  let fetchedRange: RangeId | null = null;
  let summary = $derived(availableDashboardSummary(data));
  let hadLiveConnection = false;
  let wasConnected = false;

  onMount(() => {
    const timer = setInterval(() => refreshRevision++, 60_000);
    return () => clearInterval(timer);
  });

  $effect(() => {
    const connected = liveStatus === 'connected';
    if (connected && !wasConnected && (hadLiveConnection || untrack(() => error !== null))) {
      // A live reconnection may reveal completed accounting that was not in
      // the last retained read. Do not refetch merely because the socket drops.
      untrack(() => refreshRevision++);
    }
    if (connected) hadLiveConnection = true;
    wasConnected = connected;
  });

  $effect(() => {
    const selected = range;
    void retryRevision;
    void refreshRevision;
    void terminalTransferCommitRevision;
    const request = new AbortController();
    if (fetchedRange !== selected) data = null;
    loading = true;
    error = null;
    const startedAt = performance.now();
    void daemonApi.getDashboardAnalytics(selected, request.signal).then((value) => {
      if (!request.signal.aborted && selected === range) {
        data = value;
        fetchedRange = selected;
        apiLatencyMs = Math.max(0, Math.round(performance.now() - startedAt));
      }
    }).catch((cause: unknown) => {
      if (!request.signal.aborted) {
        error = cause instanceof Error ? cause : new Error(String(cause));
        apiLatencyMs = null;
      }
    }).finally(() => {
      if (!request.signal.aborted && selected === range) loading = false;
    });
    return () => request.abort();
  });

  let downloadBytes = $derived(data?.bandwidth.map((bucket) => Number(bucket.downloadBytes)) ?? []);
  let uploadBytes = $derived(data?.bandwidth.map((bucket) => Number(bucket.uploadBytes)) ?? []);
  let activityAxis = $derived(dashboardByteAxis(downloadBytes, uploadBytes));
  let hasDownloadHistory = $derived(downloadBytes.some((value) => value > 0) || Number(summary?.downloadedFiles ?? 0) > 0);
  let hasUploadHistory = $derived(uploadBytes.some((value) => value > 0) || Number(summary?.uploadedFiles ?? 0) > 0);
  let hasHistory = $derived(hasDownloadHistory || hasUploadHistory);
  let daemonReachable = $derived(liveStatus === 'connected');
  const connectionPending = $derived(liveStatus === 'connecting' || liveStatus === 'reconnecting');

  function formatBytes(value: number | string | null | undefined): string {
    return value == null ? '—' : formatKnownBytes(value);
  }

  function formatCount(value: number | string | null | undefined): string {
    return value == null ? '—' : formatKnownCount(value);
  }

  function formatFiles(value: number | string | null | undefined): string {
    return value == null ? '— files' : formatCountWithNoun(value, 'file');
  }

  function chartY(value: number): number { return 150 - value / activityAxis.maxBytes * 138; }

  function chartPath(values: number[], width = 760): string {
    const step = width / Math.max(1, values.length - 1);
    const points = values.map((value, index) => ({ x: index * step, y: chartY(value) }));
    const first = points[0];
    if (!first) return '';
    if (points.length === 1) return `M ${first.x.toFixed(1)} ${first.y.toFixed(1)}`;
    const handle = step * 0.42;
    return points.slice(1).reduce((path, point, index) => {
      const previous = points[index]!;
      return `${path} C ${(previous.x + handle).toFixed(1)} ${previous.y.toFixed(1)}, ${(point.x - handle).toFixed(1)} ${point.y.toFixed(1)}, ${point.x.toFixed(1)} ${point.y.toFixed(1)}`;
    }, `M ${first.x.toFixed(1)} ${first.y.toFixed(1)}`);
  }

  let downloadPath = $derived(chartPath(downloadBytes));
  let uploadPath = $derived(chartPath(uploadBytes));
  let labels = $derived(data ? dashboardChartLabels(data.bandwidth, range, data.range.endUtc, Number(data.range.bucketSeconds)) : []);
  let hoveredIndex = $state<number | null>(null);
  let tooltipWidth = $state(0);
  let hoveredBucket = $derived(hoveredIndex == null ? null : data?.bandwidth[hoveredIndex]);
  let hoveredX = $derived(hoveredIndex == null ? 0 : hoveredIndex / Math.max(1, (data?.bandwidth.length ?? 0) - 1));
  $effect(() => { data; hoveredIndex = null; });

  function hoverActivity(event: PointerEvent): void {
    const count = data?.bandwidth.length ?? 0;
    if (!count) return;
    const bounds = (event.currentTarget as SVGSVGElement).getBoundingClientRect();
    const fraction = Math.max(0, Math.min(1, (event.clientX - bounds.left) / bounds.width));
    hoveredIndex = Math.round(fraction * (count - 1));
  }

  const intervalDateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'short', timeStyle: 'short' });

  function ratioDelta(): string {
    return shareRatioDelta(data);
  }

  function ratioContext(): string {
    if (!hasHistory) return '—';
    if (range === 'all') return 'All history';
    const delta = ratioDelta();
    return delta === '—' ? '—' : `${delta} vs previous range`;
  }

  function age(timestamp: string): string {
    const seconds = Math.max(0, Math.floor((Date.now() - Date.parse(timestamp)) / 1000));
    if (seconds < 60) return `${seconds}s ago`;
    if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
    if (seconds < 86_400) return `${Math.floor(seconds / 3600)}h ago`;
    return `${Math.floor(seconds / 86_400)}d ago`;
  }
</script>

<section class="page dashboard-page">
  <div class="dashboard-heading">
    <div class="page-heading dashboard-title">
      <h1>Dashboard</h1>
    </div>
    <div class="dashboard-range-control" aria-label="Dashboard history range">
      <span class="page-loading">{#if loading}<LoadingIndicator compact label="Loading analytics" />{/if}</span>
      <div class="segmented-control range-buttons">
        {#each rangeIds as id}
          <button type="button" class:active={range === id} onclick={() => (range = id)}>{rangeLabels[id]}</button>
        {/each}
      </div>
    </div>
  </div>

  {#if error}
    <div class={`resource-state-notice ${liveStatus === 'offline' ? 'offline' : 'unavailable'}`} class:blocking={!data} role="alert">
      <span class="resource-state-dot"></span>
      <span class="resource-state-copy">
        <strong>{liveStatus === 'offline' ? 'Daemon unavailable' : 'Dashboard analytics unavailable'}</strong>
        <small>{liveStatus === 'offline' ? 'This can’t be loaded while the daemon is offline.' : error.message}</small>
      </span>
      {#if liveStatus !== 'offline'}
        <button class="button" type="button" onclick={() => retryRevision++}>Retry</button>
      {/if}
    </div>
  {/if}

  <div class="panel dashboard-metrics">
    <article class="dashboard-metric-card">
      <div class="metric-label download"><Icon name="download" /> Downloaded</div>
      <strong>{formatBytes(summary?.downloadedBytes)}</strong>
      <small>{formatFiles(summary?.downloadedFiles)}</small>
    </article>
    <article class="dashboard-metric-card">
      <div class="metric-label upload"><Icon name="upload" /> Uploaded</div>
      <strong>{formatBytes(summary?.uploadedBytes)}</strong>
      <small>{formatFiles(summary?.uploadedFiles)}</small>
    </article>
    <article class="dashboard-metric-card">
      <div class="metric-label"><Icon name="refresh" /> Share ratio</div>
      <strong>{summary?.shareRatio == null ? '—' : Number(summary.shareRatio).toFixed(2)}</strong>
      <small>{ratioContext()}</small>
    </article>
    <article class="dashboard-metric-card">
      <div class="metric-label"><Icon name="user" /> Distinct peers</div>
      <strong>{formatCount(summary?.distinctPeers)}</strong>
      <small>across transfers</small>
    </article>
  </div>

  <section class="panel dashboard-panel activity-panel" data-bucket-seconds={data?.range.bucketSeconds}>
    <div class="dashboard-panel-heading">
      <h2>Transfer activity</h2>
      <span class="panel-context">{rangeContext[range]}</span>
    </div>
    <div class="well activity-chart">
      <div class="chart-y-labels">
        {#each activityAxis.labels as tick}
          <span>{formatBytes(tick)}</span>
        {/each}
      </div>
      <div class="chart-canvas">
        <svg viewBox="0 0 760 150" preserveAspectRatio="none" role="img" aria-label={`Bytes transferred per interval for ${range}`} onpointermove={hoverActivity} onpointerleave={() => hoveredIndex = null}>
          <g class="chart-grid">
            <line x1="0" y1="12" x2="760" y2="12"/>
            <line x1="0" y1="58" x2="760" y2="58"/>
            <line x1="0" y1="104" x2="760" y2="104"/>
            <line x1="0" y1="149" x2="760" y2="149"/>
            <line x1="126" y1="0" x2="126" y2="150"/>
            <line x1="253" y1="0" x2="253" y2="150"/>
            <line x1="380" y1="0" x2="380" y2="150"/>
            <line x1="506" y1="0" x2="506" y2="150"/>
            <line x1="633" y1="0" x2="633" y2="150"/>
          </g>
          <defs>
            <!-- Themes set how strongly each area fades from its line down. -->
            {#each ['download', 'upload'] as series}
              <linearGradient id={`${chartId}-${series}`} class={`chart-gradient ${series}`} x1="0" y1="0" x2="0" y2="1"><stop offset="0" /><stop offset="1" /></linearGradient>
            {/each}
          </defs>
          {#if hasHistory}
            <path class="chart-area download" fill={`url(#${chartId}-download)`} d={`${downloadPath} L 760 150 L 0 150 Z`}/>
            <path class="chart-line download" d={downloadPath}/>
            <path class="chart-area upload" fill={`url(#${chartId}-upload)`} d={`${uploadPath} L 760 150 L 0 150 Z`}/>
            <path class="chart-line upload" d={uploadPath}/>
          {/if}
          {#if hoveredBucket}<line class="chart-hover-line" x1={hoveredX * 760} x2={hoveredX * 760} y1="0" y2="150" />{/if}
        </svg>
        {#if hoveredBucket}
          <span class="chart-hover-dot download" style:left={`${hoveredX * 100}%`} style:top={`${chartY(Number(hoveredBucket.downloadBytes))}px`}></span>
          <span class="chart-hover-dot upload" style:left={`${hoveredX * 100}%`} style:top={`${chartY(Number(hoveredBucket.uploadBytes))}px`}></span>
        {/if}
        <div class="chart-x-labels">
          {#each labels as label}<span>{label}</span>{/each}
        </div>
        {#if hoveredBucket}
          <div class="popover-panel chart-tooltip" role="tooltip" bind:offsetWidth={tooltipWidth}
            style:left={`clamp(min(0px, calc(100% - ${tooltipWidth}px)), ${hoveredX * 100}% - ${tooltipWidth / 2}px, calc(100% - ${tooltipWidth}px))`}>
            <strong class="chart-tooltip-interval">
              <span>From <time datetime={hoveredBucket.startUtc}>{intervalDateFormat.format(new Date(hoveredBucket.startUtc))}</time></span>
              <span>To <time datetime={hoveredBucket.endUtc}>{intervalDateFormat.format(new Date(hoveredBucket.endUtc))}</time></span>
            </strong>
            <span class="download">Downloaded <b>{formatBytes(hoveredBucket.downloadBytes)}</b></span>
            <span class="upload">Uploaded <b>{formatBytes(hoveredBucket.uploadBytes)}</b></span>
          </div>
        {/if}
      </div>
    </div>
    <div class="chart-legend">
      <span><i class="download"></i>Download</span>
      <span><i class="upload"></i>Upload</span>
    </div>
  </section>

  <div class="dashboard-lower-area">
    <section class="panel dashboard-panel ranking-panel">
      <div class="ranking-tabs" role="tablist" aria-label="Dashboard ranking">
        <button type="button" class:active={rankingTab === 'transfers'} onclick={() => (rankingTab = 'transfers')}>Transfers</button>
        <button type="button" class:active={rankingTab === 'content'} onclick={() => (rankingTab = 'content')}>Content</button>
        <button type="button" class:active={rankingTab === 'errors'} onclick={() => (rankingTab = 'errors')}>Errors</button>
        <span>{rangeContext[range]}</span>
      </div>
      {#if rankingTab === 'transfers'}
        <div class="dashboard-peer-rankings">
          {#each ['downloads', 'uploads'] as direction}
            {@const rows = direction === 'downloads' ? data?.downloadPeers ?? [] : data?.uploadPeers ?? []}
            <div class="dashboard-ranking-table peers-table" aria-label={direction === 'downloads' ? 'Downloads' : 'Uploads'}>
              <h3 class="dashboard-peer-heading"><Icon name={direction === 'downloads' ? 'download' : 'upload'}/>{direction === 'downloads' ? 'Downloads' : 'Uploads'}</h3>
              <div class="dashboard-ranking-row heading">
                <span>#</span><span>User</span><span>{direction === 'downloads' ? 'Downloaded' : 'Uploaded'}</span><span>Files</span>
              </div>
              {#each rows as peer, index}
                <div class="dashboard-ranking-row">
                  <span>{index + 1}</span>
                  <span class="dashboard-peer-name"><UsernameLink username={peer.username} actions={userActions}/></span>
                  <span>{formatBytes(peer.transferredBytes)}</span>
                  <span>{formatCount(peer.successfulFileCount)}</span>
                </div>
              {/each}
            </div>
          {/each}
        </div>
      {:else if rankingTab === 'content'}
        <div class="dashboard-ranking-table content-table">
          <div class="dashboard-ranking-row heading">
            <span>#</span><span>Folder</span><span>Peers</span><span>Uploads</span>
          </div>
          {#each data?.content ?? [] as item, index}
            <div class="dashboard-ranking-row">
              <span>{index + 1}</span>
              <span title={item.displayPath}>{item.displayPath}</span>
              <span>{formatCount(item.distinctPeerCount)}</span>
              <span>{formatCount(item.downloadCount)}</span>
            </div>
          {/each}
        </div>
      {:else}
        <div class="dashboard-ranking-table errors-table">
          <div class="dashboard-ranking-row heading">
            <span>#</span><span>Error</span><span>Count</span><span>Last seen</span>
          </div>
          {#each data?.errors ?? [] as item, index}
            <div class="dashboard-ranking-row">
              <span>{index + 1}</span>
              <span title={item.reason}>{humanizeStateValue(item.reason)}</span>
              <span>{formatCount(item.count)}</span>
              <span>{age(item.lastSeenUtc)}</span>
            </div>
          {/each}
        </div>
      {/if}
    </section>

    <div class="dashboard-bottom-grid">
      <section class="panel dashboard-panel current-transfer-panel">
        <div class="dashboard-panel-heading compact"><h2>Transfer rates</h2></div>
        <div class="summary-metrics">
          <div>
            <span>Download</span>
            <strong class="rate-value download"><i aria-hidden="true">↓</i>{dashboardTransferRate(activity.downloadRate)}</strong>
          </div>
          <div>
            <span>Upload</span>
            <strong class="rate-value upload"><i aria-hidden="true">↑</i>{dashboardTransferRate(activity.uploadRate)}</strong>
          </div>
          <div data-testid="dashboard-active-downloads">
            <span>Active downloads</span>
            <strong>{formatCount(activity.downloads)}</strong>
          </div>
          <div data-testid="dashboard-active-uploads">
            <span>Active uploads</span>
            <strong>{formatCount(activity.uploads)}</strong>
          </div>
          <div data-testid="dashboard-running-jobs">
            <span>Running / Pending jobs</span>
            <strong>{formatCount(activity.runningJobCount)} / {formatCount(activity.pendingJobCount)}</strong>
          </div>
        </div>
      </section>
      <section class="panel dashboard-panel health-panel">
        <div class="dashboard-panel-heading compact">
          <h2>Daemon health</h2>
          {#if !daemonReachable}
            <span class:offline={!connectionPending} class="badge health-badge"><i></i>{connectionPending ? humanizeStateValue(liveStatus) : 'Offline'}</span>
          {/if}
        </div>
        <div class="health-list">
          <div><span>Soulseek</span><strong>{soulseekStatusLabel(soulseek, liveStatus)}</strong></div>
          <div><span>Daemon</span><strong>{humanizeStateValue(liveStatus)}</strong></div>
          <div><span>Transfer errors</span><strong>{activity.failed} recent</strong></div>
          <div><span>API latency</span><strong>{daemonReachable && apiLatencyMs !== null ? `${apiLatencyMs} ms` : '—'}</strong></div>
        </div>
      </section>
    </div>
  </div>
</section>
