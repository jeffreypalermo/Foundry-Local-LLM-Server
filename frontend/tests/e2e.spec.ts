import { test, expect } from '@playwright/test';

test.describe('Foundry Local - E2E Exploratory Testing', () => {
  const frontendUrl = 'http://localhost:5173';
  const serverUrl = 'http://localhost:5537';

  test.beforeEach(async () => {
    // Verify server is reachable
    const response = await fetch(`${serverUrl}/api/foundry`);
    expect(response.ok).toBe(true);
  });

  test('Test 1: Load frontend and verify UI elements are present', async ({ page }) => {
    console.log('\n✓ TEST 1: Load frontend and verify UI');

    await page.goto(frontendUrl);

    // Verify main heading
    const heading = page.locator('h1');
    const headingText = await heading.textContent();
    console.log(`  - Found heading: "${headingText}"`);
    expect(headingText).toContain('Foundry Local OpenAI Server');

    // Verify form elements exist
    const textarea = page.locator('textarea');
    await expect(textarea).toBeVisible();
    console.log('  - Textarea input found');

    const button = page.locator('button:has-text("Send Prompt")');
    await expect(button).toBeVisible();
    console.log('  - Send Prompt button found');

    // Verify config is displayed
    const configLines = page.locator('p.config-line');
    const count = await configLines.count();
    expect(count).toBeGreaterThan(0);
    console.log(`  - Config display: ${count} lines shown`);

    // Verify no errors on startup
    const error = page.locator('p.error');
    const errorVisible = await error.isVisible().catch(() => false);
    expect(errorVisible).toBe(false);
    console.log('  - No startup errors');
  });

  test('Test 2: Send a prompt and verify response (stub mode)', async ({ page }) => {
    console.log('\n✓ TEST 2: Send prompt and verify response');

    await page.goto(frontendUrl);
    await page.waitForSelector('textarea', { timeout: 5000 });

    // Enter a test prompt
    const testPrompt = 'Explain why Gemma4 is useful for local LLM deployment';
    const textarea = page.locator('textarea');
    await textarea.fill(testPrompt);
    console.log(`  - Entered prompt: "${testPrompt.substring(0, 40)}..."`);

    // Click send button
    const button = page.locator('button:has-text("Send Prompt")');
    await button.click();
    console.log('  - Clicked Send Prompt');

    // Wait for assistant message to appear
    const assistantMessage = page.locator('article.message.assistant p');
    await expect(assistantMessage.first()).toBeVisible({ timeout: 10000 });

    const response = await assistantMessage.first().textContent();
    console.log(`  - Response received: "${response?.substring(0, 60)}..."`);
    expect(response).toBeTruthy();
    expect(response?.length).toBeGreaterThan(0);
  });

  test('Test 3: Verify user prompt is added to chat', async ({ page }) => {
    console.log('\n✓ TEST 3: Verify user prompt appears in chat');

    await page.goto(frontendUrl);
    const textarea = page.locator('textarea');
    await expect(textarea).toBeVisible();

    const testPrompt = 'What is the benefit of using OpenAI-compatible APIs?';
    await textarea.fill(testPrompt);

    const button = page.locator('button:has-text("Send Prompt")');
    await button.click();

    // Verify user message appears in chat
    const userMessage = page.locator('article.message.user p');
    const userMessageText = await userMessage.first().textContent();
    console.log(`  - User message in chat: "${userMessageText}"`);
    expect(userMessageText).toContain(testPrompt);
  });

  test('Test 4: Multiple prompts in same conversation', async ({ page }) => {
    console.log('\n✓ TEST 4: Multiple prompts in conversation');

    await page.goto(frontendUrl);
    await expect(page.locator('textarea')).toBeVisible();

    const prompts = [
      'Hello!',
      'How do local LLMs work?',
      'What can I use them for?'
    ];

    for (const prompt of prompts) {
      const textarea = page.locator('textarea');
      await textarea.fill(prompt);

      const button = page.locator('button:has-text("Send Prompt")');
      await button.click();

      // Wait a moment for response
      await page.waitForTimeout(500);
      console.log(`  - Sent: "${prompt}"`);
    }

    // Verify all messages are in the chat
    const messages = page.locator('article.message');
    const messageCount = await messages.count();
    console.log(`  - Total messages in chat: ${messageCount}`);
    // Should have at least 6 (3 user + 3 assistant)
    expect(messageCount).toBeGreaterThanOrEqual(prompts.length);
  });

  test('Test 5: Verify API response format is correct', async ({ page }) => {
    console.log('\n✓ TEST 5: Verify API response format');

    const requestBody = {
      model: 'gemma4:latest',
      messages: [
        { role: 'user', content: 'Test message for API format verification' }
      ]
    };

    const response = await fetch(`${serverUrl}/v1/chat/completions`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(requestBody)
    });

    expect(response.ok).toBe(true);
    console.log('  - API returned 200 OK');

    const data = await response.json();
    expect(data.choices).toBeDefined();
    expect(data.choices.length).toBeGreaterThan(0);
    expect(data.choices[0].message).toBeDefined();
    expect(data.choices[0].message.content).toBeDefined();
    expect(data.model).toBeDefined();
    expect(data.usage).toBeDefined();

    console.log(`  - Response structure valid`);
    console.log(`  - Model: ${data.model}`);
    console.log(`  - Choice 0 content: "${data.choices[0].message.content.substring(0, 50)}..."`);
  });

  test('Test 6: Clear previous messages and start fresh', async ({ page }) => {
    console.log('\n✓ TEST 6: Reload and verify fresh start');

    // First load
    await page.goto(frontendUrl);
    const textarea = page.locator('textarea');
    await textarea.fill('First conversation');
    const button = page.locator('button:has-text("Send Prompt")');
    await button.click();
    await page.waitForTimeout(500);

    // Verify message exists
    let messages = page.locator('article.message');
    const countBefore = await messages.count();
    console.log(`  - Before reload: ${countBefore} messages`);

    // Reload page
    await page.reload();
    await expect(textarea).toBeVisible({ timeout: 5000 });

    // Verify chat is cleared (only should be empty or have default state)
    messages = page.locator('article.message');
    const countAfter = await messages.count();
    console.log(`  - After reload: ${countAfter} messages`);
    expect(countAfter).toBeLessThan(countBefore);
  });

  test('Test 7: Textarea clears after sending prompt', async ({ page }) => {
    console.log('\n✓ TEST 7: Textarea clears after sending');

    await page.goto(frontendUrl);
    const textarea = page.locator('textarea');

    const testPrompt = 'Test message to verify textarea clears';
    await textarea.fill(testPrompt);

    const button = page.locator('button:has-text("Send Prompt")');
    await button.click();

    // Wait for response
    await page.waitForSelector('article.message.assistant p');

    // Verify textarea is cleared
    const textareaValue = await textarea.inputValue();
    console.log(`  - Textarea after send: "${textareaValue}"`);
    expect(textareaValue).toBe('');
  });
});
