import { test, expect, Page, Locator } from '@playwright/test';
import * as fs from 'fs';

// Exploratory sweep of the BLAZOR WASM client (:5180) → shared Foundry Local proxy (:5537).
// Runs the default demo for every model whose recorded per-response ETA is < 30s, across each
// capability that model supports, plus a few multi-turn conversation runs. This exercises all four
// Blazor senders (SendChat / SendVision / SendTools / SendAudio) and RunConversation against real
// models, end to end, the way a user would click through them.
//
// Requires: foundrylocald daemon + Server(:5537, CORS) + Blazor client (:5180, `dotnet run`).

const BLAZOR = 'http://localhost:5180';
const BOOT = 90_000;      // WASM boot + cross-origin catalog fetch
const IDLE = 120_000;     // control re-enables after a request finishes
const SELECT = 180_000;   // model select POST (may unload/load on the GPU)
const RESULT = 300_000;   // single inference incl. cold model load
const CONV = 420_000;     // 3-turn scripted conversation

// model id → modes to exercise (in click order). Mirrors /api/models capabilities for the <30s set.
const SINGLE: Array<{ model: string; modes: Array<'chat' | 'vision' | 'tools' | 'audio'> }> = [
  { model: 'phi-4-mini', modes: ['chat', 'tools'] },
  { model: 'phi-4-mini-reasoning', modes: ['chat', 'tools'] },
  { model: 'phi-3.5-mini', modes: ['chat'] },
  { model: 'phi-3-mini-4k', modes: ['chat'] },
  { model: 'phi-3-mini-128k', modes: ['chat'] },
  { model: 'qwen3-0.6b', modes: ['chat'] },
  { model: 'qwen3-1.7b', modes: ['chat'] },
  { model: 'qwen3-4b', modes: ['chat'] },
  { model: 'qwen2.5-0.5b', modes: ['chat'] },
  { model: 'qwen2.5-1.5b', modes: ['chat'] },
  { model: 'qwen2.5-7b', modes: ['chat'] },
  { model: 'qwen2.5-coder-0.5b', modes: ['chat'] },
  { model: 'qwen2.5-coder-1.5b', modes: ['chat'] },
  { model: 'qwen2.5-coder-7b', modes: ['chat'] },
  { model: 'qwen3.5-2b-text', modes: ['chat'] },
  { model: 'deepseek-r1-1.5b', modes: ['chat'] },
  { model: 'mistral-7b-v0.2', modes: ['chat'] },
  { model: 'olmo-3-7b-instruct', modes: ['chat'] },
  { model: 'smollm3-3b', modes: ['chat'] },
  { model: 'qwen3-vl-2b-instruct', modes: ['chat', 'vision'] },
  { model: 'qwen3-vl-4b-instruct', modes: ['chat', 'vision'] },
  { model: 'qwen3.5-0.8b', modes: ['chat', 'vision'] },
  { model: 'qwen3.5-2b', modes: ['chat', 'vision'] },
  { model: 'whisper-tiny', modes: ['audio'] },
  { model: 'whisper-base', modes: ['audio'] },
  { model: 'whisper-small', modes: ['audio'] },
  { model: 'whisper-medium', modes: ['audio'] },
  { model: 'whisper-large-v3-turbo', modes: ['audio'] },
];

// model id → multi-turn scenario chip label (covers RunConversation across text/code/reasoning kinds).
const CONVERSATIONS: Array<{ model: string; chip: string }> = [
  { model: 'qwen3-0.6b', chip: 'Multi-turn chat' },
  { model: 'qwen2.5-coder-0.5b', chip: 'Iterative coding' },
  { model: 'deepseek-r1-1.5b', chip: 'Follow-up reasoning' },
];

const MODE_TAB: Record<string, string> = { chat: 'Text chat', vision: 'Vision', tools: 'Tools', audio: 'Speech-to-text' };
const MODE_RUN: Record<string, string> = { chat: 'Send Prompt', vision: 'Describe Image', tools: 'Send with Tools', audio: 'Transcribe' };

type Result = { model: string; mode: string; status: 'PASS' | 'FAIL'; detail: string };
const results: Result[] = [];

function modelSelect(page: Page): Locator {
  return page.locator('select[aria-label="Model"]');
}

async function waitIdle(page: Page) {
  await expect(modelSelect(page)).toBeEnabled({ timeout: IDLE });
}

async function loadBlazor(page: Page) {
  await page.goto(BLAZOR);
  await expect(page.locator('.client-tag')).toHaveText('Blazor WASM client', { timeout: BOOT });
  await expect(modelSelect(page).locator('option').nth(20)).toBeAttached({ timeout: BOOT });
}

async function selectModel(page: Page, alias: string) {
  await waitIdle(page);
  await modelSelect(page).selectOption(alias);
  // config-line strong flips to the new id only after /api/models/select returns — a "model ready" signal.
  await expect(page.locator('p.config-line strong')).toHaveText(alias, { timeout: SELECT });
}

