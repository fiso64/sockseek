import type { DashboardAnalyticsDto } from '../../api/types';

export function availableDashboardSummary(data: DashboardAnalyticsDto | null) {
  return data?.range.coverage.state === 'Unavailable' ? null : data?.summary ?? null;
}

export function dashboardChartLabel(timestamp: string, range: string, locale?: string): string {
  const date = new Date(timestamp);
  if (range === '24h') return new Intl.DateTimeFormat(locale, {
    hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
  }).format(date);
  if (range === '7d') return new Intl.DateTimeFormat(locale, { weekday: 'short' }).format(date);
  return new Intl.DateTimeFormat(locale, {
    month: 'short', day: range === '1y' || range === 'all' ? undefined : 'numeric',
    year: range === 'all' ? 'numeric' : undefined,
  }).format(date);
}

export function dashboardChartLabels(
  buckets: ReadonlyArray<{ startUtc: string }>,
  range: string,
  rangeEndUtc: string,
  bucketSeconds: number,
  now = Date.now(),
  locale?: string,
): string[] {
  if (!buckets.length) return [];
  const count = Math.min(7, buckets.length);
  const indexes = Array.from({ length: count }, (_, index) =>
    Math.round(index * (buckets.length - 1) / Math.max(1, count - 1)));
  const dates = indexes.map(index => new Date(buckets[index]!.startUtc));
  const labels = dates.map(date => dashboardChartLabel(date.toISOString(), range, locale));
  if (range === '90d') {
    const month = new Intl.DateTimeFormat(locale, { month: 'short' });
    for (let index = 0; index < dates.length; index += 1) {
      const current = dates[index]!;
      const previous = dates[index - 1];
      const repeatedMonth = previous && current.getMonth() === previous.getMonth()
        && current.getFullYear() === previous.getFullYear();
      labels[index] = `${repeatedMonth ? 'Late ' : ''}${month.format(current)}`;
    }
  } else if (range === 'all') {
    const spanDays = (dates.at(-1)!.getTime() - dates[0]!.getTime()) / 86_400_000;
    if (spanDays < 90) {
      for (let index = 0; index < dates.length; index += 1)
        labels[index] = dashboardChartLabel(dates[index]!.toISOString(), '30d', locale);
    } else if (spanDays < 365) {
      const month = new Intl.DateTimeFormat(locale, { month: 'short' });
      for (let index = 0; index < dates.length; index += 1)
        labels[index] = month.format(dates[index]!);
    } else {
      const latestYear = dates.at(-1)!.getFullYear();
      for (let index = 0; index < dates.length; index += 1) {
        const date = dates[index]!;
        const year = date.getFullYear();
        if (index === 0 || dates[index - 1]!.getFullYear() !== year) {
          labels[index] = index > 0 && date.getMonth() < 3 ? `Early ${year}` : String(year);
        } else if (date.getMonth() < 4) {
          labels[index] = `Early ${year}`;
        } else if (date.getMonth() < 8) {
          labels[index] = year === latestYear ? 'Summer' : `Summer ${year}`;
        } else {
          labels[index] = `Late ${year}`;
        }
      }
    }
  }
  const end = Date.parse(rangeEndUtc);
  if ((range === '24h' || range === '1y' || range === 'all')
    && Number.isFinite(end) && Number.isFinite(bucketSeconds) && bucketSeconds > 0
    && Math.abs(now - end) <= Math.min(bucketSeconds * 1_000, 5 * 60_000)) {
    labels[labels.length - 1] = 'Now';
  }
  return labels;
}

/** Match the prototype's decimal transfer-rate units and precision. */
export function dashboardTransferRate(bytesPerSecond: number): string {
  if (bytesPerSecond <= 0) return '0 B/s';
  if (bytesPerSecond >= 1_000_000) return `${(bytesPerSecond / 1_000_000).toFixed(2)} MB/s`;
  return `${Math.round(bytesPerSecond / 1_000)} KB/s`;
}

/** One byte scale for both directions, with three evenly spaced intervals. */
export function dashboardByteAxis(
  downloads: readonly number[], uploads: readonly number[],
): { maxBytes: number; labels: readonly number[] } {
  let observed = 0;
  for (const value of downloads) if (Number.isFinite(value) && value > observed) observed = value;
  for (const value of uploads) if (Number.isFinite(value) && value > observed) observed = value;
  const magnitude = 10 ** Math.max(0, Math.floor(Math.log10(Math.max(1, observed / 3))));
  const step = Math.max(1, Math.ceil(observed / 3 / magnitude)) * magnitude;
  return { maxBytes: step * 3, labels: [step * 3, step * 2, step, 0] };
}

export function shareRatioDelta(data: DashboardAnalyticsDto | null): string {
  const current = data?.summary.shareRatio;
  const previous = data?.comparison?.summary.shareRatio;
  if (current == null || previous == null || !data?.comparison?.coverage.isComplete || !data.range.coverage.isComplete) return '—';
  const delta = Number(current) - Number(previous);
  if (!Number.isFinite(delta)) return '—';
  return `${delta >= 0 ? '+' : ''}${delta.toFixed(2)}`;
}
