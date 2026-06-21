import { test, expect, Page } from '@playwright/test';

// Proves multi-client compatibility: the Blazor WebAssembly client (:5180) talks to the SAME Foundry
// Local proxy server (:5537) the React SPA uses — two different client apps, one server process.
// Requires: daemon + Server(:5537, CORS) + Blazor client (:5180, `dotnet run`).

const BLAZOR = 'http://localhost:5180';
const LONG = 6 * 60 * 1000;

async function loadBlazor(page: Page) {
  await page.goto(BLAZOR);
  // WASM boots, then fetches the model catalog cross-origin from :5537 — wait for the picker to fill.
  await expect(page.locator('select[aria-label="Model"] option').nth(20)).toBeAttached({ timeout: 90_000 });
}

async function selectModel(page: Page, alias: string) {
  await page.locator('select[aria-label="Model"]').selectOption(alias);
  await expect(page.locator('p.config-line strong')).toHaveText(alias, { timeout: 20_000 });
}

test('Blazor client loads the catalog from the shared server (cross-origin)', async ({ page }) => {
  test.setTimeout(LONG);
  await loadBlazor(page);
  await expect(page.locator('h1')).toContainText('Foundry Local OpenAI Server');
  await expect(page.locator('.client-tag')).toHaveText('Blazor WASM client');
  expect(await page.locator('select[aria-label="Model"] option').count()).toBeGreaterThan(20);
  await expect(page.locator('.scenario-chip').first()).toBeVisible();
});

test('Blazor client: text chat round-trips through the shared server', async ({ page }) => {
  test.setTimeout(LONG);
  await loadBlazor(page);
  await selectModel(page, 'qwen3-0.6b');
  await page.locator('.scenario-chip:has-text("Q&A")').click();
  await page.locator('button:has-text("Send Prompt")').click();
  const reply = page.locator('article.message.assistant p').first();
  await expect(reply).toBeVisible({ timeout: LONG });
  expect((await reply.textContent())?.length ?? 0).toBeGreaterThan(0);
});

test('Blazor client: multi-turn conversation retains context', async ({ page }) => {
  test.setTimeout(LONG);
  await loadBlazor(page);
  await selectModel(page, 'qwen2.5-1.5b');
  await page.locator('.scenario-chip:has-text("Multi-turn chat")').click();
  await page.locator('button:has-text("Run conversation")').click();
  await expect(page.locator('article.message.assistant p')).toHaveCount(3, { timeout: LONG });
  const transcript = (await page.locator('article.message p').allTextContents()).join(' ').toLowerCase();
  expect(/alice|max|retriever|golden/.test(transcript)).toBe(true);
});

test('Blazor client: speech-to-text via the shared server', async ({ page }) => {
  test.setTimeout(LONG);
  await loadBlazor(page);
  await selectModel(page, 'whisper-base'); // audio-only → audio panel
  await page.locator('.scenario-chip:has-text("Pangram clip")').click();
  await page.locator('button:has-text("Transcribe")').click();
  const transcript = page.locator('article.message.assistant p').first();
  await expect(transcript).toBeVisible({ timeout: LONG });
  expect((await transcript.textContent())?.toLowerCase()).toContain('fox');
});
