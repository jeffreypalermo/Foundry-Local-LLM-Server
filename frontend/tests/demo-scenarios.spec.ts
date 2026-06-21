import { test, expect, Page } from '@playwright/test';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

// Full-system tests for the per-model demo harness: every capability's scenario chips (structure)
// plus representative live runs (behavior). Requires the daemon + Server(:5537) + Vite(:5173).

const __dirname = dirname(fileURLToPath(import.meta.url));
const FRONTEND = 'http://localhost:5173';
const SERVER = 'http://localhost:5537';
const LONG = 6 * 60 * 1000;

async function load(page: Page) {
  await page.goto(FRONTEND);
  await page.waitForLoadState('networkidle');
  await expect(page.locator('select[aria-label="Model"] option').first()).toBeAttached();
}

async function selectModel(page: Page, alias: string) {
  await page.locator('select[aria-label="Model"]').selectOption(alias);
  await expect(page.locator('p.config-line strong')).toHaveText(alias, { timeout: 15000 });
}

async function switchTab(page: Page, label: string) {
  await page.locator(`button.mode-tab:has-text("${label}")`).click();
}

test.beforeEach(async () => {
  const res = await fetch(`${SERVER}/api/models`);
  expect(res.ok).toBe(true);
});

// ── Structure: scenario chips render and apply (fast, no inference) ────────────────

test.describe('Scenario chips — structure', () => {
  test('text model exposes 6 text scenarios; chips set the prompt', async ({ page }) => {
    await load(page);
    await selectModel(page, 'phi-4-mini'); // text + tools
    for (const id of ['qa', 'summarize', 'translate', 'sentiment', 'extract', 'rewrite']) {
      await expect(page.getByTestId(`scenario-${id}`)).toBeVisible();
    }
    await page.getByTestId('scenario-summarize').click();
    await expect(page.locator('textarea')).toHaveValue(/Summarize/i);
    await page.getByTestId('scenario-translate').click();
    await expect(page.locator('textarea')).toHaveValue(/Translate/i);
  });

  test('code model exposes 5 code scenarios', async ({ page }) => {
    await load(page);
    await selectModel(page, 'qwen2.5-coder-0.5b');
    for (const id of ['generate', 'explain', 'bug', 'tests', 'translate']) {
      await expect(page.getByTestId(`scenario-${id}`)).toBeVisible();
    }
    await page.getByTestId('scenario-bug').click();
    await expect(page.locator('textarea')).toHaveValue(/bug/i);
  });

  test('reasoning model exposes 5 reasoning scenarios', async ({ page }) => {
    await load(page);
    await selectModel(page, 'deepseek-r1-1.5b');
    for (const id of ['math', 'logic', 'plan', 'compare', 'estimate']) {
      await expect(page.getByTestId(`scenario-${id}`)).toBeVisible();
    }
  });

  test('vision model exposes 6 vision scenarios; chips swap the preview image', async ({ page }) => {
    await load(page);
    await selectModel(page, 'qwen3-vl-2b-instruct');
    await switchTab(page, 'Vision');
    for (const id of ['describe', 'ocr', 'color', 'count', 'sceneqa', 'upload']) {
      await expect(page.getByTestId(`scenario-${id}`)).toBeVisible();
    }
    await page.getByTestId('scenario-ocr').click();
    await expect(page.locator('img.preview')).toBeVisible();
    await page.getByTestId('scenario-upload').click();
    await expect(page.locator('img.preview')).toHaveCount(0); // upload scenario clears built-in image
  });

  test('tools panel exposes 5 tool scenarios', async ({ page }) => {
    await load(page);
    await selectModel(page, 'phi-4-mini');
    await switchTab(page, 'Tools');
    for (const id of ['weather', 'calculate', 'both', 'forced', 'multiturn']) {
      await expect(page.getByTestId(`scenario-${id}`)).toBeVisible();
    }
    await page.getByTestId('scenario-calculate').click();
    await expect(page.locator('textarea')).toHaveValue(/multiplied|calculate|\d/i);
  });

  test('audio model exposes 5 audio scenarios; chips set the clip/language hint', async ({ page }) => {
    await load(page);
    await selectModel(page, 'whisper-base'); // audio-only → audio panel is default
    for (const id of ['pangram', 'numbers', 'pangram-en', 'numbers-en', 'upload']) {
      await expect(page.getByTestId(`scenario-${id}`)).toBeVisible();
    }
    await page.getByTestId('scenario-pangram-en').click();
    await expect(page.locator('.panel .hint')).toContainText('language=en');
  });
});

