import { test, expect, Page } from '@playwright/test';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

// Records a single continuous, captioned walkthrough of the React SPA driving the live Foundry Local
// server across its headline capabilities. Produces one video.webm (converted to MP4 by ffmpeg in
// tools/record-demo). Requires: daemon + Server(:5537) + Vite(:5173).
//
// Run: npx playwright test tests/demo-video.spec.ts --config=playwright.video.config.ts

const __dirname = dirname(fileURLToPath(import.meta.url));
const FRONTEND = 'http://localhost:5173';
const LONG = 6 * 60 * 1000;
const VLONG = 12 * 60 * 1000;

// ── On-screen narration: a top banner shown for `holdMs`, then cleared before interacting ──
async function caption(page: Page, title: string, subtitle = '', holdMs = 2600) {
  await page.evaluate(({ title, subtitle }) => {
    let el = document.getElementById('demo-caption');
    if (!el) {
      el = document.createElement('div');
      el.id = 'demo-caption';
      el.style.cssText =
        'position:fixed;top:0;left:0;right:0;z-index:99999;background:linear-gradient(90deg,#0f2557,#2563eb);' +
        'color:#fff;padding:16px 26px;font-family:system-ui,"Segoe UI",sans-serif;box-shadow:0 3px 16px rgba(0,0,0,.35);';
      document.body.appendChild(el);
    }
    el.innerHTML =
      `<div style="font-size:22px;font-weight:700;letter-spacing:.2px">${title}</div>` +
      (subtitle ? `<div style="font-size:14px;font-weight:400;opacity:.92;margin-top:3px">${subtitle}</div>` : '');
  }, { title, subtitle });
  await page.waitForTimeout(holdMs);
}

async function clearCaption(page: Page) {
  await page.evaluate(() => document.getElementById('demo-caption')?.remove());
  await page.waitForTimeout(400);
}

async function selectModel(page: Page, alias: string) {
  await page.locator('select[aria-label="Model"]').selectOption(alias);
  await expect(page.locator('p.config-line strong')).toHaveText(alias, { timeout: 15000 });
  await page.waitForTimeout(700);
}

async function switchTab(page: Page, label: string) {
  await page.locator(`button.mode-tab:has-text("${label}")`).click();
  await page.waitForTimeout(700);
}

// Let a freshly-arrived assistant reply sit on screen so a viewer can read it.
async function showReply(page: Page, count = 1, timeout = LONG) {
  await expect(page.locator('article.message.assistant p').nth(count - 1)).toBeVisible({ timeout });
  await page.waitForTimeout(2600);
}

test('Foundry Local — capability walkthrough (video)', async ({ page }) => {
  test.setTimeout(20 * 60 * 1000);

  // ── Intro ──
  await page.goto(FRONTEND);
  await page.waitForLoadState('networkidle');
  await expect(page.locator('select[aria-label="Model"] option').first()).toBeAttached();
  await caption(page, 'Foundry Local — OpenAI-compatible server',
    'One local server process · the full GPU model catalog · text, code, vision, tools & speech', 3800);
  await clearCaption(page);

  // ── 1. Text chat ──
  await caption(page, '1 · Text chat', 'Pick a model, choose a scenario, get a real local completion');
  await selectModel(page, 'qwen3-0.6b');
  await clearCaption(page);
  await page.getByTestId('scenario-qa').click();
  await page.waitForTimeout(900);
  await page.locator('button:has-text("Send Prompt")').click();
  await showReply(page);

  // ── 2. Multi-turn conversation ──
  await caption(page, '2 · Multi-turn conversation', 'Scripted turns keep context across the dialogue');
  await selectModel(page, 'qwen2.5-1.5b');
  await page.getByTestId('scenario-conversation').click();
  await page.waitForTimeout(900);
  await clearCaption(page);
  await page.locator('button:has-text("Run conversation")').click();
  await showReply(page, 3);

  // ── 3. Code generation ──
  await caption(page, '3 · Code generation', 'A coder model writes a function on demand');
  await selectModel(page, 'qwen2.5-coder-0.5b');
  await page.getByTestId('scenario-generate').click();
  await page.waitForTimeout(900);
  await clearCaption(page);
  await page.locator('button:has-text("Send Prompt")').click();
  await showReply(page);

  // ── 4. Tool calling ──
  await caption(page, '4 · Tool calling', 'The model requests a function; the app runs it and feeds the result back');
  await selectModel(page, 'phi-4-mini');
  await switchTab(page, 'Tools');
  await page.locator('.scenario-chip:has-text("Full tool loop")').click();
  await page.waitForTimeout(900);
  await clearCaption(page);
  await page.locator('button:has-text("Send with Tools")').click();
  await showReply(page);

  // ── 5. Vision ──
  await caption(page, '5 · Vision', 'A vision-language model describes an image');
  await selectModel(page, 'qwen3-vl-2b-instruct');
  await switchTab(page, 'Vision');
  await page.getByTestId('scenario-describe').click();
  await expect(page.locator('img.preview')).toBeVisible();
  await page.waitForTimeout(900);
  await clearCaption(page);
  await page.locator('button:has-text("Describe Image")').click();
  await showReply(page, 1, VLONG);

  // ── 6. Speech-to-text ──
  await caption(page, '6 · Speech-to-text', 'Whisper transcribes an audio clip via the same server');
  await selectModel(page, 'whisper-base');
  await page.getByTestId('scenario-pangram').click();
  await page.waitForTimeout(900);
  await clearCaption(page);
  await page.locator('button:has-text("Transcribe")').click();
  await showReply(page);

  // ── Outro ──
  await caption(page, 'One server · two clients · the whole catalog',
    'React + Blazor WASM clients · OpenAI-compatible /v1 API · runs entirely on local GPU', 4200);
  await clearCaption(page);
});
