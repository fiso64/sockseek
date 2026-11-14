import type { Schema } from './types';

/** One browser session boundary. No credential is stored in JavaScript or localStorage. */
class BrowserSession {
  data = $state<Schema<'SessionDto'> | null>(null);
  notice = $state<string | null>(null);
  requests = new AbortController();
  private expiration: ReturnType<typeof setTimeout> | undefined;
  private refreshing: Promise<void> | null = null;
  private generation = 0;

  refresh(): Promise<void> {
    if (this.refreshing) return this.refreshing;
    const pending = this.readSession(this.generation).finally(() => {
      if (this.refreshing === pending) this.refreshing = null;
    });
    return this.refreshing = pending;
  }

  private async readSession(generation: number): Promise<void> {
    const response = await this.request('/session');
    const next = await response.json() as Schema<'SessionDto'>;
    if (generation !== this.generation) return;
    if (this.data?.authenticated && !next.authenticated) this.invalidate();
    this.data = next;
    if (this.requests.signal.aborted && next.authenticated) this.requests = new AbortController();
    clearTimeout(this.expiration);
    if (next.authenticated && next.expiresAt) this.scheduleExpiration(next.expiresAt);
  }

  private scheduleExpiration(expiresAt: string): void {
    const remaining = Date.parse(expiresAt) - Date.now();
    if (remaining <= 0) { this.invalidate(); return; }
    this.expiration = setTimeout(() => this.scheduleExpiration(expiresAt), Math.min(remaining, 2_147_483_647));
  }

  async login(username: string, password: string): Promise<void> {
    await this.refresh();
    await this.request('/login', { username, password });
    this.generation++;
    this.refreshing = null;
    await this.refresh();
    this.notice = null;
  }

  async logout(): Promise<void> {
    await this.request('/logout', {});
    this.invalidate(null);
    await this.refresh();
  }

  invalidate(notice: string | null = 'Your session expired. Log in again.'): void {
    this.generation++;
    this.refreshing = null;
    this.requests.abort();
    clearTimeout(this.expiration);
    this.notice = notice;
    if (this.data) this.data = { ...this.data, authenticated: false, username: null, expiresAt: null };
  }

  headers(headers?: HeadersInit, method = 'GET'): Headers {
    const result = new Headers(headers);
    if (!['GET', 'HEAD', 'OPTIONS'].includes(method) && this.data?.antiforgeryToken)
      result.set('X-CSRF-Token', this.data.antiforgeryToken);
    return result;
  }

  private async request(path: string, body?: unknown): Promise<Response> {
    const method = body === undefined ? 'GET' : 'POST';
    const response = await fetch(`/api/auth${path}`, {
      method, credentials: 'same-origin', cache: 'no-store',
      headers: this.headers(body === undefined ? undefined : { 'Content-Type': 'application/json' }, method),
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!response.ok) {
      const error = await response.json().catch(() => null) as { error?: string } | null;
      throw new Error(error?.error ?? `Authentication request failed (${response.status}).`);
    }
    return response;
  }
}

export const browserSession = new BrowserSession();
