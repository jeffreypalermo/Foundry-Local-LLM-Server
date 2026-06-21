import { defineConfig, devices } from '@playwright/test';

// Exploratory/e2e config. workers:1 is REQUIRED for the live capability tests — parallel browser
// contexts would load multiple models on the GPU at once and OOM the Foundry daemon.
export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  timeout: 6 * 60 * 1000,
  use: {
    ...devices['Desktop Chrome'],
    actionTimeout: 60_000,
  },
});
