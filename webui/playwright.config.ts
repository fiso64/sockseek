import { defineConfig, devices } from '@playwright/test';

// TODO [TESTS]: Only `npm run test:e2e` rebuilds the web UI and the test host
// (its pretest script). `npx playwright test <file>` runs whatever was built
// last, so a journey can fail or pass against stale code without saying so.
// Rebuild in a globalSetup, or fail there when sources are newer than the builds.
export default defineConfig({
  testDir: './e2e',
  // Manual timing checks: SOCKSEEK_RUN_LOAD=1 npx playwright test --grep '@Performance'.
  // CI's scheduled Load run excludes that tag.
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
