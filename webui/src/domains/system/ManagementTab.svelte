<script lang="ts">
  import { DaemonHttpClient } from '../../api/http';
  import type { Schema } from '../../api/types';
  import { formatCount } from '../../app/format';
  import Modal from '../../components/Modal.svelte';
  import Notice from '../../components/Notice.svelte';
  import Icon from '../../components/Icon.svelte';

  const groups = [
    { id: 'jobs', title: 'Jobs & searches', actions: [
      { id: 'searches', title: 'Delete all completed searches', category: 'Searches' },
      { id: 'jobs', title: 'Delete all completed download jobs', category: 'Downloads' },
    ] },
    { id: 'transfers', title: 'Transfer history', actions: [
      { id: 'downloads', title: 'Delete all completed downloads', direction: 'download' },
      { id: 'uploads', title: 'Delete all completed uploads', direction: 'upload' },
    ] },
  ] as const;
  type Action = typeof groups[number]['actions'][number];
  let confirming = $state<Action | null>(null);
  let pending = $state(false);
  let error = $state<string | null>(null);
  let result = $state<string | null>(null);
  const http = new DaemonHttpClient();

  async function remove(): Promise<void> {
    if (!confirming || pending) return;
    const action = confirming;
    pending = true;
    error = null;
    result = null;
    try {
      if ('category' in action) {
        const receipt = await http.post<Schema<'DeleteCompletedJobsResponseDto'>>('/jobs/delete-completed', { category: action.category });
        result = `${formatCount(receipt.deletedJobs)} jobs deleted from ${formatCount(receipt.deletedSubmissions)} submissions.${receipt.skippedSubmissions ? ` ${formatCount(receipt.skippedSubmissions)} submissions kept because they are still active.` : ''}`;
      } else {
        const receipt = await http.post<Schema<'TransferCommandReceiptDto'>>('/transfers/remove', { direction: action.direction });
        const active = receipt.reasons.filter(reason => reason.reason === 'nonterminal').reduce((sum, reason) => sum + Number(reason.count), 0);
        const failed = Number(receipt.failedCount) + Number(receipt.rejectedCount) - active;
        result = `${formatCount(receipt.succeededCount)} ${action.direction}s removed.${active ? ` ${formatCount(active)} active transfers kept.` : ''}${failed ? ` ${formatCount(failed)} could not be removed.` : ''}`;
      }
      confirming = null;
    } catch (cause) { error = cause instanceof Error ? cause.message : 'Cleanup failed.'; }
    finally { pending = false; }
  }
</script>

<section class="panel management-actions" aria-label="Management actions">
  <header>
    <h2>Cleanup</h2>
  </header>
  {#if result}<Notice ondismiss={() => result = null}>{result}</Notice>{/if}
  <div class="management-groups">
    {#each groups as group}
      <section aria-labelledby={`management-group-${group.id}`}>
        <h3 id={`management-group-${group.id}`}>{group.title}</h3>
        <div class="management-action-buttons">
          {#each group.actions as action}
            <button class="button danger management-action" data-testid={`management-${action.id}`} onclick={() => { confirming = action; error = null; }}>
              <Icon name="trash" /><span>{action.title}</span>
            </button>
          {/each}
        </div>
      </section>
    {/each}
  </div>
</section>

{#if confirming}
  <Modal title={`${confirming.title}?`} compact locked={pending} onclose={() => confirming = null}>
    <div class="confirmation">
      <p>Includes failed, cancelled and interrupted entries.{#if 'direction' in confirming}{' '}Files and statistics are kept.{/if}</p>
      {#if error}<Notice variant="unavailable">{error}</Notice>{/if}
      <div class="confirmation-actions">
        <button class="button auth-button" disabled={pending} onclick={() => confirming = null}>Cancel</button>
        <button class="button primary danger auth-button" data-testid="management-confirm" disabled={pending} onclick={() => void remove()} aria-keyshortcuts="Control+Enter">{pending ? 'Working…' : 'category' in confirming ? 'Delete' : 'Remove'}</button>
      </div>
    </div>
  </Modal>
{/if}

<style>
  .management-actions { display: grid; gap: 20px; width: 100%; max-width: 42rem; padding: 20px; }
  h2, h3 { margin: 0; font-weight: 600; }
  h2 { font-size: 1rem; }
  h3 { margin-bottom: 12px; font-size: .8125rem; }
  .management-groups { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 24px; }
  .management-groups > section { min-width: 0; }
  .management-action-buttons { display: grid; gap: 10px; }
  .management-action { display: flex; align-items: center; gap: 10px; min-height: 40px; padding: 10px 14px; text-align: left; font-size: .8125rem; }
  .management-action span { min-width: 0; }
  .management-action :global(.app-icon) { --app-icon-size: 16px; flex-shrink: 0; }
  .confirmation { display: grid; gap: 14px; }
  .confirmation p { margin: 0; color: var(--muted); font-size: .8125rem; line-height: 1.5; }
  .confirmation-actions { display: flex; justify-content: flex-end; gap: 8px; }
  @media (max-width: 600px) { .management-groups { grid-template-columns: minmax(0, 1fr); } }
</style>
