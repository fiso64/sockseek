import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  testIgnore: process.env.SOCKSEEK_RUN_LOAD === '1' ? [] : ['**/*.load.spec.ts'],
  fullyParallel: false,
  // Each worker owns its daemon and database. Run ordinary files in parallel;
  // keep Load measurements isolated and tests within each file ordered.
  workers: process.env.SOCKSEEK_RUN_LOAD === '1' ? 1 : 2,
  forbidOnly: Boolean(process.env.CI),
  retries: 0,
  reporter: process.env.CI ? [['line'], ['html', { open: 'never' }]] : 'list',
  use: {
    // DOM snapshots on every successful action are expensive. Failures still
    // retain screenshots, assertion context and daemon logs; request a trace
    // with `--trace retain-on-failure` when diagnosing a particular journey.
    trace: 'off',
    screenshot: 'only-on-failure',
  },
  projects: [{
    name: 'chromium',
    use: {
      ...devices['Desktop Chrome'],
      launchOptions: {
        executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH || undefined,
      },
    },
  }],
});