// ── Behavior: representative live runs (inference) ────────────────────────────────

test.describe('Scenario behavior — live', () => {
  async function runChatScenario(page: Page, model: string, scenarioId: string) {
    await load(page);
    await selectModel(page, model);
    await page.getByTestId(`scenario-${scenarioId}`).click();
    await page.locator('button:has-text("Send Prompt")').click();
    const reply = page.locator('article.message.assistant p').first();
    await expect(reply).toBeVisible({ timeout: LONG });
    const text = (await reply.textContent()) ?? '';
    expect(text.length).toBeGreaterThan(0);
    return text;
  }

  test('text: Q&A', async ({ page }) => { test.setTimeout(LONG); await runChatScenario(page, 'qwen3-0.6b', 'qa'); });
  test('code: generate', async ({ page }) => { test.setTimeout(LONG); await runChatScenario(page, 'qwen2.5-coder-0.5b', 'generate'); });
  test('reasoning: math', async ({ page }) => { test.setTimeout(LONG); const t = await runChatScenario(page, 'deepseek-r1-1.5b', 'math'); expect(t.length).toBeGreaterThan(0); });

  test('vision: describe', async ({ page }) => {
    test.setTimeout(LONG);
    await load(page);
    await selectModel(page, 'qwen3-vl-2b-instruct');
    await switchTab(page, 'Vision');
    await page.getByTestId('scenario-describe').click();
    await expect(page.locator('img.preview')).toBeVisible();
    await page.locator('button:has-text("Describe Image")').click();
    await expect(page.locator('article.message.assistant p').first()).toBeVisible({ timeout: LONG });
  });

  test('vision: OCR built-in', async ({ page }) => {
    test.setTimeout(LONG);
    await load(page);
    await selectModel(page, 'qwen3-vl-2b-instruct');
    await switchTab(page, 'Vision');
    await page.getByTestId('scenario-ocr').click();
    await page.locator('button:has-text("Describe Image")').click();
    await expect(page.locator('article.message.assistant p').first()).toBeVisible({ timeout: LONG });
  });

  test('vision: upload your own', async ({ page }) => {
    test.setTimeout(LONG);
    await load(page);
    await selectModel(page, 'qwen3-vl-2b-instruct');
    await switchTab(page, 'Vision');
    await page.getByTestId('scenario-upload').click();
    await page.locator('input[aria-label="Image"]').setInputFiles(resolve(__dirname, 'assets/green-circle.png'));
    await expect(page.locator('img.preview')).toBeVisible();
    await page.locator('button:has-text("Describe Image")').click();
    await expect(page.locator('article.message.assistant p').first()).toBeVisible({ timeout: LONG });
  });

  test('tools: calculator', async ({ page }) => {
    test.setTimeout(LONG);
    await load(page);
    await selectModel(page, 'phi-4-mini');
    await switchTab(page, 'Tools');
    await page.getByTestId('scenario-calculate').click();
    await page.locator('button:has-text("Send with Tools")').click();
    await expect(page.locator('article.message.assistant p').first()).toBeVisible({ timeout: LONG });
  });

  test('audio: pangram built-in clip', async ({ page }) => {
    test.setTimeout(LONG);
    await load(page);
    await selectModel(page, 'whisper-base');
    await page.getByTestId('scenario-pangram').click();
    await page.locator('button:has-text("Transcribe")').click();
    const transcript = page.locator('article.message.assistant p').first();
    await expect(transcript).toBeVisible({ timeout: LONG });
    expect((await transcript.textContent())?.toLowerCase()).toContain('fox');
  });

  test('audio: language hint clip', async ({ page }) => {
    test.setTimeout(LONG);
    await load(page);
    await selectModel(page, 'whisper-base');
    await page.getByTestId('scenario-numbers-en').click();
    await page.locator('button:has-text("Transcribe")').click();
    const transcript = page.locator('article.message.assistant p').first();
    await expect(transcript).toBeVisible({ timeout: LONG });
    expect((await transcript.textContent())?.length ?? 0).toBeGreaterThan(0);
  });
});
