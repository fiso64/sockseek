import { svelte } from '@sveltejs/vite-plugin-svelte';
import { defineConfig } from 'vitest/config';

const daemonTarget = process.env.SOCKSEEK_DAEMON_URL ?? 'http://127.0.0.1:5030';

export default defineConfig({
  plugins: [svelte()],
  server: {
    proxy: {
      '/api': {
        target: daemonTarget,
        changeOrigin: true,
        ws: true,
      },
    },
  },
  test: {
    include: ['tests/**/*.test.ts'],
  },
});
