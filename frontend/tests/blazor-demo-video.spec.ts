import { test, expect, Page, Browser } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { demoTimings } from '../src/demoTimings';
import { scenariosFor } from '../src/scenarios';
import type { Kind, Scenario } from '../src/scenarios';

// Records ONE short video clip PER demo of the BLAZOR WASM client (:5180) → shared proxy (:5537),
// for every demo scenario whose estimated runtime is 30 seconds or less. "Runtime" = the model's
// recorded per-response time × how many requests the scenario issues (multi-turn = N turns, full tool
// loop = 2) — the same number the UI advertises on its run buttons.
//
// Each demo runs in its own browser context so it produces an isolated clip-NNN.webm. Alongside the
// clips it writes manifest.json (clip → model/mode/scenario + an explanatory paragraph). The C# tools
// DemoNarrator (Windows TTS → per-clip WAV) and DemoNarratedVideoBuilder (ffmpeg mux + concat) turn
// that into one narrated MP4.
//
//   npx playwright test tests/blazor-demo-video.spec.ts --config=playwright.video.blazor.config.ts
//   MODELS=whisper-base,qwen3-0.6b npx playwright test ... (subset)

const BLAZOR = 'http://localhost:5180';
const SERVER = 'http://localhost:5537';
const MAX_ETA = 30;             // seconds — the gate
const RESULT_TIMEOUT = 240_000; // per scenario (covers a cold model load + a <=30s response)
const IDLE_TIMEOUT = 300_000;

const OUT_DIR = path.resolve('blazor-demo-clips');
const MANIFEST = path.join(OUT_DIR, 'manifest.json');

type ModelInfo = {
  id: string; text: boolean; code: boolean; reasoning: boolean;
  vision: boolean; audio: boolean; tools: boolean;
};
type Mode = 'chat' | 'vision' | 'tools' | 'audio';
type Work = {
  model: string; mode: Mode; kind: Kind; scenarioId: string; label: string;
  count: number; etaSec: number; paragraph: string;
};

const MODE_TAB: Record<Mode, string> = { chat: 'Text chat', vision: 'Vision', tools: 'Tools', audio: 'Speech-to-text' };
const KIND_WORD: Record<Kind, string> = {
  text: 'text', code: 'coding', reasoning: 'reasoning', vision: 'vision', tools: 'tool-use', audio: 'speech-to-text',
};

// One-line purpose per scenario, keyed by `${kind}:${id}` (ids repeat across kinds with different meaning).
const PURPOSE: Record<string, string> = {
  'text:qa': 'answering an open-ended question in a few sentences',
  'text:summarize': 'condensing a short document into three bullet points',
  'text:translate': 'translating one sentence into French, Spanish, and Japanese',
  'text:sentiment': 'classifying the sentiment of several lines of text',
  'text:extract': 'extracting structured JSON from a free-form sentence',
  'text:rewrite': 'rewriting a blunt email into a polite, professional tone',
  'text:conversation': 'holding a three-turn conversation to show it remembers earlier context',
  'code:generate': 'writing a Python function from a plain-English specification',
  'code:explain': 'explaining an unfamiliar snippet of code step by step',
  'code:bug': 'finding and fixing the bug in a small function',
  'code:tests': 'generating unit tests for a simple function',
  'code:translate': 'porting a function from Python to idiomatic JavaScript',
  'code:iterate': 'refining code across a three-turn conversation',
  'reasoning:math': 'solving a multi-step word problem and showing its working',
  'reasoning:logic': 'working through a deductive logic puzzle',
  'reasoning:plan': 'producing a step-by-step plan with time budgets',
  'reasoning:compare': 'weighing the trade-offs between two options and recommending one',
  'reasoning:estimate': 'making a Fermi estimate and stating its assumptions',
  'reasoning:followup': 'building on its own earlier answers across a three-turn conversation',
  'vision:describe': 'describing the contents of an image',
  'vision:ocr': 'reading and transcribing the text inside an image',
  'vision:color': 'identifying the shape and colour of an object in an image',
  'vision:count': 'counting the objects in an image',
  'vision:sceneqa': 'answering a yes-or-no question about an image',
  'tools:weather': 'deciding whether to call a get_weather function to answer a question',
  'tools:calculate': 'deciding whether to call a calculator function',
  'tools:both': 'choosing the right function when two tools are available',
  'tools:forced': 'calling a specific function that the request forces with tool_choice',
  'tools:multiturn': 'calling a weather tool, receiving the result, and using it to give final advice',
  'audio:pangram': 'transcribing a built-in speech clip into text',
  'audio:numbers': 'transcribing a built-in clip that contains dates and numbers',
  'audio:pangram-en': 'transcribing a speech clip with the language pinned to English',
  'audio:numbers-en': 'transcribing a numeric clip with the language pinned to English',
};

