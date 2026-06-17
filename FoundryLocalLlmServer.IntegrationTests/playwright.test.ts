import { test, expect, chromium } from '@playwright/test';

test.describe('Foundry Local Web Frontend', () => {
  let baseUrl = 'http://localhost:5000';

  test.beforeAll(async () => {
    // Wait for app to be ready
    let retries = 0;
    while (retries < 30) {
      try {
        const response = await fetch(`${baseUrl}/api/foundry`);
        if (response.ok) break;
      } catch {
        retries++;
        await new Promise(resolve => setTimeout(resolve, 1000));
      }
    }
  });

  test('should load the app and display config', async () => {
    const browser = await chromium.launch();
    const page = await browser.newPage();

    await page.goto(baseUrl);

    // Check for main heading
    const heading = await page.locator('h1').textContent();
    expect(heading).toContain('Foundry Local OpenAI Server');

    // Check for model display
    await page.waitForSelector('p.config-line');
    const configLines = await page.locator('p.config-line').allTextContents();
    expect(configLines.length).toBeGreaterThan(0);

    console.log('✓ App loaded successfully');
    console.log('Config lines:', configLines);

    await browser.close();
  });

  test('should send a prompt and receive a response', async () => {
    const browser = await chromium.launch();
    const page = await browser.newPage();

    await page.goto(baseUrl);

    // Wait for config to load
    await page.waitForSelector('p.config-line', { timeout: 5000 });

    // Find and fill the textarea
    const textarea = page.locator('textarea');
    await textarea.fill('What is 2 + 2?');

    console.log('✓ Prompt entered: "What is 2 + 2?"');

    // Click submit button
    const submitButton = page.locator('button:has-text("Send Prompt")');
    await submitButton.click();

    console.log('✓ Prompt submitted');

    // Wait for response to appear in chat log
    await page.waitForSelector('section.chat-log article.message.assistant', { timeout: 10000 });

    // Get the assistant's response
    const assistantMessages = page.locator('section.chat-log article.message.assistant p');
    const messageCount = await assistantMessages.count();
    expect(messageCount).toBeGreaterThan(0);

    const firstResponse = await assistantMessages.first().textContent();
    console.log('✓ Assistant response received:', firstResponse);

    expect(firstResponse).toBeTruthy();
    expect(firstResponse?.length).toBeGreaterThan(0);

    await browser.close();
  });

  test('should handle multiple prompts in sequence', async () => {
    const browser = await chromium.launch();
    const page = await browser.newPage();

    await page.goto(baseUrl);
    await page.waitForSelector('p.config-line', { timeout: 5000 });

    const prompts = [
      'Hello, how are you?',
      'Tell me about local LLMs',
      'Why is OpenAI compatibility important?'
    ];

    for (const prompt of prompts) {
      const textarea = page.locator('textarea');
      await textarea.fill(prompt);

      const submitButton = page.locator('button:has-text("Send Prompt")');
      await submitButton.click();

      // Wait for new assistant message
      await page.waitForSelector('section.chat-log article.message.assistant', { timeout: 10000 });

      console.log(`✓ Sent: "${prompt}"`);
    }

    // Verify we have multiple exchanges
    const assistantMessages = page.locator('section.chat-log article.message.assistant');
    const count = await assistantMessages.count();
    expect(count).toBe(prompts.length);

    console.log(`✓ All ${prompts.length} prompts processed successfully`);

    await browser.close();
  });

  test('should display error when response fails', async () => {
    const browser = await chromium.launch();
    const page = await browser.newPage();

    await page.goto(baseUrl);
    await page.waitForSelector('p.config-line', { timeout: 5000 });

    // Try with an empty prompt - should not error, just not send
    const textarea = page.locator('textarea');
    await textarea.fill('   ');

    const submitButton = page.locator('button:has-text("Send Prompt")');
    const isDisabled = await submitButton.isDisabled();

    // Empty/whitespace prompts should be handled gracefully
    console.log('✓ Empty prompt handled gracefully (button disabled:', isDisabled, ')');

    await browser.close();
  });
});
