<script lang="ts">
  /**
   * A profile picture that opens full size, or the same frame without one
   * (optionally labelled, e.g. "No picture"). Size it with --profile-picture-size.
   */
  let { src = null, username = null, emptyLabel = '', emptyTitle }: {
    src?: string | null; username?: string | null; emptyLabel?: string; emptyTitle?: string;
  } = $props();
  let name = $derived(username ? `${username} profile picture` : 'Profile picture');
  let open = $state(false);
</script>

<svelte:window onkeydown={(event) => { if (event.key === 'Escape') open = false; }} />

{#if !src}
  <span class="user-profile-picture user-profile-picture-empty" title={emptyTitle} aria-hidden={!emptyLabel}>{emptyLabel}</span>
{:else}
  <button type="button" class="user-profile-picture-button"
    aria-label={username ? `View ${username}'s profile picture` : 'View profile picture'} onclick={() => (open = true)}>
    <img class="user-profile-picture" {src} alt="" />
  </button>
{/if}

{#if open && src}
  <div class="profile-picture-lightbox">
    <button type="button" class="profile-picture-lightbox-backdrop"
      aria-label="Close profile picture" onclick={() => (open = false)}></button>
    <div class="profile-picture-lightbox-dialog" role="dialog" aria-modal="true" aria-label={name}>
      <img {src} alt={name}/>
      <button type="button" class="profile-picture-lightbox-close"
        aria-label="Close profile picture" onclick={() => (open = false)}>×</button>
    </div>
  </div>
{/if}
