<script lang="ts">
  import type { Schema } from '../../api/types';
  import Modal from '../../components/Modal.svelte';

  let { configuration, epoch }: {
    configuration: Schema<'ConfigurationStatusDto'> | null | undefined;
    epoch: string | null;
  } = $props();
  const restartDismissalKey = 'sockseek.dismissedRestart';
  let dismissedFailure = $state(false);
  let dismissedRestart = $state(readDismissedRestart());

  function readDismissedRestart(): string | null {
    try { return sessionStorage.getItem(restartDismissalKey); }
    catch { return null; }
  }
  function rememberRestart(value: string | null): void {
    dismissedRestart = value;
    try {
      if (value) sessionStorage.setItem(restartDismissalKey, value);
      else sessionStorage.removeItem(restartDismissalKey);
    } catch { /* Keep in-memory dismissal if browser storage is disabled. */ }
  }

  // Read the saved dismissal before rendering to avoid a modal flash on reload.
  // The live epoch changes on daemon restart. Errors deliberately remain unsaved.
  $effect(() => {
    if (!configuration) return;
    if (!configuration.reloadFailed) dismissedFailure = false;
    if (!configuration.restartRequired && dismissedRestart !== null) rememberRestart(null);
  });
  const visible = $derived(configuration && (
    (configuration.reloadFailed && !dismissedFailure)
    || (configuration.restartRequired && epoch && dismissedRestart !== epoch)));

  function dismiss(): void {
    dismissedFailure = !!configuration?.reloadFailed;
    if (configuration?.restartRequired && epoch) rememberRestart(epoch);
  }
</script>

{#if visible && configuration}
  <Modal title={configuration.reloadFailed ? 'Configuration reload failed' : 'Daemon restart required'} compact onclose={dismiss}>
    <div class="configuration-message">
      {#if configuration.reloadFailed}
        <p role="alert">{configuration.errorLine ? `Line ${configuration.errorLine}: ` : ''}{configuration.error ?? 'Configuration could not be reloaded.'}</p>
        <p>{configuration.watching
          ? 'Correct the configuration file to retry automatically.'
          : 'Configuration watching stopped. Restart the daemon to resume automatic reloading.'}</p>
      {/if}
      {#if configuration.restartRequired}
        <p>Some configuration changes require a daemon restart. Those settings will keep their current values until then.</p>
      {/if}
    </div>
  </Modal>
{/if}

<style>
  .configuration-message { display: grid; gap: 14px; }
  p { margin: 0; color: var(--muted); font-size: .8125rem; line-height: 1.5; overflow-wrap: anywhere; }
</style>
