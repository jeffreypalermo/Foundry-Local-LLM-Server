import { test, expect } from '@playwright/test';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

// Hands-on exploratory run that drives the REAL app (SPA + Server + live Foundry) through the
// per-model capability panels. Requires:
//   - Foundry daemon running (`foundry server start`)
//   - Server in real mode on :5537
//   - Vite dev server on :5173 proxying /api and /v1 to the Server
// Each model load is slow, so tests use long timeouts. Results are console.logged for the journal.

const __dirname = dirname(fileURLToPath(import.meta.url));
const FRONTEND = 'http://localhost:5173';
const SERVER = 'http://localhost:5537';
const LONG = 5 * 60 * 1000;

async function selectModel(page: import('@playwright/test').Page, alias: string) {
  await page.locator('select[aria-label="Model"]').selectOption(alias);
  // Selection posts to /api/models/select and resets the panels.
  await expect(page.locator('p.config-line strong')).toHaveText(alias, { timeout: 15000 });
}

test.describe('Per-model capability harness (live)', () => {
  test.beforeEach(async () => {
    const res = await fetch(`${SERVER}/api/models`);
    expect(res.ok).toBe(true);
  });

  test('catalog + picker render with capability badges', async ({ page }) => {
    await page.goto(FRONTEND);
    await page.waitForLoadState('networkidle');
    await expect(page.locator('select[aria-label="Model"] option').first()).toBeAttached();
    await expect(page.locator('h1')).toHaveText('Foundry Local OpenAI Server');
    const options = page.locator('select[aria-label="Model"] option');
    expect(await options.count()).toBeGreaterThan(20);
    await expect(page.locator('.cap-badge').first()).toBeVisible();
    console.log(`  picker options: ${await options.count()}`);
  });

  test('text chat — phi-4-mini', async ({ page }) => {
    test.setTimeout(LONG);
    await page.goto(FRONTEND);
    await page.waitForLoadState('networkidle');
    await expect(page.locator('select[aria-label="Model"] option').first()).toBeAttached();
    await selectModel(page, 'phi-4-mini');
    await page.locator('textarea').fill('In one sentence, what is an ocean?');
    await page.locator('button:has-text("Send Prompt")').click();
    const reply = page.locator('article.message.assistant p').first();
    await expect(reply).toBeVisible({ timeout: LONG });
    const text = await reply.textContent();
    expect(text?.length ?? 0).toBeGreaterThan(0);
    console.log(`  [phi-4-mini/text] ${text?.slice(0, 80)}`);
  });

  test('code — qwen2.5-coder-0.5b', async ({ page }) => {
    test.setTimeout(LONG);
    await page.goto(FRONTEND);
    await page.waitForLoadState('networkidle');
    await expect(page.locator('select[aria-label="Model"] option').first()).toBeAttached();
    await selectModel(page, 'qwen2.5-coder-0.5b');
    await page.locator('textarea').fill('Write a Python function add(a,b) that returns a+b. Code only.');
    await page.locator('button:has-text("Send Prompt")').click();
    const reply = page.locator('article.message.assistant p').first();
    await expect(reply).toBeVisible({ timeout: LONG });
    console.log(`  [coder/code] ${(await reply.textContent())?.slice(0, 80)}`);
  });

  test('reasoning — deepseek-r1-1.5b', async ({ page }) => {
    test.setTimeout(LONG);
    await page.goto(FRONTEND);
    await page.waitForLoadState('networkidle');
    await expect(page.locator('select[aria-label="Model"] option').first()).toBeAttached();
    await selectModel(page, 'deepseek-r1-1.5b');
    await page.locator('textarea').fill('What is 17 plus 26? State the final number.');
    await page.locator('button:has-text("Send Prompt")').click();
    const reply = page.locator('article.message.assistant p').first();
    await expect(reply).toBeVisible({ timeout: LONG });
    console.log(`  [reasoning] ${(await reply.textContent())?.slice(0, 120)}`);
  });

  test('vision — qwen3-vl-2b-instruct (image upload)', async ({ page }) => {
    test.setTimeout(LONG);
    await page.goto(FRONTEND);
    await page.waitForLoadState('networkidle');
    await expect(page.locator('select[aria-label="Model"] option').first()).toBeAttached();
    await selectModel(page, 'qwen3-vl-2b-instruct');
    await page.locator('button.mode-tab:has-text("Vision")').click();
    await page.locator('input[aria-label="Image"]').setInputFiles(resolve(__dirname, 'assets/green-circle.png'));
    await expect(page.locator('img.preview')).toBeVisible();
    await page.locator('textarea').fill('Describe this image including the main shape and color.');
    await page.locator('button:has-text("Describe Image")').click();
    const reply = page.locator('article.message.assistant p').first();
    await expect(reply).toBeVisible({ timeout: LONG });
    console.log(`  [vision] ${(await reply.textContent())?.slice(0, 120)}`);
  });

  test('tools — phi-4-mini (get_weather)', async ({ page }) => {
    test.setTimeout(LONG);
    await page.goto(FRONTEND);
    await page.waitForLoadState('networkidle');
    await expect(page.locator('select[aria-label="Model"] option').first()).toBeAttached();
    await selectModel(page, 'phi-4-mini');
    await page.locator('button.mode-tab:has-text("Tools")').click();
    await page.locator('textarea').fill("What's the weather in Paris?");
    await page.locator('button:has-text("Send with Tools")').click();
    const reply = page.locator('article.message.assistant p').first();
    await expect(reply).toBeVisible({ timeout: LONG });
    console.log(`  [tools] ${(await reply.textContent())?.slice(0, 120)}`);
  });

  test('speech-to-text — whisper-base (audio upload)', async ({ page }) => {
    test.setTimeout(LONG);
    await page.goto(FRONTEND);
    await page.waitForLoadState('networkidle');
    await expect(page.locator('select[aria-label="Model"] option').first()).toBeAttached();
    await selectModel(page, 'whisper-base');
    // whisper is audio-only → the Speech-to-text panel is the default (no tabs needed)
    await page.locator('input[aria-label="Audio file"]').setInputFiles(resolve(__dirname, 'assets/speech.wav'));
    await page.locator('button:has-text("Transcribe")').click();
    const transcript = page.locator('article.message.assistant p').first();
    await expect(transcript).toBeVisible({ timeout: LONG });
    const text = await transcript.textContent();
    expect(text?.toLowerCase()).toContain('fox');
    console.log(`  [audio] ${text}`);
  });
});
