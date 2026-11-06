<script lang="ts">
  import { onMount } from 'svelte';
  import App from './App.svelte';
  import LoadingIndicator from './components/LoadingIndicator.svelte';
  import { browserSession } from './api/session.svelte';

  let loading = $state(true);
  let pending = $state(false);
  let error = $state<string | null>(null);
  let username = $state('');
  let password = $state('');

  async function bootstrap(): Promise<void> {
    loading = true;
    error = null;
    try { await browserSession.refresh(); }
    catch (cause) { error = failureMessage(cause); }
    finally { loading = false; }
  }

  onMount(() => {
    void bootstrap();
    const check = () => {
      if (!document.hidden && browserSession.data?.authenticationEnabled)
        void browserSession.refresh().catch(() => { /* The app reports connectivity failures. */ });
    };
    document.addEventListener('visibilitychange', check);
    return () => document.removeEventListener('visibilitychange', check);
  });

  async function login(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    if (pending) return;
    pending = true;
    error = null;
    try { await browserSession.login(username, password); }
    catch (cause) { error = failureMessage(cause); }
    finally { pending = false; password = ''; }
  }

  function failureMessage(cause: unknown): string {
    return cause instanceof TypeError ? 'Unable to reach the server. Check your connection and try again.'
      : cause instanceof Error ? cause.message : 'Unable to contact Sockseek.';
  }
</script>

{#if loading}
  <main class="session-screen"><LoadingIndicator label="Checking session" /></main>
{:else if browserSession.data?.authenticated}
  <App />
{:else}
  <main class="session-screen login-screen">
    <section class="login-card" aria-label="Sockseek login">
      <header>
        <div class="brand">
          <div class="brand-mark" aria-hidden="true">S</div>
          <div class="brand-copy"><strong>sockseek</strong></div>
        </div>
        {#if !browserSession.data}<h1>Connection unavailable</h1>{/if}
      </header>
      {#if browserSession.data}
        {#if browserSession.notice}<p class="login-notice" role="status">{browserSession.notice}</p>{/if}
        <form class="config-stack" onsubmit={login} aria-label="Log in to Sockseek" aria-busy={pending}>
          <label>Username<input name="username" autocomplete="username" autocapitalize="none" spellcheck="false" required readonly={pending} bind:value={username} /></label>
          <label>Password<input name="password" type="password" autocomplete="current-password" required readonly={pending} bind:value={password} /></label>
          {#if error}<p class="login-error" role="alert">{error}</p>{/if}
          <button class="auth-button primary" type="submit" disabled={pending}>{pending ? 'Logging in…' : 'Log in'}</button>
        </form>
      {:else}
        {#if error}<p class="login-error" role="alert">{error}</p>{/if}
        <button class="auth-button" type="button" onclick={() => void bootstrap()}>Retry connection</button>
      {/if}
    </section>
  </main>
{/if}

<style>
  .session-screen { min-height: 100dvh; display: grid; place-items: center; padding: 24px 20px; background: var(--bg); }
  .login-screen {
    position: relative;
    isolation: isolate;
    background-image:
      radial-gradient(ellipse 70% 85% at 28% 28%, color-mix(in srgb, var(--accent) 16%, transparent), transparent 75%),
      radial-gradient(ellipse 65% 75% at 76% 72%, rgb(111 104 190 / 12%), transparent 75%),
      radial-gradient(ellipse 55% 45% at 58% 42%, rgb(78 143 166 / 7%), transparent 80%);
  }
  .login-screen::before {
    content: '';
    position: absolute;
    inset: 0;
    pointer-events: none;
    opacity: .045;
    /* A small, static tile adds grain without animating or filtering the page. */
    background-image: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='180' height='180'%3E%3Cfilter id='grain'%3E%3CfeTurbulence type='fractalNoise' baseFrequency='.85' numOctaves='3' stitchTiles='stitch'/%3E%3CfeColorMatrix type='saturate' values='0'/%3E%3C/filter%3E%3Cpath fill='%23808080' filter='url(%23grain)' d='M0 0h180v180H0z'/%3E%3C/svg%3E");
  }
  .login-card { position: relative; width: min(100%, 360px); display: grid; gap: 20px; padding: 24px; border: 1px solid var(--border); border-radius: 14px; background: var(--surface); }
  header { display: grid; gap: 20px; }
  .brand { padding: 0; }
  form { gap: 16px; }
  h1, p { margin: 0; }
  h1 { font-size: 1.08rem; font-weight: 650; }
  .config-stack input { min-height: 36px; padding: 8px 10px; }
  p { color: var(--muted); font-size: .78rem; line-height: 1.5; overflow-wrap: anywhere; }
  .login-notice { padding: 12px; border: 1px solid var(--border); border-radius: 8px; background: var(--surface-subtle); }
  .login-error { padding: 12px; border: 1px solid color-mix(in srgb, var(--danger) 35%, var(--border)); border-radius: 8px; background: color-mix(in srgb, var(--danger) 7%, var(--surface)); color: var(--danger); }
  @media (max-width: 600px) {
    .config-stack input { min-height: 44px; font-size: 1rem; }
    button { min-height: 44px; }
  }
</style>
