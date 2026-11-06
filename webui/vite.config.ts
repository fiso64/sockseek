import { svelte } from '@sveltejs/vite-plugin-svelte';
import { defineConfig } from 'vitest/config';

const daemonTarget = process.env.SOCKSEEK_DAEMON_URL ?? 'http://127.0.0.1:5030';

export default defineConfig({
  plugins: [svelte()],
  server: {
    proxy: {
      '/api': {
        target: daemonTarget,
        // Preserve the browser's Host so the daemon can enforce same-origin requests.
        changeOrigin: false,
        ws: true,
      },
    },
  },
  test: {
    include: ['tests/**/*.test.ts'],
  },
});
