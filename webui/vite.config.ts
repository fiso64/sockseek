import { svelte } from '@sveltejs/vite-plugin-svelte';
import { defineConfig } from 'vitest/config';

const daemonTarget = process.env.SOCKSEEK_DAEMON_URL ?? 'http://127.0.0.1:5030';

export default defineConfig({
  // Upstream deepmerge advisory GHSA-gcqx-w48m-w2h4 has no fix yet; this plugin
  // uses it only for trusted build configuration. Keep the npm audit finding visible.
  plugins: [svelte()],
  // The daemon supplies color-scheme in the document. Preserve native light-dark()
  // instead of lowering it to flags inferred only from the bundled stylesheet.
  build: {
    cssTarget: ['chrome123', 'firefox120', 'safari17.5'],
    // The full client is about 590 kB minified (170 kB gzip); flag substantial growth.
    chunkSizeWarningLimit: 750,
  },
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
