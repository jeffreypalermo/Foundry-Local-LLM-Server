import { defineConfig, devices } from '@playwright/test';

// Video-recording config for the demonstration walkthrough (tests/demo-video.spec.ts).
// Records one video.webm per test into ./test-results-video; tools/record-demo transcodes it to MP4.
export default defineConfig({
  testDir: './tests',
  testMatch: /demo-video\.spec\.ts/,
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  timeout: 20 * 60 * 1000,
  outputDir: './test-results-video',
  use: {
    ...devices['Desktop Chrome'],
    viewport: { width: 1280, height: 720 },
    video: { mode: 'on', size: { width: 1280, height: 720 } },
    actionTimeout: 60_000,
  },
});
