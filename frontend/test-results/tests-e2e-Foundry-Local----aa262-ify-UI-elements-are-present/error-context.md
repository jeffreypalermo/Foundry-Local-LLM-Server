# Instructions

- Following Playwright test failed.
- Explain why, be concise, respect Playwright best practices.
- Provide a snippet of code with the fix, if possible.

# Test info

- Name: tests\e2e.spec.ts >> Foundry Local - E2E Exploratory Testing >> Test 1: Load frontend and verify UI elements are present
- Location: tests\e2e.spec.ts:13:3

# Error details

```
Error: expect(received).toBe(expected) // Object.is equality

Expected: false
Received: true
```

# Page snapshot

```yaml
- main [ref=e3]:
  - heading "Foundry Local OpenAI Server" [level=1] [ref=e4]
  - paragraph [ref=e5]:
    - text: "Model:"
    - strong [ref=e6]: loading...
  - paragraph [ref=e7]:
    - text: "Foundry endpoint:"
    - code [ref=e8]: loading...
  - paragraph [ref=e9]:
    - text: "OpenAI-compatible endpoint for tools:"
    - code [ref=e10]: /v1/chat/completions
  - generic [ref=e11]:
    - textbox "Enter a prompt" [ref=e12]: Explain why local OpenAI-compatible endpoints are useful.
    - button "Send Prompt" [disabled] [ref=e13] [cursor=pointer]
  - paragraph [ref=e14]: Could not load Foundry Local settings (502)
  - region "Chat transcript"
```

# Test source