async function ensureMode(page: Page, mode: string) {
  const tab = page.locator(`button.mode-tab:has-text("${MODE_TAB[mode]}")`);
  if (await tab.count() > 0) {
    await expect(tab.first()).toBeEnabled({ timeout: IDLE });
    await tab.first().click();
  }
  // else: single-capability model — the panel for its only mode is already shown.
}

// Waits for either an assistant message or an error banner, whichever appears first.
async function awaitOutcome(page: Page, timeout: number) {
  const assistant = page.locator('article.message.assistant p').first();
  const err = page.locator('p.error').first();
  await Promise.race([
    assistant.waitFor({ state: 'visible', timeout }),
    err.waitFor({ state: 'visible', timeout }),
  ]).catch(() => { /* evaluate state below regardless of timeout */ });
}

async function runSingle(page: Page, model: string, mode: string) {
  try {
    await ensureMode(page, mode);
    await waitIdle(page);
    await page.locator('.scenario-chip').first().click(); // deterministic default (never an upload chip)
    await waitIdle(page);
    const runBtn = page.locator(`button:has-text("${MODE_RUN[mode]}")`).first();
    await expect(runBtn).toBeEnabled({ timeout: IDLE });
    await runBtn.click();
    await awaitOutcome(page, RESULT);

    if (await page.locator('p.error').count() > 0) {
      const msg = (await page.locator('p.error').first().textContent())?.trim() ?? 'unknown error';
      results.push({ model, mode, status: 'FAIL', detail: `error banner: ${msg}` });
      return;
    }
    const assistant = page.locator('article.message.assistant p').first();
    if (await assistant.count() === 0) {
      results.push({ model, mode, status: 'FAIL', detail: 'no assistant output and no error (timed out)' });
      return;
    }
    const text = (await assistant.textContent())?.trim() ?? '';
    if (text.length === 0) {
      results.push({ model, mode, status: 'FAIL', detail: 'assistant message was empty' });
      return;
    }
    const snippet = text.replace(/\s+/g, ' ').slice(0, 70);
    results.push({ model, mode, status: 'PASS', detail: snippet });
  } catch (e) {
    results.push({ model, mode, status: 'FAIL', detail: `exception: ${(e as Error).message.split('\n')[0]}` });
  }
}

async function runConversation(page: Page, model: string, chip: string) {
  const mode = 'conversation';
  try {
    await ensureMode(page, 'chat');
    await waitIdle(page);
    await page.locator(`.scenario-chip:has-text("${chip}")`).first().click();
    await waitIdle(page);
    const runBtn = page.locator('button:has-text("Run conversation")').first();
    await expect(runBtn).toBeEnabled({ timeout: IDLE });
    await runBtn.click();
    await expect(page.locator('article.message.assistant p')).toHaveCount(3, { timeout: CONV });
    if (await page.locator('p.error').count() > 0) {
      const msg = (await page.locator('p.error').first().textContent())?.trim() ?? 'unknown error';
      results.push({ model, mode, status: 'FAIL', detail: `error banner: ${msg}` });
      return;
    }
    results.push({ model, mode, status: 'PASS', detail: '3 assistant turns returned' });
  } catch (e) {
    results.push({ model, mode, status: 'FAIL', detail: `exception: ${(e as Error).message.split('\n')[0]}` });
  }
}

test('Blazor under-30s demos: every supported mode round-trips', async ({ page }) => {
  test.setTimeout(90 * 60 * 1000);
  await loadBlazor(page);

  for (const { model, modes } of SINGLE) {
    await selectModel(page, model);
    for (const mode of modes) {
      await runSingle(page, model, mode);
      const r = results[results.length - 1];
      console.log(`[${r.status}] ${model} · ${mode} — ${r.detail}`);
    }
  }

  for (const { model, chip } of CONVERSATIONS) {
    await selectModel(page, model);
    await runConversation(page, model, chip);
    const r = results[results.length - 1];
    console.log(`[${r.status}] ${model} · conversation — ${r.detail}`);
  }

  const pass = results.filter((r) => r.status === 'PASS').length;
  const fail = results.filter((r) => r.status === 'FAIL');
  console.log('\n================ BLAZOR UNDER-30s SWEEP ================');
  for (const r of results) console.log(`${r.status.padEnd(4)}  ${(`${r.model} · ${r.mode}`).padEnd(44)} ${r.detail}`);
  console.log(`-------------------------------------------------------`);
  console.log(`TOTAL ${results.length}   PASS ${pass}   FAIL ${fail.length}`);
  fs.writeFileSync('test-results-blazor-under30.json', JSON.stringify(results, null, 2));

  expect(fail, `Failing demos:\n${fail.map((f) => `  ${f.model} · ${f.mode}: ${f.detail}`).join('\n')}`).toEqual([]);
});
