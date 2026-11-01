import { describe, expect, it } from 'vitest';
import { availableDashboardSummary, bandwidthMegabytesPerSecond, dashboardBandwidthAxis, dashboardChartLabel, dashboardChartLabels, dashboardTransferRate, shareRatioDelta } from '../src/domains/dashboard/presentation';
import type { DashboardAnalyticsDto } from '../src/api/types';

function analytics(current: number | null, previous: number | null): DashboardAnalyticsDto {
  const coverage = { state: 'Available' as const, completeFromUtc: null, isComplete: true };
  const summary = { downloadedBytes: 0, uploadedBytes: 0, downloadedFiles: 0, uploadedFiles: 0, distinctPeers: 0, shareRatio: current };
  return {
    accountingVersion: 1,
    range: { range: '24h', startUtc: '2026-09-07T00:00:00Z', endUtc: '2026-09-08T00:00:00Z', bucketSeconds: 60, coverage },
    bandwidth: [], summary, downloadPeers: [], uploadPeers: [], content: [], errors: [],
    comparison: { startUtc: '2026-09-06T00:00:00Z', endUtc: '2026-09-07T00:00:00Z', coverage, summary: { ...summary, shareRatio: previous } },
  };
}

describe('dashboard presentation', () => {
  it('does not present unavailable accounting placeholders as measured zeros', () => {
    const data = analytics(null, null);
    expect(availableDashboardSummary(data)?.downloadedBytes).toBe(0);
    data.range.coverage.state = 'Unavailable';
    expect(availableDashboardSummary(data)).toBeNull();
    expect(availableDashboardSummary(null)).toBeNull();
    data.range.coverage.state = 'Degraded';
    expect(availableDashboardSummary(data)?.downloadedBytes).toBe(0);
  });
  it('uses local time-only labels for a day and weekdays for a week', () => {
    const timestamp = new Date(2026, 8, 21, 14, 5).toISOString();
    expect(dashboardChartLabel(timestamp, '24h', 'en-US')).toBe('14:05');
    expect(dashboardChartLabel(timestamp, '7d', 'en-US')).toBe('Mon');
    expect(dashboardChartLabel(new Date(2026, 8, 21, 0, 0).toISOString(), '24h', 'en-US')).toBe('00:00');
  });
  it('labels a current axis endpoint Now without mislabeling retained history', () => {
    const now = Date.parse('2026-09-23T12:00:00Z');
    const buckets = [
      { startUtc: '2026-09-23T08:00:00Z' },
      { startUtc: '2026-09-23T10:00:00Z' },
    ];
    expect(dashboardChartLabels(buckets, '24h', '2026-09-23T12:00:00Z', 7_200, now, 'en-US'))
      .toEqual([dashboardChartLabel(buckets[0]!.startUtc, '24h', 'en-US'), 'Now']);
    expect(dashboardChartLabels(buckets, '24h', '2026-09-22T12:00:00Z', 7_200, now, 'en-US')[1])
      .not.toBe('Now');
    expect(dashboardChartLabels(buckets, '7d', '2026-09-23T12:00:00Z', 7_200, now, 'en-US')[1])
      .not.toBe('Now');
    expect(dashboardChartLabels(buckets, '1y', '2026-09-20T12:00:00Z', 2_102_400, now, 'en-US')[1])
      .not.toBe('Now');
    expect(dashboardChartLabels([], '24h', '2026-09-23T12:00:00Z', 7_200, now))
      .toEqual([]);
  });
  it('derives longer-range calendar labels from real bucket dates', () => {
    const axis = (dates: string[], range: string, now = Date.parse('2026-09-23T12:00:00Z')) =>
      dashboardChartLabels(dates.map(date => ({ startUtc: `${date}T12:00:00Z` })),
        range, '2026-09-23T12:00:00Z', 86_400, now, 'en-US');
    expect(axis(['2026-05-08', '2026-05-23', '2026-06-07', '2026-06-22',
      '2026-07-07', '2026-07-22', '2026-08-06'], '90d'))
      .toEqual(['May', 'Late May', 'Jun', 'Late Jun', 'Jul', 'Late Jul', 'Aug']);
    expect(axis(['2024-05-01', '2024-11-01', '2025-04-01', '2025-11-01',
      '2026-02-01', '2026-07-01', '2026-09-23'], 'all'))
      .toEqual(['2024', 'Late 2024', '2025', 'Late 2025', 'Early 2026', 'Summer', 'Now']);
    expect(axis(['2026-09-19', '2026-09-20'], 'all', Date.parse('2026-10-23T12:00:00Z')))
      .toEqual(['Sep 19', 'Sep 20']);
  });
  it('uses decimal transfer-rate units and precision', () => {
    expect(dashboardTransferRate(0)).toBe('0 B/s');
    expect(dashboardTransferRate(12_500)).toBe('13 KB/s');
    expect(dashboardTransferRate(1_000_000)).toBe('1.00 MB/s');
    expect(dashboardTransferRate(12_345_678)).toBe('12.35 MB/s');
  });
  it('displays byte accounting in decimal MB/s using the actual bucket duration', () => {
    expect(bandwidthMegabytesPerSecond('120000000', '2026-09-08T00:00:00Z', '2026-09-08T00:01:00Z')).toBe(2);
    expect(bandwidthMegabytesPerSecond(30_000_000, '2026-09-08T00:00:00Z', '2026-09-08T00:00:15Z')).toBe(2);
  });

  it('keeps chart ticks aligned with the same scale used by high-rate paths', () => {
    expect(dashboardBandwidthAxis([], [])).toEqual({ maxMbps: 12, labels: [12, 8, 4, 0] });
    expect(dashboardBandwidthAxis([2, 12], [8])).toEqual({ maxMbps: 12, labels: [12, 8, 4, 0] });
    expect(dashboardBandwidthAxis([16], [8])).toEqual({ maxMbps: 18, labels: [18, 12, 6, 0] });
    expect(dashboardBandwidthAxis([120], [80])).toEqual({ maxMbps: 120, labels: [120, 80, 40, 0] });
    expect(dashboardBandwidthAxis([Number.NaN], [0])).toEqual({ maxMbps: 12, labels: [12, 8, 4, 0] });
  });

  it('distinguishes unknown ratios and incomplete coverage from a real zero', () => {
    expect(shareRatioDelta(analytics(2, 0))).toBe('+2.00');
    expect(shareRatioDelta(analytics(2, null))).toBe('—');
    expect(shareRatioDelta(analytics(null, 2))).toBe('—');
    const partial = analytics(2, 1);
    partial.comparison!.coverage.isComplete = false;
    expect(shareRatioDelta(partial)).toBe('—');
  });
});
