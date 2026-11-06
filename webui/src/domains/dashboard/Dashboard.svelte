<script lang="ts">
  import { onMount, untrack } from 'svelte';
  import type { DashboardAnalyticsDto } from '../../api/types';
  import type { LiveTransferSummary } from '../../api/state.svelte';
  import type { LiveConnectionStatus } from '../../api/live';
  import type { UserLinkActions } from '../../app/navigation';
  import { daemonApi } from '../../api/client';
  import { formatBytes as formatKnownBytes, formatCount as formatKnownCount, formatCountWithNoun, humanizeStateValue, soulseekStatusLabel } from '../../app/format';
  import UsernameLink from '../../components/UsernameLink.svelte';
  import Icon from '../../components/Icon.svelte';
  import { availableDashboardSummary, dashboardByteAxis, dashboardChartLabels, dashboardTransferRate, shareRatioDelta } from './presentation';

  const rangeIds = ['24h', '7d', '30d', '90d', '1y', 'all'] as const;
  type RangeId = (typeof rangeIds)[number];
  const rangeLabels: Record<RangeId, string> = { '24h': '24h', '7d': '7d', '30d': '30d', '90d': '90d', '1y': '1y', all: 'All' };
  const rangeContext: Record<RangeId, string> = {
    '24h': 'Last 24 hours', '7d': 'Last 7 days', '30d': 'Last 30 days',
    '90d': 'Last 90 days', '1y': 'Last 1 year', all: 'All retained history',
  };

  interface Props {
    liveStatus: LiveConnectionStatus;
    terminalTransferCommitRevision: number | string;
    soulseek?: import('../../api/types').Schema<'SoulseekClientStatusDto'> | null;
    transfers: Readonly<LiveTransferSummary>;
    userActions: UserLinkActions;
  }

  let { liveStatus, terminalTransferCommitRevision, soulseek = null, transfers, userActions }: Props = $props();
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
  let shareRatioSamples = $derived(uploadBytes.map((upload, index) => downloadBytes[index] ? upload / downloadBytes[index]! : 0));
  let daemonReachable = $derived(liveStatus === 'connected');

  function formatBytes(value: number | string | null | undefined): string {
    return value == null ? '—' : formatKnownBytes(value);
  }

  function formatCount(value: number | string | null | undefined): string {
    return value == null ? '—' : formatKnownCount(value);
  }

  function filesInRange(value: number | string | null | undefined): string {
    return value == null ? '— files in range' : `${formatCountWithNoun(value, 'file')} in range`;
  }

  function metricBarHeight(values: number[], value: number): number {
    if (value <= 0) return 0;
    const maximum = Math.max(1, ...values);
    return Math.round(14 + value / maximum * 86);
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
    if (range === 'all') return 'All retained history';
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
      <span class="dashboard-range-label" role="status">{loading ? 'Loading analytics' : ''}</span>
      <div class="range-buttons">
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
        <small>{liveStatus === 'offline' ? 'This resource cannot be loaded while the daemon is offline.' : error.message}</small>
      </span>
      {#if liveStatus !== 'offline'}
        <button type="button" onclick={() => retryRevision++}>Retry</button>
      {/if}
    </div>
  {/if}

  <div class="dashboard-metrics">
    <article class="dashboard-metric-card">
      <div class="metric-label download"><span aria-hidden="true">↓</span> Downloaded</div>
      <strong>{formatBytes(summary?.downloadedBytes)}</strong>
      <small>{filesInRange(summary?.downloadedFiles)}</small>
      <div class="metric-sparkline" aria-hidden="true">
        {#if hasDownloadHistory}
          {#each downloadBytes.slice(-8) as point}
            <i class:zero={point <= 0} style={`height:${metricBarHeight(downloadBytes, point)}%`}></i>
          {/each}
        {/if}
      </div>
    </article>
    <article class="dashboard-metric-card">
      <div class="metric-label upload"><span aria-hidden="true">↑</span> Uploaded</div>
      <strong>{formatBytes(summary?.uploadedBytes)}</strong>
      <small>{filesInRange(summary?.uploadedFiles)}</small>
      <div class="metric-sparkline upload" aria-hidden="true">
        {#if hasUploadHistory}
          {#each uploadBytes.slice(-8) as point}
            <i class:zero={point <= 0} style={`height:${metricBarHeight(uploadBytes, point)}%`}></i>
          {/each}
        {/if}
      </div>
    </article>
    <article class="dashboard-metric-card">
      <div class="metric-label"><span aria-hidden="true">⇵</span> Share ratio</div>
      <strong>{summary?.shareRatio == null ? '—' : Number(summary.shareRatio).toFixed(2)}</strong>
      <small>{ratioContext()}</small>
      <div class="metric-sparkline ratio" aria-hidden="true">
        {#if hasHistory}
          {#each shareRatioSamples.slice(-8) as point}
            <i class:zero={point <= 0} style={`height:${metricBarHeight(shareRatioSamples, point)}%`}></i>
          {/each}
        {/if}
      </div>
    </article>
    <article class="dashboard-metric-card">
      <div class="metric-label"><span aria-hidden="true">◎</span> Distinct peers</div>
      <strong>{formatCount(summary?.distinctPeers)}</strong>
      <small>across transfers in range</small>
    </article>
  </div>

  <section class="dashboard-panel activity-panel" data-bucket-seconds={data?.range.bucketSeconds}>
    <div class="dashboard-panel-heading">
      <h2>Transfer activity</h2>
      <span class="panel-context">{rangeContext[range]}</span>
    </div>
    <div class="activity-chart">
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
          {#if hasHistory}
            <path class="chart-area download" d={`${downloadPath} L 760 150 L 0 150 Z`}/>
            <path class="chart-line download" d={downloadPath}/>
            <path class="chart-area upload" d={`${uploadPath} L 760 150 L 0 150 Z`}/>
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
      </div>
      {#if hoveredBucket}
        <div class="chart-tooltip" role="tooltip" bind:offsetWidth={tooltipWidth} style:left={`clamp(0px, ${hoveredX * 100}% - ${tooltipWidth / 2}px, calc(100% - ${tooltipWidth}px))`}>
          <strong class="chart-tooltip-interval">
            <span>From <time datetime={hoveredBucket.startUtc}>{intervalDateFormat.format(new Date(hoveredBucket.startUtc))}</time></span>
            <span>To <time datetime={hoveredBucket.endUtc}>{intervalDateFormat.format(new Date(hoveredBucket.endUtc))}</time></span>
          </strong>
          <span class="download">Downloaded <b>{formatBytes(hoveredBucket.downloadBytes)}</b></span>
          <span class="upload">Uploaded <b>{formatBytes(hoveredBucket.uploadBytes)}</b></span>
        </div>
      {/if}
    </div>
    <div class="chart-legend">
      <span><i class="download"></i>Download</span>
      <span><i class="upload"></i>Upload</span>
    </div>
  </section>

  <div class="dashboard-lower-area">
    <section class="dashboard-panel ranking-panel">
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
            <span>#</span><span>Folder</span><span>Downloads</span><span>Peers</span>
          </div>
          {#each data?.content ?? [] as item, index}
            <div class="dashboard-ranking-row">
              <span>{index + 1}</span>
              <span title={item.displayPath}>{item.displayPath}</span>
              <span>{formatCount(item.downloadCount)}</span>
              <span>{formatCount(item.distinctPeerCount)}</span>
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
      <section class="dashboard-panel current-transfer-panel">
        <div class="dashboard-panel-heading compact"><h2>Transfer rates</h2></div>
        <div class="summary-metrics">
          <div>
            <span>Download</span>
            <strong class="rate-value download"><i aria-hidden="true">↓</i>{dashboardTransferRate(transfers.downloadRate)}</strong>
            <small>{transfers.downloads} active download{transfers.downloads === 1 ? '' : 's'}</small>
          </div>
          <div>
            <span>Upload</span>
            <strong class="rate-value upload"><i aria-hidden="true">↑</i>{dashboardTransferRate(transfers.uploadRate)}</strong>
            <small>{transfers.uploads} active upload{transfers.uploads === 1 ? '' : 's'}</small>
          </div>
          <div>
            <span>Active transfers</span>
            <strong>{transfers.active}</strong>
            <small>{transfers.queued} queued transfer row{transfers.queued === 1 ? '' : 's'}</small>
          </div>
        </div>
      </section>
      <section class="dashboard-panel health-panel">
        <div class="dashboard-panel-heading compact">
          <h2>Daemon health</h2>
          <span class:offline={!daemonReachable} class="health-badge"><i></i>{daemonReachable ? 'Healthy' : 'Offline'}</span>
        </div>
        <div class="health-list">
          <div><span>Soulseek</span><strong>{soulseekStatusLabel(soulseek, daemonReachable)}</strong></div>
          <div><span>Daemon</span><strong>{humanizeStateValue(liveStatus)}</strong></div>
          <div><span>Transfer errors</span><strong>{transfers.failed} recent</strong></div>
          <div><span>API latency</span><strong>{daemonReachable && apiLatencyMs !== null ? `${apiLatencyMs} ms` : '—'}</strong></div>
        </div>
      </section>
    </div>
  </div>
</section>