function modesFor(m: ModelInfo): Mode[] {
  const modes: Mode[] = [];
  if (m.text) modes.push('chat');
  if (m.vision) modes.push('vision');
  if (m.tools) modes.push('tools');
  if (m.audio) modes.push('audio');
  return modes.length ? modes : ['chat'];
}
function kindFor(mode: Mode, m: ModelInfo): Kind {
  if (mode === 'vision') return 'vision';
  if (mode === 'tools') return 'tools';
  if (mode === 'audio') return 'audio';
  if (m.code) return 'code';
  if (m.reasoning) return 'reasoning';
  return 'text';
}
function requestCount(s: Scenario): number {
  if (s.turns) return s.turns.length;
  if (s.followUpToolResult) return 2;
  return 1;
}
function fmtEta(sec: number): string {
  return sec < 90 ? `about ${sec} seconds` : `about ${Math.round(sec / 60)} minutes`;
}
function buildParagraph(model: string, kind: Kind, s: Scenario, count: number, etaSec: number): string {
  const purpose = PURPOSE[`${kind}:${s.id}`] ?? `running the ${s.label} scenario`;
  const turns = count > 1 ? ` This is a ${count}-turn exchange.` : '';
  return (
    `Here is the ${s.label} ${KIND_WORD[kind]} demo, running on the ${model} model. ` +
    `In this demo the model is ${purpose}.${turns} ` +
    `The Blazor client sends the request to the local, OpenAI-compatible server, which runs ${model} ` +
    `entirely on your own GPU — no cloud, no API keys. A typical response from this model takes ${fmtEta(etaSec)}.`
  );
}

async function setCaption(page: Page, line1: string, line2 = '') {
  await page.evaluate(({ line1, line2 }) => {
    let el = document.getElementById('demo-caption');
    if (!el) {
      el = document.createElement('div');
      el.id = 'demo-caption';
      el.style.cssText =
        'position:fixed;top:0;left:0;right:0;z-index:99999;background:linear-gradient(90deg,#5b21b6,#7c3aed);' +
        'color:#fff;padding:10px 22px;font-family:system-ui,"Segoe UI",sans-serif;box-shadow:0 3px 16px rgba(0,0,0,.35);';
      document.body.appendChild(el);
      document.body.style.paddingTop = '64px';
    }
    el.innerHTML =
      `<div style="font-size:19px;font-weight:700;letter-spacing:.2px">${line1}</div>` +
      (line2 ? `<div style="font-size:13px;font-weight:400;opacity:.92;margin-top:2px">${line2}</div>` : '');
  }, { line1, line2 });
}

async function scrollToLatestResult(page: Page) {
  await page.evaluate(() => {
    const els = document.querySelectorAll('article.message.assistant, article.message');
    els[els.length - 1]?.scrollIntoView({ behavior: 'smooth', block: 'center' });
  });
  await page.waitForTimeout(2000);
}

async function clickRunButton(page: Page): Promise<boolean> {
  for (const label of ['Run conversation', 'Send with Tools', 'Describe Image', 'Transcribe', 'Send Prompt']) {
    const btn = page.locator(`button:has-text("${label}")`).first();
    if (await btn.count() > 0 && await btn.isVisible() && await btn.isEnabled()) {
      await btn.click();
      return true;
    }
  }
  return false;
}

async function buildWorkList(browser: Browser): Promise<Work[]> {
  const probe = await browser.newContext();
  const res = await probe.request.get(`${SERVER}/api/models`);
  const catalog = (await res.json()) as { available: ModelInfo[] };
  await probe.close();

  const work: Work[] = [];
  for (const m of catalog.available) {
    const sec = demoTimings[m.id];
    if (sec === undefined || sec > MAX_ETA) continue;
    for (const mode of modesFor(m)) {
      const kind = kindFor(mode, m);
      for (const s of scenariosFor(kind)) {
        if (s.upload) continue;
        const count = requestCount(s);
        const etaSec = sec * count;
        if (etaSec <= MAX_ETA) {
          work.push({
            model: m.id, mode, kind, scenarioId: s.id, label: s.label, count, etaSec,
            paragraph: buildParagraph(m.id, kind, s, count, etaSec),
          });
        }
      }
    }
  }
  return work;
}

