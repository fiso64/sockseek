import { defineConfig } from '@playwright/test';
import base from './playwright.config';

// Documentation screenshots, generated against the test daemon's invented
// "showcase" network: npm run docs:screenshots. Not part of the test suite.
export default defineConfig({
  ...base,
  testMatch: 'screenshots.ts',
  testIgnore: [],
  workers: 1,
  reporter: 'list',
});
