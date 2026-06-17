import { test, expect } from '@playwright/test';

// Playwright tests for Foundry Local Web Frontend
// These tests verify that the frontend can successfully communicate with the backend API

test.describe('Foundry Local Web Frontend - Exploratory Testing', () => {
  // The Aspire dashboard runs on https, but the frontend should be accessible
  const baseUrl = 'http://localhost';

  test('Test 1: Load page and verify UI elements', async ({ page }) => {
    console.log('\n=== Test 1: Load page and verify UI elements ===');

    try {
      // Try common ports where Vite might run
      for (const port of [5173, 5174, 5175, 3000, 8080]) {
        try {
          await page.goto(`http://localhost:${port}`, { waitUntil: 'networkidle', timeout: 5000 });
          console.log(`✓ Successfully navigated to http://localhost:${port}`);
          break;
        } catch {
          console.log(`✗ Port ${port} not responding`);
          continue;
        }
      }

      // Wait for main heading
      const heading = page.locator('h1');
      await heading.waitFor({ state: 'visible', timeout: 5000 });
      const headingText = await heading.textContent();

      console.log(`✓ Found heading: "${headingText}"`);
      expect(headingText).toContain('Foundry Local OpenAI Server');

      // Check for form elements
      const textarea = page.locator('textarea');
      await textarea.waitFor({ state: 'visible', timeout: 5000 });
      console.log('✓ Found textarea input');

      const button = page.locator('button:has-text("Send Prompt")');
      await button.waitFor({ state: 'visible', timeout: 5000 });
      console.log('✓ Found Send Prompt button');

      // Check for config display
      const configLines = page.locator('p.config-line');
      const count = await configLines.count();
      console.log(`✓ Found ${count} config lines`);

      for (let i = 0; i < Math.min(count, 3); i++) {
        const text = await configLines.nth(i).textContent();
        console.log(`  - Config ${i + 1}: ${text?.substring(0, 60)}`);
      }
    } catch (error) {
      console.error('✗ Test failed:', error);
      throw error;
    }
  });

  test('Test 2: Send a simple prompt and verify response', async ({ page }) => {
    console.log('\n=== Test 2: Send prompt and verify response ===');

    try {
      // Navigate to app
      for (const port of [5173, 5174, 5175, 3000, 8080]) {
        try {
          await page.goto(`http://localhost:${port}`, { waitUntil: 'networkidle', timeout: 5000 });
          break;
        } catch {
          continue;
        }
      }

      // Wait for textarea
      const textarea = page.locator('textarea');
      await textarea.waitFor({ state: 'visible', timeout: 5000 });

      // Enter a test prompt
      const testPrompt = 'What is artificial intelligence?';
      await textarea.fill(testPrompt);
      console.log(`✓ Entered prompt: "${testPrompt}"`);

      // Click submit
      const button = page.locator('button:has-text("Send Prompt")');
      await button.click();
      console.log('✓ Clicked Send Prompt button');

      // Wait for response in chat log
      const chatLog = page.locator('section.chat-log');
      await chatLog.waitFor({ state: 'visible', timeout: 5000 });

      // Look for assistant message
      const assistantMessage = page.locator('article.message.assistant p');
      await assistantMessage.first().waitFor({ state: 'visible', timeout: 10000 });

      const response = await assistantMessage.first().textContent();
      console.log(`✓ Received response: "${response?.substring(0, 100)}${(response?.length ?? 0) > 100 ? '...' : ''}"`);

      // Verify response is not empty
      expect(response).toBeTruthy();
      expect(response?.length).toBeGreaterThan(0);
    } catch (error) {
      console.error('✗ Test failed:', error);
      throw error;
    }
  });

  test('Test 3: Multiple prompts in conversation', async ({ page }) => {
    console.log('\n=== Test 3: Multiple prompts in conversation ===');

    try {
      // Navigate to app
      for (const port of [5173, 5174, 5175, 3000, 8080]) {
        try {
          await page.goto(`http://localhost:${port}`, { waitUntil: 'networkidle', timeout: 5000 });
          break;
        } catch {
          continue;
        }
      }

      const prompts = [
        'Hello!',
        'How do you work?',
        'What can you do?'
      ];

      for (const prompt of prompts) {
        const textarea = page.locator('textarea');
        await textarea.fill(prompt);

        const button = page.locator('button:has-text("Send Prompt")');
        await button.click();

        console.log(`✓ Sent: "${prompt}"`);

        // Wait a bit for response
        await page.waitForTimeout(1000);
      }

      // Verify multiple exchanges
      const messages = page.locator('article.message');
      const messageCount = await messages.count();
      console.log(`✓ Total messages in chat: ${messageCount}`);
      expect(messageCount).toBeGreaterThan(0);

    } catch (error) {
      console.error('✗ Test failed:', error);
      throw error;
    }
  });

  test('Test 4: Verify stub response format', async ({ page }) => {
    console.log('\n=== Test 4: Verify API response format ===');

    try {
      // Navigate to app
      for (const port of [5173, 5174, 5175, 3000, 8080]) {
        try {
          await page.goto(`http://localhost:${port}`, { waitUntil: 'networkidle', timeout: 5000 });
          break;
        } catch {
          continue;
        }
      }

      // Intercept API calls
      await page.route('**/v1/chat/completions', (route) => {
        route.continue();
      });

      // Send a prompt
      const textarea = page.locator('textarea');
      await textarea.fill('Test prompt for API verification');

      const button = page.locator('button:has-text("Send Prompt")');

      // Wait for response
      const responsePromise = page.waitForResponse(
        response => response.url().includes('/v1/chat/completions') && response.status() === 200
      );

      await button.click();
      const response = await responsePromise;

      const jsonBody = await response.json();
      console.log(`✓ API Response status: ${response.status()}`);
      console.log(`  - Has choices: ${!!jsonBody.choices}`);
      console.log(`  - Has model: ${!!jsonBody.model}`);
      console.log(`  - Has usage: ${!!jsonBody.usage}`);

      expect(jsonBody.choices).toBeTruthy();
      expect(jsonBody.choices.length).toBeGreaterThan(0);
      expect(jsonBody.choices[0].message).toBeTruthy();
      expect(jsonBody.choices[0].message.content).toBeTruthy();

      console.log(`✓ API response format is correct`);
    } catch (error) {
      console.error('✗ Test failed:', error);
      throw error;
    }
  });

  test('Test 5: Error handling - config loading', async ({ page }) => {
    console.log('\n=== Test 5: Verify config loads without errors ===');

    try {
      // Navigate to app
      for (const port of [5173, 5174, 5175, 3000, 8080]) {
        try {
          await page.goto(`http://localhost:${port}`, { waitUntil: 'networkidle', timeout: 5000 });
          break;
        } catch {
          continue;
        }
      }

      // Check for error messages
      const errorElement = page.locator('p.error');
      const errorVisible = await errorElement.isVisible().catch(() => false);

      if (errorVisible) {
        const errorText = await errorElement.textContent();
        console.log(`✗ Error found on page: "${errorText}"`);
        throw new Error(`Page error: ${errorText}`);
      } else {
        console.log('✓ No errors on page load');
      }

      // Verify config values are displayed
      const configText = await page.locator('main').textContent();
      expect(configText).toContain('Model:');
      expect(configText).toContain('endpoint');
      console.log('✓ Config values displayed successfully');

    } catch (error) {
      console.error('✗ Test failed:', error);
      throw error;
    }
  });
});
