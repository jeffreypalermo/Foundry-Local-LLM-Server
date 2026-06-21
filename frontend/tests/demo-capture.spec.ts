import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Exhaustive demo capture: for every model, exercise every applicable scenario (click chip, run,
// validate output) and screenshot each step. Results + screenshots are written incrementally to
// docs/demo-captures so a chunked, resumable run can build the full demo document.
//
// Run a subset per chunk via MODELS env (comma-separated ids); omit for all. Requires the live app
// (Server :5537 + Vite :5173 + daemon).

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.resolve(__dirname, '../..');
const OUT = path.join(REPO, 'docs', 'demo-captures');
fs.mkdirSync(OUT, { recursive: true });

const FRONTEND = 'http://localhost:5173';
const SERVER = 'http://localhost:5537';
const ASSET_IMG = path.resolve(__dirname, 'assets/green-circle.png');
const ASSET_WAV = path.resolve(__dirname, 'assets/speech.wav');

const SCENARIOS: Record<string, string[]> = {
  text: ['qa', 'summarize', 'translate', 'sentiment', 'extract', 'rewrite'],
  code: ['generate', 'explain', 'bug', 'tests', 'translate'],
  reasoning: ['math', 'logic', 'plan', 'compare', 'estimate'],
  vision: ['describe', 'ocr', 'color', 'count', 'sceneqa', 'upload'],
  tools: ['weather', 'calculate', 'both', 'forced', 'multiturn'],
  audio: ['pangram', 'numbers', 'pangram-en', 'numbers-en', 'upload'],
};
const RUN_BUTTON: Record<string, string> = {
  text: 'Send Prompt', code: 'Send Prompt', reasoning: 'Send Prompt',
  vision: 'Describe Image', tools: 'Send with Tools', audio: 'Transcribe',
};

type ModelInfo = { id: string; code: boolean; reasoning: boolean; vision: boolean; audio: boolean; tools: boolean };

function kindsFor(m: ModelInfo): Array<[string, string | null]> {
  if (m.audio) return [['audio', null]];
  const chat = m.code ? 'code' : m.reasoning ? 'reasoning' : 'text';
  const ks: Array<[string, string | null]> = [[chat, 'Text chat']];
  if (m.vision) ks.push(['vision', 'Vision']);
  if (m.tools) ks.push(['tools', 'Tools']);
  return ks;
}

function record(rec: object) {
  const f = path.join(OUT, 'results.json');
  const arr = fs.existsSync(f) ? JSON.parse(fs.readFileSync(f, 'utf8')) : [];
  arr.push({ ...rec, at: new Date().toISOString() });
  fs.writeFileSync(f, JSON.stringify(arr, null, 2));
}

const all: ModelInfo[] = (await (await fetch(`${SERVER}/api/models`)).json()).available;
const wanted = (process.env.MODELS ?? '').split(',').map((s) => s.trim()).filter(Boolean);
let models = wanted.length ? all.filter((m) => wanted.includes(m.id)) : all;

// Per-scenario resume: a killed-mid-chunk run leaves partial records; skip exactly the (model,kind,
// scenario) tuples already captured so repeated ≤10-min chunks converge with no duplicates.
const resultsFile = path.join(OUT, 'results.json');
const doneKeys = new Set<string>();
if (fs.existsSync(resultsFile) && !process.env.NO_RESUME) {
  // Only successful captures count as done — errored/timed-out scenarios are retried.
  for (const r of JSON.parse(fs.readFileSync(resultsFile, 'utf8'))) if (r.ok) doneKeys.add(`${r.model}|${r.kind}|${r.scenario}`);
}
const remaining = (m: ModelInfo) =>
  kindsFor(m).some(([k]) => SCENARIOS[k].some((s) => !doneKeys.has(`${m.id}|${k}|${s}`)));
models = models.filter(remaining);

for (const m of models) {
  test(`capture ${m.id}`, async ({ page }) => {
    test.setTimeout(50 * 60 * 1000); // large thinking models (14B+) can take many minutes per scenario
    await page.goto(FRONTEND);
    await page.waitForLoadState('networkidle');
    await page.locator('select[aria-label="Model"]').selectOption(m.id);
    await expect(page.locator('p.config-line strong')).toHaveText(m.id, { timeout: 15000 });

    const kindFilter = (process.env.KINDS ?? '').split(',').map((s) => s.trim()).filter(Boolean);
    for (const [kind, tab] of kindsFor(m).filter(([k]) => kindFilter.length === 0 || kindFilter.includes(k))) {
      // Mode tabs only render for multi-capability models; single-capability models show the panel
      // directly. Click the tab only when it exists.
      if (tab) {
        const tabBtn = page.locator(`button.mode-tab:has-text("${tab}")`);
        if (await tabBtn.count() > 0) await tabBtn.click();
      }
      for (const sid of SCENARIOS[kind]) {
        if (doneKeys.has(`${m.id}|${kind}|${sid}`)) continue; // resume: already captured
        const rec: Record<string, unknown> = { model: m.id, kind, scenario: sid, ok: false, output: '', error: '' };
        const started = Date.now();
        try {
          // Wait out any still-running previous request so a slow scenario can't cascade into the next
          // (the chip is disabled while busy). Large thinking models can run for many minutes.
          await page.locator('button:has-text("Running...")').first()
            .waitFor({ state: 'detached', timeout: 16 * 60 * 1000 }).catch(() => {});
          await page.getByTestId(`scenario-${sid}`).click();
          if (kind === 'vision' && sid === 'upload') await page.locator('input[aria-label="Image"]').setInputFiles(ASSET_IMG);
          if (kind === 'audio' && sid === 'upload') await page.locator('input[aria-label="Audio file"]').setInputFiles(ASSET_WAV);

          const prompt = await page.locator('textarea').inputValue().catch(() => '');
          rec.prompt = prompt;

          await page.locator(`button:has-text("${RUN_BUTTON[kind]}")`).click();
          const reply = page.locator('article.message.assistant p').first();
          await reply.waitFor({ state: 'visible', timeout: 15 * 60 * 1000 });
          const out = (await reply.textContent()) ?? '';
          rec.output = out.slice(0, 4000);
          rec.ok = out.trim().length > 0;
        } catch (e) {
          rec.error = String(e).slice(0, 400);
        }
        rec.ms = Date.now() - started;
        const shot = `${m.id}__${kind}__${sid}`.replace(/[^a-z0-9_.-]/gi, '_') + '.png';
        try { await page.locator('main.app-shell').screenshot({ path: path.join(OUT, shot) }); } catch { /* ignore */ }
        rec.screenshot = shot;
        record(rec);
      }
    }
  });
}