```ts
  1   | import { test, expect } from '@playwright/test';
  2   | 
  3   | test.describe('Foundry Local - E2E Exploratory Testing', () => {
  4   |   const frontendUrl = 'http://localhost:5173';
  5   |   const serverUrl = 'http://localhost:5537';
  6   | 
  7   |   test.beforeEach(async () => {
  8   |     // Verify server is reachable
  9   |     const response = await fetch(`${serverUrl}/api/foundry`);
  10  |     expect(response.ok).toBe(true);
  11  |   });
  12  | 
  13  |   test('Test 1: Load frontend and verify UI elements are present', async ({ page }) => {
  14  |     console.log('\n✓ TEST 1: Load frontend and verify UI');
  15  | 
  16  |     await page.goto(frontendUrl);
  17  | 
  18  |     // Verify main heading
  19  |     const heading = page.locator('h1');
  20  |     const headingText = await heading.textContent();
  21  |     console.log(`  - Found heading: "${headingText}"`);
  22  |     expect(headingText).toContain('Foundry Local OpenAI Server');
  23  | 
  24  |     // Verify form elements exist
  25  |     const textarea = page.locator('textarea');
  26  |     await expect(textarea).toBeVisible();
  27  |     console.log('  - Textarea input found');
  28  | 
  29  |     const button = page.locator('button:has-text("Send Prompt")');
  30  |     await expect(button).toBeVisible();
  31  |     console.log('  - Send Prompt button found');
  32  | 
  33  |     // Verify config is displayed
  34  |     const configLines = page.locator('p.config-line');
  35  |     const count = await configLines.count();
  36  |     expect(count).toBeGreaterThan(0);
  37  |     console.log(`  - Config display: ${count} lines shown`);
  38  | 
  39  |     // Verify no errors on startup
  40  |     const error = page.locator('p.error');
  41  |     const errorVisible = await error.isVisible().catch(() => false);
> 42  |     expect(errorVisible).toBe(false);
      |                          ^ Error: expect(received).toBe(expected) // Object.is equality
  43  |     console.log('  - No startup errors');
  44  |   });
  45  | 
  46  |   test('Test 2: Send a prompt and verify response (stub mode)', async ({ page }) => {
  47  |     console.log('\n✓ TEST 2: Send prompt and verify response');
  48  | 
  49  |     await page.goto(frontendUrl);
  50  |     await page.waitForSelector('textarea', { timeout: 5000 });
  51  | 
  52  |     // Enter a test prompt
  53  |     const testPrompt = 'Explain why Gemma4 is useful for local LLM deployment';
  54  |     const textarea = page.locator('textarea');
  55  |     await textarea.fill(testPrompt);
  56  |     console.log(`  - Entered prompt: "${testPrompt.substring(0, 40)}..."`);
  57  | 
  58  |     // Click send button
  59  |     const button = page.locator('button:has-text("Send Prompt")');
  60  |     await button.click();
  61  |     console.log('  - Clicked Send Prompt');
  62  | 
  63  |     // Wait for assistant message to appear
  64  |     const assistantMessage = page.locator('article.message.assistant p');
  65  |     await expect(assistantMessage.first()).toBeVisible({ timeout: 10000 });
  66  | 
  67  |     const response = await assistantMessage.first().textContent();
  68  |     console.log(`  - Response received: "${response?.substring(0, 60)}..."`);
  69  |     expect(response).toBeTruthy();
  70  |     expect(response?.length).toBeGreaterThan(0);
  71  |   });
  72  | 
  73  |   test('Test 3: Verify user prompt is added to chat', async ({ page }) => {
  74  |     console.log('\n✓ TEST 3: Verify user prompt appears in chat');
  75  | 
  76  |     await page.goto(frontendUrl);
  77  |     const textarea = page.locator('textarea');
  78  |     await expect(textarea).toBeVisible();
  79  | 
  80  |     const testPrompt = 'What is the benefit of using OpenAI-compatible APIs?';
  81  |     await textarea.fill(testPrompt);
  82  | 
  83  |     const button = page.locator('button:has-text("Send Prompt")');
  84  |     await button.click();
  85  | 
  86  |     // Verify user message appears in chat
  87  |     const userMessage = page.locator('article.message.user p');
  88  |     const userMessageText = await userMessage.first().textContent();
  89  |     console.log(`  - User message in chat: "${userMessageText}"`);
  90  |     expect(userMessageText).toContain(testPrompt);
  91  |   });
  92  | 
  93  |   test('Test 4: Multiple prompts in same conversation', async ({ page }) => {
  94  |     console.log('\n✓ TEST 4: Multiple prompts in conversation');
  95  | 
  96  |     await page.goto(frontendUrl);
  97  |     await expect(page.locator('textarea')).toBeVisible();
  98  | 
  99  |     const prompts = [
  100 |       'Hello!',
  101 |       'How do local LLMs work?',
  102 |       'What can I use them for?'
  103 |     ];
  104 | 
  105 |     for (const prompt of prompts) {
  106 |       const textarea = page.locator('textarea');
  107 |       await textarea.fill(prompt);
  108 | 
  109 |       const button = page.locator('button:has-text("Send Prompt")');
  110 |       await button.click();
  111 | 
  112 |       // Wait a moment for response
  113 |       await page.waitForTimeout(500);
  114 |       console.log(`  - Sent: "${prompt}"`);
  115 |     }
  116 | 
  117 |     // Verify all messages are in the chat
  118 |     const messages = page.locator('article.message');
  119 |     const messageCount = await messages.count();
  120 |     console.log(`  - Total messages in chat: ${messageCount}`);
  121 |     // Should have at least 6 (3 user + 3 assistant)
  122 |     expect(messageCount).toBeGreaterThanOrEqual(prompts.length);
  123 |   });
  124 | 
  125 |   test('Test 5: Verify API response format is correct', async ({ page }) => {
  126 |     console.log('\n✓ TEST 5: Verify API response format');
  127 | 
  128 |     const requestBody = {
  129 |       model: 'gemma4:latest',
  130 |       messages: [
  131 |         { role: 'user', content: 'Test message for API format verification' }
  132 |       ]
  133 |     };
  134 | 
  135 |     const response = await fetch(`${serverUrl}/v1/chat/completions`, {
  136 |       method: 'POST',
  137 |       headers: { 'Content-Type': 'application/json' },
  138 |       body: JSON.stringify(requestBody)
  139 |     });
  140 | 
  141 |     expect(response.ok).toBe(true);
  142 |     console.log('  - API returned 200 OK');
```