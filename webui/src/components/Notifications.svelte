<script lang="ts">
  import { dismiss, notifications } from '../lib/notifications.svelte';
</script>

<div class="app-notifications" aria-live="polite">
  {#each notifications as notification (notification.id)}
    {@const action = notification.action}
    <div class={`popover-panel app-notification ${notification.tone}`} role={notification.tone === 'error' ? 'alert' : 'status'}>
      <!-- The whole bubble is the target: its action, or dismissal when it has none. -->
      <button type="button" class="app-notification-main" data-testid={action?.testId}
        onclick={() => { dismiss(notification.id); action?.run(); }}>
        <span class="resource-state-dot" aria-hidden="true"></span>
        <span class="app-notification-copy">
          <strong>{notification.title}</strong>
          {#each notification.details ?? [] as detail}<small>{detail}</small>{/each}
        </span>
        {#if action}<span class="app-notification-action">{action.label}</span>{/if}
      </button>
      <button type="button" class="app-notification-close" aria-label="Dismiss notification" onclick={() => dismiss(notification.id)}>×</button>
    </div>
  {/each}
</div>
