<script lang="ts">
  import { onMount } from 'svelte';
  import { DaemonHttpClient } from '../../api/http';
  import type { Schema } from '../../api/types';
  import LoadingIndicator from '../../components/LoadingIndicator.svelte';
  import Modal from '../../components/Modal.svelte';

  const http = new DaemonHttpClient();
  let keys = $state<Schema<'ApiKeyDto'>[]>([]);
  let loading = $state(true);
  let pending = $state(false);
  let error = $state<string | null>(null);
  let creating = $state(false);
  let name = $state('');
  let created = $state<Schema<'CreatedApiKeyDto'> | null>(null);
  let revoking = $state<Schema<'ApiKeyDto'> | null>(null);
  let copyNotice = $state('');
  onMount(() => {
    const abort = new AbortController();
    void http.get<Schema<'ApiKeyDto'>[]>('/auth/keys', abort.signal)
      .then(result => { keys = result; })
      .catch(cause => { if (!abort.signal.aborted) error = message(cause); })
      .finally(() => { loading = false; });
    return () => abort.abort();
  });

  function message(cause: unknown): string { return cause instanceof Error ? cause.message : 'Request failed.'; }

  async function create(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    if (pending) return;
    pending = true;
    error = null;
    try {
      created = await http.post<Schema<'CreatedApiKeyDto'>>('/auth/keys', { name });
      keys = [...keys, created.key];
      copyNotice = '';
    } catch (cause) { error = message(cause); }
    finally { pending = false; }
  }

  async function revoke(): Promise<void> {
    if (!revoking || pending) return;
    pending = true;
    error = null;
    try {
      await http.delete(`/auth/keys/${revoking.id}`);
      keys = keys.filter(key => key.id !== revoking!.id);
      revoking = null;
    } catch (cause) { error = message(cause); }
    finally { pending = false; }
  }

  async function copy(): Promise<void> {
    try { await navigator.clipboard.writeText(created!.secret); copyNotice = 'Copied'; }
    catch { copyNotice = 'Select the key and copy it manually.'; }
  }

</script>

  <section class="settings-section" aria-labelledby="keys-heading">
    <div class="section-heading key-heading">
      <div><h2 id="keys-heading">API keys</h2><p>Connect the remote CLI or other integrations. Each key grants full access.</p></div>
      <button class="auth-button primary" disabled={loading || pending} onclick={() => { name = ''; created = null; error = null; creating = true; }}>Create API key</button>
    </div>
    {#if error && !creating && !revoking}<p class="error" role="alert">{error}</p>{/if}
    {#if loading}<LoadingIndicator />
    {:else if keys.length}
      <ul aria-label="API keys">
        {#each keys as key (key.id)}
          <li><div><strong>{key.name}</strong><small>{new Date(key.createdAt).toLocaleString()}</small></div>
            <button class="auth-button" disabled={pending} aria-label={`Revoke ${key.name}`} onclick={() => { error = null; revoking = key; }}>Revoke</button></li>
        {/each}
      </ul>
    {:else if !error}<p class="keys-empty">No API keys yet.</p>{/if}
  </section>

{#if creating}
  <Modal title={created ? 'Save your API key' : 'Create API key'} compact locked={pending} onclose={() => { creating = false; created = null; error = null; }}>
    {#if created}
      <div class="key-secret">
        <p>This key is shown only once. Save it before closing this dialog.</p>
        <label>API key<input class="auth-input" readonly value={created.secret} onfocus={event => event.currentTarget.select()} /></label>
        <div class="key-actions"><button class="auth-button" onclick={() => void copy()}>Copy key</button><span role="status">{copyNotice}</span></div>
      </div>
    {:else}
      <form onsubmit={create}>
        <label>Name<input class="auth-input" required maxlength="200" placeholder="Laptop CLI" bind:value={name} /></label>
        {#if error}<p class="error" role="alert">{error}</p>{/if}
        <button class="auth-button primary" type="submit" disabled={pending || !name.trim()} aria-keyshortcuts="Control+Enter">Create key</button>
      </form>
    {/if}
  </Modal>
{/if}
{#if revoking}
  <Modal title="Revoke API key" compact locked={pending} onclose={() => { revoking = null; error = null; }}>
    <div class="key-secret">
      <p>Revoke “{revoking.name}”? Its clients will lose access.</p>
      {#if error}<p class="error" role="alert">{error}</p>{/if}
      <button class="auth-button" disabled={pending} onclick={() => void revoke()} aria-keyshortcuts="Control+Enter">Revoke key</button>
    </div>
  </Modal>
{/if}

<style>
  .settings-section { border: 1px solid var(--border); border-radius: 14px; background: var(--surface); padding: 18px; margin-bottom: 14px; }
  .section-heading, li { display: flex; align-items: center; justify-content: space-between; gap: 16px; }
  .section-heading > div, li > div { min-width: 0; overflow-wrap: anywhere; }
  .key-heading { flex-wrap: wrap; }
  .key-heading > div { flex: 1 1 280px; }
  h2 { margin: 0; font-size: .9rem; font-weight: 600; }
  p { margin: 4px 0 0; color: var(--muted); font-size: .78rem; line-height: 1.5; }
  button { flex-shrink: 0; }
  ul { list-style: none; padding: 0; margin: 14px 0 0; }
  li { padding: 12px 0; border-top: 1px solid var(--border); font-size: .8125rem; }
  li:last-child { padding-bottom: 0; }
  strong { font-weight: 550; }
  small { display: block; margin-top: 4px; color: var(--muted); font-size: .72rem; }
  .keys-empty { border-top: 1px solid var(--border); margin-top: 14px; padding-top: 14px; }
  form, .key-secret { display: grid; gap: 14px; font-size: .8125rem; }
  .key-secret p { margin: 0; }
  form > button, .key-secret > button { justify-self: end; }
  .key-actions { display: flex; align-items: center; flex-wrap: wrap; gap: 10px; color: var(--muted); }
  label { display: grid; gap: 7px; font-size: .78rem; }
  input { width: 100%; min-height: 36px; padding: 8px 10px; font-size: .875rem; }
  .error { margin: 0; color: var(--danger); }
  .settings-section > .error { margin-top: 14px; }
  @media (max-width: 600px) {
    input { font-size: 1rem; }
  }
</style>
