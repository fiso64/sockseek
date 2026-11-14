<script lang="ts">
  import type { AppIconName } from '../app/icons';

  interface Props {
    name: AppIconName;
    class?: string;
    spin?: boolean;
  }

  let { name, class: className = '', spin = false }: Props = $props();
</script>

<svg class={`app-icon ${className}`} class:spinning={spin} viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
  {#if name === 'search-split' || name === 'search-merge'}
    <rect x="3.5" y="6.5" width="17" height="11" rx="2" />
    {#if name === 'search-split'}<path d="M12 6.5v11" />{/if}
  {:else if name === 'refresh'}
    <path d="M3 12a9 9 0 0 1 9-9c2.5 0 4.9 1 6.7 2.8L21 8M21 3v5h-5M21 12a9 9 0 0 1-9 9c-2.5 0-4.9-1-6.7-2.8L3 16M8 16H3v5" />
  {:else if name === 'help'}
    <circle cx="12" cy="12" r="9" />
    <path d="M9.5 9a2.5 2.5 0 0 1 5 0c0 1.8-2.5 2-2.5 4" />
    <path d="M12 16.5h.01" />
  {:else if name === 'lock'}
    <rect x="5.5" y="10" width="13" height="10" rx="2" />
    <path d="M8 10V7a4 4 0 0 1 8 0v3M12 14v2" />
  {:else if name === 'dashboard'}
    <rect x="4" y="4" width="6" height="6" rx="1" />
    <rect x="14" y="4" width="6" height="6" rx="1" />
    <rect x="4" y="14" width="6" height="6" rx="1" />
    <rect x="14" y="14" width="6" height="6" rx="1" />
  {:else if name === 'jobs'}
    <rect x="5" y="4" width="14" height="16" rx="2" />
    <path d="M8.5 8h7M8.5 12h7M8.5 16h4.5" />
  {:else if name === 'search'}
    <circle cx="10.5" cy="10.5" r="5.5" />
    <path d="m15 15 4.5 4.5" />
  {:else if name === 'user'}
    <circle cx="12" cy="8.5" r="3.2" />
    <path d="M5.8 20a6.2 6.2 0 0 1 12.4 0" />
  {:else if name === 'folder'}
    <path d="M3.5 7h6l1.8 2h9.2v8.5A2.5 2.5 0 0 1 18 20H6a2.5 2.5 0 0 1-2.5-2.5z" />
    <path d="M3.5 7V5.5A1.5 1.5 0 0 1 5 4h4l1.5 1.5" />
  {:else if name === 'file'}
    <path d="M7 3.5h6l4 4V20H7z" />
    <path d="M13 3.5V8h4" />
  {:else if name === 'download'}
    <path d="M12 4v11" />
    <path d="m7.5 10.5 4.5 4.5 4.5-4.5" />
    <path d="M5 20h14" />
  {:else if name === 'upload'}
    <path d="M12 20V9" />
    <path d="m7.5 13.5 4.5-4.5 4.5 4.5" />
    <path d="M5 4h14" />
  {:else if name === 'chat'}
    <path d="M5 6.5A3.5 3.5 0 0 1 8.5 3h7A3.5 3.5 0 0 1 19 6.5v5a3.5 3.5 0 0 1-3.5 3.5H11l-4.5 3v-3.2A3.5 3.5 0 0 1 5 11.5z" />
  {:else if name === 'settings'}
    <circle cx="12" cy="12" r="3" />
    <path d="M19 12a7.5 7.5 0 0 0-.1-1l2-1.5-2-3.4-2.5 1a8 8 0 0 0-1.8-1L14.2 3h-4.4l-.4 3.1a8 8 0 0 0-1.8 1l-2.5-1-2 3.4 2 1.5a7.5 7.5 0 0 0 0 2l-2 1.5 2 3.4 2.5-1a8 8 0 0 0 1.8 1l.4 3.1h4.4l.4-3.1a8 8 0 0 0 1.8-1l2.5 1 2-3.4-2-1.5a7.5 7.5 0 0 0 .1-1z" />
  {:else if name === 'log-out'}
    <path d="M15 5h4a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2h-4M15 12H3m4-4-4 4 4 4" />
  {:else if name === 'sliders'}
    <path d="M4 6h4M12 6h8" />
    <circle cx="10" cy="6" r="2" />
    <path d="M4 12h10M18 12h2" />
    <circle cx="16" cy="12" r="2" />
    <path d="M4 18h2M10 18h10" />
    <circle cx="8" cy="18" r="2" />
  {:else if name === 'trash'}
    <path d="M5 7h14" />
    <path d="M9 7V4.5h6V7" />
    <path d="M7 7l.8 12h8.4L17 7" />
    <path d="M10 10.5v5M14 10.5v5" />
  {:else if name === 'check'}
    <path d="m5.5 12.5 4 4 9-9" />
  {:else if name === 'clock'}
    <circle cx="12" cy="12" r="8" />
    <path d="M12 7.5V12l3 2" />
  {:else if name === 'x'}
    <path d="m7.5 7.5 9 9" />
    <path d="m16.5 7.5-9 9" />
  {:else if name === 'more'}
    <circle cx="5" cy="12" r="1" fill="currentColor" stroke="none" />
    <circle cx="12" cy="12" r="1" fill="currentColor" stroke="none" />
    <circle cx="19" cy="12" r="1" fill="currentColor" stroke="none" />
  {:else if name === 'generic'}
    <path d="M6.5 3.5h6l4 4V18H6.5z" />
    <path d="M12.5 3.5V8h4" />
    <circle cx="15.5" cy="15.5" r="3.2" />
    <path d="m18 18 2.2 2.2" />
  {:else if name === 'track'}
    <path d="M9 17.5V6.5l9-2v10.8" />
    <circle cx="6.5" cy="17.5" r="2.5" />
    <circle cx="15.5" cy="15.5" r="2.5" />
  {:else if name === 'album'}
    <rect x="4" y="4" width="16" height="16" rx="2.5" />
    <circle cx="12" cy="12" r="4.2" />
    <circle cx="12" cy="12" r="1" />
  {:else if name === 'song-groups'}
    <path d="M8 17V7l7-1.6v9.7" />
    <circle cx="5.8" cy="17" r="2.2" />
    <circle cx="12.8" cy="15.2" r="2.2" />
    <path d="M17.5 7.5h2M18.5 6.5v2" />
  {:else if name === 'album-groups'}
    <rect x="3.5" y="5.5" width="14" height="14" rx="2.2" />
    <circle cx="10.5" cy="12.5" r="3.3" />
    <path d="M7 3.5h11a2.5 2.5 0 0 1 2.5 2.5v11" />
  {:else if name === 'group-job'}
    <path d="M8 17V7l7-1.6v9.7" />
    <circle cx="5.8" cy="17" r="2.2" />
    <circle cx="12.8" cy="15.2" r="2.2" />
    <path d="M17 4v5M14.5 6.5h5" />
  {:else if name === 'album-groups-job'}
    <rect x="3.5" y="5.5" width="14" height="14" rx="2.2" />
    <circle cx="10.5" cy="12.5" r="3.3" />
    <path d="M7 3.5h11a2.5 2.5 0 0 1 2.5 2.5v11" />
    <path d="M18 3v4M16 5h4" />
  {:else if name === 'extract'}
    <path d="M5 5h6l2 2h6v12H5z" />
    <path d="M12 10v6M9.5 13.5 12 16l2.5-2.5" />
  {:else if name === 'job-list'}
    <rect x="5" y="4" width="14" height="16" rx="2" />
    <path d="M9 8h6M9 12h6M9 16h6" />
    <path d="M7 8h.01M7 12h.01M7 16h.01" />
  {:else if name === 'retrieve-folder'}
    <path d="M3.5 7h6l1.8 2h9.2v8.5A2.5 2.5 0 0 1 18 20H6a2.5 2.5 0 0 1-2.5-2.5z" />
    <path d="M12 12a3.5 3.5 0 1 0 3.1 1.9M16 11v3h-3" />
  {:else if name === 'spotify'}
    <circle cx="12" cy="12" r="9" />
    <path d="M7.2 9.2c3.5-1 7.6-.7 10.2.8" />
    <path d="M7.8 12.3c3-.8 6.5-.5 8.9.8" />
    <path d="M8.5 15.2c2.4-.6 5.1-.4 7.2.7" />
  {:else if name === 'youtube'}
    <rect x="3" y="6.2" width="18" height="11.6" rx="4" />
    <path d="m10 9 5 3-5 3z" fill="currentColor" stroke="none" />
  {:else if name === 'soundcloud'}
    <!-- SoundCloud mark from Simple Icons (CC0): https://simpleicons.org/ -->
    <path d="M23.999 14.165c-.052 1.796-1.612 3.169-3.4 3.169h-8.18a.68.68 0 0 1-.675-.683V7.862a.747.747 0 0 1 .452-.724s.75-.513 2.333-.513a5.364 5.364 0 0 1 2.763.755 5.433 5.433 0 0 1 2.57 3.54c.282-.08.574-.121.868-.12.884 0 1.73.358 2.347.992s.948 1.49.922 2.373ZM10.721 8.421c.247 2.98.427 5.697 0 8.672a.264.264 0 0 1-.53 0c-.395-2.946-.22-5.718 0-8.672a.264.264 0 0 1 .53 0ZM9.072 9.448c.285 2.659.37 4.986-.006 7.655a.277.277 0 0 1-.55 0c-.331-2.63-.256-5.02 0-7.655a.277.277 0 0 1 .556 0Zm-1.663-.257c.27 2.726.39 5.171 0 7.904a.266.266 0 0 1-.532 0c-.38-2.69-.257-5.21 0-7.904a.266.266 0 0 1 .532 0Zm-1.647.77a26.108 26.108 0 0 1-.008 7.147.272.272 0 0 1-.542 0 27.955 27.955 0 0 1 0-7.147.275.275 0 0 1 .55 0Zm-1.67 1.769c.421 1.865.228 3.5-.029 5.388a.257.257 0 0 1-.514 0c-.21-1.858-.398-3.549 0-5.389a.272.272 0 0 1 .543 0Zm-1.655-.273c.388 1.897.26 3.508-.01 5.412-.026.28-.514.283-.54 0-.244-1.878-.347-3.54-.01-5.412a.283.283 0 0 1 .56 0Zm-1.668.911c.4 1.268.257 2.292-.026 3.572a.257.257 0 0 1-.514 0c-.241-1.262-.354-2.312-.023-3.572a.283.283 0 0 1 .563 0Z" fill="currentColor" stroke="none" />
  {:else if name === 'bandcamp'}
    <path d="M8.2 6h12.3l-4.7 12H3.5z" fill="currentColor" stroke="none" />
  {:else if name === 'musicbrainz'}
    <path d="M11 2.7 4.2 6.8v10.4l6.8 4.1z" fill="currentColor" stroke="none" />
    <path d="m13 2.7 6.8 4.1v10.4L13 21.3z" fill="currentColor" stroke="none" />
  {:else if name === 'soulseek'}
    <path d="M2.8 10.5c2.4 2.3 4.9 3.6 7.1 3.5l-2 6.2 4.6-2.7 3.1-4.1c2.1-.6 4.2-2 5.9-4-2.8 1.2-5 1.4-6.8.5-1.1-2.7-2.6-5-4.7-6.6.5 1.9.4 3.5-.1 4.7-2.3.6-4.6 1.5-7.1 2.5z" fill="currentColor" stroke="none" />
  {:else if name === 'link'}
    <path d="M9.5 14.5 7.8 16.2a3.2 3.2 0 1 1-4.5-4.5L6 9" />
    <path d="m14.5 9.5 1.7-1.7a3.2 3.2 0 1 1 4.5 4.5L18 15" />
    <path d="m8.5 15.5 7-7" />
  {:else if name === 'upload-file'}
    <path d="M7 3.5h6l4 4V20H7z" />
    <path d="M13 3.5V8h4M12 17v-6M9.5 13.5 12 11l2.5 2.5" />
  {/if}
</svg>

<style>
  .spinning { animation: icon-spin 1s linear infinite; transform-origin: center; }
  @keyframes icon-spin { to { transform: rotate(360deg); } }
  @media (prefers-reduced-motion: reduce) { .spinning { animation: none; } }
</style>
