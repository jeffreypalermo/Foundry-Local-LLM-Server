import { defineConfig, devices } from '@playwright/test';

// Per-demo video recording for the Blazor walkthrough (tests/blazor-demo-video.spec.ts). The spec
// creates one browser context per demo (so each demo flushes its own clip-NNN.webm into
// ./blazor-demo-clips); video is configured per-context in the spec, not here. workers:1 is required
// so only one model is on the GPU at a time.
export default defineConfig({
  testDir: './tests',
  testMatch: /blazor-demo-video\.spec\.ts/,
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  timeout: 3 * 60 * 60 * 1000,
  use: {
    ...devices['Desktop Chrome'],
    viewport: { width: 1280, height: 720 },
    actionTimeout: 60_000,
  },
});
