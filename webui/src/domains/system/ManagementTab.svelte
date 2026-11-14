<script lang="ts">
  import { DaemonHttpClient } from '../../api/http';
  import type { Schema } from '../../api/types';
  import { formatCount } from '../../app/format';
  import Modal from '../../components/Modal.svelte';
  import Notice from '../../components/Notice.svelte';

  const actions = [
    { id: 'searches', title: 'Delete all completed searches', category: 'Searches' },
    { id: 'jobs', title: 'Delete all completed download jobs', category: 'Downloads' },
    { id: 'downloads', title: 'Delete all completed downloads', direction: 'download' },
    { id: 'uploads', title: 'Delete all completed uploads', direction: 'upload' },
  ] as const;
  type Action = typeof actions[number];
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

<section class="management-actions" aria-label="Management actions">
  {#if result}<Notice ondismiss={() => result = null}>{result}</Notice>{/if}
  <ul>
    {#each actions as action}
      <li>
        <span id={`action-${action.id}`}>{action.title}</span>
        <button class="auth-button" onclick={() => { confirming = action; error = null; }} aria-describedby={`action-${action.id}`}>Run</button>
      </li>
    {/each}
  </ul>
</section>

{#if confirming}
  <Modal title={`${confirming.title}?`} compact locked={pending} onclose={() => confirming = null}>
    <div class="confirmation">
      <p>This includes failed, cancelled and interrupted entries, across all pages. Active work is kept.</p>
      <p>{'category' in confirming ? 'Job details and search results cannot be restored. Submissions with active jobs will be kept.' : 'Transfers will be removed from the list. Statistics will be kept.'} Downloaded files are untouched.</p>
      {#if error}<Notice variant="unavailable">{error}</Notice>{/if}
      <button class="auth-button" disabled={pending} onclick={() => void remove()} aria-keyshortcuts="Control+Enter">{pending ? 'Running…' : 'Run'}</button>
    </div>
  </Modal>
{/if}

<style>
  .management-actions { border: 1px solid var(--border); border-radius: 14px; background: var(--surface); padding: 0 18px; }
  ul { list-style: none; padding: 0; margin: 0; }
  li { display: flex; align-items: center; justify-content: space-between; gap: 16px; padding: 14px 0; border-bottom: 1px solid var(--border); font-size: .8125rem; }
  li:last-child { border-bottom: 0; }
  li button { flex-shrink: 0; }
  .confirmation { display: grid; gap: 14px; }
  .confirmation p { margin: 0; color: var(--muted); font-size: .8125rem; line-height: 1.5; }
  .confirmation button { justify-self: end; }
</style>
