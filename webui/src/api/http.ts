import type { ApiErrorDto, Schema } from './types';
import { browserSession } from './session.svelte';

export class DaemonApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly code: string | null = null,
  ) {
    super(message);
    this.name = 'DaemonApiError';
  }
}

function isJson(response: Response): boolean {
  return response.headers.get('content-type')?.toLowerCase().includes('json') ?? false;
}

export function queryString(values: Record<string, string | number | boolean | null | undefined>): string {
  const query = new URLSearchParams();
  for (const [name, value] of Object.entries(values)) {
    if (value !== null && value !== undefined) query.set(name, String(value));
  }
  const serialized = query.toString();
  return serialized ? `?${serialized}` : '';
}

export class DaemonHttpClient {
  constructor(private readonly basePath = '/api') {}

  get<T>(path: string, signal?: AbortSignal): Promise<T> {
    return this.request<T>(path, { method: 'GET', signal });
  }

  post<T>(path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
    return this.request<T>(path, {
      method: 'POST',
      body: body === undefined ? undefined : JSON.stringify(body),
      headers: body === undefined ? undefined : { 'content-type': 'application/json' },
      signal,
    });
  }

  put<T>(path: string, body: unknown, signal?: AbortSignal): Promise<T> {
    return this.request<T>(path, {
      method: 'PUT',
      body: JSON.stringify(body),
      headers: { 'content-type': 'application/json' },
      signal,
    });
  }

  postBlob<T>(path: string, body: Blob, signal?: AbortSignal): Promise<T> {
    return this.request<T>(path, {
      method: 'POST',
      body,
      headers: { 'content-type': 'application/octet-stream' },
      signal,
    });
  }

  delete<T>(path: string, signal?: AbortSignal): Promise<T> {
    return this.request<T>(path, { method: 'DELETE', signal });
  }

  async getCursorPage<T>(path: string, signal?: AbortSignal): Promise<{
    items: T; nextCursor: string | null; readPosition?: Schema<'StateStreamPositionDto'>;
  }> {
    const response = await this.send(path, { method: 'GET', signal });
    const epoch = response.headers.get('x-sockseek-read-epoch');
    const sequence = response.headers.get('x-sockseek-read-sequence');
    return {
      items: await this.read<T>(response),
      nextCursor: response.headers.get('x-next-cursor'),
      readPosition: epoch && sequence ? { epoch, sequence } : undefined,
    };
  }

  private async request<T>(path: string, init: RequestInit): Promise<T> {
    const response = await this.send(path, init);
    return this.read<T>(response);
  }

  private async send(path: string, init: RequestInit): Promise<Response> {
    const sessionSignal = browserSession.requests.signal;
    const response = await fetch(`${this.basePath}${path}`, {
      ...init, credentials: 'same-origin',
      headers: browserSession.headers(init.headers, init.method),
      signal: init.signal ? AbortSignal.any([init.signal, sessionSignal]) : sessionSignal,
    });
    if (response.status === 401 && !sessionSignal.aborted && browserSession.data?.authenticated) browserSession.invalidate();
    if (!response.ok) {
      let error: Partial<ApiErrorDto> | null = null;
      if (isJson(response)) {
        try {
          error = await response.json() as Partial<ApiErrorDto>;
        } catch {
          // A malformed error response is still reported with HTTP context.
        }
      }
      throw new DaemonApiError(
        error?.error || `${response.status} ${response.statusText}`,
        response.status,
        error?.code ?? null,
      );
    }
    return response;
  }

  private async read<T>(response: Response): Promise<T> {
    if (response.status === 204 || response.headers.get('content-length') === '0') {
      return undefined as T;
    }
    if (!isJson(response)) {
      throw new DaemonApiError('The daemon returned a non-JSON response.', response.status);
    }
    return await response.json() as T;
  }
}