test('Blazor demos <=30s — record one clip per demo + manifest', async ({ browser }) => {
  test.setTimeout(3 * 60 * 60 * 1000);

  fs.mkdirSync(OUT_DIR, { recursive: true });
  let work = await buildWorkList(browser);
  const wanted = (process.env.MODELS || '').split(',').map((s) => s.trim()).filter(Boolean);
  if (wanted.length) work = work.filter((w) => wanted.includes(w.model));

  console.log(`Recording ${work.length} demo clips (<=${MAX_ETA}s) across ${new Set(work.map((w) => w.model)).size} models.`);
  const manifest: any[] = [];

  for (let i = 0; i < work.length; i++) {
    const w = work[i];
    const ctx = await browser.newContext({
      viewport: { width: 1280, height: 720 },
      recordVideo: { dir: OUT_DIR, size: { width: 1280, height: 720 } },
    });
    const page = await ctx.newPage();
    let ok = false;
    try {
      await page.goto(BLAZOR);
      await expect(page.locator('.client-tag')).toHaveText('Blazor WASM client', { timeout: 90_000 });
      await expect(page.locator('select[aria-label="Model"] option').nth(20)).toBeAttached({ timeout: 90_000 });

      await setCaption(page, `${w.model} — ${w.label}`,
        `${w.mode} · ${PURPOSE[`${w.kind}:${w.scenarioId}`] ?? w.label} · expect ~${w.etaSec}s  (demo ${i + 1}/${work.length})`);

      await expect(page.locator('select[aria-label="Model"]')).toBeEnabled({ timeout: IDLE_TIMEOUT });
      await page.locator('select[aria-label="Model"]').selectOption(w.model);
      await expect(page.locator('p.config-line strong')).toHaveText(w.model, { timeout: 60_000 });

      const tab = page.locator(`button.mode-tab:has-text("${MODE_TAB[w.mode]}")`);
      if (await tab.count() > 0) { await tab.first().click(); await page.waitForTimeout(300); }

      const chip = page.locator(`[data-testid="scenario-${w.scenarioId}"]`).first();
      await chip.click();
      await page.waitForTimeout(500);

      const before = await page.locator('article.message.assistant p').count();
      if (await clickRunButton(page)) {
        await page.waitForFunction(
          (arg) => document.querySelectorAll(arg.sel).length > arg.before,
          { sel: 'article.message.assistant p', before },
          { timeout: RESULT_TIMEOUT },
        );
        await page.locator('button:has-text("Running...")')
          .waitFor({ state: 'hidden', timeout: RESULT_TIMEOUT }).catch(() => {});
        await scrollToLatestResult(page);
        await page.waitForTimeout(700);
        ok = true;
      }
    } catch (e) {
      console.log(`[skip] ${w.model} · ${w.mode} · ${w.scenarioId} — ${(e as Error).message.split('\n')[0]}`);
    }

    const video = page.video();
    await ctx.close(); // flushes the .webm to disk
    let clip = '';
    if (video) {
      const src = await video.path();
      clip = `clip-${String(i).padStart(3, '0')}.webm`;
      try { fs.renameSync(src, path.join(OUT_DIR, clip)); } catch { clip = path.basename(src); }
    }
    manifest.push({
      index: i, clip, ok, model: w.model, mode: w.mode, kind: w.kind,
      scenarioId: w.scenarioId, label: w.label, count: w.count, etaSec: w.etaSec, paragraph: w.paragraph,
    });
    fs.writeFileSync(MANIFEST, JSON.stringify(manifest, null, 2)); // checkpoint after each clip
    console.log(`[${ok ? 'ok' : 'EMPTY'}] ${i + 1}/${work.length}  ${w.model} · ${w.mode} · ${w.label} -> ${clip}`);
  }

  const okCount = manifest.filter((m) => m.ok).length;
  console.log(`\nDONE: ${okCount}/${manifest.length} clips recorded with output. Manifest: ${MANIFEST}`);
  expect(manifest.length).toBeGreaterThan(0);
});
