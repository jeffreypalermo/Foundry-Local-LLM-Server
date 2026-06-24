import { test, expect, Page } from '@playwright/test';

// Records ONE continuous walkthrough that demonstrates EVERY mode of EVERY model: for each model it
// selects it, then for each capability tab the app renders, runs every scenario chip and scrolls the
// fresh result into view. DOM-driven, so it covers exactly what the SPA exposes. Per-scenario errors
// are caught so a single failure can't abort the recording. Produces one video.webm (→ MP4 via
// tools/DemoVideoBuilder).
//
// Run with a reduced server MaxResponseTokens (e.g. 512) so each clip stays short and watchable.
//   npx playwright test tests/demo-video.spec.ts --config=playwright.video.config.ts

const FRONTEND = 'http://localhost:5173';
const RESULT_TIMEOUT = 420_000; // per scenario: long enough that slow thinking models finish and
                                // render their result (the slowest 14B responses observed ~385s)
const IDLE_TIMEOUT = 900_000;   // waitIdle ceiling — must exceed any single request so a slow model's
                                // in-flight request never makes the NEXT model/scenario get skipped.
                                // (It returns the instant the app goes idle, so a high ceiling is free.)

async function setCaption(page: Page, line1: string, line2 = '') {
  await page.evaluate(({ line1, line2 }) => {
    let el = document.getElementById('demo-caption');
    if (!el) {
      el = document.createElement('div');
      el.id = 'demo-caption';
      el.style.cssText =
        'position:fixed;top:0;left:0;right:0;z-index:99999;background:linear-gradient(90deg,#0f2557,#2563eb);' +
        'color:#fff;padding:10px 22px;font-family:system-ui,"Segoe UI",sans-serif;box-shadow:0 3px 16px rgba(0,0,0,.35);';
      document.body.appendChild(el);
      // keep the app clear of the fixed banner
      document.body.style.paddingTop = '64px';
    }
    el.innerHTML =
      `<div style="font-size:19px;font-weight:700;letter-spacing:.2px">${line1}</div>` +
      (line2 ? `<div style="font-size:13px;font-weight:400;opacity:.92;margin-top:2px">${line2}</div>` : '');
  }, { line1, line2 });
}

async function scrollToLatestResult(page: Page) {
  // Smooth-scroll the newest assistant message into view so the result is shown as it arrives.
  await page.evaluate(() => {
    const els = document.querySelectorAll('article.message.assistant');
    els[els.length - 1]?.scrollIntoView({ behavior: 'smooth', block: 'center' });
  });
  await page.waitForTimeout(2200);
}

// The model <select> and the run buttons are disabled while a request is in flight (busy). Block
// until the app is idle again so selectOption / run never hit a disabled control (which would time
// out and get the whole model or scenario skipped — the bug that dropped 19 models in run #1).
async function waitIdle(page: Page) {
  await expect(page.locator('select[aria-label="Model"]')).toBeEnabled({ timeout: IDLE_TIMEOUT });
}

// Click whichever run-button the current panel/scenario exposes; returns false if none is actionable.
async function clickRunButton(page: Page): Promise<boolean> {
  const candidates = ['Run conversation', 'Send with Tools', 'Describe Image', 'Transcribe', 'Send Prompt'];
  for (const label of candidates) {
    const btn = page.locator(`button:has-text("${label}")`).first();
    if (await btn.count() > 0 && await btn.isVisible() && await btn.isEnabled()) {
      await btn.click();
      return true;
    }
  }
  return false;
}

test('Foundry Local — every mode of every model (video)', async ({ page }) => {
  test.setTimeout(5 * 60 * 60 * 1000); // up to 5 hours for the full matrix

  await page.goto(FRONTEND);
  await page.waitForLoadState('networkidle');
  await expect(page.locator('select[aria-label="Model"] option').first()).toBeAttached();

  await setCaption(page, 'Foundry Local — every mode of every model',
    'One local server · the full GPU catalog · text · code · reasoning · vision · tools · speech');
  await page.waitForTimeout(3500);

  const allModels: string[] = await page
    .locator('select[aria-label="Model"] option')
    .evaluateAll((opts) => opts.map((o) => (o as HTMLOptionElement).value).filter(Boolean));

  // Optional MODELS=a,b,c env filter (used to record a specific subset, e.g. models missed in a prior run).
  const wanted = (process.env.MODELS || '').split(',').map((s) => s.trim()).filter(Boolean);
  const models = wanted.length ? allModels.filter((m) => wanted.includes(m)) : allModels;

  let mi = 0;
  for (const model of models) {
    mi++;
    try {
      await waitIdle(page); // never switch models while a request is still in flight
      await page.locator('select[aria-label="Model"]').selectOption(model);
      await expect(page.locator('p.config-line strong')).toHaveText(model, { timeout: 20_000 });
      await page.waitForTimeout(500);
    } catch {
      continue; // couldn't select — skip this model
    }

    // Tabs the app renders for this model (single-capability models render none → one default panel).
    const tabLabels: string[] = await page
      .locator('button.mode-tab')
      .evaluateAll((els) => els.map((e) => (e.textContent || '').trim()).filter(Boolean));
    const tabs = tabLabels.length > 0 ? tabLabels : ['(default)'];

    for (const tab of tabs) {
      if (tab !== '(default)') {
        try {
          await page.locator(`button.mode-tab:has-text("${tab}")`).first().click();
          await page.waitForTimeout(500);
        } catch { continue; }
      }

      // Scenario chips in this panel.
      const chipCount = await page.locator('.scenario-chip').count();
      for (let i = 0; i < chipCount; i++) {
        const chip = page.locator('.scenario-chip').nth(i);
        const chipLabel = ((await chip.textContent()) || `scenario ${i + 1}`).trim();
        const testId = (await chip.getAttribute('data-testid')) || '';
        if (testId.endsWith('upload')) continue; // skip "upload your own" (needs a user file)

        await setCaption(page, `Model ${mi}/${models.length}: ${model}`, `${tab} · ${chipLabel}`);
        try {
          await waitIdle(page); // never start a scenario while the previous request is still running
          await chip.click();
          await page.waitForTimeout(500);

          const before = await page.locator('article.message.assistant p').count();
          if (!(await clickRunButton(page))) continue;

          // Wait for a new assistant message, then for the busy state to clear (covers multi-turn).
          await page.waitForFunction(
            (arg) => document.querySelectorAll(arg.sel).length > arg.before,
            { sel: 'article.message.assistant p', before },
            { timeout: RESULT_TIMEOUT },
          );
          await page.locator('button:has-text("Running...")')
            .waitFor({ state: 'hidden', timeout: RESULT_TIMEOUT }).catch(() => {});

          await scrollToLatestResult(page);
        } catch {
          // A scenario timed out or errored — leave whatever rendered on screen and move on.
          await page.waitForTimeout(400);
        }
      }
    }
  }

  await setCaption(page, 'Every mode · every model · one local server',
    'React + Blazor clients · OpenAI-compatible /v1 API · 100% local GPU inference');
  await page.waitForTimeout(4000);
});
